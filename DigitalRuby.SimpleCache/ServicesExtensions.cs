namespace DigitalRuby.SimpleCache;

/// <summary>
/// Extension methods to assist with setting up simple cache
/// </summary>
public static class ServicesExtensions
{
    private sealed class Resolver
    {
        public IServiceProvider? Provider { get; set; }
    }

    /// <summary>
    /// Sole purpose is to pass IServiceProvider to the Resolver class
    /// </summary>
    private sealed class SimpleCacheHelperService : BackgroundService
    {
        public SimpleCacheHelperService(IServiceProvider provider, Resolver resolver)
        {
            resolver.Provider = provider;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Add simple cache to dependency injection.<br/>
    /// You can put ILayeredCache in your constructors to access cache functionality.<br/>
    /// This will setup your IConnectionMultiplexer for you.<br/>
    /// </summary>
    /// <param name="services">Services</param>
    /// <param name="configuration">Configuration</param>
    public static void AddSimpleCache(this IServiceCollection services, IConfiguration configuration)
    {
        SimpleCacheConfiguration configurationObj = new();
        var configPath = $"{nameof(DigitalRuby)}.{nameof(SimpleCache)}";
        if (configuration.GetSection(configPath) is null)
        {
            throw new InvalidOperationException("You must add your config to config path " + configPath);
        }
        configuration.Bind(configPath, configurationObj);
        services.AddSimpleCache(configurationObj);
    }

    /// <summary>
    /// Add simple cache to dependency injection.<br/>
    /// You can put ILayeredCache in your constructors to access cache functionality.<br/>
    /// This will setup your IConnectionMultiplexer and IMemoryCache for you.<br/>
    /// </summary>
    /// <param name="services">Services</param>
    /// <param name="configuration">Configuration</param>
    public static void AddSimpleCache(this IServiceCollection services, SimpleCacheConfiguration configuration)
    {
        // assign defaults if needed
        if (!string.IsNullOrWhiteSpace(configuration.SerializerType))
        {
            var serializerType = Type.GetType(configuration.SerializerType) ??
                throw new ArgumentException("Invalid serializer type " + configuration.SerializerType);
            var serializer = Activator.CreateInstance(serializerType);
            if (serializer is ISerializer serializerInterface)
            {
                configuration.SerializerObject = serializerInterface;
            }
            else
            {
                throw new ArgumentException("Failed to detect serializer interface from type " + configuration.SerializerType);
            }
        }
        configuration.SerializerObject ??= new JsonLZ4Serializer();

        var fileOptions = new FileCacheOptions
        {
            CacheDirectory = configuration.FileCacheDirectory,
            FreeSpaceThreshold = configuration.FileCacheFreeSpaceThreshold
        };
        services.AddSingleton(fileOptions);

        var layerCacheOptions = new LayeredCacheOptions
        {
            KeyPrefix = configuration.KeyPrefix,
            MaxMemoryCacheSize = configuration.MaxMemorySize
        };

        // a little hacky because of poor api design around AddStackExchangeRedisCache not exposing IServiceProvider
        Resolver resolver = new();
        var redisOptions = ConfigurationOptions.Parse(configuration.RedisConnectionString);
        redisOptions.AbortOnConnectFail = false; // can connect later if initial connection fails
        services.AddSingleton(resolver);
        services.AddHostedService<SimpleCacheHelperService>();
        services.AddSingleton(layerCacheOptions);
        services.AddSingleton<IConnectionMultiplexer>(provider => ConnectionMultiplexer.Connect(redisOptions));
        services.AddStackExchangeRedisCache(cfg =>
        {
            cfg.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(resolver.Provider!.GetRequiredService<IConnectionMultiplexer>());
        });
        services.AddMemoryCache();
        services.AddSingleton<ISerializer>(configuration.SerializerObject);
        services.AddSingleton<IDateTimeProvider>(new DateTimeProvider());
        services.AddSingleton<IFileCache>(provider => !string.IsNullOrWhiteSpace(configuration.FileCacheDirectory)
            ? new FileCache(fileOptions,
            provider.GetRequiredService<ISerializer>(),
            new DiskSpace(),
            provider.GetRequiredService<IDateTimeProvider>(),
            provider.GetRequiredService<ILogger<FileCache>>())
            : new NullFileCache());
        services.AddSingleton<IDistributedCache, DistributedRedisCache>();
        services.AddSingleton<ILayeredCache, LayeredCache>();
    }
}

/// <summary>
/// Simple cache configuration
/// </summary>
public sealed class SimpleCacheConfiguration
{
    /// <summary>
    /// Connection string for redis
    /// </summary>
    public string RedisConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Set the file cache directory. Default is %temp% (temp folder). Set to empty to not use file cache.
    /// </summary>
    public string FileCacheDirectory { get; set; } = "%temp%";

    /// <summary>
    /// Threshold percent (0-100) in which to clean up file cache files to reclaim disk space
    /// </summary>
    public int FileCacheFreeSpaceThreshold { get; set; } = 15;

    /// <summary>
    /// Specify the full type name of a serializer to use, or empty for default (json-lz4)
    /// </summary>
    public string SerializerType { get; set; } = string.Empty;

    /// <summary>
    /// Serializer to use for converting objects to bytes
    /// </summary>
    public ISerializer SerializerObject { get; set; } = new JsonLZ4Serializer();

    /// <summary>
    /// Key prefix. Defaults to entry assembly name, if any.
    /// </summary>
    public string KeyPrefix { get; set; } = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;

    /// <summary>
    /// Max memory size for memory cache (in megabytes)
    /// </summary>
    public long MaxMemorySize { get; set; } = 1024;
}
