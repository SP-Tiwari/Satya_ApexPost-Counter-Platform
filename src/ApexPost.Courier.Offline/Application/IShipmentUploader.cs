using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Offline.Application;

// Sends a batch of scans to the central system: at most OutboxService.BatchSize,
// oldest first. Part A uses FakeShipmentUploader; Part D's ApiShipmentUploader
// (ApexPost.Courier.Sync) calls the API. It returns one result per scan, by
// scan ID; a scan without a result is treated as RetryLater, so it is never
// lost.
public interface IShipmentUploader
{
	Task<IReadOnlyList<ShipmentUploadResult>> UploadAsync(
		IReadOnlyList<ShipmentScan> scans,
		CancellationToken cancellationToken = default);
}

public enum ShipmentUploadOutcome
{
	// The server holds the scan: accepted now, or already recorded (a duplicate).
	Synced,

	// The server refused the scan for good; FailureReason says why.
	Rejected,

	// No final answer (no network, timeout, server error, sign-in needed): the
	// scan stays Pending and is sent again in a later run.
	RetryLater
}

public sealed record ShipmentUploadResult(
	string ScanId,
	ShipmentUploadOutcome Outcome,
	string? FailureReason)
{
	public static ShipmentUploadResult Success(string scanId) =>
		new(scanId, ShipmentUploadOutcome.Synced, null);

	public static ShipmentUploadResult Failure(string scanId, string reason) =>
		new(scanId, ShipmentUploadOutcome.Rejected, reason);

	public static ShipmentUploadResult RetryLater(string scanId, string reason) =>
		new(scanId, ShipmentUploadOutcome.RetryLater, reason);
}
