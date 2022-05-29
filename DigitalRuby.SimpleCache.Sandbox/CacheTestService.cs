namespace DigitalRuby.SimpleCache.Sandbox;

/// <summary>
/// Test caching
/// </summary>
public sealed class CacheTestService : BackgroundService
{
    private readonly ILayeredCache cache;
    private readonly ILogger logger;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="cache">Cache</param>
    /// <param name="logger">Logger</param>
    public CacheTestService(ILayeredCache cache, ILogger<CacheTestService> logger)
    {
        this.cache = cache;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string key = "test";
        const string value = "TEST RESULT";
        TimeSpan duration = TimeSpan.FromMinutes(5.0);

        var result = await cache.GetOrCreateAsync<string>(key, context =>
        {
            context.CacheParameters = duration;
            return Task.FromResult<string>(value);
        }, stoppingToken);
        logger.LogWarning("Cache get or create: {result}", result);

        result = await cache.GetAsync<string>("test2");
        logger.LogWarning("Key not exists: {result}", result);

        result = await cache.GetAsync<string>(key);
        logger.LogWarning("Key exists: {result}", result);

        await cache.DeleteAsync<string>(key);

        result = await cache.GetAsync<string>(key);
        logger.LogWarning("Key exists after delete: {result}", result);

        await cache.SetAsync<string>(key, value, duration);
        result = await cache.GetAsync<string>(key);
        logger.LogWarning("Key exists after set: {result}", result);

    }
}
