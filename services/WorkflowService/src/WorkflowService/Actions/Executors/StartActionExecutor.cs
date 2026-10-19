using MassTransit;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Utils;

namespace WorkflowService.Actions.Executors;

public class StartActionExecutor(
    ITopicProducer<string, ActionCompletedEvent> producer,
    ISessionRepository sessionRepository,
    IVariableService variableService,
    IWorkflowGraphCache graphCache) : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.Start;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        Console.WriteLine("StartActionExecutor");

        var session = action.Session ?? await sessionRepository.GetAsync(action.SessionId, ct);
        if (session == null)
            return;

        var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);
        var node = graph.GetNode(session.CurrentNodeId!.Value);

        variableService.SetVariable(session, $"$node.{node.Id}.output", action.Payload);
        variableService.SetVariable(session, $"$node.{node.Id}.messageKind", action.MessageKind.ToString());
        await variableService.SyncIfDirtyAsync(session, ct);
        await sessionRepository.SaveAsync(session, ct);

        var partitionKey = $"{session.ChannelId}:{message.ExternalUserId}";
        await producer.Produce(partitionKey, new ActionCompletedEvent(action.Id, Success: true), ct);
    }
}
