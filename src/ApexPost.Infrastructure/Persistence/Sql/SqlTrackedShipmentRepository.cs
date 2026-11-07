using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Scoped, sharing the request's ApexPostDbContext and unit of work with the
// Week 3 repositories. See ADR 0028.
public sealed class SqlTrackedShipmentRepository(ApexPostDbContext database)
	: ITrackedShipmentRepository
{
	private IQueryable<TrackedShipment> Shipments =>
		database.Set<TrackedShipment>().Include(shipment => shipment.Scans);

	public Task<TrackedShipment?> FindAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default) =>
		Shipments.SingleOrDefaultAsync(
			shipment => shipment.TrackingNumber == trackingNumber,
			cancellationToken);

	public Task<TrackedShipment?> FindForReadAsync(
		string trackingNumber,
		CancellationToken cancellationToken = default) =>
		Shipments
			.AsNoTracking()
			.SingleOrDefaultAsync(
				shipment => shipment.TrackingNumber == trackingNumber,
				cancellationToken);

	public async Task<TrackedShipmentPage> GetPageAsync(
		int page,
		int pageSize,
		ShipmentStatus? status,
		string? city,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

		var filtered = database.Set<TrackedShipment>().AsNoTracking();
		if (status is { } wantedStatus)
			filtered = filtered.Where(shipment => shipment.Status == wantedStatus);
		if (!string.IsNullOrWhiteSpace(city))
		{
			var wantedCity = city.Trim();
			filtered = filtered.Where(shipment => shipment.RecipientCity == wantedCity);
		}

		var totalCount = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
		// Split, so a shipment's columns are not repeated for each of its scans.
		var items = await filtered
			.OrderBy(shipment => shipment.TrackingNumber)
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.Include(shipment => shipment.Scans)
			.AsSplitQuery()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		return new TrackedShipmentPage(items, totalCount);
	}

	public async Task<IReadOnlyList<string>> FindStoredScanIdsAsync(
		IReadOnlyCollection<string> scanIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(scanIds);
		if (scanIds.Count == 0)
			return [];

		return await database.Set<TrackedShipmentScan>()
			.AsNoTracking()
			.Where(scan => scanIds.Contains(scan.ScanId))
			.Select(scan => scan.ScanId)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
	}

	public void Add(TrackedShipment shipment) =>
		database.Set<TrackedShipment>().Add(shipment);

	public void Remove(TrackedShipment shipment) =>
		database.Set<TrackedShipment>().Remove(shipment);
}
