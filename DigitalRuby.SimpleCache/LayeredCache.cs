namespace DigitalRuby.SimpleCache;

/// <summary>
/// Layered cache interface. A layered cache aggregates multiple caches, such as memory, file and distributed cache (redis, etc.).<br/>
/// Internally, keys are prefixed with the entry assembyly name and the type full name. You can change the entry assembly by specifying a KeyPrefix in the configuration.<br/>
/// </summary>
public interface ILayeredCache : IDisposable
{
	/// <summary>
	/// Get or create an item from the cache.
	/// </summary>
	/// <typeparam name="T">Type of item</typeparam>
	/// <param name="key">Cache key</param>
	/// <param name="factory">Factory method to create the item if no item is in the cache for the key. This factory is guaranteed to execute only one per key.<br/>
	/// Inside your factory, you should set the CacheParameters on the GetOrCreateAsyncContext to a duration and size tuple: (TimeSpan duration, int size)</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task of return of type T</returns>
	Task<T> GetOrCreateAsync<T>(string key, Func<GetOrCreateAsyncContext, Task<T>> factory, CancellationToken cancelToken = default);

	/// <summary>
	/// Attempts to retrieve value of T by key.
	/// </summary>
	/// <typeparam name="T">Type of object to get</typeparam>
	/// <param name="key">Cache key</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Result of type T or null if nothing found for the key</returns>
	Task<T?> GetAsync<T>(string key, CancellationToken cancelToken = default);

	/// <summary>
	/// Sets value T by key.
	/// </summary>
	/// <typeparam name="T">Type of object</typeparam>
	/// <param name="key">Cache key to set</param>
	/// <param name="value">Value to set</param>
	/// <param name="cacheParam">Cache parameters</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	Task SetAsync<T>(string key, T value, CacheParameters cacheParam, CancellationToken cancelToken = default);

	/// <summary>
	/// Attempts to delete an entry of T type by key. If there is no key found, nothing happens.
	/// </summary>
	/// <typeparam name="T">The type of object to delete</typeparam>
	/// <param name="key">The key to delete</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	Task DeleteAsync<T>(string key, CancellationToken cancelToken = default);
}

/// <summary>
/// Null layered cache, always executes factory
/// </summary>
public sealed class NullLayeredCache : ILayeredCache
{
    /// <inheritdoc />
    public void Dispose() { }

	/// <inheritdoc />
	public Task DeleteAsync<T>(string key, CancellationToken cancelToken = default) => Task.CompletedTask;

	/// <inheritdoc />
	public Task<T?> GetAsync<T>(string key, CancellationToken cancelToken = default) => Task.FromResult<T?>(default);

	/// <inheritdoc />
	public Task<T> GetOrCreateAsync<T>(string key, Func<GetOrCreateAsyncContext, Task<T>> factory, CancellationToken cancelToken = default) =>
		factory(new GetOrCreateAsyncContext(cancelToken));

	/// <inheritdoc />
	public Task SetAsync<T>(string key, T obj, CacheParameters cacheParam, CancellationToken cancelToken = default) => Task.CompletedTask;
}

/// <summary>
/// Layered cache options
/// </summary>
public sealed class LayeredCacheOptions
{
	/// <summary>
	/// Key prefix, all keys will be automatically prefixed with this value. You could use your service name for example.
	/// </summary>
	public string KeyPrefix { get; set; } = string.Empty;
}

/// <inheritdoc />
public sealed class LayeredCache : AsyncPolicy, ILayeredCache, IKeyStrategy, IDisposable
{
	private static readonly TimeSpan defaultCacheTime = TimeSpan.FromMinutes(5.0);

	private readonly string keyPrefix;
	private readonly ISerializer serializer;
	private readonly IMemoryCache memoryCache;
	private readonly IFileCache fileCache;
	private readonly IDistributedCache distributedCache;
	private readonly ILogger logger;
	private readonly AsyncPolicyWrap cachePolicy;
	private readonly AsyncPolicy distributedCacheCircuitBreakPolicy;

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="options">Options.</param>
	/// <param name="serializer">Serializer. This must be the same serializer that was used to create the file cache.</param>
	/// <param name="memoryCache">Memory cache</param>
	/// <param name="fileCache">File cache. Can pass NullFileCache to skip file caching layer. Recommend to use SSD only for this.</param>
	/// <param name="distributedCache">Distributed cache</param>
	/// <param name="logger">Logger</param>
	public LayeredCache(LayeredCacheOptions options,
		ISerializer serializer,
		IMemoryCache memoryCache,
		IFileCache fileCache,
		IDistributedCache distributedCache,
		ILogger<LayeredCache> logger)
	{
		this.keyPrefix = (options.KeyPrefix ?? string.Empty) + ":";
		this.serializer = serializer;
		this.memoryCache = memoryCache;
		this.fileCache = fileCache;
		this.distributedCache = distributedCache;
		this.logger = logger;

		// create collapser, this will ensure keys do not cache storm
		var collapser = AsyncRequestCollapserPolicy.Create(this);

		// wrap this class (the cache policy) behind the collapser policy
		this.cachePolicy = PolicyWrap.WrapAsync(collapser, this);

		// circuit break if distributed cache goes down, re-enable circuit attempts after 5 seconds
		distributedCacheCircuitBreakPolicy = Policy.Handle<Exception>().CircuitBreakerAsync(5, TimeSpan.FromSeconds(5.0));

		this.distributedCache.KeyChanged += DistributedCacheKeyChanged;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		distributedCache.KeyChanged -= DistributedCacheKeyChanged;
	}

	/// <inheritdoc />
	public async Task<T?> GetAsync<T>(string key, CancellationToken cancelToken = default)
	{
		ValidateType<T>();

		key = FormatKey<T>(key);

		// L1 lookup (RAM)
		var memoryResult = memoryCache.Get<T>(key);
		if (memoryResult is not null)
		{
			logger.LogDebug("Memory cache hit for {key}", key);
			return memoryResult;
		}
		logger.LogDebug("Memory cache miss for {key}", key);

		// L2 lookup (file)
		var fileResult = await fileCache.GetAsync<T>(key, cancelToken);
		if (fileResult is not null)
		{
			return fileResult.Item;
		}

		// L3 lookup (redis)
		DistributedCacheItem distributedCacheItem = default;
		try
		{
			distributedCacheItem = await distributedCacheCircuitBreakPolicy.ExecuteAsync(() => distributedCache.GetAsync(key, cancelToken));
		}
		catch (Exception ex)
		{
			var method = nameof(GetAsync);
			var type = typeof(T).FullName ?? string.Empty;
			logger.LogError(ex, "Distributed cache error on {method}, {key}, {type}", method, key, type);
		}

		// not found from distributed cache, give up
		if (!distributedCacheItem.HasValue)
		{
			return default;
		}

		// deserialize and return value from distributed cache
		var deserializedResult = DeserializeObject<T>(distributedCacheItem.Bytes);
		return deserializedResult;
	}

	/// <inheritdoc />
	public async Task SetAsync<T>(string key, T obj, CacheParameters cacheParam, CancellationToken cancelToken = default)
	{
		ValidateType<T>();
		if (obj is null)
		{
			return;
		}

		key = FormatKey<T>(key);
		var distributedCacheBytes = SerializeObject(obj);

		// L1 cache (RAM)
		memoryCache.Set(key, obj, new MemoryCacheEntryOptions
		{
			AbsoluteExpirationRelativeToNow = cacheParam.Duration,
			Size = cacheParam.Size
		});
		logger.LogDebug("Memory cache set {key}", key);

		// L2 cache (file)
		await fileCache.SetAsync(key, distributedCacheBytes, cacheParam, cancelToken);

		// L3 cache (redis)
		try
		{
			await distributedCacheCircuitBreakPolicy.ExecuteAsync(() => distributedCache.SetAsync(key, new DistributedCacheItem
			{
				Bytes = distributedCacheBytes,
				Expiry = cacheParam.Duration
			}));
		}
		catch (Exception ex)
		{
			// don't fail the call, we can stomach redis being down
			var method = nameof(SetAsync);
			var type = typeof(T).FullName ?? string.Empty;
			logger.LogError(ex, "Distributed cache error on {method}, {key}, {type}", method, key, type);
		}
	}

	/// <inheritdoc />
	public async Task DeleteAsync<T>(string key, CancellationToken cancelToken = default)
	{
		ValidateType<T>();

		key = FormatKey<T>(key);

		// L1 cache (RAM)
		memoryCache.Remove(key);
		logger.LogDebug("Memory cache deleted {key}", key);

		// L2 cache (file)
		await fileCache.RemoveAsync(key, cancelToken);

		// L3 cache (redis)
		// note- unlike SetAsync, we don't catch the exception here, a deletion that fails in distributed cache is bad and we need it to propagate all the way out
		await distributedCacheCircuitBreakPolicy.ExecuteAsync(() => distributedCache.DeleteAsync(key, cancelToken));
	}

	/// <inheritdoc />
	public Task<T> GetOrCreateAsync<T>(string key, Func<GetOrCreateAsyncContext, Task<T>> factory, CancellationToken cancelToken = default)
	{
		ValidateType<T>();

		key = FormatKey<T>(key);

		var ctx = new GetOrCreateAsyncContext(cancelToken); 
		var pollyContext = new Context(key, new Dictionary<string, object> { { "Context", ctx } });
		return cachePolicy.ExecuteAsync((pollyContext, cancelToken) => factory(ctx), pollyContext, cancelToken);
	}

	/// <summary>
	/// The polly policy implementation to GetOrCreateAsync a cache item
	/// </summary>
	/// <typeparam name="T">Type of object</typeparam>
	/// <param name="factory">Factory method</param>
	/// <param name="context">Context</param>
	/// <param name="cancellationToken">Cancel token</param>
	/// <param name="continueOnCapturedContext">Whether to continue on captured context</param>
	/// <returns>Task of return value of T</returns>
	protected override Task<T> ImplementationAsync<T>(Func<Context, CancellationToken, Task<T>> factory,
		Context context,
		CancellationToken cancellationToken,
		bool continueOnCapturedContext)
	{
		// get the cache key
		string key = context.OperationKey;
		var getOrCreateContext = context["Context"] as GetOrCreateAsyncContext ??
			throw new ArgumentException("Context was not passed to polly correctly");
		logger.LogDebug("Layered cache get or create {key}", key);

		return memoryCache.GetOrCreateAsync<T>(key, async entry =>
		{
			logger.LogDebug("Memory cache get or create miss {key}", key);

			// check file cache (L2)
			var fileItem = await fileCache.GetAsync<T>(key, cancellationToken);
			if (fileItem is not null)
			{
				// set the size and expiration
				entry.Size = fileItem.Size * 2;
				entry.AbsoluteExpiration = fileItem.Expires;
				return fileItem.Item;
			}

			try
			{
				// attempt to grab from distributed cache (L3)
				DistributedCacheItem distributedCacheItem = await distributedCacheCircuitBreakPolicy.ExecuteAsync(() => distributedCache.GetAsync(key, cancellationToken));
				if (distributedCacheItem.HasValue)
				{
					logger.LogDebug("Get or create {key} in distributed cache", key);

					// grabbed from distributed cache, use that value and don't invoke the factory
					var result = DeserializeObject<T>(distributedCacheItem.Bytes);
					if (result is null)
					{
						throw new IOException("Failed to deserialize object of type " + typeof(T).FullName);
					}
					entry.Size = distributedCacheItem.Bytes.Length * 2;
					entry.AbsoluteExpirationRelativeToNow = distributedCacheItem.Expiry;
					return result;
				}
			}
			catch (Exception ex)
			{
				// eat error but log it, we don't want serializer or redis to fail the entire call
				var method = nameof(GetOrCreateAsync);
				var type = typeof(T).FullName ?? string.Empty;
				logger.LogError(ex, "Distributed cache read error on {method}, {key}, {type}", method, key, type);
			}

			// get the item from the factory
			var item = await factory(context, cancellationToken);
			entry.Size = getOrCreateContext.CacheParameters.Size;
			entry.AbsoluteExpirationRelativeToNow = getOrCreateContext.CacheParameters.Duration;
			var distributedCacheBytes = SerializeObject(item);

			// L2 cache (file)
			// file cache can take raw bytes and will not additional serialization on them
			await fileCache.SetAsync(key, distributedCacheBytes, getOrCreateContext.CacheParameters.Duration);

			// L3 cache (redis)
			try
			{
				await distributedCacheCircuitBreakPolicy.ExecuteAsync(() =>
				{
					return distributedCache.SetAsync(key, new DistributedCacheItem
					{
						Bytes = distributedCacheBytes,
						Expiry = getOrCreateContext.CacheParameters.Duration
					});
				});
			}
			catch (Exception ex)
			{
				// don't fail the call, we can stomach file or redis cache being down
				var method = nameof(GetOrCreateAsync);
				var type = typeof(T).FullName ?? string.Empty;
				logger.LogError(ex, "Distributed cache write error on {method}, {key}, {type}", method, key, type);
			}

			return item;
		});
	}

	private void DistributedCacheKeyChanged(string key)
	{
		// magic key to indicate a flush all operation
		if (key.Contains("__flushall__"))
		{
			logger.LogWarning("Received a flush all command, purging memory and file cache");
			(memoryCache as MemoryCache)?.Compact(1.0);
			fileCache.ClearAsync().GetAwaiter(); // this can run in the background
		}
		else if (key.StartsWith(keyPrefix))
		{
			memoryCache.Remove(key);
			fileCache.RemoveAsync(key).GetAwaiter().GetResult();
			logger.LogDebug("Distributed cache key changed: {key}, removed from memory and file cache", key);
		}
		else
		{
			logger.LogDebug("Ignoring distributed cache key change: {key}, not prefixed with {keyPrefix}", key, keyPrefix);
		}
	}

	/// <inheritdoc />
	string IKeyStrategy.GetKey(Context context)
	{
		// get the key to collapse on
		return context.OperationKey;
	}

	private T? DeserializeObject<T>(byte[] bytes)
	{
		return (T?)serializer.Deserialize(bytes, typeof(T?));
	}

	private byte[] SerializeObject(object obj)
	{
		return serializer.Serialize(obj) ?? throw new IOException("Failed to serialize object of type " + obj.GetType().FullName); ;
	}

	private string FormatKey<T>(string key)
	{
		return $"{keyPrefix}{typeof(T).FullName}:{serializer.Description}:{key}";
	}

	private static void ValidateType<T>()
	{
		Type t = typeof(T);
		if (t.IsInterface)
		{
			throw new InvalidOperationException("Interfaces cannot be cached");
		}
		else if (t.IsPrimitive)
		{
			throw new InvalidOperationException("Primitives cannot be cached");
		}
	}
}

/// <summary>
/// Context for GetOrCreateAsync
/// </summary>
public class GetOrCreateAsyncContext
{
	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="cancelToken">Cancel token</param>
	public GetOrCreateAsyncContext(CancellationToken cancelToken)
	{
		CancelToken = cancelToken;
	}

	/// <summary>
	/// Cache parameters. You can set these to a new value for duration and size inside the factory method
	/// </summary>
	public CacheParameters CacheParameters { get; set; }

	/// <summary>
	/// Cancellation token
	/// </summary>
	public CancellationToken CancelToken { get; }
}