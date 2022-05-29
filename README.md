<h1 align="center">SimpleCache</h1>

SimpleCache removes the headache and pain of getting caching right in .NET.

**Features**:
- Simple and intuitive API using generics and tasks.
- Cache storm prevention using `GetOrCreateAsync`. Your factory is guaranteed to execute only once per key, regardless of how many callers stack on it.
- Exceptions are not cached.
- Thread safe.
- Three layers: RAM, disk and redis. Disk and redis can be disabled if desired.
- Null and memory versions of both file and redis caches available for mocking.
- Excellent test coverage.
- Optimized usage of all your resources. Simple cache has three layers to give you maximum performance: RAM, disk and redis.
- Built in json-lz4 serializer for file and redis caching for smaller values and minimal implementation pain.
- You can create your own serializer if you want to use protobuf or other compression options.

## Setup and Configuration

```cs
using DigitalRuby.SimpleCache;

// create your builder, add simple cache
var builder = WebApplication.CreateBuilder(args);

// bind to IConfiguration, see the DigitalRuby.SimpleCache.Sandbox project appsettings.json for an example
builder.Services.AddSimpleCache(builder.Configuration);

// you can also create a builder with a strongly typed configuration
builder.Services.AddSimpleCache(new SimpleCacheConfiguration
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

    /* optional redis connection string */
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

If the `RedisConnectionString` is empty, no redis cache will be used, an no key change notifications will be sent, preventing auto purge of cache values that are modified.

For production usage, you should load this from an environment variable.

## Usage

You can inject the following interface into your constructors to use the layered cache:

```cs
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
```

Your cache key will be modified by the type parameter, `<T>`. This means you can have duplicate `key` parameters for different types.

Cache keys are also prefixed by the entry assembly name by default. This can be changed in the configuration.

The `CacheParameters` struct can be simplified by just passing a `TimeSpan` if you don't know the size. You can also pass a tuple of `(TimeSpan, int)` for a duration, size pair.

If you do know the approximate size of your object, you should specify the size to assist the memory compaction background task to be more accurate.

`GetOrCreateAsync` example:

```cs
TimeSpan duration = TimeSpan.FromSeconds(60.0);
var result = await cache.GetOrCreateAsync<string>(key, duration, async context =>
{
    // if your method returns a Task<T> here, you don't have to await if you are just forwarding a method call
    var value = await MyExpensiveFunctionThatReturnsAStringAsync(key);

    // set the cache duration and size, this is an important step to not miss
    context.CacheParameters = (TimeSpan.FromSeconds(30.0), value.Length * 2);

    // the context also has a CancelToken property if you need it
}, stoppingToken);
```

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
    /// <returns>Deserialized object or null if bytes is null or empty</returns>
    object? Deserialize(byte[]? bytes, Type type);

    /// <summary>
    /// Deserialize using generic type parameter
    /// </summary>
    /// <typeparam name="T">Type of object to deserialize</typeparam>
    /// <param name="bytes">Bytes</param>
    /// <returns>Deserialized object or null if bytes is null or empty</returns>
    T? Deserialize<T>(byte[]? bytes) => (T?)Deserialize(bytes, typeof(T));

    /// <summary>
    /// Serialize an object
    /// </summary>
    /// <param name="obj">Object to serialize</param>
    /// <returns>Serialized bytes or null if obj is null</returns>
    byte[]? Serialize(object? obj);

    /// <summary>
    /// Serialize using generic type parameter
    /// </summary>
    /// <typeparam name="T">Type of object</typeparam>
    /// <param name="obj">Object to serialize</param>
    /// <returns>Serialized bytes or null if obj is null</returns>
    byte[]? Serialize<T>(T? obj) => Serialize(obj);

    /// <summary>
    /// Get a short description for the serializer, i.e. json or json-lz4.
    /// </summary>
    string Description { get; }
}
```

## Layers

Simple cache uses layers, just like a modern CPU. Modern CPU's have moultiple layers of cache just like simple cache.

Using multiple layers allows ever increasing amounts of data to be stored at slightly slower retrieval times.

### Memory cache

The first layer (L1), the memory cache portion of simple cache uses IMemoryCache. This will be registered for you automatically in the services collection.

.NET will compact the memory cache based on your settings from the configuration.

### File cache

The second layer (L2), the file cache portion of simple cache uses the temp directory by default. You can override this.

Keys are hashed using Blake2B and converted to base64.

A background file cleanup task runs to ensure you do not overrun disk space.

If you are not running on an SSD, it is recommended to disable the file cache by specifying an empty string for the file cache directory.

### Redis cache

The third and final layer, the redis cache uses StackExchange.Redis nuget package.

The redis layer detects when there is a failover and failback in a cluster and handles this gracefully.

Keyspace notifications are sent to keep cache in sync between machines. Run `CONFIG SET notify-keyspace-events KEA` on your redis servers for this to take effect. Simple cache will attempt to do this as well.

Sometimes you need to purge your entire cache, do this with caution. To cause simple cache to clear memory and file caches, set a redis key that equals `__flushall__` with any value, then wait a second then execute a `FLUSHALL` or `FLUSHDB` command.

As a bonus, a distributed lock factory is provided to acquire locks that need to be synchronized accross machines.

You can inject this interface into your constructors for distributed locking:

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

Simple cache uses a `ClockHandler` class that implements the `ISystemClock` and `IClockHandler` interfaces.

You can inject your own implementation for these interfaces if you have a different needs, for example tests.

---

Thanks for reading!

-- Jeff

https://www.digitalruby.com