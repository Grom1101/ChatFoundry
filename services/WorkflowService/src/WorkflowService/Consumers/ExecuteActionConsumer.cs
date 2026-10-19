using MassTransit;
using WorkflowService.Actions.Factories;
using WorkflowService.Events;
using WorkflowService.Interfaces;

using Shared.Domain.Enums;

namespace WorkflowService.Consumers;

public class ExecuteActionConsumer(
    IActionRepository actionRepository,
    IActionExecutorFactory executorFactory,
    ISessionResolver sessionResolver)
    : IConsumer<ExecuteActionCommand>
{
    public async Task Consume(ConsumeContext<ExecuteActionCommand> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var action = await actionRepository.GetAsync(message.ActionId, ct);

        if (action == null)
            return;

        if (action.Status != ActionStatus.Pending)
            return;

        var session = action.Session;
        if (session == null || session.Status == SessionStatus.Completed || session.Status == SessionStatus.Failed || session.Status == SessionStatus.Cancelled)
            return;

        if (session.CurrentNodeId != action.NodeId)
            return;

        var executor = executorFactory.Get(action.WorkflowNodeType);

        try
        {
            action.MarkInProgress();
            await actionRepository.SaveAsync(action, ct);

            await executor.ExecuteAsync(action, message, ct);
        }
        catch (Exception ex)
        {
            const int maxErrorLength = 2000;
            action.ErrorMessage = ex.Message.Length <= maxErrorLength
                ? ex.Message
                : ex.Message[..maxErrorLength] + "...";
            action.MarkFailed();
            await actionRepository.SaveAsync(action, ct);
            await sessionResolver.CloseSessionAndHierarchyAsync(action.SessionId, ct);
        }
    }
}