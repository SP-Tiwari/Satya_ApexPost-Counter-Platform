using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Harness;

// Writes log lines straight to the harness output, under the same lock as the
// menu, so a listener line from Couchbase Lite's background thread never splits
// a line the menu is writing.
internal sealed class HarnessLogger<T>(TextWriter output, Lock outputLock) : ILogger<T>
{
	private static readonly string Category = typeof(T).Name;

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
			return;

		var level = logLevel switch
		{
			LogLevel.Information => "info",
			LogLevel.Warning => "warn",
			LogLevel.Error => "fail",
			_ => logLevel.ToString().ToLowerInvariant()
		};

		lock (outputLock)
		{
			output.WriteLine($"[{level}] {Category}: {formatter(state, exception)}");
			if (exception is not null)
				output.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
		}
	}
}
