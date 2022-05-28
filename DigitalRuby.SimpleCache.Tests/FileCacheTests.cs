namespace DigitalRuby.SimpleCache.Tests;

/// <summary>
/// File cache tests
/// </summary>
public sealed class FileCacheTests : IClockHandler, IDiskSpace
{
	private DateTimeOffset utcNow;

	/// <inheritdoc />
	DateTimeOffset ISystemClock.UtcNow => utcNow;

	/// <inheritdoc />
	Task IClockHandler.DelayAsync(TimeSpan interval, CancellationToken cancelToken)
	{
		return Task.CompletedTask;
	}

	/// <summary>
	/// Mock disk space
	/// </summary>
	/// <param name="path">Path</param>
	/// <param name="availableFreeSpace">Available free space</param>
	/// <param name="totalSpace">Total space</param>
	/// <returns>Percent free</returns>
	double IDiskSpace.GetPercentFreeSpace(string path, out long availableFreeSpace, out long totalSpace)
	{
		availableFreeSpace = (long)(999999999.0 * freeSpace);
		totalSpace = 999999999;
		return freeSpace;
	}
	long IDiskSpace.GetFileSize(string fileName) => 0;

	private double freeSpace = 0.5;

	/// <summary>
	/// Test file cache
	/// </summary>
	[Test]
	public async Task TestFileCache()
	{
		const int testCount = 10;
		const string data = "74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F74956375-DD97-4857-816E-188BC8D4090F";

		utcNow = new DateTimeOffset(2022, 1, 1, 1, 1, 1, TimeSpan.Zero);
		using FileCache fileCache = new(new() { FreeSpaceThreshold = 20 }, new JsonLZ4Serializer(), this, this, new NullLogger<FileCache>());

		var item = await fileCache.GetAsync<string>("key1");
		Assert.That(item, Is.Null);

		await fileCache.SetAsync("key1", data, new CacheParameters(TimeSpan.FromSeconds(5.0)));
		item = await fileCache.GetAsync<string>("key1");
		Assert.That(item, Is.Not.Null);
		Assert.That(item.Item, Is.EqualTo(data));

		// step time, item should expire out
		utcNow += TimeSpan.FromSeconds(6.0);
		await fileCache.CleanupFreeSpaceAsync();
		item = await fileCache.GetAsync<string>("key1");
		Assert.That(item, Is.Null);

		// put item back
		await fileCache.SetAsync("key1", data, new CacheParameters(TimeSpan.FromSeconds(5.0)));

		// remove it
		await fileCache.RemoveAsync("key1");

		// should be gone
		Assert.That(item, Is.Null);

		Stopwatch sw = Stopwatch.StartNew();

		// put in a bunch of items
		List<Task> tasks = new();
		for (int i = 0; i < testCount; i++)
		{
			int iCopy = i;
			tasks.Add(fileCache.SetAsync("key_" + iCopy, "key_" + iCopy + "_" + data, new CacheParameters(TimeSpan.FromSeconds(5.0))));
		}
		await Task.WhenAll(tasks);
		tasks.Clear();
		Console.WriteLine("Write {0} items in {1} ms", testCount, sw.Elapsed.TotalMilliseconds);
		sw.Restart();

		// items should be found
		for (int i = 0; i < testCount; i++)
		{
			int iCopy = i;
			tasks.Add(Task.Run(async () =>
			{
				var item2 = await fileCache.GetAsync<string>("key_" + iCopy);
				Assert.Multiple(() =>
				{
					Assert.That(item2, Is.Not.Null);
					Assert.That("key_" + iCopy + "_" + data, Is.EqualTo(item2!.Item));
				});
			}));
		}
		await Task.WhenAll(tasks);
		tasks.Clear();
		Console.WriteLine("Read {0} items in {1} ms", testCount, sw.Elapsed.TotalMilliseconds);
		sw.Restart();

		// step time, item should expire out
		utcNow += TimeSpan.FromSeconds(6.0);

		for (int i = 0; i < testCount; i++)
		{
			int iCopy = i;
			tasks.Add(Task.Run(async () =>
			{
				var item2 = await fileCache.GetAsync<string>("key_" + iCopy);
				Assert.That(item2, Is.Null);
			}));
		}
		await Task.WhenAll(tasks);
		tasks.Clear();
		Console.WriteLine("Read/Delete {0} items in {1} ms", testCount, sw.Elapsed.TotalMilliseconds);
		sw.Restart();

		// put in a bunch of items
		for (int i = 0; i < testCount; i++)
		{
			int iCopy = i;
			tasks.Add(fileCache.SetAsync("key_" + iCopy, "key_" + iCopy, new CacheParameters(TimeSpan.FromSeconds(5.0))));
		}
		await Task.WhenAll(tasks);
		tasks.Clear();
		Console.WriteLine("Write {0} items in {1} ms", testCount, sw.Elapsed.TotalMilliseconds);
		sw.Restart();

		// set free space too low, items should pop out
		freeSpace = 0.15;
		await fileCache.CleanupFreeSpaceAsync();

		for (int i = 0; i < testCount; i++)
		{
			int iCopy = i;
			tasks.Add(Task.Run(async () =>
			{
				var item2 = await fileCache.GetAsync<string>("key_" + iCopy);
				Assert.That(item2, Is.Null);
			}));
		}
		await Task.WhenAll(tasks);
		Console.WriteLine("Read/Expire {0} items in {1} ms", testCount, sw.Elapsed.TotalMilliseconds);
		sw.Restart();

		// make sure we can clear the entire cache in one shot
		await fileCache.SetAsync("key", "value", TimeSpan.FromSeconds(30.0));
		await fileCache.ClearAsync();
		var result = await fileCache.GetAsync<string>("key");
		Assert.That(result, Is.Null);
	}
}