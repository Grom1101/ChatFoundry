using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using WorkflowService.Data;
using WorkflowService.Interfaces;
using WorkflowService.Models.Workflow;
using WorkflowService.Utils;

namespace WorkflowService.Services;

public sealed class WorkflowGraphCache : IWorkflowGraphCache, IDisposable
{
    private const string RedisChannelName = "workflow:invalidated";
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkflowGraphParser _parser;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<WorkflowGraphCache> _logger;
    private readonly ISubscriber _subscriber;

    private readonly ConcurrentDictionary<Guid, Task<WorkflowGraph>> _inFlightLoads = new();

    public WorkflowGraphCache(
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        WorkflowGraphParser parser,
        IConnectionMultiplexer redis,
        ILogger<WorkflowGraphCache> logger)
    {
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _parser = parser;
        _redis = redis;
        _logger = logger;

        _subscriber = _redis.GetSubscriber();
        try
        {
            _subscriber.Subscribe(RedisChannel.Literal(RedisChannelName), (_, message) =>
            {
                if (Guid.TryParse((string?)message, out var invalidatedId))
                {
                    InvalidateLocal(invalidatedId);
                    _logger.LogInformation("Workflow {WorkflowId} evicted from local memory cache via Redis Pub/Sub", invalidatedId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Redis Pub/Sub channel {Channel}", RedisChannelName);
        }
    }

    public async Task<WorkflowGraph> GetGraphAsync(Guid workflowId, CancellationToken ct = default)
    {
        if (workflowId == Guid.Empty)
            return WorkflowGraph.Empty;

        var cacheKey = GetCacheKey(workflowId);

        if (_memoryCache.TryGetValue(cacheKey, out WorkflowGraph? cachedGraph) && cachedGraph != null)
        {
            return cachedGraph;
        }

        var loadTask = _inFlightLoads.GetOrAdd(
            workflowId,
            id => LoadFromDbAndCacheAsync(id, cacheKey));

        try
        {
            return await loadTask;
        }
        finally
        {
            _inFlightLoads.TryRemove(workflowId, out _);
        }
    }

    public WorkflowGraph? TryGetCachedGraph(Guid workflowId)
    {
        if (workflowId == Guid.Empty)
            return WorkflowGraph.Empty;

        return _memoryCache.TryGetValue(GetCacheKey(workflowId), out WorkflowGraph? cachedGraph) ? cachedGraph : null;
    }


    private async Task<WorkflowGraph> LoadFromDbAndCacheAsync(Guid workflowId, string cacheKey)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var data = await db.Workflows
            .AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Select(w => new
            {
                w.Id,
                w.BotId,
                BotName = w.Bot != null ? w.Bot.Name : "",
                CompanyId = w.Bot != null ? (Guid?)w.Bot.CompanyId : null,
                w.NodesDefinition,
                w.EdgesDefinition
            })
            .FirstOrDefaultAsync();

        if (data == null)
            return WorkflowGraph.Empty;

        var graph = _parser.Parse(
            data.NodesDefinition,
            data.EdgesDefinition,
            workflowId: data.Id,
            botId: data.BotId,
            botName: data.BotName,
            companyId: data.CompanyId ?? Guid.Empty);

        _memoryCache.Set(cacheKey, graph, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(2),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1),
            Size = 1
        });

        return graph;
    }

    public async Task InvalidateAsync(Guid workflowId, CancellationToken ct = default)
    {
        InvalidateLocal(workflowId);

        try
        {
            await _subscriber.PublishAsync(
                RedisChannel.Literal(RedisChannelName),
                workflowId.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish invalidation signal to Redis for workflow {WorkflowId}", workflowId);
        }
    }

    private void InvalidateLocal(Guid workflowId)
    {
        _memoryCache.Remove(GetCacheKey(workflowId));
        _inFlightLoads.TryRemove(workflowId, out _);
    }

    private static string GetCacheKey(Guid id) => $"wf_graph:{id}";

    public void Dispose()
    {
        try
        {
            _subscriber.Unsubscribe(RedisChannel.Literal(RedisChannelName));
        }
        catch
        {
            // Suppress errors during shutdown
        }
    }
}
