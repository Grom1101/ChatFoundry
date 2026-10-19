using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Shared.Infrastructure.Caching;
using Shared.Infrastructure.Options;
using TelegramService.Interfaces;

namespace TelegramService.Services;

public sealed class CachingBotTokenProvider(
    GrpcBotTokenProvider inner,
    IDistributedCache cache,
    IOptions<FoundryRedisCacheOptions> cacheOptions,
    ILogger<CachingBotTokenProvider> logger) : IBotTokenProvider
{
    private const string KeyPrefix = "bot:token:telegram:";

    private static string CacheKey(Guid channelId) => $"{KeyPrefix}{channelId}";

    public Task<string> GetByChannelIdAsync(Guid channelId, CancellationToken ct)
    {
        var key = CacheKey(channelId);
        var ttl = TimeSpan.FromSeconds(cacheOptions.Value.DefaultTtlSeconds);

        return cache.GetOrSetAsync(
            key,
            () => inner.GetByChannelIdAsync(channelId, ct),
            ttl,
            logger,
            ct)!;
    }
}
