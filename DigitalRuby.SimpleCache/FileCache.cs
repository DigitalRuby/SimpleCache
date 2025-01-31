namespace DigitalRuby.SimpleCache;

/// <summary>
/// File cache item
/// </summary>
/// <typeparam name="T">Type of item</typeparam>
/// <remarks>
/// Constructor
/// </remarks>
/// <param name="expires">Expires</param>
/// <param name="item">Item</param>
/// <param name="size">Size on disk</param>
public sealed class FileCacheItem<T>(DateTimeOffset expires, T item, int size)
{

    /// <summary>
    /// Expiration
    /// </summary>
    public DateTimeOffset Expires { get; } = expires;

    /// <summary>
    /// Item
    /// </summary>
    public T Item { get; } = item;

    /// <summary>
    /// Size on disk
    /// </summary>
    public int Size { get; } = size;
}

/// <summary>
/// Disk space checker
/// </summary>
public interface IDiskSpace
{
	/// <summary>
	/// Get free space for a path
	/// </summary>
	/// <param name="path">Path</param>
	/// <param name="availableFreeSpace">Available free space</param>
	/// <param name="totalSpace">Total space</param>
	/// <returns>Free space for drive path is on</returns>
	double GetPercentFreeSpace(string path, out long availableFreeSpace, out long totalSpace);

	/// <summary>
	/// Get size of a file
	/// </summary>
	/// <param name="fileName">File name</param>
	/// <returns>Size</returns>
	long GetFileSize(string fileName);
}

/// <summary>
/// Determine free and total disk space for a given path
/// </summary>
public sealed class DiskSpace : IDiskSpace
{
	/// <inheritdoc />
	public double GetPercentFreeSpace(string path, out long availableFreeSpace, out long totalSpace)
	{
		var info = new DriveInfo(path);
		availableFreeSpace = info.AvailableFreeSpace;
		totalSpace = info.TotalSize;
		return ((double)availableFreeSpace / (double)totalSpace);
	}

	/// <inheritdoc />
	public long GetFileSize(string fileName) => new FileInfo(fileName).Length;
}

/// <summary>
/// Cache items using files
/// </summary>
public interface IFileCache
{
	/// <summary>
	/// The serializer used to convert objects to bytes
	/// </summary>
	public ISerializer Serializer { get; }

	/// <summary>
	/// Time provider
	/// </summary>
	public TimeProvider Clock { get; }

	/// <summary>
	/// Get a cache value
	/// </summary>
	/// <typeparam name="T">Type of value</typeparam>
	/// <param name="key">Key</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task of FileCacheItem of type T, null if not found</returns>
	/// <exception cref="NullReferenceException">Key is null</exception>
	Task<FileCacheItem<T>?> GetAsync<T>(string key, CancellationToken cancelToken = default);

	/// <summary>
	/// Set a cache value, overwriting any existing value
	/// </summary>
	/// <param name="key">Key</param>
	/// <param name="value">Value. If the value is a byte array, it will not be serialized but will instead
	/// be assumed to have already been serialized.</param>
	/// <param name="cacheParameters">Cache parameters or null for default</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	/// <exception cref="NullReferenceException">Key is null</exception>
	Task SetAsync(string key, object value, CacheParameters cacheParameters = default, CancellationToken cancelToken = default);

    /// <summary>
    /// Get or create a cache item if it doesn't exist
    /// </summary>
    /// <param name="key">Key</param>
    /// <param name="valueFactory">Value factory</param>
    /// <param name="cacheParameters">Cache parameters</param>
    /// <param name="cancelToken">Cancel token</param>
    /// <returns>Task of file cache item of type T</returns>
    /// <exception cref="NullReferenceException">Key is null</exception>
	async Task<FileCacheItem<T>> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> valueFactory, CacheParameters cacheParameters = default, CancellationToken cancelToken = default)
    {
        var result = await GetAsync<T>(key, cancelToken);
        if (result is null)
        {
            var value = await valueFactory(cancelToken);
            if (value is not null && value is not Exception && value is not Task)
            {
                await SetAsync(key, value, cacheParameters, cancelToken);
            }
            result = new FileCacheItem<T>(Clock.GetUtcNow() + cacheParameters.Duration, value, cacheParameters.Size);
        }
        return result;
    }

    /// <summary>
    /// Remove an item from the cache
    /// </summary>
    /// <param name="key">Key</param>
    /// <param name="cancelToken">Cancel token</param>
    /// <returns>Task</returns>
    /// <exception cref="NullReferenceException">Key is null</exception>
    /// <remarks>Ensure the the type parameter is the exact type that you used to add the item to the cache</remarks>
    Task RemoveAsync(string key, CancellationToken cancelToken = default);

	/// <summary>
	/// Remove all cache items
	/// </summary>
	/// <returns>Task</returns>
	Task ClearAsync();
}

/// <summary>
/// Null file cache that does nothing
/// </summary>
public sealed class NullFileCache : IFileCache
{
	/// <summary>
	/// Serializer
	/// </summary>
	public ISerializer Serializer { get; } = new JsonLZ4Serializer();

	/// <summary>
	/// Clock
	/// </summary>
	public TimeProvider Clock { get; } = TimeProvider.System;

    /// <inheritdoc />
    public Task<FileCacheItem<T>?> GetAsync<T>(string key, CancellationToken cancelToken = default)
    {
		return Task.FromResult<FileCacheItem<T>?>(null);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancelToken = default)
    {
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task SetAsync(string key, object value, CacheParameters cacheParameters = default, CancellationToken cancelToken = default)
    {
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task ClearAsync() => Task.CompletedTask;
}

/// <summary>
/// File cache for unit tests
/// </summary>
/// <remarks>
/// Constructor
/// </remarks>
/// <param name="serializer">Serializer</param>
/// <param name="clock">Clock</param>
public sealed class MemoryFileCache(ISerializer serializer, TimeProvider clock) : IFileCache
{
	private readonly ISerializer serializer = serializer;
	private readonly ConcurrentDictionary<string, FileCacheItem<byte[]>> items = new();

    /// <inheritdoc />
    public ISerializer Serializer => serializer;

	/// <summary>
	/// Clock
	/// </summary>
	public TimeProvider Clock { get; } = TimeProvider.System;

	/// <inheritdoc />
	public Task<FileCacheItem<T>?> GetAsync<T>(string key, CancellationToken cancelToken = default)
    {
		if (items.TryGetValue(key, out var item) && item.Expires > clock.GetUtcNow())
		{
			var value = serializer.Deserialize<T?>(item.Item);
			if (value is null)
			{
				return Task.FromResult<FileCacheItem<T>?>(null);
			}
			return Task.FromResult<FileCacheItem<T>?>(new FileCacheItem<T>(item.Expires, value, item.Size));
		}
		return Task.FromResult<FileCacheItem<T>?>(null);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancelToken = default)
    {
		items.TryRemove(key, out _);
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task SetAsync(string key, object value, CacheParameters cacheParameters = default, CancellationToken cancelToken = default)
    {
		var bytes = (value is byte[] alreadyBytes ? alreadyBytes : serializer.Serialize(value) ?? throw new IOException("Serialize failed for key " + key));
		items[key] = new FileCacheItem<byte[]>(clock.GetUtcNow() + cacheParameters.Duration, bytes, bytes.Length);
		return Task.CompletedTask;
    }

	/// <inheritdoc />
	public Task ClearAsync()
	{
		items.Clear();
		return Task.CompletedTask;
	}
}

/// <inheritdoc />
public sealed class FileCache : BackgroundService, IFileCache
{
	/// <summary>
	/// Clock
	/// </summary>
	public TimeProvider Clock { get; }

	private static readonly Type byteArrayType = typeof(byte[]);
	private static readonly TimeSpan cleanupLoopDelay = TimeSpan.FromMilliseconds(1.0);

	private readonly MultithreadedKeyLocker keyLocker = new(512);

	private readonly IDiskSpace diskSpace;
	private readonly ILogger logger;

	private readonly string baseDir;
	private readonly double freeSpaceThreshold;

	private bool directoryLocked;

	/// <summary>
	/// Serializer
	/// </summary>
	public ISerializer Serializer { get; }

	private string GetHashFileName(string key)
	{
		var hash = Blake2b.ComputeHash(16, Encoding.UTF8.GetBytes(key));

		// get a base64 file name from hash bytes and fix invalid path chars
		var fileName = Convert.ToBase64String(hash)
			.Replace('/', '_')
			.Replace('+', '-')
			.Replace("=", string.Empty); // padding, can remove for shorter file name

		return Path.Combine(baseDir, fileName);
	}

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="options">Options</param>
	/// <param name="serializer">Serializer</param>
	/// <param name="diskSpace">Disk space</param>
	/// <param name="clock">Clock</param>
	/// <param name="logger">Logger</param>
	public FileCache(FileCacheOptions options,
		ISerializer serializer,
		IDiskSpace diskSpace,
		TimeProvider clock,
		ILogger<FileCache> logger)
	{
		Serializer = serializer;
		this.diskSpace = diskSpace;
		Clock = clock;
		this.logger = logger;
		string assemblyName = Assembly.GetEntryAssembly()!.GetName().Name!;
		baseDir = options.CacheDirectory;
		if (string.IsNullOrWhiteSpace(baseDir) || baseDir.IndexOfAny(Path.GetInvalidPathChars()) > 0)
		{
			throw new ArgumentException("Invalid file cache directory: " + baseDir);
		}
		else if (baseDir.Equals("%temp%", StringComparison.OrdinalIgnoreCase))
		{
			baseDir = Path.GetTempPath();
		}
		baseDir = Path.Combine(baseDir, assemblyName, nameof(FileCache));
		Directory.CreateDirectory(baseDir);
		freeSpaceThreshold = (double)options.FreeSpaceThreshold * 0.01;
		double freePercent = diskSpace.GetPercentFreeSpace(baseDir, out long availableFreeSpace, out long totalSpace);
		this.logger.LogWarning("Disk space free: {freePercent:0.00}% ({availableFreeSpace}/{totalFreeSpace})",
			freePercent * 100.0, availableFreeSpace, totalSpace);
	}

	/// <inheritdoc />
	public async Task<FileCacheItem<T>?> GetAsync<T>(string key, CancellationToken cancelToken = default)
	{
		ArgumentNullException.ThrowIfNull(key, nameof(key));

		while (directoryLocked)
		{
			await Task.Delay(cleanupLoopDelay, cancelToken);
		}

		string fileName = GetHashFileName(key);
		using var keyLock = keyLocker.Lock(fileName);

		try
		{
			if (!File.Exists(fileName))
			{
				logger.LogDebug("File cache miss {key}, {fileName}", key, fileName);
				return default;
			}
			using FileStream readerStream = new(fileName, FileMode.Open, FileAccess.Read, FileShare.None);
			using BinaryReader reader = new(readerStream);
			long ticks = reader.ReadInt64();
			DateTimeOffset cutOff = new(ticks, TimeSpan.Zero);
			if (Clock.GetUtcNow() >= cutOff)
			{
				// expired, delete, no exception for performance
				logger.LogDebug("File cache expired {key}, {fileName}, deleting", key, fileName);
				reader.Close();
				try
				{
					File.Delete(fileName);
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Error removing expired file cache {key}, {fileName}", key, fileName);
				}
				return default;
			}
			else
			{
				// read item from file
				int size = reader.ReadInt32();
				byte[] bytes = reader.ReadBytes(size);
				if (bytes.Length != size)
				{
					throw new IOException("Byte counts are off for file cache item");
				}
				FileCacheItem<T> result;
				if (typeof(T) == byteArrayType)
				{
					result = new FileCacheItem<T>(new DateTimeOffset(ticks, TimeSpan.Zero), (T)(object)bytes, size);
				}
				else
				{
					var item = (T?)Serializer.Deserialize(bytes, typeof(T?)) ?? throw new IOException("Corrupt cache file " + fileName);
					result = new FileCacheItem<T>(new DateTimeOffset(ticks, TimeSpan.Zero), item, size);
				}
				logger.LogDebug("File cache hit {key}, {fileName}", key, fileName);
				return result;
			}
		}
		catch (Exception ex)
		{
			// ignore, just pretend item not exists
			try
			{
				// clear out file
				logger.LogError(ex, "Error reading cache file {fileName}, deleting file", fileName);
				File.Delete(fileName);
			}
			catch (Exception ex2)
			{
				logger.LogError(ex2, "Error removing corrupted cache file {fileName}", fileName);
			}
			return default;
		}
	}

	/// <inheritdoc />
	public async Task RemoveAsync(string key, CancellationToken cancelToken = default)
	{
		ArgumentNullException.ThrowIfNull(key, nameof(key));

		while (directoryLocked)
		{
			await Task.Delay(cleanupLoopDelay, cancelToken);
		}

		string fileName = GetHashFileName(key);
		using var keyLock = keyLocker.Lock(fileName);

		try
		{
			if (File.Exists(fileName))
			{
				File.Delete(fileName);
				logger.LogDebug("File cache removed {key}, {fileName}", key, fileName);
			}
			else
			{
				logger.LogDebug("File cache remove ignored for non existing {key}, {fileName}", key, fileName);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error removing existing cache file {key}, {fileName}", key, fileName);
		}
	}

	/// <inheritdoc />
	public async Task SetAsync(string key, object value, CacheParameters cacheParameters = default, CancellationToken cancelToken = default)
	{
		ArgumentNullException.ThrowIfNull(key, nameof(key));

		while (directoryLocked)
		{
			await Task.Delay(cleanupLoopDelay, cancelToken);
		}

		string fileName = GetHashFileName(key);
		using var keyLock = keyLocker.Lock(fileName);

		try
		{
			using FileStream writerStream = new(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
			using BinaryWriter writer = new(writerStream);
			DateTimeOffset expires = Clock.GetUtcNow() + cacheParameters.Duration;
			writer.Write(expires.Ticks);
			byte[]? bytes = (value is byte[] alreadyBytes ? alreadyBytes : Serializer.Serialize(value));
			if (bytes is not null)
			{
				writer.Write(bytes.Length);
				writer.Write(bytes);
			}
			logger.LogDebug("File cache set {key}, {fileName}", key, fileName);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error setting cache file {key}, {fileName}", key, fileName);
		}
	}

	/// <inheritdoc />
	public async Task ClearAsync()
	{
		// give us 10 seconds to nuke the directory
		directoryLocked = true;
		for (int i = 0; i <= 10; i++)
		{
			try
			{
				Directory.Delete(baseDir, true);
				Directory.CreateDirectory(baseDir);
				break;
			}
			catch
			{
				await Task.Delay(1000);
			}
		}
		directoryLocked = false;
	}

	/// <summary>
	/// Clean up free space if needed
	/// </summary>
	/// <param name="stoppingToken">Stopping token</param>
	/// <returns>Task</returns>
	public async Task CleanupFreeSpaceAsync(CancellationToken stoppingToken = default)
	{
		// if low on disk space, purge it all
		while (true)
		{
			double freeSpacePercent = diskSpace.GetPercentFreeSpace(baseDir, out long availableFreeSpace, out long totalSpace);
			if (freeSpacePercent >= freeSpaceThreshold)
			{
				break;
			}
			bool foundFile = false;
			foreach (string fileName in Directory.EnumerateFiles(baseDir))
			{
				foundFile = true;
				using var keyLock = keyLocker.Lock(fileName);
				try
				{
					// delete file and increment free space
					long fileSize = diskSpace.GetFileSize(fileName);
					File.Delete(fileName);
					availableFreeSpace += fileSize;

					// if we have freed up enough space, stop deleting files
					if ((double)availableFreeSpace / (double)totalSpace >= freeSpaceThreshold)
					{
						break;
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Error cleaning up cache file {fileName}", fileName);
				}

				// don't gobble up too much cpu
				await Task.Delay(cleanupLoopDelay, stoppingToken);
			}

			// if no files, we are done, get out of the loop
			if (!foundFile)
			{
				break;
			}
		}
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken = default)
	{
		// loop and continually free up space as needed
		while (!stoppingToken.IsCancellationRequested)
		{
			await CleanupFreeSpaceAsync(stoppingToken);
			await Task.Delay(10000, stoppingToken);
		}
	}
}

/// <summary>
/// File cache options
/// </summary>
public sealed class FileCacheOptions
{
	/// <summary>
	/// Cache directory. Default is %temp% which means to use temp directory.
	/// </summary>
	public string CacheDirectory { get; set; } = "%temp%";

	/// <summary>
	/// Percentage (0 - 100) of free space remaining to trigger cleanup of files. Default is 15.
	/// </summary>
	public int FreeSpaceThreshold { get; set; } = 15;
}