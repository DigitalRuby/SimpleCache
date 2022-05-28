namespace DigitalRuby.SimpleCache.Sandbox;

/// <summary>
/// Test caching
/// </summary>
public sealed class CacheTestService : BackgroundService
{
    private readonly ILayeredCache cache;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="cache">Cache</param>
    public CacheTestService(ILayeredCache cache)
    {
        this.cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string key = "test";
        var result = await cache.GetOrCreateAsync<string>(key, TimeSpan.FromMinutes(5.0), token =>
        {
            Console.WriteLine("Cache miss");
            return Task.FromResult<string>("TEST RESULT");
        }, stoppingToken);
        Console.WriteLine("Cache get or create result: {0}", result);

        result = await cache.GetAsync<string>("test2");
        Console.WriteLine("Key not exists: {0}", result);

        result = await cache.GetAsync<string>(key);
        Console.WriteLine("Key exists: {0}", result);

        await cache.DeleteAsync<string>(key);

        result = await cache.GetAsync<string>(key);
        Console.WriteLine("Key exists after delete: {0}", result);

    }
}
