using Microsoft.EntityFrameworkCore;
using Shared.Domain.Enums;
using Shared.Infrastructure.GraphQl;
using WorkflowService.Data;
using WorkflowService.Entities;
using WorkflowService.Interfaces;

namespace WorkflowService.Repositories;

public class SessionRepository(WorkflowDbContext db, IGraphQlCacheService cacheService) : ISessionRepository
{
    public Task<Session?> FindActiveAsync(Guid channelId, string clientId, Guid botId, CancellationToken ct)
    {
        return db.Sessions
            .FirstOrDefaultAsync(
                c => c.ChannelId == channelId
                     && c.ClientId == clientId
                     && c.Workflow.BotId == botId
                     && c.Status == SessionStatus.Active,
                ct);
    }

    public Task<Session?> FindWaitingForWebhookAsync(Guid botId, string clientId, DefaultChannel channel, CancellationToken ct)
    {
        return db.Sessions
            .Include(c => c.Workflow)
                .ThenInclude(w => w.Bot)
            .FirstOrDefaultAsync(
                c => c.ClientId == clientId
                     && c.Channel == channel
                     && c.Workflow.BotId == botId
                     && c.Status == SessionStatus.Active
                     && c.Actions.Any(a => a.WorkflowNodeType == WorkflowService.Enums.WorkflowNodeType.WebhookWait && a.Status == ActionStatus.Processing),
                ct);
    }

    public Task<Session?> FindActiveChildAsync(Guid parentSessionId, CancellationToken ct)
    {
        return db.Sessions
            .FirstOrDefaultAsync(
                c => c.ParentSessionId == parentSessionId
                     && c.Status == SessionStatus.Active,
                ct);
    }

    public async Task<Session?> GetAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await db.Sessions
            .Include(c => c.Workflow)
            .ThenInclude(w => w.Bot)
            .FirstOrDefaultAsync(c => c.Id == sessionId, ct);
    }

    public async Task<IReadOnlyList<Session>> GetByParentSessionIdAsync(Guid parentSessionId, CancellationToken ct = default)
    {
        return await db.Sessions
            .Include(s => s.Workflow)
            .Where(s => s.ParentSessionId == parentSessionId)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Session session, CancellationToken ct = default)
    {
        try
        {
            await db.Sessions.AddAsync(session, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(session).State = EntityState.Detached;
            throw;
        }

        var companyId = await db.Bots
            .Where(b => b.Workflows.Any(w => w.Id == session.WorkflowId))
            .Select(b => b.CompanyId)
            .FirstOrDefaultAsync(ct);

        if (companyId.HasValue)
        {
            await cacheService.EvictByTagsAsync(new[] { $"company:{companyId.Value}:sessions" }, ct);
        }
    }

    public async Task SaveAsync(Session session, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);

        var companyId = await db.Bots
            .Where(b => b.Workflows.Any(w => w.Id == session.WorkflowId))
            .Select(b => b.CompanyId)
            .FirstOrDefaultAsync(ct);

        if (companyId.HasValue)
        {
            await cacheService.EvictByTagsAsync(new[] { $"company:{companyId.Value}:sessions", $"session:{session.Id}" }, ct);
        }
    }
}