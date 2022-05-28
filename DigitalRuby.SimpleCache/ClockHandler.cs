namespace DigitalRuby.SimpleCache;

/// <summary>
/// Allows mocking current date/time and delays
/// </summary>
public interface IClockHandler : ISystemClock
{
	/// <summary>
	/// Delay for a set amount of time
	/// </summary>
	/// <param name="interval">Interval to delay</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	Task DelayAsync(TimeSpan interval, CancellationToken cancelToken = default);
}

/// <summary>
/// Implement IClockHandler
/// </summary>
public sealed class ClockHandler : IClockHandler
{
	/// <inheritdoc />
	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	/// <inheritdoc />
	public Task DelayAsync(TimeSpan interval, CancellationToken cancelToken = default)
    {
		return Task.Delay(interval, cancelToken);
    }
}
