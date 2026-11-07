namespace ApexPost.Domain.Entities;

// One courier scan uploaded from a device (Week 4 Part A). The ID is the
// device's scan::{Guid} document ID, so a repeated upload of the same scan is
// recognised by its ID.
public sealed class TrackedShipmentScan
{
	private const string ScanIdPrefix = "scan::";

	public string ScanId { get; }
	public ShipmentScanType ScanType { get; }
	public string CourierId { get; }
	public string Location { get; }
	public DateTimeOffset ScannedAtUtc { get; }
	public string? Notes { get; }

	// Used by EF Core when it materializes a stored row.
	private TrackedShipmentScan()
	{
		ScanId = null!;
		CourierId = null!;
		Location = null!;
	}

	public TrackedShipmentScan(
		string scanId,
		ShipmentScanType scanType,
		string courierId,
		string location,
		DateTimeOffset scannedAtUtc,
		string? notes)
	{
		if (!IsValidScanId(scanId))
		{
			throw new ArgumentException(
				$"A scan ID must have the format {ScanIdPrefix}{{Guid}}.",
				nameof(scanId));
		}

		if (!Enum.IsDefined(scanType))
			throw new ArgumentOutOfRangeException(nameof(scanType));

		if (string.IsNullOrWhiteSpace(courierId))
			throw new ArgumentException("Courier ID cannot be empty.", nameof(courierId));

		if (string.IsNullOrWhiteSpace(location))
			throw new ArgumentException("Location cannot be empty.", nameof(location));

		ScanId = scanId;
		ScanType = scanType;
		CourierId = courierId.Trim();
		Location = location.Trim();
		ScannedAtUtc = scannedAtUtc.ToUniversalTime();
		Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
	}

	// The same rule as the device: the prefix, then a non-empty GUID in its
	// standard hyphenated form. Public so an upload can report a malformed ID
	// as a rejected scan instead of failing the whole request.
	public static bool IsValidScanId(string? scanId) =>
		scanId is not null
		&& scanId.StartsWith(ScanIdPrefix, StringComparison.Ordinal)
		&& Guid.TryParseExact(scanId[ScanIdPrefix.Length..], "D", out var guid)
		&& guid != Guid.Empty;
}
