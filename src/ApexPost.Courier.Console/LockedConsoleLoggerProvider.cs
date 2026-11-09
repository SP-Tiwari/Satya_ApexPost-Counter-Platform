using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Console;

// Writes log messages (the retry and circuit-breaker messages of D7, sync
// warnings) to the console output under the menu's lock, so a message from a
// background sync never splits a line the menu is writing. Levels are filtered
// by the Logging section of appsettings.json. Only the exception's type and
// message are written, never a stack trace; the client's messages carry no
// token or request body (SECURITY.md).
internal sealed class LockedConsoleLoggerProvider(TextWriter output, Lock outputLock) : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) =>
		new LockedConsoleLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..], output, outputLock);

	public void Dispose()
	{
	}

	private sealed class LockedConsoleLogger(string category, TextWriter output, Lock outputLock) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			var level = logLevel switch
			{
				LogLevel.Information => "info",
				LogLevel.Warning => "warn",
				LogLevel.Error => "fail",
				LogLevel.Critical => "crit",
				_ => logLevel.ToString().ToLowerInvariant()
			};

			lock (outputLock)
			{
				output.WriteLine($"[{level}] {category}: {formatter(state, exception)}");
				if (exception is not null)
					output.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
			}
		}
	}
}
