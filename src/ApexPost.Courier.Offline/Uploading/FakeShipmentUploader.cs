using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Offline.Uploading;

// Part A stand-in for the real API call, still used by the harness. It makes
// no network request: it accepts every scan unless the supplied rule rejects
// it, so a demo or test can produce both Synced and Failed outcomes.
public sealed class FakeShipmentUploader(Func<ShipmentScan, bool>? rejects = null)
	: IShipmentUploader
{
	public Task<IReadOnlyList<ShipmentUploadResult>> UploadAsync(
		IReadOnlyList<ShipmentScan> scans,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scans);
		cancellationToken.ThrowIfCancellationRequested();

		IReadOnlyList<ShipmentUploadResult> results =
		[
			.. scans.Select(scan => rejects?.Invoke(scan) == true
				? ShipmentUploadResult.Failure(scan.Id, "Rejected by the fake uploader.")
				: ShipmentUploadResult.Success(scan.Id))
		];
		return Task.FromResult(results);
	}
}
