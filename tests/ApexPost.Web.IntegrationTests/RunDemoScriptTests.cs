using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ApexPost.Web.IntegrationTests;

// Runs the real run-demo.ps1 with stand-ins for dotnet and sqlcmd first on the PATH.
// They only record their calls, and sqlcmd answers with fixed file paths, so these
// tests cannot reach a database, even if a check in the script breaks.
public sealed class RunDemoScriptTests : IDisposable
{
	private const int DemoPort = 7034;
	private const string EnvironmentPassword = "fictional-environment-password";
	private const string StoredPassword = "fictional-stored-password";
	private const string StoredConnection = "Server=stored;Database=StoredDb";
	private const string Disposable = "Server=.;Database=ApexPost_Test_Demo;Integrated Security=True";
	private const string ApprovedData = @"E:\SqlData\ApexPost\Tests\ApexPost_Test_Demo.mdf";
	private const string ApprovedLog = @"E:\SqlData\ApexPost\Tests\ApexPost_Test_Demo_log.ldf";

	private readonly string _directory = Directory.CreateTempSubdirectory("ApexPostRunDemo").FullName;

	public RunDemoScriptTests()
	{
		File.WriteAllLines(Path.Combine(_directory, "secrets.txt"),
		[
			$"ConnectionStrings:ApexPostDb = {StoredConnection}",
			$"DemoData:StaffPassword = {StoredPassword}"
		]);
		File.WriteAllLines(Path.Combine(_directory, "dotnet.cmd"),
		[
			"@echo off",
			"echo %*>>\"%FAKE_DOTNET_LOG%\"",
			"echo app-database=%ConnectionStrings__ApexPostDb%>>\"%FAKE_DOTNET_LOG%\"",
			"echo app-password=%DemoData__StaffPassword%>>\"%FAKE_DOTNET_LOG%\"",
			"if \"%1\"==\"user-secrets\" if \"%2\"==\"list\" type \"%FAKE_DOTNET_SECRETS%\"",
			"exit /b 0"
		]);
		File.WriteAllLines(Path.Combine(_directory, "sqlcmd.cmd"),
		[
			"@echo off",
			"echo sqlcmd %*>>\"%FAKE_DOTNET_LOG%\"",
			"type \"%FAKE_SQLCMD_FILES%\"",
			"exit /b %FAKE_SQLCMD_EXIT%"
		]);
		UseDatabaseFiles(ApprovedData, ApprovedLog);
	}

	[Theory]
	[InlineData("-Reset", "needs -ConnectionString")]
	[InlineData("-ConnectionString|Server=.;Database=ApexPostDb", "refuses it")]
	[InlineData("-ConnectionString|Server=.;Initial Catalog=apexpostdb", "refuses it")]
	[InlineData("-Reset|-ConnectionString|Server=.;Database=ApexPostDb", "refuses it")]
	[InlineData("-ConnectionString|Server=.;Database=master", "must start with")]
	[InlineData("-ConnectionString|Server=.;Database=apexpost_test_lowercase", "must start with")]
	[InlineData("-ConnectionString|Server=.;Integrated Security=True", "must name a database")]
	[InlineData("-ConnectionString|Server=.;Database=ApexPost_Test_A;Initial Catalog=ApexPostDb", "refuses it")]
	[InlineData("-ConnectionString|Server=.;Database=ApexPost_Test_A;AttachDbFilename=other.mdf", "not accepted")]
	[InlineData("-ConnectionString|Server=.;Database=ApexPost_Test_A;User ID=someone", "Windows authentication")]
	[InlineData("-ConnectionString|Database=ApexPost_Test_A;Integrated Security=True", "must name a server")]
	public async Task RejectedRequest_StopsBeforeAnyDotnetCall(string arguments, string message)
	{
		var result = await RunAsync(EnvironmentPassword, arguments.Split('|'));

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains(message, Flatten(result.Output), StringComparison.Ordinal);
		Assert.Empty(result.Calls);
	}

	[Fact]
	public async Task ConnectionString_WithoutThePasswordVariable_StopsBeforeAnyDotnetCall()
	{
		var result = await RunAsync(null, "-ConnectionString", Disposable);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("DemoData__StaffPassword", result.Output, StringComparison.Ordinal);
		Assert.Empty(result.Calls);
	}

	[Fact]
	public async Task PasswordParameter_WithThePasswordVariable_StopsBeforeAnyDotnetCall()
	{
		var result = await RunAsync(EnvironmentPassword, "-Password", "another-password");

		Assert.NotEqual(0, result.ExitCode);
		Assert.Empty(result.Calls);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task PortInUse_StopsBeforeTheMigrations(bool disposable)
	{
		using var holder = HoldDemoPort();

		var result = disposable
			? await RunAsync(EnvironmentPassword, "-ConnectionString", Disposable)
			: await RunAsync(null);

		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains($"Port {DemoPort}", result.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("ef ", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("run ", StringComparison.Ordinal));
	}

	// A demo already running here makes these two stop at the port check instead,
	// which PortInUse_StopsBeforeTheMigrations covers.
	[Fact]
	public async Task DefaultPath_UsesTheStoredConnectionAndPassword()
	{
		if (DemoPortIsInUse())
			return;

		var result = await RunAsync(null);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains(
			result.Calls,
			call => call.StartsWith($"ef database update --connection {StoredConnection} ", StringComparison.Ordinal));
		Assert.Contains(result.Calls, call => call.StartsWith("run ", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("user-secrets set", StringComparison.Ordinal));
		Assert.Contains($"Password: {StoredPassword}", result.Output, StringComparison.Ordinal);
		Assert.Equal(["app-database="], [.. result.Log.Where(line => line.StartsWith("app-database=", StringComparison.Ordinal)).Distinct()]);
	}

	[Fact]
	public async Task ConnectionString_ReachesTheMigrationsAndApp_AndLeavesSecretsAlone()
	{
		if (DemoPortIsInUse())
			return;

		var result = await RunAsync(EnvironmentPassword, "-Reset", "-ConnectionString", Disposable);

		Assert.Equal(0, result.ExitCode);
		var migrations = result.Calls.Where(call => call.StartsWith("ef ", StringComparison.Ordinal)).ToArray();
		Assert.Equal(2, migrations.Length);
		Assert.StartsWith($"ef database update 0 --connection \"{Disposable}\"", migrations[0], StringComparison.Ordinal);
		Assert.StartsWith($"ef database update --connection \"{Disposable}\"", migrations[1], StringComparison.Ordinal);
		Assert.Equal(
			$"app-database={Disposable}",
			result.Log[Array.FindIndex(result.Log, line => line.StartsWith("run ", StringComparison.Ordinal)) + 1]);
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("user-secrets", StringComparison.Ordinal));
		Assert.DoesNotContain(EnvironmentPassword, result.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(StoredPassword, result.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(Disposable, result.Output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ConnectionString_ChecksTheLocationThroughMasterBeforeMigrating()
	{
		if (DemoPortIsInUse())
			return;

		var result = await RunAsync(EnvironmentPassword, "-ConnectionString", Disposable);

		var calls = result.Calls.ToList();
		var check = calls.FindIndex(call => call.StartsWith("sqlcmd ", StringComparison.Ordinal));
		Assert.True(check >= 0, "The location was not checked.");
		Assert.True(check < calls.FindIndex(call => call.StartsWith("ef ", StringComparison.Ordinal)));
		Assert.Contains("-d master", calls[check], StringComparison.Ordinal);
		Assert.Contains("sys.master_files", calls[check], StringComparison.Ordinal);
		Assert.Contains("sys.databases", calls[check], StringComparison.Ordinal);
		Assert.DoesNotContain("ApexPostDb", calls[check], StringComparison.Ordinal);
		Assert.Single(calls, call => call.StartsWith("sqlcmd ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ApprovedDirectory_IsMatchedWithoutRegardToCase()
	{
		if (DemoPortIsInUse())
			return;
		UseDatabaseFiles(@"e:\sqldata\apexpost\tests\a.mdf", @"E:\SQLDATA\APEXPOST\TESTS\a_log.ldf");

		var result = await RunAsync(EnvironmentPassword, "-ConnectionString", Disposable);

		Assert.Equal(0, result.ExitCode);
	}

	[Fact]
	public async Task MissingDatabase_StopsBeforeTheMigrations()
	{
		if (DemoPortIsInUse())
			return;
		UseDatabaseFiles();

		var result = await RunAsync(EnvironmentPassword, "-Reset", "-ConnectionString", Disposable);

		AssertStoppedBeforeMigrating(result, "was not found");
	}

	[Theory]
	[InlineData(@"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA\a.mdf", ApprovedLog)]
	[InlineData(ApprovedData, @"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA\a_log.ldf")]
	[InlineData(@"E:\SqlData\ApexPost\a.mdf", ApprovedLog)]
	[InlineData(@"E:\SqlData\Tests\a.mdf", ApprovedLog)]
	[InlineData(@"E:\SqlData\ApexPost\Tests\nested\a.mdf", ApprovedLog)]
	[InlineData(@"E:\SqlData\ApexPost\Tests\..\a.mdf", ApprovedLog)]
	[InlineData(@"E:\SqlData\ApexPost\TestsOther\a.mdf", ApprovedLog)]
	public async Task FileOutsideTheApprovedDirectory_StopsBeforeTheMigrations(string data, string log)
	{
		if (DemoPortIsInUse())
			return;
		UseDatabaseFiles(data, log);

		var result = await RunAsync(EnvironmentPassword, "-Reset", "-ConnectionString", Disposable);

		AssertStoppedBeforeMigrating(result, "outside");
	}

	[Fact]
	public async Task FailedLocationCheck_StopsBeforeTheMigrations()
	{
		if (DemoPortIsInUse())
			return;

		var result = await RunAsync(EnvironmentPassword, 1, ["-ConnectionString", Disposable]);

		AssertStoppedBeforeMigrating(result, "could not be checked");
	}

	// The application reads the variable ahead of user-secrets, so the script must
	// not show the stored password as the login.
	[Fact]
	public async Task DefaultPath_PasswordVariable_TakesPrecedenceOverUserSecrets()
	{
		if (DemoPortIsInUse())
			return;

		var result = await RunAsync(EnvironmentPassword);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("from DemoData__StaffPassword", result.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(EnvironmentPassword, result.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(StoredPassword, result.Output, StringComparison.Ordinal);
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("user-secrets set", StringComparison.Ordinal));
		Assert.Contains(
			result.Calls,
			call => call.StartsWith($"ef database update --connection {StoredConnection} ", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("sqlcmd ", StringComparison.Ordinal));
		Assert.Equal(
			$"app-password={EnvironmentPassword}",
			result.Log[Array.FindIndex(result.Log, line => line.StartsWith("run ", StringComparison.Ordinal)) + 2]);
	}

	public void Dispose() => Directory.Delete(_directory, recursive: true);

	private void UseDatabaseFiles(params string[] files) =>
		File.WriteAllLines(Path.Combine(_directory, "files.txt"), files);

	private static void AssertStoppedBeforeMigrating(ScriptResult result, string message)
	{
		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains(message, Flatten(result.Output), StringComparison.Ordinal);
		Assert.Contains(result.Calls, call => call.StartsWith("sqlcmd ", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("ef ", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Calls, call => call.StartsWith("run ", StringComparison.Ordinal));
	}

	private Task<ScriptResult> RunAsync(string? password, params string[] arguments) =>
		RunAsync(password, 0, arguments);

	private async Task<ScriptResult> RunAsync(string? password, int sqlcmdExitCode, string[] arguments)
	{
		var log = Path.Combine(_directory, "calls.log");
		var start = new ProcessStartInfo("powershell.exe")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var argument in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), .. arguments])
			start.ArgumentList.Add(argument);
		start.Environment["PATH"] = $"{_directory};{Environment.GetEnvironmentVariable("PATH")}";
		start.Environment["FAKE_DOTNET_LOG"] = log;
		start.Environment["FAKE_DOTNET_SECRETS"] = Path.Combine(_directory, "secrets.txt");
		start.Environment["FAKE_SQLCMD_FILES"] = Path.Combine(_directory, "files.txt");
		start.Environment["FAKE_SQLCMD_EXIT"] = sqlcmdExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
		start.Environment.Remove("ConnectionStrings__ApexPostDb");
		start.Environment.Remove("DemoData__StaffPassword");
		if (password is not null)
			start.Environment["DemoData__StaffPassword"] = password;

		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();

		var lines = File.Exists(log) ? await File.ReadAllLinesAsync(log) : [];
		return new ScriptResult(process.ExitCode, await output + await error, lines);
	}

	// PowerShell wraps long error lines.
	private static string Flatten(string output) =>
		string.Join(' ', output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));

	private static TcpListener? HoldDemoPort()
	{
		if (DemoPortIsInUse())
			return null;
		var listener = new TcpListener(IPAddress.Loopback, DemoPort);
		listener.Start();
		return listener;
	}

	private static bool DemoPortIsInUse() =>
		IPGlobalProperties.GetIPGlobalProperties()
			.GetActiveTcpListeners()
			.Any(listener => listener.Port == DemoPort);

	private static string ScriptPath()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApexPost.slnx")))
			directory = directory.Parent;
		return Path.Combine(
			directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."),
			"scripts",
			"run-demo.ps1");
	}

	private sealed record ScriptResult(int ExitCode, string Output, string[] Log)
	{
		public string[] Calls { get; } = [.. Log.Where(line => !line.StartsWith("app-", StringComparison.Ordinal))];
	}
}
