namespace DigitalRuby.SimpleCache;

/// <summary>
/// Distributed cache item
/// </summary>
public readonly struct DistributedCacheItem
{
	/// <summary>
	/// The item bytes or null if no item found
	/// </summary>
	public byte[]? Bytes { get; init; }

	/// <summary>
	/// The item expiration relative to now or null if none
	/// </summary>
	public TimeSpan? Expiry { get; init; }

	/// <summary>
	/// Whether there is an item
	/// </summary>
	[MemberNotNullWhen(true, nameof(Bytes))]
	[MemberNotNullWhen(true, nameof(Expiry))]
	public bool HasValue => Bytes is not null && Expiry is not null;
}

/// <summary>
/// Distributed cache interface
/// </summary>
public interface IDistributedCache
{
	/// <summary>
	/// Attempt to get an item from the cache
	/// </summary>
	/// <param name="key">Key</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task that returns the item</returns>
	Task<DistributedCacheItem> GetAsync(string key, CancellationToken cancelToken = default);

	/// <summary>
	/// Set an item in the cache
	/// </summary>
	/// <param name="key">Key</param>
	/// <param name="item">Item</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	Task SetAsync(string key, DistributedCacheItem item, CancellationToken cancelToken = default);

	/// <summary>
	/// Delete an item from the cache
	/// </summary>
	/// <param name="key">Key</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	Task DeleteAsync(string key, CancellationToken cancelToken = default);

	/// <summary>
	/// Key change event, get notified if a key changes outside of this machine
	/// </summary>
	event Action<string>? KeyChanged;
}

/// <summary>
/// Null distributed cache that no-ops everything
/// </summary>
public sealed class NullDistributedCache : IDistributedCache, IDistributedLockFactory
{
#pragma warning disable CS0067 // never used
	/// <inheritdoc />
	public event Action<string>? KeyChanged;
#pragma warning restore

	/// <inheritdoc />
	public Task DeleteAsync(string key, CancellationToken cancelToken = default)
    {
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task<DistributedCacheItem> GetAsync(string key, CancellationToken cancelToken = default)
    {
		return Task.FromResult<DistributedCacheItem>(new DistributedCacheItem());
    }

	/// <inheritdoc />
	public Task SetAsync(string key, DistributedCacheItem item, CancellationToken cancelToken = default)
    {
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan lockTime, TimeSpan timeout = default)
    {
		return Task.FromResult<IAsyncDisposable?>(new DistributedMemoryCache.FakeDistributedLock());
    }
}

/// <summary>
/// Distributed cache but all in memory (for testing)
/// </summary>
public sealed class DistributedMemoryCache : IDistributedCache, IDistributedLockFactory
{
	private readonly Microsoft.Extensions.Internal.ISystemClock clock;

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="clock">Clock</param>
	public DistributedMemoryCache(Microsoft.Extensions.Internal.ISystemClock clock) => this.clock = clock;

	internal sealed class FakeDistributedLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
			return new();
        }
    }

    private readonly ConcurrentDictionary<string, (DateTimeOffset, byte[])> items = new();

	/// <inheritdoc />
	public event Action<string>? KeyChanged;

	/// <inheritdoc />
	public Task DeleteAsync(string key, CancellationToken cancelToken = default)
    {
		if (items.TryRemove(key, out _))
		{
			KeyChanged?.Invoke(key);
		}
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task<DistributedCacheItem> GetAsync(string key, CancellationToken cancelToken = default)
    {
		if (items.TryGetValue(key, out var item) && item.Item1 > clock.UtcNow)
		{
			return Task.FromResult(new DistributedCacheItem { Bytes = item.Item2, Expiry = item.Item1 - clock.UtcNow });
		}
		return Task.FromResult<DistributedCacheItem>(default);
    }

	/// <inheritdoc />
	public Task SetAsync(string key, DistributedCacheItem item, CancellationToken cancelToken = default)
    {
		if (item.Bytes is not null)
		{
			DateTimeOffset expire = clock.UtcNow + item.Expiry ?? throw new ArgumentException("Null expiry not allowed");
			items[key] = new(expire, item.Bytes);
		}
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan lockTime, TimeSpan timeout = default)
    {
		if (items.TryAdd(key, new()))
		{
			return Task.FromResult<IAsyncDisposable?>(new FakeDistributedLock());
		}
		return Task.FromResult<IAsyncDisposable?>(null);
	}
}

/// <summary>
/// Distributed redis cache
/// </summary>
public sealed class DistributedRedisCache : BackgroundService, IDistributedCache, IDistributedLockFactory
{
	private readonly IConnectionMultiplexer connectionMultiplexer;
	private readonly string keyPrefix;
	private readonly ILogger<DistributedRedisCache> logger;

	private ISubscriber? changeQueue;

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="options">Options</param>
	/// <param name="connectionMultiplexer">Connection multiplexer</param>
	/// <param name="logger">Logger</param>
	public DistributedRedisCache(DistributedRedisCacheOptions options,
		IConnectionMultiplexer connectionMultiplexer,
		ILogger<DistributedRedisCache> logger)
	{
		this.keyPrefix = string.IsNullOrWhiteSpace(options.KeyPrefix) ? string.Empty : options.KeyPrefix + ":";
		this.connectionMultiplexer = connectionMultiplexer;
		this.logger = logger;

		// if we get a connection multiplexer that hasn't connected properly, log an error
		if (!connectionMultiplexer.IsConnected)
		{
			logger.LogError("Connection multiplexer has failed to connect");
		}
	}

	/// <inheritdoc />
	public Task DeleteAsync(string key, CancellationToken cancelToken = default)
	{
		return PerformOperation(async () =>
		{
			await connectionMultiplexer.GetDatabase().KeyDeleteAsync(key);
			logger.LogDebug("Redis cache deleted {key}", key);
			return true;
		});
	}

	/// <inheritdoc />
	public Task<DistributedCacheItem> GetAsync(string key, CancellationToken cancelToken = default)
	{
		return PerformOperation(async () =>
		{
			var item = await connectionMultiplexer.GetDatabase().StringGetWithExpiryAsync(key);
			if (item.Value.HasValue)
			{
				logger.LogDebug("Redis cache hit {key}", key);
				return new DistributedCacheItem { Bytes = item.Value, Expiry = item.Expiry };
			}
			logger.LogDebug("Redis cache miss {key}", key);
			return default;
		});
	}

	/// <inheritdoc />
	public Task SetAsync(string key, DistributedCacheItem item, CancellationToken cancelToken = default)
	{
		if (!item.HasValue)
		{
			throw new ArgumentException("Cannot add a null item or null expiration to redis cache, key: " + key);
		}

		return PerformOperation(async () =>
		{
			await connectionMultiplexer.GetDatabase().StringSetAsync(key, item.Bytes, expiry: item.Expiry);
			logger.LogDebug("Redis cache set {key}", key);
			return true;
		});
	}

	/// <inheritdoc />
	public event Action<string>? KeyChanged;

	private async Task<T?> PerformOperation<T>(Func<Task<T>> operation)
	{
		T? returnValue = default;
		await PerformOperationInternal(async () => returnValue = await operation());
		return returnValue;
	}

	private async Task PerformOperationInternal(Func<Task> operation)
	{
		try
		{
			await operation();
		}
		catch (RedisCommandException ex)
		{
			// handle replica going down and then coming back alive
			if (ex.Message.Contains("replica", StringComparison.OrdinalIgnoreCase))
			{
				logger.LogError(ex, "Command failure on replica, re-init connection multiplexer and trying again...");
				connectionMultiplexer.Configure();
				RegisterChangeQueue();
				await operation();
				return;
			}

			// some other error, fail
			throw;
		}
	}

	private void RegisterChangeQueue()
	{
		try
		{
			const string keyspace = "__keyspace@0__:";
			var queue = changeQueue;
			changeQueue = null;
			queue?.UnsubscribeAll();
			var namespaceForSubscribe = $"{keyspace}{keyPrefix}*";
			var namespaceForSubscribeFlushAll = $"{keyspace}__flushall__*";
			queue = connectionMultiplexer.GetSubscriber();
			queue.Subscribe(namespaceForSubscribe, (channel, value) =>
			{
				string key = channel.ToString()[keyspace.Length..];
				KeyChanged?.Invoke(key);
			});
			queue.Subscribe(namespaceForSubscribeFlushAll, (channel, value) =>
			{
				if (value == "set")
				{
					string key = channel.ToString()[keyspace.Length..];
					KeyChanged?.Invoke(key);
				}
			});
			changeQueue = queue;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error registering change queue");
		}
	}

	private class DistributedLock : IAsyncDisposable
	{
		private readonly IConnectionMultiplexer connection;
		private readonly string lockKey;
		private readonly string lockToken;

		public DistributedLock(IConnectionMultiplexer connection, string lockKey, string lockToken)
		{
			this.connection = connection;
			this.lockKey = lockKey;
			this.lockToken = lockToken;
		}

		public async ValueTask DisposeAsync()
		{
			await connection.GetDatabase().LockReleaseAsync(lockKey, lockToken);
		}
	}

	private static readonly TimeSpan distributedLockSleepTime = TimeSpan.FromMilliseconds(100.0);

	/// <inheritdoc />
	public async Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan lockTime, TimeSpan timeout = default)
	{
		var db = connectionMultiplexer.GetDatabase();
		Stopwatch timer = Stopwatch.StartNew();
		string lockKey = "DistributedLock_" + key;
		string lockToken = Guid.NewGuid().ToString("N");

		do
		{
			if (await db.LockTakeAsync(lockKey, lockToken, lockTime))
			{
				logger.LogDebug("Acquired redis cache distributed lock {lockKey}", lockKey);
				return new DistributedLock(connectionMultiplexer, lockKey, lockToken);
			}
			if (timeout > distributedLockSleepTime)
			{
				await Task.Delay(distributedLockSleepTime);
			}
		}
		while (timer.Elapsed < timeout);

		return null;
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			// make sure pub/sub is up
			if (changeQueue is null)
			{
				RegisterChangeQueue();
			}
			await Task.Delay(10000, stoppingToken);
		}
	}
}

/// <summary>
/// Distributed redis cache options
/// </summary>
public sealed class DistributedRedisCacheOptions
{
	/// <summary>
	/// Key prefix
	/// </summary>
	public string KeyPrefix { get; set; } = string.Empty;
}

/// <summary>
/// Interface for distributed locks
/// </summary>
public interface IDistributedLockFactory
{
	/// <summary>
	/// Attempt to acquire a distributed lock
	/// </summary>
	/// <param name="key">Lock key</param>
	/// <param name="lockTime">Duration to hold the lock before it auto-expires. Set this to the maximum possible duration you think your code might hold the lock.</param>
	/// <param name="timeout">Time out to acquire the lock or default to only make one attempt to acquire the lock</param>
	/// <returns>The lock or null if the lock could not be acquired</returns>
	Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan lockTime, TimeSpan timeout = default);
}
