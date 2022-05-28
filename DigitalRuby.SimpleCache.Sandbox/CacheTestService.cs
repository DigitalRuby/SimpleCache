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

        var result = await cache.GetOrCreateAsync<string>(key, duration, token =>
        {
            logger.LogWarning("Cache miss for {key}", key);
            return Task.FromResult<string>(value);
        }, stoppingToken);
        Console.WriteLine("Cache get or create result: {0}", result);

        result = await cache.GetAsync<string>("test2");
        Console.WriteLine("Key not exists: {0}", result);

        result = await cache.GetAsync<string>(key);
        Console.WriteLine("Key exists: {0}", result);

        await cache.DeleteAsync<string>(key);

        result = await cache.GetAsync<string>(key);
        Console.WriteLine("Key exists after delete: {0}", result);

        await cache.SetAsync<string>(key, value, duration);
        result = await cache.GetAsync<string>(key);
        Console.WriteLine("Key exists after set: {0}", result);

    }
}
