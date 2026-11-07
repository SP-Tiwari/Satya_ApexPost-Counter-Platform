using System.ComponentModel.DataAnnotations;
using ApexPost.Api.Validation;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models;

/// <summary>
/// A batch of offline scans from a courier device (Week 4 Part A). A malformed
/// scan ID or a tracking number that differs from the route does not fail the
/// request: that scan is returned as rejected, with its reason.
/// </summary>
public sealed record ScanUploadRequest
{
	/// <summary>The largest number of scans in one batch.</summary>
	public const int MaximumScans = 100;

	/// <summary>The scans, from 1 to 100. Required.</summary>
	[Required, MinLength(1), MaxLength(MaximumScans)]
	public IReadOnlyList<ScanUploadItem>? Scans { get; init; }
}

/// <summary>One scan as the courier device recorded it.</summary>
public sealed record ScanUploadItem
{
	/// <summary>
	/// The device's document ID, <c>scan::</c> followed by a GUID. Required. It
	/// makes the upload idempotent: an ID already recorded is not stored again.
	/// </summary>
	/// <example>scan::3f2504e0-4f89-41d3-9a0c-0305e82c3301</example>
	[Required]
	public string? ScanId { get; init; }

	/// <summary>The scanned shipment's tracking number; it must match the route. Required.</summary>
	/// <example>APX-00000001</example>
	[Required]
	public string? TrackingNumber { get; init; }

	/// <summary>The scan type. Required: 0 Pickup, 1 InTransit, 2 Delivered or 3 Failed.</summary>
	/// <example>0</example>
	[Required, EnumDataType(typeof(ShipmentScanType))]
	public ShipmentScanType? ScanType { get; init; }

	/// <summary>The courier who scanned the shipment. Required; at most 50 characters.</summary>
	/// <example>COURIER-DEMO-07</example>
	[Required, MaxLength(50)]
	public string? CourierId { get; init; }

	/// <summary>Where the scan was made. Required; at most 200 characters.</summary>
	/// <example>Pune hub</example>
	[Required, MaxLength(200)]
	public string? Location { get; init; }

	/// <summary>When the scan was made. Required, and not later than the server's current UTC time.</summary>
	/// <example>2026-11-07T09:30:00Z</example>
	[Required, NotInFuture]
	public DateTimeOffset? ScannedAtUtc { get; init; }

	/// <summary>The courier's optional notes; at most 500 characters.</summary>
	[MaxLength(500)]
	public string? Notes { get; init; }
}
