namespace DigitalRuby.SimpleCache.Tests;

/// <summary>
/// Tests for layered cache
/// </summary>
[TestFixture]
public sealed class LayeredCacheTests : IDiskSpace, IClockHandler, IOptions<MemoryCacheOptions>, ISystemClock
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
        utcNow = new DateTimeOffset(2022, 1, 1, 1, 1, 1, TimeSpan.Zero);
        fileCache = new MemoryFileCache(serializer, this);
        distributedCache = new DistributedMemoryCache(this);
        layeredCache = new LayeredCache(new LayeredCacheOptions { KeyPrefix = "test" }, serializer,
            memoryCache, fileCache, distributedCache, new NullLogger<LayeredCache>());
    }

    /// <summary>
    /// Test GetOrCreate prevents cache storm
    /// </summary>
    /// <returns>Task</returns>
    [Test]
    public async Task TestGetOrCreatePreventsCacheStorm()
    {
        ManualResetEvent evt = new(false);
        List<Task> tasks = new();
        int cacheMiss = 0;
        for (int i = 0; i < 1000; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await AsTask(evt);
                await layeredCache.GetOrCreateAsync<string>(testKey, TimeSpan.FromMinutes(1.0),
                    async cancelToken =>
                    {
                        await Task.Delay(100, cancelToken);
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
        await layeredCache.GetOrCreateAsync<string>(testKey, expire,
            cancelToken => Task.FromResult<string>(testValue));
        var foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.EqualTo(testValue));

        // add different key
        await layeredCache.GetOrCreateAsync<string>(testKey2, expire,
            cancelToken => Task.FromResult<string>(testValue2));

        // keys should exist with different values
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Not.Null);
        Assert.That(foundValue, Is.EqualTo(testValue));
        foundValue = await layeredCache.GetAsync<string>(testKey2);
        Assert.That(foundValue, Is.Not.Null);
        Assert.That(foundValue, Is.EqualTo(testValue2));

        // get or create values should expire properly
        utcNow += expire;
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
        utcNow += expire;

        // key should no longer exist
        foundValue = await layeredCache.GetAsync<string>(testKey);
        Assert.That(foundValue, Is.Null);
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
    /// Convert a wait handle to a task with a timeout. Allow multiple waiters to release on a single manual reset event.
    /// </summary>
    /// <param name="handle">Wait handle</param>
    /// <returns>Task</returns>
    private static Task AsTask(WaitHandle handle)
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

    private ISerializer serializer = new JsonSerializer();
    private Dictionary<string, long> fileSpaces = new();
    private MemoryCache memoryCache;
    private MemoryFileCache fileCache;
    private DistributedMemoryCache distributedCache;
    private LayeredCache layeredCache;
    private long freeSpace;
    private long totalSpace;
    private DateTimeOffset utcNow;

    MemoryCacheOptions IOptions<MemoryCacheOptions>.Value => new()
    {
        Clock = this,
        SizeLimit = 100000,
        CompactionPercentage = 0.5,
        ExpirationScanFrequency = TimeSpan.FromMilliseconds(1.0)
    };

    DateTimeOffset ISystemClock.UtcNow => utcNow;

    Task IClockHandler.DelayAsync(System.TimeSpan interval, System.Threading.CancellationToken cancelToken) => Task.Delay(0, cancelToken);

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
