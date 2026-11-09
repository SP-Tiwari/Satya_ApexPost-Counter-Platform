using System.Text;
using ApexPost.Courier.Console;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Persistence;
using ApexPost.Courier.Sync;
using Couchbase.Lite.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Composition root of the courier console client (Week 4 Part D), the
// console substitute for the .NET MAUI app (ADR 0030).
// - API root: ApexPostApi:BaseUrl from appsettings.json, the ApexPostApi__BaseUrl
//   environment variable or --ApexPostApi:BaseUrl=... on the command line.
// - Device database: Courier:DataDirectory, by default the folder the Part A
//   harness uses, %LOCALAPPDATA%\ApexPost\CourierOffline. Only one process can
//   open it at a time.
// The content root is the app's own folder, so appsettings.json is found from
// any working directory. System.Console is named in full: inside the
// ApexPost.Courier.Console namespace, "Console" means that namespace.
const string DataDirectoryKey = "Courier:DataDirectory";

var output = System.Console.Out;
var outputLock = new Lock();

// Couchbase Lite logs to the console by default; keep only its errors so they
// do not bury the menu.
LogSinks.Console = new ConsoleLogSink(Couchbase.Lite.Logging.LogLevel.Error);

try
{
	var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
	{
		Args = args,
		ContentRootPath = AppContext.BaseDirectory
	});
	builder.Logging.ClearProviders();
	builder.Logging.AddProvider(new LockedConsoleLoggerProvider(output, outputLock));

	var dataDirectory = builder.Configuration[DataDirectoryKey] is { Length: > 0 } configured
		? Path.GetFullPath(configured)
		: Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"ApexPost",
			"CourierOffline");
	builder.Services.AddSingleton(provider => new CourierDatabase(
		dataDirectory,
		provider.GetRequiredService<ILogger<CourierDatabase>>()));
	builder.Services.AddSingleton<IScanRepository>(provider =>
		new CouchbaseScanRepository(provider.GetRequiredService<CourierDatabase>()));
	builder.Services.AddCourierSync(builder.Configuration);

	// Disposed asynchronously at the end, because SyncCoordinator is only
	// IAsyncDisposable. Services are disposed in reverse order of creation, so
	// the coordinator (created after the database) cancels a running sync
	// before the database closes.
	var host = builder.Build();
	try
	{
		var database = host.Services.GetRequiredService<CourierDatabase>();
		var coordinator = host.Services.GetRequiredService<SyncCoordinator>();

		var courierConsole = new CourierConsole(
			System.Console.In,
			output,
			outputLock,
			host.Services.GetRequiredService<IScanRepository>(),
			host.Services.GetRequiredService<CourierSession>(),
			host.Services.GetRequiredService<CourierSignIn>,
			coordinator,
			host.Services.GetRequiredService<SyncState>(),
			host.Services.GetRequiredService<TimeProvider>(),
			database.CountPendingScans(),
			ReadSecret);

		database.StartChangeListener(courierConsole.OnScansChanged);
		coordinator.Start();
		await courierConsole.RunAsync();
		return 0;
	}
	finally
	{
		await ((IAsyncDisposable)host).DisposeAsync();
	}
}
catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
{
	System.Console.Error.WriteLine(exception.Message);
	return 1;
}

// Reads a pasted token without showing it: each key is read without echo and
// shown as '*'. Redirected input (a script) is read as a plain line.
static string? ReadSecret()
{
	if (System.Console.IsInputRedirected)
		return System.Console.ReadLine();

	var secret = new StringBuilder();
	while (true)
	{
		var key = System.Console.ReadKey(intercept: true);
		if (key.Key == ConsoleKey.Enter)
		{
			System.Console.WriteLine();
			return secret.ToString();
		}

		if (key.Key == ConsoleKey.Backspace)
		{
			if (secret.Length > 0)
			{
				secret.Length--;
				System.Console.Write("\b \b");
			}
		}
		else if (!char.IsControl(key.KeyChar))
		{
			secret.Append(key.KeyChar);
			System.Console.Write('*');
		}
	}
}
