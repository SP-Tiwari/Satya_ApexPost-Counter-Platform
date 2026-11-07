namespace ApexPost.Application.Persistence;

// Tracked shipments always load with their scans.
public interface ITrackedShipmentRepository
{
	// Tracked, for a use case that changes the shipment.
	Task<TrackedShipment?> FindAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default);

	// The same shipment, not tracked for changes.
	Task<TrackedShipment?> FindForReadAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default);

	// One page, ordered by tracking number. Page numbers start at 1; a null
	// status or city does not filter.
	Task<TrackedShipmentPage> GetPageAsync(
		int page,
		int pageSize,
		ShipmentStatus? status,
		string? city,
		CancellationToken cancellationToken = default);

	// Those of the given scan IDs that are already stored, under any shipment.
	Task<IReadOnlyList<string>> FindStoredScanIdsAsync(
		IReadOnlyCollection<string> scanIds,
		CancellationToken cancellationToken = default);

	void Add(TrackedShipment shipment);

	// Removes the shipment and its scans.
	void Remove(TrackedShipment shipment);
}

public sealed record TrackedShipmentPage(
	IReadOnlyList<TrackedShipment> Items,
	int TotalCount);
