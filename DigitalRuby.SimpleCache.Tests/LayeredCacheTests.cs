namespace DigitalRuby.SimpleCache.Tests;

/// <summary>
/// Tests for layered cache
/// </summary>
[TestFixture]
public sealed class LayeredCacheTests : IDiskSpace, IClockHandler, IOptions<MemoryCacheOptions>, ISystemClock
{
    private ISerializer serializer = new JsonSerializer();
    private Dictionary<string, long> fileSpaces = new();
    private MemoryCache memoryCache;
    private FileCache fileCache;
    private DistributedMemoryCache distributedCache;
    private LayeredCache layeredCache;
    private long freeSpace;
    private long totalSpace;
    private DateTimeOffset utcNow;

    [SetUp]
    public void Setup()
    {
        fileSpaces.Clear();
        memoryCache = new(this);
        freeSpace = 10000;
        totalSpace = 100000;
        utcNow = new DateTimeOffset(2022, 1, 1, 1, 1, 1, TimeSpan.Zero);
        fileCache = new FileCache(new FileCacheOptions(), serializer, this, this, new NullLogger<FileCache>());
        distributedCache = new DistributedMemoryCache(this);
        layeredCache = new LayeredCache(new LayeredCacheOptions { KeyPrefix = "test" }, serializer,
            memoryCache, fileCache, distributedCache, new NullLogger<LayeredCache>());
    }

    MemoryCacheOptions IOptions<MemoryCacheOptions>.Value => new() { Clock = this };

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
