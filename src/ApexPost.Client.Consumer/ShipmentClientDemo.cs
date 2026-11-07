using ApexPost.Client;

namespace ApexPost.Client.Consumer;

// The consumer's steps, through the generated version 2 client only: create a
// shipment, fetch it, upload two scans, and handle a 404 through the
// generated exception type. All data is fictional.
public static class ShipmentClientDemo
{
	public const string UnknownTrackingNumber = "APX-00000000";

	public static async Task RunAsync(
		IApexPostApiClient client,
		TextWriter output,
		CancellationToken cancellationToken)
	{
		// A new tracking number on each run, so the demo can be repeated.
		var trackingNumber = $"APX-{Random.Shared.Next(1, 100_000_000):D8}";

		var created = await client.CreateShipmentAsync(
			new CreateShipmentRequest
			{
				TrackingNumber = trackingNumber,
				RecipientName = "DEMO Recipient",
				RecipientPhone = "+919876543210",
				RecipientAddress = "1 Demo Street",
				RecipientCity = "Pune",
				WeightKg = 2.5m
			},
			cancellationToken);
		output.WriteLine(
			$"1. Created {created.TrackingNumber}: status {created.Status}, " +
			$"recipient {created.Recipient.Name}, {created.Recipient.City}, {created.WeightKg} kg");

		var fetched = await client.GetShipmentAsync(trackingNumber, cancellationToken);
		output.WriteLine(
			$"2. Fetched {fetched.TrackingNumber}: status {fetched.Status}, " +
			$"latest scan {fetched.LatestScan?.ScanType.ToString() ?? "none"}");

		var now = DateTimeOffset.UtcNow;
		var upload = await client.UploadShipmentScansAsync(
			trackingNumber,
			new ScanUploadRequest
			{
				Scans =
				[
					Scan(trackingNumber, ShipmentScanType.Pickup, "Pune branch", now.AddMinutes(-30)),
					Scan(trackingNumber, ShipmentScanType.InTransit, "Pune hub", now.AddMinutes(-5))
				]
			},
			cancellationToken);
		output.WriteLine(
			$"3. Uploaded two scans: accepted {upload.Accepted}, " +
			$"duplicates skipped {upload.DuplicatesSkipped}, rejected {upload.Rejected}");

		try
		{
			await client.GetShipmentAsync(UnknownTrackingNumber, cancellationToken);
			output.WriteLine($"4. {UnknownTrackingNumber} was found, but no shipment was expected.");
		}
		catch (ApiException<ProblemDetails> notFound) when (notFound.StatusCode == 404)
		{
			output.WriteLine(
				$"4. {UnknownTrackingNumber}: {notFound.StatusCode} {notFound.Result.Title} " +
				$"(handled as ApiException<ProblemDetails>)");
		}
	}

	private static ScanUploadItem Scan(
		string trackingNumber,
		ShipmentScanType scanType,
		string location,
		DateTimeOffset scannedAtUtc) =>
		new()
		{
			// The Part A scan ID format: scan:: followed by a GUID.
			ScanId = $"scan::{Guid.NewGuid()}",
			TrackingNumber = trackingNumber,
			ScanType = scanType,
			CourierId = "COURIER-DEMO-07",
			Location = location,
			ScannedAtUtc = scannedAtUtc
		};
}
