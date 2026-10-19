using Shared.Domain.Enums;
using WorkflowService.Entities;

namespace WorkflowService.Interfaces;

public interface IActionRepository
{
    Task<ActionEntity?> GetAsync(Guid actionId, CancellationToken ct);

    Task<List<ActionEntity>> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default);

    Task AddAsync(ActionEntity action, CancellationToken ct = default);

    Task SaveAsync(ActionEntity action, CancellationToken ct = default);

    Task<ActionEntity?> GetPendingActionAsync(Guid sessionId, Guid nodeId, WorkflowService.Enums.WorkflowNodeType type, CancellationToken ct);

    Task<ActionEntity?> GetProcessingWebhookActionAsync(Guid botId, string clientId, DefaultChannel channel, CancellationToken ct);

    Task CancelPendingOrProcessingBySessionIdAsync(Guid sessionId, CancellationToken ct);
}
