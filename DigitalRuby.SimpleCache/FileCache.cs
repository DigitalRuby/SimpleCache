using System;

namespace DigitalRuby.SimpleCache;

/// <summary>
/// File cache item
/// </summary>
/// <typeparam name="T">Type of item</typeparam>
public class FileCacheItem<T>
{
	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="expires">Expires</param>
	/// <param name="item">Item</param>
	public FileCacheItem(DateTime expires, T item)
	{
		Expires = expires;
		Item = item;
	}

	/// <summary>
	/// Expiration
	/// </summary>
	public DateTime Expires { get; }

	/// <summary>
	/// Item
	/// </summary>
	public T Item { get; }
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
	double GetPercentFreeSpace(string path, out long availableFreeSpace, out long totalSpace)
	{
		var info = new DriveInfo(path);
		availableFreeSpace = info.AvailableFreeSpace;
		totalSpace = info.TotalSize;
		return ((double)availableFreeSpace / (double)totalSpace);
	}

	/// <summary>
	/// Get size of a file
	/// </summary>
	/// <param name="fileName">File name</param>
	/// <returns>Size</returns>
	long GetFileSize(string fileName) => new FileInfo(fileName).Length;
}

/// <summary>
/// Piggy back off the interface that has default implementations
/// </summary>
public class DiskSpace : IDiskSpace { }

/// <summary>
/// Cache items using files
/// </summary>
public interface IFileCache
{
	/// <summary>
	/// Get a cache value
	/// </summary>
	/// <typeparam name="T">Type of value</typeparam>
	/// <param name="key">Key</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task of type T, T will be null if not found</returns>
	/// <exception cref="NullReferenceException">Key is null</exception>
	Task<FileCacheItem<T>?> GetAsync<T>(string key, CancellationToken cancelToken = default);

	/// <summary>
	/// Set a cache value, overwriting any existing value
	/// </summary>
	/// <typeparam name="T">Type of object to set</typeparam>
	/// <param name="key">Key</param>
	/// <param name="value">Value</param>
	/// <param name="cacheParameters">Cache parameters or null for default</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	/// <exception cref="NullReferenceException">Key is null</exception>
	Task SetAsync<T>(string key, T value, CacheParameters cacheParameters = default, CancellationToken cancelToken = default);

	/// <summary>
	/// Remove an item from the cache
	/// </summary>
	/// <typeparam name="T">Type of object to remove</typeparam>
	/// <param name="key">Key</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	/// <exception cref="NullReferenceException">Key is null</exception>
	/// <remarks>Ensure the the type parameter is the exact type that you used to add the item to the cache</remarks>
	Task RemoveAsync<T>(string key, CancellationToken cancelToken = default);
}

/// <inheritdoc />
public class FileCache : BackgroundService, IFileCache
{
	private static readonly TimeSpan cleanupLoopDelay = TimeSpan.FromMilliseconds(1.0);

	private readonly KeyLocker keyLocker = new(512);

	private readonly ISerializer serializer;
	private readonly IDiskSpace diskSpace;
	private readonly IDateTimeProvider dateTimeProvider;

	private readonly string baseDir;

	/// <summary>
	/// Threshold of free space to purge all files
	/// </summary>
	public double FreeSpaceThreshold { get; set; } = 0.2;

	/// <summary>
	/// Converts a byte array to a string of hexadecimals.
	/// </summary>
	public static string ToHexString(byte[] bytes)
	{
		if (bytes == null)
		{
			throw new ArgumentNullException(nameof(bytes));
		}
		var sb = new StringBuilder(bytes.Length * 2);
		for (int i = 0; i < bytes.Length; i++)
		{
			sb.Append(bytes[i].ToString("x2"));
		}
		return sb.ToString();
	}

	private string GetHashFileName(string key)
	{
		var hash = Blake2b.ComputeHash(16, Encoding.UTF8.GetBytes(key));
		return Path.Combine(baseDir, ToHexString(hash));
	}

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="serializer">Serializer</param>
	/// <param name="diskSpace">Disk space</param>
	/// <param name="dateTimeProvider">Date time provider</param>
	/// <param name="logger">Logger</param>
	public FileCache(ISerializer serializer,
		IDiskSpace diskSpace,
		IDateTimeProvider dateTimeProvider,
		ILogger<FileCache> logger)
	{
		this.serializer = serializer;
		this.diskSpace = diskSpace;
		this.dateTimeProvider = dateTimeProvider;
		string assemblyName = Assembly.GetEntryAssembly()!.GetName().Name!;
		baseDir = Path.Combine(Path.GetTempPath(), assemblyName, nameof(FileCache));
		if (Directory.Exists(baseDir))
		{
			try
			{
				Directory.Delete(baseDir, true);
			}
			catch
			{
			}
		}
		Directory.CreateDirectory(baseDir);
		double freePercent = diskSpace.GetPercentFreeSpace(baseDir, out long availableFreeSpace, out long totalSpace);
		logger.LogWarning("Disk space free: {freePercent:0.00}% ({availableFreeSpace}/{totalFreeSpace})",
			freePercent * 100.0, availableFreeSpace, totalSpace);
	}

	/// <inheritdoc />
	public async Task<FileCacheItem<T>?> GetAsync<T>(string key, CancellationToken cancelToken = default)
	{
		ArgumentNullException.ThrowIfNull(key, nameof(key));

		string fileName = GetHashFileName(key);
		using var keyLock = keyLocker.AcquireSpinLock(fileName);

		try
		{
			if (!File.Exists(fileName))
			{
				return null;
			}
			using FileStream readerStream = new(fileName, FileMode.Open, FileAccess.Read, FileShare.None);
			using BinaryReader reader = new(readerStream);
			long ticks = reader.ReadInt64();
			DateTime cutOff = new(ticks, DateTimeKind.Utc);
			if (dateTimeProvider.UtcNow >= cutOff)
			{
				// expired, delete, no exception for performance
				reader.Close();
				File.Delete(fileName);
				return null;
			}
			else
			{
				// read item from file
				int size = reader.ReadInt32();
				byte[] bytes = new byte[size];
				int bytesRead = await readerStream.ReadAsync(bytes, cancelToken);
				if (bytesRead != size)
				{
					throw new IOException("Byte counts are off for file cache item");
				}
				var item = (T?)serializer.Deserialize(bytes, typeof(T?));
				if (item is null)
				{
					throw new IOException("Corrupt cache file " + fileName);
				}
				return new FileCacheItem<T>(new DateTime(ticks, DateTimeKind.Utc), item);
			}
		}
		catch
		{
			// ignore, just pretend item not exists
			try
			{
				// clear out file
				File.Delete(fileName);
			}
			catch
			{
				// ignore
			}
			return null;
		}
	}

	/// <inheritdoc />
	public Task RemoveAsync<T>(string key, CancellationToken cancelToken = default)
	{
		ArgumentNullException.ThrowIfNull(key, nameof(key));

		string fileName = GetHashFileName(key);
		using var keyLock = keyLocker.AcquireSpinLock(fileName);

		try
		{
			if (File.Exists(fileName))
			{
				File.Delete(fileName);
			}
		}
		catch
		{
			// ignore
		}
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public async Task SetAsync<T>(string key, T value, CacheParameters cacheParameters = default, CancellationToken cancelToken = default)
	{
		ArgumentNullException.ThrowIfNull(key, nameof(key));

		string fileName = GetHashFileName(key);
		using var keyLock = keyLocker.AcquireSpinLock(fileName);

		try
		{
			using FileStream writerStream = new(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
			using BinaryWriter writer = new(writerStream);
			DateTimeOffset expires = dateTimeProvider.UtcNow + cacheParameters.Duration;
			writer.Write(expires.Ticks);
			byte[]? bytes = serializer.Serialize(value);
			if (bytes is not null)
			{
				writer.Write(bytes.Length);
				await writerStream.WriteAsync(bytes, cancelToken);
			}
		}
		catch
		{
			// ignore
		}
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
			double freeSpace = diskSpace.GetPercentFreeSpace(baseDir, out long availableFreeSpace, out long totalSpace);
			if (freeSpace >= FreeSpaceThreshold)
			{
				break;
			}
			bool foundFile = false;
			foreach (string fileName in Directory.EnumerateFiles(baseDir))
			{
				foundFile = true;
				using var keyLock = keyLocker.AcquireSpinLock(fileName);
				try
				{
					// delete file and increment free space
					long fileSize = diskSpace.GetFileSize(fileName);
					File.Delete(fileName);
					availableFreeSpace += fileSize;

					// if we have freed up enough space, stop deleting files
					if ((double)availableFreeSpace / (double)totalSpace >= FreeSpaceThreshold)
					{
						break;
					}
				}
				catch
				{
					// ignore
				}

				// don't gobble up too much cpu
				await dateTimeProvider.DelayAsync(cleanupLoopDelay, stoppingToken);
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
