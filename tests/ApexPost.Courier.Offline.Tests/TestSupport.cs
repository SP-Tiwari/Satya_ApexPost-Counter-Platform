using System.Collections.Concurrent;
using System.Threading.Channels;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Persistence;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Offline.Tests;

// Where the per-test database folders go. A configured folder always wins and is
// never replaced silently: it must be absolute and on a drive that exists.
// Without one, the development folder on E: is used when that drive exists, and
// the normal temporary folder everywhere else.
internal static class CourierTestStorage
{
	public const string DirectoryVariable = "APEXPOST_COURIER_TEST_DATA_DIRECTORY";
	public const string DevelopmentDirectory = @"E:\ApexPostTestData\CourierOffline";
	public const string TempFolderName = "ApexPostCourierTests";

	public static string FromEnvironment() =>
		Resolve(
			Environment.GetEnvironmentVariable(DirectoryVariable),
			Directory.Exists,
			System.IO.Path.GetTempPath());

	public static string Resolve(
		string? configuredDirectory,
		Func<string, bool> directoryExists,
		string tempDirectory)
	{
		if (!string.IsNullOrWhiteSpace(configuredDirectory))
		{
			if (!System.IO.Path.IsPathFullyQualified(configuredDirectory))
				throw new InvalidOperationException(
					$"{DirectoryVariable} must be an absolute folder path.");

			if (!directoryExists(System.IO.Path.GetPathRoot(configuredDirectory)!))
				throw new InvalidOperationException(
					$"The drive for {DirectoryVariable} is unavailable. A configured folder is never replaced by another one.");

			return configuredDirectory;
		}

		return directoryExists(System.IO.Path.GetPathRoot(DevelopmentDirectory)!)
			? DevelopmentDirectory
			: System.IO.Path.Combine(tempDirectory, TempFolderName);
	}
}

// A fresh database folder per test, deleted afterwards. Couchbase Lite runs in
// process against these files; no server or network is involved.
internal sealed class CourierDatabaseFolder : IDisposable
{
	public string Path { get; } = System.IO.Path.Combine(
		CourierTestStorage.FromEnvironment(),
		Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(Path))
			Directory.Delete(Path, recursive: true);
	}
}

internal sealed class CourierTestContext : IDisposable
{
	private readonly CourierDatabaseFolder _folder = new();

	public CourierTestContext()
	{
		Database = new CourierDatabase(_folder.Path, DatabaseLog);
		Scans = new CouchbaseScanRepository(Database);
	}

	public string FolderPath => _folder.Path;
	public RecordingLogger<CourierDatabase> DatabaseLog { get; } = new();
	public CourierDatabase Database { get; private set; }
	public CouchbaseScanRepository Scans { get; private set; }

	public void Reopen()
	{
		Database.Dispose();
		Database = new CourierDatabase(_folder.Path, DatabaseLog);
		Scans = new CouchbaseScanRepository(Database);
	}

	public void Dispose()
	{
		Database.Dispose();
		_folder.Dispose();
	}
}

internal static class Scans
{
	public static readonly DateTimeOffset BaseTime = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

	// Fictional values only.
	public static ShipmentScan New(
		string trackingNumber = "APX-TEST-0001",
		ScanType scanType = ScanType.Pickup,
		string courierId = "COURIER-TEST-01",
		int minutesAfterBase = 0,
		string? notes = null) =>
		ShipmentScan.Record(
			trackingNumber,
			scanType,
			courierId,
			"Test Depot",
			BaseTime.AddMinutes(minutesAfterBase),
			notes);
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
	public ConcurrentQueue<string> Messages { get; } = new();

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter) =>
		Messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
}

// Couchbase Lite delivers change notifications on a background thread, so tests
// wait for them with a timeout instead of assuming they have already arrived.
internal sealed class NotificationRecorder
{
	private readonly Channel<ScanChangeNotification> _pending =
		Channel.CreateUnbounded<ScanChangeNotification>();

	public ConcurrentQueue<ScanChangeNotification> Received { get; } = new();

	public void Record(ScanChangeNotification notification)
	{
		Received.Enqueue(notification);
		_pending.Writer.TryWrite(notification);
	}

	// Couchbase Lite may batch several changes into one notification, so this
	// waits for the first one that satisfies the condition.
	public async Task<ScanChangeNotification> WaitForAsync(
		Func<ScanChangeNotification, bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true)
		{
			var notification = await _pending.Reader.ReadAsync(timeout.Token);
			if (condition(notification))
				return notification;
		}
	}

	public Task<ScanChangeNotification> WaitForDocumentAsync(string documentId) =>
		WaitForAsync(notification => notification.DocumentIds.Contains(documentId));

	public async Task<bool> AnyWithinAsync(TimeSpan period)
	{
		using var timeout = new CancellationTokenSource(period);
		try
		{
			return await _pending.Reader.WaitToReadAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}
}
