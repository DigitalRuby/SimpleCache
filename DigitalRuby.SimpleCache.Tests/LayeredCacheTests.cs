﻿namespace DigitalRuby.SimpleCache.Tests;

/// <summary>
/// Tests for layered cache
/// </summary>
[TestFixture]
public sealed class LayeredCacheTests : TimeProvider, ISystemClock, IDiskSpace, IOptions<MemoryCacheOptions>
{
    /// <summary>
    /// Setup
    /// </summary>
    [SetUp]
    public void Setup()
    {
        fileSpaces.Clear();
        memoryCache = new(this);
        freeSpace = 10000;
        totalSpace = 100000;
        UtcNow = new DateTimeOffset(2022, 1, 1, 1, 1, 1, TimeSpan.Zero);
        fileCache = new MemoryFileCache(serializer, this);
        distributedCache = new DistributedMemoryCache(this);
        layeredCache = new LayeredCache(new LayeredCacheOptions { KeyPrefix = "test" }, serializer,
            memoryCache, fileCache, distributedCache, this, new NullLogger<LayeredCache>());
    }

    /// <summary>
    /// Teardown
    /// </summary>
    [TearDown]
    public void Teardown()
    {
        layeredCache?.Dispose();
    }

    /// <summary>
    /// Test GetOrCreate prevents cache storm
    /// </summary>
    /// <returns>Task</returns>
    [Test]
    public async Task TestGetOrCreatePreventsCacheStorm()
    {
        ManualResetEvent evt = new(false);
        List<Task> tasks = [];
        int cacheMiss = 0;
        for (int i = 0; i < 1000; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await AsTask(evt);
                await layeredCache.GetOrCreateAsync<string>(testKey, async context =>
                {
                    await Task.Delay(100, context.CancelToken);
                    context.CacheParameters = TimeSpan.FromMinutes(1.0);
                    Interlocked.Increment(ref cacheMiss);
                    return testValue;
                });
            }));
        }
        await Task.Delay(100); // give time to wait
        // signal all waiters
        evt.Set();
        await Task.WhenAll(tasks);

        // we had better only have 1 cache miss
        Assert.That(cacheMiss, Is.EqualTo(1));
        var foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.EqualTo(testValue));
    }

    /// <summary>
    /// Test get or create functionality
    /// </summary>
    /// <returns>Task</returns>
    [Test]
    public async Task TestGetOrCreate()
    {
        // add one key
        object state = "hello";
        await layeredCache.GetOrCreateAsync<string>(testKey, context =>
        {
            context.CacheParameters = expire;
            Assert.That(context.State, Is.EqualTo("hello"));
            return Task.FromResult<string?>(testValue);
        }, state);
        var foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.EqualTo(testValue));

        // add different key
        await layeredCache.GetOrCreateAsync<string>(testKey2, context =>
        {
            context.CacheParameters = expire;
            Assert.That(context.State, Is.Null);
            return Task.FromResult<string?>(testValue2);
        });

        // keys should exist with different values
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Not.Null);
        Assert.That(foundValue, Is.EqualTo(testValue));
        foundValue = await layeredCache.GetAsync<string>(testKey2);
        Assert.That(foundValue, Is.Not.Null);
        Assert.That(foundValue, Is.EqualTo(testValue2));

        // get or create values should expire properly
        UtcNow += expire;
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Null);
        foundValue = await layeredCache.GetAsync<string>(testKey2);
        Assert.That(foundValue, Is.Null);
    }

    /// <summary>
    /// Test that get, set and delete work
    /// </summary>
    /// <returns>Task</returns>
    [Test]
    public async Task TestGetSetDelete()
    {
        var expire = TimeSpan.FromSeconds(30.0);

        // deleting non-existant key should not throw
        await layeredCache.DeleteAsync<string>(testKey);

        var foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Null);

        // put a key in
        await layeredCache.SetAsync(testKey, testValue, expire);

        // should be able to get
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Not.Null);
        Assert.That(foundValue, Is.EqualTo(testValue));

        // delete key
        await layeredCache.DeleteAsync<string>(testKey);

        // key should no longer exist
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Null);

        // put a key in
        await layeredCache.SetAsync(testKey, testValue, expire);

        // values created by set async should expire out
        UtcNow += expire;

        // key should no longer exist
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Null);
    }

    /// <summary>
    /// Test we can cache primitives
    /// </summary>
    /// <returns>Task</returns>
    [Test]
    public async Task TestCachePrimitive()
    {
        var expire = TimeSpan.FromSeconds(30.0);
        var foundValue = await layeredCache.GetAsync<int>(testKey);
        Assert.That(foundValue, Is.EqualTo(0));
        await layeredCache.SetAsync(testKey, 1, expire);
        foundValue = await layeredCache.GetAsync<int>(testKey);
        Assert.That(foundValue, Is.EqualTo(1));
        UtcNow += expire; // cache should clear
        foundValue = await layeredCache.GetAsync<int>(testKey);
        Assert.That(foundValue, Is.EqualTo(0));
    }

    /// <summary>
    /// Test that memory cache compacts
    /// </summary>
    [Test]
    public async Task TestMemoryCompaction()
    {
        await layeredCache.SetAsync(testKey, testValue, (expire, 75000));
        await layeredCache.SetAsync(testKey2, testValue2, (expire, 175000));

        // smaller key should exist
        var foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Not.Null);
        Assert.That(foundValue, Is.EqualTo(testValue));

        // larger key overran memory
        foundValue = memoryCache.Get<string>(testKey2);
        Assert.That(foundValue, Is.Null);

        // we can still grab it from the file cache
        foundValue = await layeredCache.GetAsync<string>(testKey2);
        Assert.That(foundValue, Is.Not.Null);

        // it has still better not be in memory
        foundValue = memoryCache.Get<string>(testKey2);
        Assert.That(foundValue, Is.Null);
    }

    /// <summary>
    /// Make sure we don't cache exceptions
    /// </summary>
    [Test]
    public async Task TestException()
    {
        Assert.ThrowsAsync<ApplicationException>(() =>
        {
            return layeredCache.GetOrCreateAsync<string>(testKey, context =>
            {
                context.CacheParameters = TimeSpan.FromSeconds(30.0);
                throw new ApplicationException();
            });
        });
        var foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Null);
    }

    /// <summary>
    /// Make sure we don't cache null
    /// </summary>
    /// <returns>Task</returns>
    [Test]
    public async Task TestNull()
    {
        int factories = 0;
        var result = await layeredCache.GetOrCreateAsync<string>(testKey, context =>
        {
            factories++;
            return Task.FromResult<string?>(null);
        });
        Assert.That(result, Is.Null);

        result = await layeredCache.GetOrCreateAsync<string>(testKey, context =>
        {
            factories++;
            return Task.FromResult<string?>(null);
        });
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(factories, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// Convert a wait handle to a task with a timeout. Allow multiple waiters to release on a single manual reset event.
    /// </summary>
    /// <param name="handle">Wait handle</param>
    /// <returns>Task</returns>
    private static Task<object> AsTask(WaitHandle handle)
    {
        TaskCompletionSource<object> tcs = new();
        TimeSpan timeout = TimeSpan.FromMinutes(1.0);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) =>
        {
            TaskCompletionSource<object> localTcs = (TaskCompletionSource<object>)state!;
            if (timedOut)
            {
                localTcs.TrySetCanceled();
            }
            else
            {
                localTcs.TrySetResult(new());
            }
        }, tcs, timeout, executeOnlyOnce: true);
        tcs.Task.ContinueWith((_, state) => ((RegisteredWaitHandle)state!).Unregister(null), registration, TaskScheduler.Default);
        return tcs.Task;
    }

    private const string testKey = "testkey";
    private const string testKey2 = "testkey2";
    private const string testValue = "testvalue";
    private const string testValue2= "testvalue2";
    private static readonly TimeSpan expire = TimeSpan.FromSeconds(30.0);

    private readonly ISerializer serializer = new JsonSerializer();
    private readonly Dictionary<string, long> fileSpaces = [];
    
    private MemoryCache memoryCache;
    private MemoryFileCache fileCache;
    private DistributedMemoryCache distributedCache;
    private LayeredCache layeredCache;
    private long freeSpace;
    private long totalSpace;

    MemoryCacheOptions IOptions<MemoryCacheOptions>.Value => new()
    {
        Clock = this,
        SizeLimit = 100000,
        CompactionPercentage = 0.5,
        ExpirationScanFrequency = TimeSpan.FromMilliseconds(1.0)
    };
    
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => UtcNow;

    long IDiskSpace.GetFileSize(string fileName)
    {
        fileSpaces.TryGetValue(fileName, out long space);
        return space;
    }

    double IDiskSpace.GetPercentFreeSpace(string path, out long availableFreeSpace, out long totalSpace)
    {
        availableFreeSpace = this.freeSpace;
        totalSpace = this.totalSpace;
        return (double)availableFreeSpace / (double)totalSpace;
    }
}
