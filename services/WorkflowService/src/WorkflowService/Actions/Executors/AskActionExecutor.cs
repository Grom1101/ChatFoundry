using MassTransit;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Models.Workflow;
using WorkflowService.Utils;

namespace WorkflowService.Actions.Executors;

public class AskActionExecutor(
    IMessageSender messageSender,
    IActionFactory actionFactory,
    IActionRepository actionRepository,
    IWorkflowGraphCache graphCache,
    ITopicProducer<string, ExecuteActionCommand> producer
) : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.Ask;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        await messageSender.SendAsync(action, message, ct);
    }

    public async Task<bool> CompleteAsync(ActionEntity action, ActionCompletedEvent message, Session session, CancellationToken ct)
    {
        action.MarkCompleted();

        var existingInput = await actionRepository.GetPendingActionAsync(
            session.Id, action.NodeId, WorkflowNodeType.Input, ct);

        if (existingInput == null)
        {
            var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);
            var currentNode = graph.GetNode(action.NodeId);
            var inputAction = await actionFactory.CreateAsync(
                session,
                currentNode,
                WorkflowNodeType.Input,
                cancellationToken: ct);
            await actionRepository.AddAsync(inputAction, ct);
        }

        return false;
    }

    public bool CanHandleUserInput => true;

    public async Task HandleUserInputAsync(Session session, WorkflowNode currentNode, BotIncomingMessage message, CancellationToken ct)
    {
        var pendingInputAction = await actionRepository.GetPendingActionAsync(
            session.Id, currentNode.Id, WorkflowNodeType.Input, ct);

        if (pendingInputAction == null)
            return;

        pendingInputAction.Payload = message.Payload;
        pendingInputAction.MessageKind = message.MessageKind;
        await actionRepository.SaveAsync(pendingInputAction, ct);

        var partitionKey = $"{message.ChannelId}:{message.ExternalUserId}";
        await producer.Produce(partitionKey, new ExecuteActionCommand(pendingInputAction.Id, message.ExternalUserId, message.Channel, message.ChannelId), ct);
    }
}