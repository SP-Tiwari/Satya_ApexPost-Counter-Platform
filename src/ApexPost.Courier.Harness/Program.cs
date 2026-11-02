using ApexPost.Courier.Harness;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Persistence;
using ApexPost.Courier.Offline.Uploading;
using Couchbase.Lite.Logging;

// Composition root for the courier offline harness. The database lives in the
// user's local application data folder unless --data-dir is given, so it never
// depends on the folder the harness was started from.
var dataDirectory = ReadDataDirectory(args)
	?? Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"ApexPost",
		"CourierOffline");

// Couchbase Lite logs to the console by default; keep only its errors so they
// do not bury the menu. This is a process-wide setting, so it belongs here.
LogSinks.Console = new ConsoleLogSink(LogLevel.Error);

var output = Console.Out;
var outputLock = new Lock();

try
{
	using var database = new CourierDatabase(
		dataDirectory,
		new HarnessLogger<CourierDatabase>(output, outputLock));
	var scans = new CouchbaseScanRepository(database);
	var outboxLogger = new HarnessLogger<OutboxService>(output, outputLock);

	var harness = new CourierHarness(
		Console.In,
		output,
		outputLock,
		scans,
		rejects => new OutboxService(scans, new FakeShipmentUploader(rejects), outboxLogger),
		TimeProvider.System,
		database.CountPendingScans());

	database.StartChangeListener(harness.OnScansChanged);
	await harness.RunAsync();

	// Disposing the database removes the listener and closes the database.
	return 0;
}
catch (InvalidOperationException exception)
{
	Console.Error.WriteLine(exception.Message);
	return 1;
}

static string? ReadDataDirectory(string[] args)
{
	var index = Array.IndexOf(args, "--data-dir");
	return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
}
