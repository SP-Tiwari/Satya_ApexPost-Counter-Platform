using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Persistence;
using ApexPost.Courier.Sync;
using Polly.CircuitBreaker;

namespace ApexPost.Courier.Console;

// D11: the courier's screens, as console menus instead of MAUI pages.
// - Signed out: Login (request a development token, or paste a token),
//   Pending Scans, Sync Status, Exit.
// - Signed in: New Scan, Pending Scans, Sync Status, Sync Now, Logout, Exit.
// Every screen shows the live pending count (the Part A change listener) and,
// while the circuit breaker is not closed, the "Server unavailable – working
// offline" banner (D7). A 401 clears the token (D10) and the next screen is
// Login. Input and output go through TextReader and TextWriter, so a script
// can drive it; lines written from background threads take the output lock.
internal sealed class CourierConsole(
	TextReader input,
	TextWriter output,
	Lock outputLock,
	IScanRepository scans,
	CourierSession session,
	Func<CourierSignIn> createSignIn,
	SyncCoordinator coordinator,
	SyncState syncState,
	TimeProvider timeProvider,
	int initialPendingCount,
	Func<string?> readSecret)
{
	public const string OfflineBanner = "*** Server unavailable – working offline. New scans are saved on this device. ***";

	private int _livePendingCount = initialPendingCount;
	private int _shownCircuitState = (int)CircuitState.Closed;

	// Called from the Part A change listener with the count it has just queried.
	public void OnScansChanged(ScanChangeNotification change) =>
		Volatile.Write(ref _livePendingCount, change.PendingCount);

	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		session.SignInRequired += OnSignInRequired;
		syncState.Changed += OnSyncStateChanged;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var courierId = session.CourierId;
				WriteMenu(courierId);
				var choice = ReadLine("Choose an option: ")?.Trim();
				if (choice is null)
					return;

				try
				{
					var keepRunning = courierId is null
						? await SignedOutChoiceAsync(choice, cancellationToken).ConfigureAwait(false)
						: await SignedInChoiceAsync(choice, courierId).ConfigureAwait(false);
					if (!keepRunning)
					{
						Write("Goodbye.");
						return;
					}
				}
				catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
				{
					Write($"Error: {exception.Message}");
				}
			}
		}
		finally
		{
			session.SignInRequired -= OnSignInRequired;
			syncState.Changed -= OnSyncStateChanged;
		}
	}

	private async Task<bool> SignedOutChoiceAsync(string choice, CancellationToken cancellationToken)
	{
		switch (choice)
		{
			case "1":
				var courierId = Required("Courier ID: ").Trim();
				AfterSignIn(await createSignIn()
					.RequestDevelopmentTokenAsync(courierId, cancellationToken)
					.ConfigureAwait(false));
				return true;
			case "2":
				Write("Paste the access token (it is not shown):");
				AfterSignIn(createSignIn().UseToken(readSecret() ?? ""));
				return true;
			case "3":
				ListPending();
				return true;
			case "4":
				WriteSyncStatus();
				return true;
			case "5":
				return false;
			default:
				Write("Please choose 1 to 5.");
				return true;
		}
	}

	private async Task<bool> SignedInChoiceAsync(string choice, string courierId)
	{
		switch (choice)
		{
			case "1":
				NewScan(courierId);
				return true;
			case "2":
				ListPending();
				return true;
			case "3":
				WriteSyncStatus();
				return true;
			case "4":
				await SyncNowAsync().ConfigureAwait(false);
				return true;
			case "5":
				session.SignOut();
				Write("Signed out.");
				return true;
			case "6":
				return false;
			default:
				Write("Please choose 1 to 6.");
				return true;
		}
	}

	private void AfterSignIn(SignInResult result)
	{
		Write(result.Succeeded ? result.Message : $"Login failed: {result.Message}");
		if (result.Succeeded)
			coordinator.SyncIfOnline();
	}

	// New Scan: saved locally first as Pending, under the signed-in courier's
	// courierId (the API refuses any other, D2), then synced if online.
	private void NewScan(string courierId)
	{
		var trackingNumber = Required("Tracking number: ");
		var scanType = ReadScanType();
		var location = Required("Location: ");
		var notes = ReadLine("Notes (optional): ");

		var scan = ShipmentScan.Record(
			trackingNumber,
			scanType,
			courierId,
			location,
			timeProvider.GetUtcNow(),
			notes);
		scans.Create(scan);
		Write($"Saved {scan.Id} on this device as {scan.SyncStatus}.");
		coordinator.SyncIfOnline();
	}

	private void ListPending()
	{
		var pending = scans.ListPending();
		lock (outputLock)
		{
			output.WriteLine($"Pending scans, oldest first: {pending.Count}");
			foreach (var scan in pending)
			{
				output.WriteLine(
					$"  {scan.ScannedAtUtc:yyyy-MM-dd HH:mm:ss}Z  {scan.TrackingNumber}  {scan.ScanType}  {scan.Location}  {scan.Id}");
			}
		}
	}

	private void WriteSyncStatus()
	{
		var status = syncState.Snapshot();
		lock (outputLock)
		{
			output.WriteLine("Sync status");
			output.WriteLine(status.LastSyncUtc is { } last
				? $"  Last sync: {last:yyyy-MM-dd HH:mm:ss}Z"
				: "  Last sync: never");
			if (status.LastResult is { } result)
				output.WriteLine($"  Last result: {Summary(result)}");
			output.WriteLine($"  Circuit breaker: {Describe(status.CircuitState)}");
			output.WriteLine($"  Sync in progress: {(coordinator.IsSyncing ? "yes" : "no")}");
			output.WriteLine($"  Pending scans (live): {Volatile.Read(ref _livePendingCount)}");
			output.WriteLine(status.RecentFailures.Count == 0 ? "  Recent failures: none" : "  Recent failures, newest first:");
			foreach (var failure in status.RecentFailures)
				output.WriteLine($"    {failure}");
		}
	}

	private async Task SyncNowAsync()
	{
		Write("Syncing...");
		var result = await coordinator.SyncNowAsync().ConfigureAwait(false);
		if (result is not null)
			Write($"Sync finished: {Summary(result)}");
		else if (session.IsSignInRequired)
			Write("Not synced: please log in first. The scans stay on this device.");
		else
			Write("The sync did not finish; see Sync Status. The scans stay on this device.");
	}

	private void WriteMenu(string? courierId)
	{
		var circuit = syncState.Snapshot().CircuitState;
		lock (outputLock)
		{
			output.WriteLine();
			output.WriteLine(
				$"=== ApexPost courier | {(courierId is null ? "not signed in" : $"signed in as {courierId}")}"
				+ $" | pending scans (live): {Volatile.Read(ref _livePendingCount)} ===");
			if (circuit != CircuitState.Closed)
				output.WriteLine(OfflineBanner);

			string[] options = courierId is null
				? ["Login (request a development token)", "Login (paste an access token)", "Pending Scans", "Sync Status", "Exit"]
				: ["New Scan", "Pending Scans", "Sync Status", "Sync Now", "Logout", "Exit"];
			for (var index = 0; index < options.Length; index++)
				output.WriteLine($"{index + 1}. {options[index]}");
		}
	}

	// From the API's 401, possibly during a background sync.
	private void OnSignInRequired(object? sender, EventArgs e) =>
		Write("Your sign-in was refused or has expired. Please log in again; scans stay on this device.");

	// Reports circuit-breaker changes as they happen, so the banner's meaning
	// is visible without waiting for the next menu.
	private void OnSyncStateChanged(object? sender, EventArgs e)
	{
		var current = syncState.Snapshot().CircuitState;
		var previous = (CircuitState)Interlocked.Exchange(ref _shownCircuitState, (int)current);
		if (current == previous)
			return;

		if (current == CircuitState.Open)
			Write(OfflineBanner);
		else if (current == CircuitState.Closed)
			Write("Server available again; Pending scans are being synced.");
	}

	private ScanType ReadScanType()
	{
		var names = Enum.GetNames<ScanType>();
		var text = Required(
			"Scan type (" + string.Join(", ", names.Select((name, index) => $"{index + 1} {name}")) + "): ").Trim();

		if (int.TryParse(text, out var number) && number >= 1 && number <= names.Length)
			return Enum.Parse<ScanType>(names[number - 1]);

		return names.Contains(text, StringComparer.OrdinalIgnoreCase)
			? Enum.Parse<ScanType>(text, ignoreCase: true)
			: throw new ArgumentException("Unknown scan type.");
	}

	private static string Summary(OutboxRunResult result) =>
		$"{result.Attempted} attempted, {result.Synced} synced, {result.Failed} failed, {result.Pending} still pending."
		+ (result.StoppedBecause is { } reason ? $" Stopped: {reason}" : "");

	private static string Describe(CircuitState state) => state switch
	{
		CircuitState.Closed => "closed (the API is answering)",
		CircuitState.Open => "open (server unavailable, working offline)",
		CircuitState.HalfOpen => "half-open (the next call tests the server)",
		_ => state.ToString()
	};

	private string Required(string prompt)
	{
		var value = ReadLine(prompt);
		return string.IsNullOrWhiteSpace(value)
			? throw new ArgumentException("A value is required.")
			: value;
	}

	private string? ReadLine(string prompt)
	{
		lock (outputLock)
			output.Write(prompt);
		return input.ReadLine();
	}

	private void Write(string line)
	{
		lock (outputLock)
			output.WriteLine(line);
	}
}
