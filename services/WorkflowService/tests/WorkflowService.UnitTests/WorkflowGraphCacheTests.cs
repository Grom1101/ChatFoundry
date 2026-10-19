using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WorkflowService.Data;
using WorkflowService.Entities;
using WorkflowService.Models.Workflow;
using WorkflowService.Services;
using WorkflowService.Utils;
using Xunit;

namespace WorkflowService.UnitTests;

public class WorkflowGraphCacheTests
{
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkflowGraphParser _parser;
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private readonly ILogger<WorkflowGraphCache> _logger;
    private readonly WorkflowDbContext _dbContext;

    public WorkflowGraphCacheTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _parser = new WorkflowGraphParser();
        _redis = Substitute.For<IConnectionMultiplexer>();
        _subscriber = Substitute.For<ISubscriber>();
        _redis.GetSubscriber(Arg.Any<object>()).Returns(_subscriber);
        _logger = Substitute.For<ILogger<WorkflowGraphCache>>();

        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new WorkflowDbContext(options);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(WorkflowDbContext)).Returns(_dbContext);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);
    }

    [Fact]
    public async Task GetGraphAsync_FirstCallLoadsFromDb_SubsequentCallHitsMemoryCache()
    {
        // Arrange
        var workflowId = Guid.NewGuid();
        var bot = new Bot
        {
            Id = Guid.NewGuid(),
            Name = "TestBot",
            CompanyId = Guid.NewGuid()
        };
        var workflow = new BotWorkflow
        {
            Id = workflowId,
            BotId = bot.Id,
            Bot = bot,
            NodesDefinition = "[]",
            EdgesDefinition = "[]"
        };
        await _dbContext.Bots.AddAsync(bot);
        await _dbContext.Workflows.AddAsync(workflow);
        await _dbContext.SaveChangesAsync();

        var cache = new WorkflowGraphCache(_memoryCache, _scopeFactory, _parser, _redis, _logger);

        // Act - Call 1 (miss)
        var graph1 = await cache.GetGraphAsync(workflowId);

        // Act - Call 2 (hit)
        var graph2 = await cache.GetGraphAsync(workflowId);

        // Assert
        graph1.Should().NotBeNull();
        graph1.WorkflowId.Should().Be(workflowId);
        graph1.BotName.Should().Be("TestBot");
        graph1.CompanyId.Should().Be(bot.CompanyId!.Value);

        graph2.Should().BeSameAs(graph1);

        _scopeFactory.Received(1).CreateScope();
    }

    [Fact]
    public async Task SingleFlight_ConcurrentRequests_OnlyOneDbQueryExecuted()
    {
        // Arrange
        var workflowId = Guid.NewGuid();
        var bot = new Bot
        {
            Id = Guid.NewGuid(),
            Name = "Bot",
            CompanyId = Guid.NewGuid()
        };
        var workflow = new BotWorkflow
        {
            Id = workflowId,
            BotId = bot.Id,
            Bot = bot,
            NodesDefinition = "[]",
            EdgesDefinition = "[]"
        };
        await _dbContext.Bots.AddAsync(bot);
        await _dbContext.Workflows.AddAsync(workflow);
        await _dbContext.SaveChangesAsync();

        var cache = new WorkflowGraphCache(_memoryCache, _scopeFactory, _parser, _redis, _logger);

        // Act - 20 concurrent tasks
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.GetGraphAsync(workflowId))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(20);
        foreach (var graph in results)
        {
            graph.Should().BeSameAs(results[0]);
        }

        // Only 1 DB query despite 20 concurrent caller threads!
        _scopeFactory.Received(1).CreateScope();
    }

    [Fact]
    public async Task InvalidateAsync_EvictsMemoryCache_AndPublishesToRedis()
    {
        // Arrange
        var workflowId = Guid.NewGuid();
        var bot = new Bot
        {
            Id = Guid.NewGuid(),
            Name = "Bot",
            CompanyId = Guid.NewGuid()
        };
        var workflow = new BotWorkflow
        {
            Id = workflowId,
            BotId = bot.Id,
            Bot = bot,
            NodesDefinition = "[]",
            EdgesDefinition = "[]"
        };
        await _dbContext.Bots.AddAsync(bot);
        await _dbContext.Workflows.AddAsync(workflow);
        await _dbContext.SaveChangesAsync();

        var cache = new WorkflowGraphCache(_memoryCache, _scopeFactory, _parser, _redis, _logger);

        // Load into cache
        await cache.GetGraphAsync(workflowId);
        _scopeFactory.Received(1).CreateScope();

        // Act - Invalidate
        await cache.InvalidateAsync(workflowId);

        // Assert - verify Redis publish
        await _subscriber.Received(1).PublishAsync(
            Arg.Is<RedisChannel>(c => c.ToString() == "workflow:invalidated"),
            Arg.Is<RedisValue>(v => v.ToString() == workflowId.ToString()),
            Arg.Any<CommandFlags>());

        // Next call should reload from DB
        await cache.GetGraphAsync(workflowId);
        _scopeFactory.Received(2).CreateScope();
    }
}
