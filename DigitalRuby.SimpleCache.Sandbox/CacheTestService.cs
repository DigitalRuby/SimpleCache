namespace DigitalRuby.SimpleCache.Sandbox;

/// <summary>
/// Test caching
/// </summary>
/// <remarks>
/// Constructor
/// </remarks>
/// <param name="cache">Cache</param>
/// <param name="logger">Logger</param>
public sealed class CacheTestService(ILayeredCache cache, ILogger<CacheTestService> logger) : BackgroundService
{
    private readonly ILayeredCache cache = cache;
    private readonly ILogger logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string key = "test";
        const string value = "TEST RESULT";
        TimeSpan duration = TimeSpan.FromMinutes(5.0);

        var result = await cache.GetOrCreateAsync<string>(key, context =>
        {
            context.CacheParameters = duration;
            return Task.FromResult<string?>(value);
        }, null, stoppingToken);
        logger.LogWarning("Cache get or create: {result}", result);

        result = await cache.GetAsync<string>("test2", stoppingToken);
        logger.LogWarning("Key not exists: {result}", result);

        result = await cache.GetAsync<string>(key, stoppingToken);
        logger.LogWarning("Key exists: {result}", result);

        await cache.DeleteAsync<string>(key, stoppingToken);

        result = await cache.GetAsync<string>(key, stoppingToken);
        logger.LogWarning("Key exists after delete: {result}", result);

        await cache.SetAsync<string>(key, value, duration, stoppingToken);
        result = await cache.GetAsync<string>(key, stoppingToken);
        logger.LogWarning("Key exists after set: {result}", result);

    }
}
