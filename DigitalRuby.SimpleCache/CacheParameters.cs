namespace DigitalRuby.SimpleCache;

/// <summary>
/// Caching parameters that includes duration and size
/// </summary>
public struct CacheParameters
{
	/// <summary>
	/// Default cache duration
	/// </summary>
	public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(30.0);

	/// <summary>
	/// Default cache size of 128
	/// </summary>
	public const int DefaultSize = 128;

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="duration">Duration</param>
	public CacheParameters(TimeSpan duration)
	{
		Duration = duration;
		Size = DefaultSize;
	}

	/// <summary>
	/// Constructor
	/// </summary>
	/// <param name="duration">Duration</param>
	/// <param name="size">Size</param>
	public CacheParameters(TimeSpan duration, int size)
	{
		Duration = duration;
		Size = size;
	}

	/// <summary>
	/// Assign default cache parameters if duration is less than or equal to zero
	/// </summary>
	/// <param name="cacheParameters">Cache parameters</param>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	internal static void AssignDefaultIfNeeded(ref CacheParameters cacheParameters)
	{
		if (cacheParameters.Duration.Ticks <= 0)
		{
			cacheParameters.Duration = DefaultDuration;
		}
	}

	// allow jittering cache in a thread-safe way
	private static readonly ThreadLocal<Random> jitter = new(() => new Random());

	/// <summary>
	/// Jitter the cache duration, ensure that we don't have a bunch of stuff expiring right at the same time
	/// </summary>
	/// <param name="cacheParameters">Cache parameters</param>
	internal static void JitterDuration(ref CacheParameters cacheParameters)
	{
		double upperJitter;
		if (cacheParameters.Duration.TotalMinutes <= 1.0)
		{
			// don't jitter cache times 1 minute or less
			return;
		}
		else if (cacheParameters.Duration.TotalMinutes <= 15.0)
		{
			// up to 15 min
			upperJitter = 1.2;
		}
		else if (cacheParameters.Duration.TotalMinutes <= 60.0)
		{
			// up to 1 hour
			upperJitter = 1.15;
		}
		else if (cacheParameters.Duration.TotalMinutes <= 360.0)
		{
			// up to 6 hours
			upperJitter = 1.1;
		}
		else if (cacheParameters.Duration.TotalMinutes <= 1440.0)
		{
			// up to 24 hours
			upperJitter = 1.05;
		}
		else
		{
			// > 1 day
			upperJitter = 1.025;
		}

		double randomDouble = jitter.Value!.NextDouble();
		double multiplier = 1.0 + (randomDouble * upperJitter);
		long jitteredTicks = (long)(cacheParameters.Duration.Ticks * multiplier);
		cacheParameters.Duration = TimeSpan.FromTicks(jitteredTicks);
	}

	/// <summary>
	/// Implicit operator of cache parameters to tuple
	/// </summary>
	/// <param name="value">Value</param>
	public static implicit operator ValueTuple<TimeSpan, int>(in CacheParameters value) => new(value.Duration, value.Size);

	/// <summary>
	/// Implicit operator of int seconds to cache parameters
	/// </summary>
	/// <param name="seconds">Seconds to cache</param>
	public static implicit operator CacheParameters(in int seconds) => new(TimeSpan.FromSeconds(seconds), DefaultSize);

	/// <summary>
	/// Implicit operator of TimeSpan to cache parameters
	/// </summary>
	/// <param name="timespan">Timespan</param>
	public static implicit operator CacheParameters(in TimeSpan timespan) => new(timespan, DefaultSize);

	/// <summary>
	/// Implicit operator of tuple of timespan, int size to cache parameters
	/// </summary>
	/// <param name="tuple">Value</param>
	public static implicit operator CacheParameters(in ValueTuple<TimeSpan, int> tuple) => new(tuple.Item1, tuple.Item2);

	/// <summary>
	/// Implicit operator of tuple of int seconds, int size to cache parameters
	/// </summary>
	/// <param name="tuple">Value</param>
	public static implicit operator CacheParameters(in ValueTuple<int, int> tuple) => new(TimeSpan.FromSeconds(tuple.Item1), tuple.Item2);

	/// <summary>
	/// Default cache parameters
	/// </summary>
	public static readonly CacheParameters Default = new(DefaultDuration);

	/// <summary>
	/// The duration before the cache item expires.
	/// </summary>
	public TimeSpan Duration { get; private set; }

	/// <summary>
	/// The estimated size, in bytes, of the cached object. Default is 128. Ignored for some cache like distributed caches.
	/// </summary>
	public int Size { get; private set; }
}
