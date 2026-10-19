using MassTransit;
using Shared.Application.Events;
using Shared.Domain.Enums;
using WorkflowService.Entities;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Models.Node;
using WorkflowService.Utils;

using WorkflowService.Actions.Factories;

namespace WorkflowService.Consumers;

public class ActionCompletedConsumer(
    IActionFactory actionFactory,
    IActionRepository actionRepository,
    ISessionRepository sessionRepository,
    ISessionResolver sessionResolver,
    IVariableService variableService,
    IActionExecutorFactory executorFactory,
    ITopicProducer<string, ExecuteActionCommand> producer,
    ITopicProducer<string, ActionCompletedEvent> actionCompletedProducer,
    IWorkflowGraphCache graphCache)
    : IConsumer<ActionCompletedEvent>
{
    public async Task Consume(ConsumeContext<ActionCompletedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        Session? session = null;
        try
        {
            if (msg.ActionId == Guid.Empty)
                return;

            var lastUserAction = await actionRepository.GetAsync(msg.ActionId, ct);
            if (lastUserAction == null)
                return;

            session = lastUserAction.Session;

            if (lastUserAction.Status != ActionStatus.Failed)
            {
                if (!msg.Success)
                {
                    lastUserAction.MarkFailed();
                    await actionRepository.SaveAsync(lastUserAction, ct);
                    await sessionResolver.CloseSessionAndHierarchyAsync(session.Id, ct);
                    return;
                }

                var executor = executorFactory.Get(lastUserAction.WorkflowNodeType);
                var shouldAdvance = await executor.CompleteAsync(lastUserAction, msg, session, ct);
                await actionRepository.SaveAsync(lastUserAction, ct);

                if (!shouldAdvance)
                    return;
            }
            session.CompletedAt = DateTime.UtcNow;

            var workflowGraph = await graphCache.GetGraphAsync(session.WorkflowId, ct);

            var nextNode = workflowGraph.GetNextNode(lastUserAction.NodeId, session, variableService);
            if (nextNode == null)
            {
                await sessionResolver.CloseSessionAsync(session.Id, ct);

                if (session.ParentSessionId != null)
                    await ResumeParentSessionAsync(session, ct);

                return;
            }

            session.MoveTo(nextNode.Id);

            var nextAction = await actionFactory.CreateAsync(
                session,
                nextNode,
                nextNode.Type,
                cancellationToken: ct);

            await actionRepository.AddAsync(nextAction, ct);

            var partitionKey = $"{session.ChannelId}:{session.ClientId}";
            await producer.Produce(partitionKey, new ExecuteActionCommand(nextAction.Id, session.ClientId, session.Channel, session.ChannelId), ct);
        }
        catch
        {
            if (session != null)
                await sessionResolver.CloseSessionAndHierarchyAsync(session.Id, ct);
        }
    }

    private async Task ResumeParentSessionAsync(Session childSession, CancellationToken ct)
    {
        var parentSession = await sessionRepository.GetAsync(childSession.ParentSessionId!.Value, ct);
        if (parentSession == null || parentSession.Status != SessionStatus.Active)
            return;

        var parentAction = await actionRepository.GetAsync(childSession.ParentActionId!.Value, ct);
        if (parentAction == null)
            return;

        var parentGraph = await graphCache.GetGraphAsync(parentSession.WorkflowId, ct);
        var subNode = parentGraph.GetNode(parentAction.NodeId);

        if (subNode.Data is SubWorkflowNodeData subData)
        {
            foreach (var (parentKey, childKey) in subData.OutputMappings)
            {
                if (string.IsNullOrEmpty(childKey) || string.IsNullOrEmpty(parentKey)) continue;

                var value = variableService.GetVariable(childSession, childKey);
                variableService.SetVariable(parentSession, parentKey, value);
            }
        }

        await sessionRepository.SaveAsync(parentSession, ct);

        var partitionKey = $"{parentSession.ChannelId}:{parentSession.ClientId}";
        await actionCompletedProducer.Produce(
            partitionKey,
            new ActionCompletedEvent(parentAction.Id, Success: true),
            ct);
    }
}