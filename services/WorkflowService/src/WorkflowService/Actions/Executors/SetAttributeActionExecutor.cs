using MassTransit;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Interfaces;
using WorkflowService.Models.Node;
using WorkflowService.Utils;

namespace WorkflowService.Actions.Executors;

public class SetAttributeActionExecutor(
    ITopicProducer<string, ActionCompletedEvent> producer,
    ISessionRepository sessionRepository,
    IVariableService variableService,
    IWorkflowGraphCache graphCache,
    WorkflowTextRenderer workflowTextRenderer)
    : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.SetAttribute;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        var session = action.Session ?? await sessionRepository.GetAsync(action.SessionId, ct);
        if (session == null)
            return;

        var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);
        var node = graph.GetNode(session.CurrentNodeId!.Value);

        if (node.Data is not SetAttributeNodeData setAttributeData)
            return;

        var renderedValue = workflowTextRenderer.RenderText(setAttributeData.Value, session);

        variableService.SetAttribute(session, setAttributeData.Attribute, renderedValue);
        await variableService.SyncIfDirtyAsync(session, ct);
        await sessionRepository.SaveAsync(session, ct);

        var partitionKey = $"{session.ChannelId}:{message.ExternalUserId}";
        await producer.Produce(partitionKey, new ActionCompletedEvent(action.Id, Success: true), ct);
    }
}
