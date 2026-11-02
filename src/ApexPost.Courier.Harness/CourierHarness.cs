using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Persistence;

namespace ApexPost.Courier.Harness;

// The console menu. It reads and writes through TextReader and TextWriter so it
// can also be driven by a script. All storage is local: nothing here needs a
// network connection.
internal sealed class CourierHarness(
	TextReader input,
	TextWriter output,
	Lock outputLock,
	IScanRepository scans,
	Func<Func<ShipmentScan, bool>?, OutboxService> createOutbox,
	TimeProvider timeProvider,
	int initialPendingCount)
{
	private int _livePendingCount = initialPendingCount;

	// Called from the change listener with the count it has just queried.
	public void OnScansChanged(ScanChangeNotification change) =>
		Volatile.Write(ref _livePendingCount, change.PendingCount);

	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			WriteMenu();
			var choice = ReadLine("Choose an option: ");
			if (choice is null)
				return;

			try
			{
				switch (choice)
				{
					case "1":
						AddScan();
						break;
					case "2":
						FindByTrackingNumber();
						break;
					case "3":
						ListPending();
						break;
					case "4":
						await SimulateSyncAsync(cancellationToken).ConfigureAwait(false);
						break;
					case "5":
						DeleteScan();
						break;
					case "6":
						Write("Goodbye.");
						return;
					default:
						Write("Please choose 1 to 6.");
						break;
				}
			}
			catch (Exception exception) when (exception is ArgumentException
				or InvalidOperationException
				or KeyNotFoundException
				or InvalidDataException)
			{
				Write($"Error: {exception.Message}");
			}
		}
	}

	private void WriteMenu()
	{
		lock (outputLock)
		{
			output.WriteLine();
			output.WriteLine(
				$"=== ApexPost courier (offline) | pending scans (live): {Volatile.Read(ref _livePendingCount)} ===");
			output.WriteLine("1. Add Scan");
			output.WriteLine("2. Find by Tracking Number");
			output.WriteLine("3. List Pending");
			output.WriteLine("4. Simulate Sync");
			output.WriteLine("5. Delete Scan");
			output.WriteLine("6. Exit");
		}
	}

	private void AddScan()
	{
		var trackingNumber = Required("Tracking number: ");
		var scanType = ReadScanType();
		var courierId = Required("Courier ID: ");
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
		Write($"Saved {scan.Id} locally as {scan.SyncStatus}.");

		// Query 3: scans per scan type for this courier.
		var counts = scans.CountByScanType(scan.CourierId);
		Write($"Scans per type for courier {scan.CourierId}: "
			+ string.Join(", ", counts.Select(count => $"{count.ScanType} {count.Count}")));
	}

	private void FindByTrackingNumber()
	{
		var trackingNumber = Required("Tracking number: ");
		WriteScans(
			$"Scans for {trackingNumber.Trim()}, oldest first",
			scans.FindByTrackingNumber(trackingNumber));
	}

	private void ListPending() =>
		WriteScans("Pending scans, oldest first", scans.ListPending());

	private async Task SimulateSyncAsync(CancellationToken cancellationToken)
	{
		var reject = ReadLine(
			"Tracking number the fake uploader should reject (blank to accept all): ")
			?.Trim();
		Func<ShipmentScan, bool>? rejects = string.IsNullOrEmpty(reject)
			? null
			: scan => string.Equals(scan.TrackingNumber, reject, StringComparison.Ordinal);

		var result = await createOutbox(rejects)
			.SyncPendingAsync(cancellationToken)
			.ConfigureAwait(false);
		Write($"Sync finished: {result.Attempted} attempted, "
			+ $"{result.Synced} synced, {result.Failed} failed.");
	}

	private void DeleteScan()
	{
		var id = Required("Scan ID (scan::{Guid}): ").Trim();
		Write(scans.Delete(id) ? $"Deleted {id}." : $"No scan {id}.");
	}

	private ScanType ReadScanType()
	{
		var names = Enum.GetNames<ScanType>();
		var prompt = "Scan type ("
			+ string.Join(", ", names.Select((name, index) => $"{index + 1} {name}"))
			+ "): ";
		var text = Required(prompt).Trim();

		if (int.TryParse(text, out var number) && number >= 1 && number <= names.Length)
			return Enum.Parse<ScanType>(names[number - 1]);

		return names.Contains(text, StringComparer.OrdinalIgnoreCase)
			? Enum.Parse<ScanType>(text, ignoreCase: true)
			: throw new ArgumentException("Unknown scan type.");
	}

	private void WriteScans(string title, IReadOnlyList<ShipmentScan> found)
	{
		lock (outputLock)
		{
			output.WriteLine($"{title}: {found.Count}");
			foreach (var scan in found)
			{
				output.WriteLine(
					$"  {scan.Id} | {scan.TrackingNumber} | {scan.ScanType} | {scan.CourierId} | "
					+ $"{scan.Location} | {scan.ScannedAtUtc:yyyy-MM-dd HH:mm:ss.fff}Z | "
					+ $"{scan.SyncStatus} | {scan.Notes ?? "-"}");
			}
		}
	}

	private string Required(string prompt) =>
		ReadLine(prompt) is { } text && !string.IsNullOrWhiteSpace(text)
			? text
			: throw new ArgumentException("A value is required.");

	private string? ReadLine(string prompt)
	{
		lock (outputLock)
		{
			output.Write(prompt);
			output.Flush();
		}

		var line = input.ReadLine();

		// When the input is a script rather than a keyboard, echo it so the
		// transcript shows what was entered.
		if (line is not null && Console.IsInputRedirected && ReferenceEquals(input, Console.In))
			Write(line);

		return line;
	}

	private void Write(string line)
	{
		lock (outputLock)
		{
			output.WriteLine(line);
		}
	}
}
