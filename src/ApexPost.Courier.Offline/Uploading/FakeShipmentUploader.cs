using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Offline.Uploading;

// Part A stand-in for the real API call. It makes no network request: it
// accepts every scan unless the supplied rule rejects it, so a demo or test can
// produce both Synced and Failed outcomes.
public sealed class FakeShipmentUploader(Func<ShipmentScan, bool>? rejects = null)
	: IShipmentUploader
{
	public Task<ShipmentUploadResult> UploadAsync(
		ShipmentScan scan,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scan);
		cancellationToken.ThrowIfCancellationRequested();

		return Task.FromResult(rejects?.Invoke(scan) == true
			? ShipmentUploadResult.Failure("Rejected by the fake uploader.")
			: ShipmentUploadResult.Success());
	}
}
