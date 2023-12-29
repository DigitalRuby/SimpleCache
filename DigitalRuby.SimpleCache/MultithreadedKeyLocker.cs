namespace DigitalRuby.SimpleCache;

/// <summary>
/// Allows locking on keys in a more thread friendly manner than just lock keyword
/// </summary>
/// <remarks>
/// Constructor
/// </remarks>
/// <param name="size">Size of key locks, default of 512 is usually good enough</param>
public sealed class MultithreadedKeyLocker(int size = 512)
{
	private readonly int[] keyLocks = new int[size];

	private readonly struct KeyLockerDisposer(int[] keyLocks, uint index) : IDisposable
	{
		private readonly int[] keyLocks = keyLocks;
		private readonly uint index = index;

        public void Dispose()
		{
			keyLocks[index] = 0;
		}
	}

    /// <summary>
    /// Acquire a lock on a key
    /// </summary>
    /// <param name="key">Key</param>
    /// <returns>Disposable to release the lock</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal IDisposable Lock(string key)
	{
		// faster than lock, especially if many threads or connections are active at once
		uint keyHash = (uint)key.GetHashCode() % (uint)keyLocks.Length;
		int count = 0;
		while (Interlocked.CompareExchange(ref keyLocks[keyHash], 1, 0) == 1)
		{
			if (++count < 10)
			{
				// we are usually talking a few clock cycles to acquire the lock, so just yield once to another thread
				Thread.Yield();
			}
			else if (count < 50)
			{
				Thread.Sleep(1);
			}
			else
			{
				Thread.Sleep(20);
			}
		}
		return new KeyLockerDisposer(keyLocks, keyHash);
	}
}
