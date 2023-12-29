namespace DigitalRuby.SimpleCache;

/// <summary>
/// Extension methods to assist with setting up simple cache
/// </summary>
public static class ServicesExtensions
{
    private sealed class MemoryOptionsProvider(MemoryCacheOptions options) : IOptions<MemoryCacheOptions>
    {
        public MemoryCacheOptions Value { get; } = options;
    }

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
        SetConfigurationDefaults(configuration);
        var layerCacheOptions = AddLayerCacheOptions(services, configuration);
        AddMemoryCache(services, configuration);
        AddFileCache(services, configuration);
        AddDistributedCache(services, configuration, layerCacheOptions);
        services.AddSingleton<ISerializer>(configuration.SerializerObject);
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<ILayeredCache, LayeredCache>();
    }

    private static void SetConfigurationDefaults(SimpleCacheConfiguration configuration)
    {
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
    }

    private static LayeredCacheOptions AddLayerCacheOptions(IServiceCollection services, SimpleCacheConfiguration configuration)
    {
        // add layer cache options
        var layerCacheOptions = new LayeredCacheOptions
        {
            KeyPrefix = configuration.KeyPrefix
        };
        services.AddSingleton(layerCacheOptions);
        return layerCacheOptions;
    }

    private static void AddDistributedCache(IServiceCollection services,
        SimpleCacheConfiguration configuration,
        LayeredCacheOptions layerCacheOptions)
    {
        if (string.IsNullOrWhiteSpace(configuration.RedisConnectionString))
        {
            // add null distributed cache
            services.AddSingleton<IDistributedCache>(new NullDistributedCache());
        }
        else
        {
            // add redis cache options
            var redisOptions = new DistributedRedisCacheOptions
            {
                KeyPrefix = layerCacheOptions.KeyPrefix
            };
            services.AddSingleton(redisOptions);

            // a little hacky because of poor api design around AddStackExchangeRedisCache not exposing IServiceProvider
            Resolver resolver = new();
            services.AddSingleton(resolver);
            services.AddHostedService<SimpleCacheHelperService>(); // transfers IServiceProvider to resolver

            // setup stackexchange redis
            var stackExchangeRedisOptions = ConfigurationOptions.Parse(configuration.RedisConnectionString);
            stackExchangeRedisOptions.AbortOnConnectFail = false; // can connect later if initial connection fails
            services.AddSingleton<IConnectionMultiplexer>(provider =>
            {
                try
                {
                    stackExchangeRedisOptions.AllowAdmin = true;
                    using var admin = ConnectionMultiplexer.Connect(stackExchangeRedisOptions);
                    var existing = admin.GetServer(admin.GetEndPoints().Single()).ConfigGet("notify-keyspace-events");
                    if (existing is null ||
                        existing.Length == 0 ||
                        !existing.Any(kv =>
                        {
                            // this seems to be a random order
                            var v = kv.Value ?? string.Empty;
                            return v.Contains('K', StringComparison.OrdinalIgnoreCase) &&
                            v.Contains('E', StringComparison.OrdinalIgnoreCase) &&
                            v.Contains('A', StringComparison.OrdinalIgnoreCase);
                        }))
                    {
                        admin.GetServer(admin.GetEndPoints().Single()).ConfigSet("notify-keyspace-events", "KEA");
                    }
                }
                catch
                {
                    var logger = provider.GetRequiredService<ILogger<DistributedRedisCache>>();
                    const string keySpaceCommand = "CONFIG SET notify-keyspace-events KEA";
                    logger.LogError($"Connection multiplexer has failed to enable key space events, you must manually run this command on your redis servers: '{keySpaceCommand}'");
                }
                stackExchangeRedisOptions.AllowAdmin = false;
                return ConnectionMultiplexer.Connect(stackExchangeRedisOptions);
            });
            services.AddStackExchangeRedisCache(cfg =>
            {
                cfg.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(resolver.Provider!.GetRequiredService<IConnectionMultiplexer>());
            });

            // add redis cache
            services.AddSingleton<DistributedRedisCache>();
            services.AddHostedService(provider => provider.GetRequiredService<DistributedRedisCache>());
            services.AddSingleton<IDistributedCache>(provider => provider.GetRequiredService<DistributedRedisCache>());
            services.AddSingleton<IDistributedLockFactory>(provider => provider.GetRequiredService<DistributedRedisCache>());
        }
    }

    private static void AddMemoryCache(IServiceCollection services, SimpleCacheConfiguration configuration)
    {
        // add memory cache
        // another deficiency in the .NET api here, we need the provider to pull out the correct system clock
        // in case it has been replaced
        // the add memory cache extension method is not sufficient
        services.AddSingleton<MemoryCache>(provider => new MemoryCache(new MemoryOptionsProvider(new MemoryCacheOptions
        {
            // set size limit in bytes
            SizeLimit = configuration.MaxMemorySize * 1024 * 1024,
            ExpirationScanFrequency = TimeSpan.FromSeconds(10.0),
            CompactionPercentage = 0.5,
            Clock = provider.GetRequiredService<Microsoft.Extensions.Internal.ISystemClock>()
        })));
        services.AddSingleton<IMemoryCache>(provider => provider.GetRequiredService<MemoryCache>());
    }

    private static void AddFileCache(IServiceCollection services, SimpleCacheConfiguration configuration)
    {
        // add file cache
        bool useRealFileCache = !string.IsNullOrWhiteSpace(configuration.FileCacheDirectory);
        if (useRealFileCache)
        {
            // add file cache options
            var fileOptions = new FileCacheOptions
            {
                CacheDirectory = configuration.FileCacheDirectory,
                FreeSpaceThreshold = configuration.FileCacheFreeSpaceThreshold
            };
            services.AddSingleton(fileOptions);
            services.AddSingleton<FileCache>();
            services.AddHostedService(provider => provider.GetRequiredService<FileCache>());
            services.AddSingleton<IFileCache>(provider => provider.GetRequiredService<FileCache>());
        }
        else
        {
            services.AddSingleton<IFileCache>(new NullFileCache());
        }
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
