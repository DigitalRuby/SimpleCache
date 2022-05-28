# SimpleCache
SimpleCache removes the headache and pain of getting caching right in .NET.

Simple cache has three layers to give you maximum performance:

- RAM. You can specify the maximum amount of memory to use for cache.
- Disk. You can specify at what free space percentage to start cleaning up cache files on disk. You can turn off the disk cache if desired, recommended if not running on SSD. Why not take advantage of that free disk space being wasted on your servers?
- Redis. The third and final layer of simple cache is redis, leveraging the StackExchange.Redis nuget package. Key change notifications ensure cache keys are purged if a key is changed on another machine.

Simple cache also has cache storm prevention built in, use the `GetOrCreateAsync` method.

## Setup and Configuration

```cs
using DigitalRuby.SimpleCache;

// create your builder, add simple cache
var builder = WebApplication.CreateBuilder(args);

// bind to IConfiguration, see the DigitalRuby.SimpleCache.Sandbox project appsettings.json for an example
builder.Services.AddSimpleCache(builder.Configuration);

// you can also create a builder with a strongly typed configuration
// builder.Services.AddSimpleCache(new SimpleCacheConfiguration
{
    // fill in values here
});
```

The configuration options are:

```json
{
  "DigitalRuby.SimpleCache":
  {
    /*
    optional, cache key prefix, by default the entry assembly name is used
    you can set this to an empty string to share keys between services that are using the same redis cluster
    */
    "KeyPrefix": "sandbox",

    /* optional, override max memory size (in megabytes). Default is 1024. */
    "MaxMemorySize": 2048,

    /* redis connection string, required */
    "RedisConnectionString": "localhost:6379",

    /*
    opptional, override file cache directory, set to empty to not use file cache (recommended if not on SSD)
    the default is %temp% which means to use the temp directory
    this example assumes running on Windows, for production, use an environment variable or just leave off for default of %temp%.
    */
    "FileCacheDirectory": "c:/temp",

    /* optional, override the file cache cleanup threshold (0-100 percent). default is 15 */
    "FileCacheFreeSpaceThreshold": 10,

    /*
    optional, override the default json-lz4 serializer with your own class that implements DigitalRuby.SimpleCache.ISerializer
    the serializer is used to convert objects to bytes for the file and redis caches
    this should be an assembly qualified type name
    */
    "SerializerType": "DigitalRuby.SimpleCache.JsonSerializer, DigitalRuby.SimpleCache"
  }
}

```

Only the `RedisConnectionString` is required. For production usage, you should load this from an environment variable.

## Usage

You can inject the following interface into your constructors to use the layered cache:

```cs
/// <summary>
/// Layered cache interface. A layered cache aggregates multiple caches, such as memory, file and distributed cache (redis, etc.).
/// </summary>
public interface ILayeredCache : IDisposable
{
    /// <summary>
    /// Get or create an item from the cache. This will lock the key, preventing cache storm.
    /// </summary>
    /// <typeparam name="T">Type of item</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="cacheParam">Cache parameters</param>
    /// <param name="factory">Factory method if no item is in the cache</param>
    /// <param name="cancelToken">Cancel token</param>
    /// <returns>Task of return of type T</returns>
    Task<T> GetOrCreateAsync<T>(string key, CacheParameters cacheParam, Func<CancellationToken, Task<T>> factory, CancellationToken cancelToken = default);

    /// <summary>
    /// Attempts to retrieve value of T by key.
    /// </summary>
    /// <typeparam name="T">Type of object to get</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="cancelToken">Cancel token</param>
    /// <returns>Result of null if nothing found with the key</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancelToken = default);

    /// <summary>
    /// Sets value T by key.
    /// </summary>
    /// <typeparam name="T">Type of object</typeparam>
    /// <param name="key">Cache key to set</param>
    /// <param name="obj">Object to set</param>
    /// <param name="cacheParam">Cache parameters</param>
    /// <param name="cancelToken">Cancel token</param>
    /// <returns>Task</returns>
    Task SetAsync<T>(string key, T obj, CacheParameters cacheParam, CancellationToken cancelToken = default);

    /// <summary>
    /// Attempts to delete an entry of T type by key. If there is no key found, nothing happens.
    /// </summary>
    /// <typeparam name="T">The type object object to delete</typeparam>
    /// <param name="key">The key to delete</param>
    /// <param name="cancelToken">Cancel token</param>
    /// <returns>Task</returns>
    Task DeleteAsync<T>(string key, CancellationToken cancelToken = default);
}
```

Your cache key will be modified by the type parameter, `<T>`. This means you can have duplicate `key` parameters for different types.

Cache keys are also prefixed by the entry assembly name by default. This can be changed in the configuration.

The `CacheParameters` struct can be simplified by just passing a `TimeSpan` if you don't know the size. You can also pass a tuple of `(TimeSpan, int)` for a duration, size pair.

If you do know the approximate size of your object, you should specify the size to assist the memory compaction background task to be more accurate.

## Serialization

The configuration options mention a serializer. The default serializer is a json-lz4 serializer that gives a balance of ease of use, performance and smaller cache value sizes.

You can create your own serializer if desired, or use the json serializer that does not compress, as is shown in the configuration example.

When implementing your own serializer, inherit and complete the following interface:

```cs
/// <summary>
/// Interface for serializing cache objects to/from bytes
/// </summary>
public interface ISerializer
{
    /// <summary>
    /// Deserialize
    /// </summary>
    /// <param name="bytes">Bytes to deserialize</param>
    /// <param name="type">Type of object to deserialize to</param>
    /// <returns>Deserialized object or null if failure</returns>
    object? Deserialize(byte[]? bytes, Type type);

    /// <summary>
    /// Serialize an object
    /// </summary>
    /// <param name="obj">Object to serialize</param>
    /// <returns>Serialized bytes or null if obj is null</returns>
    byte[]? Serialize(object? obj);

    /// <summary>
    /// Get a description for the serializer
    /// </summary>
    string Description { get; }
}
```

## Layers

Simple cache uses layers, just like a modern CPU. Modern CPU's have moultiple layers of cache just like simple cache.

Using multiple layers allows ever increasing amounts of data to be stored at slightly slower retrieval times.

### Memory cache

The first layer (L1), the memory cache portion of simple cache uses IMemoryCache. This will be registered for you automatically in the services collection.

A background memory compaction task runs to ensure you don't overrun memory.

### File cache

The second layer (L2), the file cache portion of simple cache uses the temp directory by default. You can override this.

Keys are hashed using Blake2B and converted to base64.

A background file cleanup task runs to ensure you do not overrun disk space.

If you are not running on an SSD, it is recommended to disable the file cache by specifying an empty string for the file cache directory.

### Redis cache

The third and final layer, the redis cache uses StackExchange.Redis nuget package.

The redis layer detects when there is a failover and failback in a cluster and handles this gracefully.

As a bonus, a distributed lock factory is provided to acquire locks that need to be synchronized accross machines.

You can inject this interface into your constructors:

```cs
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
```

## ISystemClock

Simple cache uses it's own `DateTimeProvider` class that implements the `ISystemClock` and `IDateTimeProvider` interfaces.

You can inject your own implementation for these interfaces if you have a different needs, for example tests.

---

Thanks for reading!

-- Jeff

https://www.digitalruby.com