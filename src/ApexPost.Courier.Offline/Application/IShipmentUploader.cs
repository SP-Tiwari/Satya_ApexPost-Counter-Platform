using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Offline.Application;

// Sends one scan to the central system. Part A uses FakeShipmentUploader;
// Part D replaces it with the real API call, which is why this is async and
// takes a cancellation token.
public interface IShipmentUploader
{
	Task<ShipmentUploadResult> UploadAsync(
		ShipmentScan scan,
		CancellationToken cancellationToken = default);
}

public sealed record ShipmentUploadResult(bool Succeeded, string? FailureReason)
{
	public static ShipmentUploadResult Success() => new(true, null);

	public static ShipmentUploadResult Failure(string reason) => new(false, reason);
}
