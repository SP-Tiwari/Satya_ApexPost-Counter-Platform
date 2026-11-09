using System.Net;
using System.Text.Json;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Persistence;
using ApexPost.Courier.Sync;
using ApexPost.Courier.Sync.Tests;

namespace ApexPost.Courier.Console.Tests;

// D11 (with D7 and D10): the console screens, driven by a script, over the
// production client composition (AddCourierSync) with a stub API, a fake
// network, a fake clock and in-memory token and scan stores.
public sealed class CourierConsoleTests
{
	private const string A = "APX-00000401";
	private const string B = "APX-00000402";

	[Fact]
	public async Task Login_WithADevelopmentToken_StoresIt_AndShowsTheCourierScreens()
	{
		var issued = TestJwt.Courier();
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accessToken":"{{issued}}","tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00Z"}"""));

		var transcript = await RunAsync(host, "1", "COURIER-DEMO-07", "6");

		Assert.Contains("not signed in", transcript);
		Assert.Contains("1. Login (request a development token)", transcript);
		Assert.Contains("Signed in as COURIER-DEMO-07.", transcript);
		Assert.Contains("signed in as COURIER-DEMO-07", transcript);
		Assert.Contains("1. New Scan", transcript);
		Assert.Contains("Goodbye.", transcript);
		Assert.Equal(issued, host.Tokens.Token!.Value);
		Assert.DoesNotContain(issued, transcript);
	}

	[Fact]
	public async Task Login_WithAPastedToken_StoresIt_WithoutShowingIt()
	{
		using var host = new ClientHost();
		var token = TestJwt.Courier("COURIER-DEMO-12");

		var transcript = await RunAsync(host, "2", token, "6");

		Assert.Contains("Paste the access token (it is not shown):", transcript);
		Assert.Contains("Signed in as COURIER-DEMO-12.", transcript);
		Assert.Equal(token, host.Tokens.Token!.Value);
		Assert.DoesNotContain(token, transcript);
	}

	[Theory]
	[InlineData("not-a-token", "Login failed: This is not an access token: a JWT has three parts separated by dots.")]
	[InlineData("expired", "Login failed: The access token has expired.")]
	[InlineData("admin", "Login failed: The access token is not a Courier's token.")]
	public async Task Login_WithAnUnusableToken_StaysOnTheLoginScreen(string kind, string message)
	{
		using var host = new ClientHost();
		var token = kind switch
		{
			"expired" => TestJwt.Create("COURIER-DEMO-07", TestValues.Now.AddMinutes(-1), "Courier"),
			"admin" => TestJwt.Create(null, TestValues.Now.AddHours(1), "Admin"),
			_ => kind
		};

		var transcript = await RunAsync(host, "2", token, "5");

		Assert.Contains(message, transcript);
		Assert.DoesNotContain("signed in as", transcript);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public async Task NewScan_IsSavedLocally_UnderTheSignedInCourier_AndThenSynced()
	{
		using var host = SignedIn(Accept);

		var transcript = await RunAsync(host, "1", A, "2", "Pune hub", "Front desk", "6");

		var scan = Assert.Single(host.Scans.ListAll());
		Assert.Contains($"Saved {scan.Id} on this device as Pending.", transcript);
		Assert.Equal((A, ScanType.InTransit, TestValues.CourierId, "Pune hub", "Front desk"), (scan.TrackingNumber, scan.ScanType, scan.CourierId, scan.Location, scan.Notes));

		// The save started a sync in the background; wait for it (or join it).
		await host.Get<SyncCoordinator>().SyncNowAsync();
		Assert.Equal(SyncStatus.Synced, host.Scans.StatusOf(scan));
		Assert.Contains(host.Network.Requests, request => request.Uri.AbsolutePath.Contains(A));
	}

	[Fact]
	public async Task NewScan_WhileOffline_IsSaved_AndNothingIsSent()
	{
		using var host = SignedIn(Accept);
		host.Connectivity.IsAvailable = false;

		var transcript = await RunAsync(host, "1", A, "Pickup", "Pune hub", "", "6");

		Assert.Contains("on this device as Pending.", transcript);
		Assert.Equal(SyncStatus.Pending, Assert.Single(host.Scans.ListAll()).SyncStatus);
		Assert.Empty(host.Network.Requests);
	}

	[Fact]
	public async Task NewScan_WithAMissingValue_ShowsAnError_AndSavesNothing()
	{
		using var host = SignedIn(Accept);

		var transcript = await RunAsync(host, "1", "", "6");

		Assert.Contains("Error: A value is required.", transcript);
		Assert.Empty(host.Scans.ListAll());
	}

	[Fact]
	public async Task PendingScans_ShowsTheListAndTheLiveCount()
	{
		using var host = SignedIn(Accept);
		host.Connectivity.IsAvailable = false;
		TestScans.Store(host.Scans, TestScans.For(A, 1), TestScans.For(B, 2));

		var transcript = await RunAsync(
			host,
			console => console.OnScansChanged(new ScanChangeNotification(["scan::x"], 2)),
			"2", "6");

		Assert.Contains("pending scans (live): 2", transcript);
		Assert.Contains("Pending scans, oldest first: 2", transcript);
		Assert.Contains(A, transcript);
		Assert.Contains(B, transcript);
	}

	[Fact]
	public async Task SyncNow_UploadsThePendingScans_AndReportsTheResult()
	{
		using var host = SignedIn(Accept);
		host.Connectivity.IsAvailable = false;
		TestScans.Store(host.Scans, TestScans.For(A, 1));

		var transcript = await RunAsync(host, "4", "6");

		Assert.Contains("Sync finished: 1 attempted, 1 synced, 0 failed, 0 still pending.", transcript);
		Assert.Empty(host.Scans.ListPending());
	}

	[Fact]
	public async Task SyncStatus_ShowsTheLastSync_TheCircuitState_AndTheFailures()
	{
		var scan = TestScans.For(A, 1);
		using var host = SignedIn(_ => Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accepted":0,"duplicatesSkipped":0,"rejected":1,"rejections":[{"scanId":"{{scan.Id}}","reason":"The scan ID is already recorded for another shipment."}]}"""));
		host.Connectivity.IsAvailable = false;
		TestScans.Store(host.Scans, scan);

		var transcript = await RunAsync(host, "4", "3", "6");

		Assert.Contains("Last sync: 2026-11-10 09:00:00Z", transcript);
		Assert.Contains("Last result: 1 attempted, 0 synced, 1 failed, 0 still pending.", transcript);
		Assert.Contains("Circuit breaker: closed (the API is answering)", transcript);
		Assert.Contains("Sync in progress: no", transcript);
		Assert.Contains($"{A} {scan.Id}: The scan ID is already recorded for another shipment.", transcript);
	}

	[Fact]
	public async Task Unauthorized_ClearsTheToken_ReturnsToLogin_AndKeepsTheScans()
	{
		using var host = SignedIn(_ => Responses.Empty(HttpStatusCode.Unauthorized));
		host.Connectivity.IsAvailable = false;
		TestScans.Store(host.Scans, TestScans.For(A, 1));

		var transcript = await RunAsync(host, "4", "5");

		Assert.Contains("Your sign-in was refused or has expired. Please log in again; scans stay on this device.", transcript);
		Assert.Contains("1 still pending. Stopped: Your sign-in is missing, invalid or expired. Sign in again.", transcript);
		var afterSync = transcript[transcript.LastIndexOf("Sync finished", StringComparison.Ordinal)..];
		Assert.Contains("not signed in", afterSync);
		Assert.Contains("1. Login (request a development token)", afterSync);
		Assert.Null(host.Tokens.Token);
		Assert.Single(host.Scans.ListPending());

		// While signed out, nothing triggers another (unauthenticated) request.
		host.Connectivity.IsAvailable = true;
		host.Get<SyncCoordinator>().SyncIfOnline();
		Assert.Null(await host.Get<SyncCoordinator>().SyncNowAsync());
		Assert.Single(host.Network.Requests);
	}

	[Fact]
	public async Task CircuitOpen_ShowsTheBanner_ScansAreStillSaved_AndSyncNowRecovers()
	{
		var serverUp = false;
		using var host = SignedIn(request => serverUp ? Accept(request) : Responses.Empty(HttpStatusCode.ServiceUnavailable));
		host.Connectivity.IsAvailable = false;
		TestScans.Store(host.Scans, TestScans.For(A, 1));

		var transcript = await RunAsync(
			host,
			"4",
			"4",
			"1", B, "Delivered", "Pune hub", "",
			Then(() =>
			{
				serverUp = true;
				host.Clock.Advance(CourierResilience.BreakDuration);
			}),
			"4",
			"6");

		var opened = transcript.IndexOf(CourierConsole.OfflineBanner, StringComparison.Ordinal);
		var saved = transcript.IndexOf("on this device as Pending.", StringComparison.Ordinal);
		var recovered = transcript.IndexOf("Server available again", StringComparison.Ordinal);
		Assert.True(opened >= 0 && saved > opened && recovered > saved, transcript);
		Assert.Contains("Sync finished: 2 attempted, 2 synced, 0 failed, 0 still pending.", transcript);
		Assert.DoesNotContain(CourierConsole.OfflineBanner, transcript[recovered..]);
		Assert.Empty(host.Scans.ListPending());
	}

	[Fact]
	public async Task Navigation_InvalidChoices_Logout_AndEndOfInput()
	{
		using var host = SignedIn(Accept);
		host.Connectivity.IsAvailable = false;

		var transcript = await RunAsync(host, "9", "5", "7");

		Assert.Contains("Please choose 1 to 6.", transcript);
		Assert.Contains("Signed out.", transcript);
		Assert.Contains("Please choose 1 to 5.", transcript);
		Assert.DoesNotContain("Goodbye.", transcript);
		Assert.Null(host.Tokens.Token);
	}

	[Fact]
	public async Task SignedOut_PendingScansAndSyncStatus_AreStillAvailable()
	{
		using var host = new ClientHost();
		host.Connectivity.IsAvailable = false;
		TestScans.Store(host.Scans, TestScans.For(A, 1));

		var transcript = await RunAsync(host, "3", "4", "5");

		Assert.Contains("Pending scans, oldest first: 1", transcript);
		Assert.Contains("Last sync: never", transcript);
		Assert.Contains("Goodbye.", transcript);
	}

	private static ClientHost SignedIn(Func<HttpRequestMessage, HttpResponseMessage> respond)
	{
		var host = new ClientHost(respond);
		host.Tokens.Token = TestValues.ValidToken(TestJwt.Courier());
		return host;
	}

	private static Task<string> RunAsync(ClientHost host, params object[] script) =>
		RunAsync(host, null, script);

	// Runs the console over the script: each string is one line of input, and
	// each Action runs when the console asks for the next line after it.
	private static async Task<string> RunAsync(ClientHost host, Action<CourierConsole>? before, params object[] script)
	{
		var input = new ScriptedInput(script);
		var output = new StringWriter();
		var console = new CourierConsole(
			input,
			output,
			new Lock(),
			host.Scans,
			host.Session,
			host.Get<CourierSignIn>,
			host.Get<SyncCoordinator>(),
			host.State,
			host.Clock,
			host.Scans.ListPending().Count,
			input.ReadLine);
		before?.Invoke(console);

		await console.RunAsync();
		return output.ToString();
	}

	private static Action Then(Action action) => action;

	private static HttpResponseMessage Accept(HttpRequestMessage request)
	{
		var count = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result)
			.RootElement.GetProperty("scans").GetArrayLength();
		return Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accepted":{{count}},"duplicatesSkipped":0,"rejected":0,"rejections":[]}""");
	}

	private sealed class ScriptedInput(IEnumerable<object> script) : TextReader
	{
		private readonly Queue<object> _script = new(script);

		public override string? ReadLine()
		{
			while (_script.TryDequeue(out var next))
			{
				if (next is Action action)
					action();
				else
					return (string)next;
			}

			return null;
		}
	}
}
