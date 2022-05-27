namespace DigitalRuby.SimpleCache;

/// <summary>
/// Allows mocking current date/time and delays
/// </summary>
public interface IDateTimeProvider
{
	/// <summary>
	/// Current date/time
	/// </summary>
	DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	/// <summary>
	/// Delay for a set amount of time
	/// </summary>
	/// <param name="interval">Interval to delay</param>
	/// <param name="cancelToken">Cancel token</param>
	/// <returns>Task</returns>
	Task DelayAsync(TimeSpan interval, CancellationToken cancelToken = default) => Task.Delay(interval, cancelToken);
}
