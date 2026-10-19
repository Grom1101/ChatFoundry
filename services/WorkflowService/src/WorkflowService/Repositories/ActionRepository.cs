using Microsoft.EntityFrameworkCore;
using Shared.Domain.Enums;
using WorkflowService.Data;
using WorkflowService.Entities;
using WorkflowService.Interfaces;

namespace WorkflowService.Repositories;

public class ActionRepository(WorkflowDbContext db) : IActionRepository
{
    public async Task<ActionEntity?> GetAsync(
        Guid actionId,
        CancellationToken ct)
    {
        return await db.Actions
            .Include(c => c.Session)
            .FirstOrDefaultAsync(x => x.Id == actionId, ct);
    }

    public async Task<List<ActionEntity>> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await db.Actions
            .Where(a => a.SessionId == sessionId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task AddAsync(
        ActionEntity action,
        CancellationToken ct = default)
    {
        await db.Actions.AddAsync(action, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(
        ActionEntity action,
        CancellationToken ct = default)
    {
        db.Actions.Update(action);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ActionEntity?> GetPendingActionAsync(Guid sessionId, Guid nodeId, WorkflowService.Enums.WorkflowNodeType type, CancellationToken ct)
    {
        return await db.Actions
            .Include(c => c.Session)
            .FirstOrDefaultAsync(x => x.SessionId == sessionId
                && x.NodeId == nodeId
                && x.WorkflowNodeType == type
                && x.Status == ActionStatus.Pending, ct);
    }

    public async Task<ActionEntity?> GetProcessingWebhookActionAsync(Guid botId, string clientId, DefaultChannel channel, CancellationToken ct)
    {
        return await db.Actions
            .Include(a => a.Session)
                .ThenInclude(s => s.Workflow)
                    .ThenInclude(w => w.Bot)
            .FirstOrDefaultAsync(a => a.WorkflowNodeType == WorkflowService.Enums.WorkflowNodeType.WebhookWait
                && a.Status == ActionStatus.Processing
                && a.Session.Status == SessionStatus.Active
                && a.Session.ClientId == clientId
                && a.Session.Channel == channel
                && a.Session.Workflow.BotId == botId, ct);
    }

    public async Task CancelPendingOrProcessingBySessionIdAsync(Guid sessionId, CancellationToken ct)
    {
        var actions = await db.Actions
            .Where(a => a.SessionId == sessionId && (a.Status == ActionStatus.Pending || a.Status == ActionStatus.Processing))
            .ToListAsync(ct);

        foreach (var action in actions)
        {
            action.MarkCancelled();
        }

        if (actions.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}