using Shared.Application.Events;
using WorkflowService.Entities;
using WorkflowService.Enums;
using WorkflowService.Events;
using WorkflowService.Models.Workflow;

namespace WorkflowService.Actions.Executors;

public interface IActionExecutor
{
    WorkflowNodeType WorkflowNodeType { get; }

    Task ExecuteAsync(ActionEntity action, ExecuteActionCommand message, CancellationToken ct);

    Task<bool> CompleteAsync(ActionEntity action, ActionCompletedEvent message, Session session, CancellationToken ct)
    {
        action.MarkCompleted();
        return Task.FromResult(true);
    }

    bool CanHandleUserInput => false;

    Task HandleUserInputAsync(Session session, WorkflowNode currentNode, BotIncomingMessage message, CancellationToken ct)
        => Task.CompletedTask;
}