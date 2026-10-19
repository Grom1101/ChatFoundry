using MassTransit;
using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Interfaces;

namespace WorkflowService.Actions.Executors;

public class TransferToOperatorActionExecutor(
    ISessionRepository sessionRepository,
    IVariableService variableService,
    IWorkflowGraphCache graphCache,
    ITopicProducer<LiveChatRequestedEvent> producer
) : IActionExecutor
{
    public WorkflowNodeType WorkflowNodeType => WorkflowNodeType.TransferToOperator;

    public async Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct)
    {
        var session = action.Session ?? await sessionRepository.GetAsync(action.SessionId, ct);
        if (session == null)
            throw new InvalidOperationException($"Session {action.SessionId} not found");

        var graph = await graphCache.GetGraphAsync(session.WorkflowId, ct);

        var clientFirstName = variableService.GetVariable(session, "$global.name");
        var clientUserName = variableService.GetVariable(session, "$global.username");

        await producer.Produce(new LiveChatRequestedEvent(
            session.Id,
            session.ClientId,
            session.Channel,
            session.ChannelId,
            graph.BotId,
            graph.BotName,
            graph.CompanyId,
            clientFirstName,
            clientUserName,
            "Transferring to operator...",
            ActionId: action.Id
        ), ct);
    }
}
