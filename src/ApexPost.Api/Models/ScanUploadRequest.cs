using System.ComponentModel.DataAnnotations;
using ApexPost.Api.Validation;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models;

// A batch of offline scans from a courier device (Week 4 Part A). A malformed
// scan ID or a tracking number that differs from the route does not fail the
// request: that scan is returned as rejected, with its reason.
public sealed record ScanUploadRequest
{
	public const int MaximumScans = 100;

	[Required, MinLength(1), MaxLength(MaximumScans)]
	public IReadOnlyList<ScanUploadItem>? Scans { get; init; }
}

public sealed record ScanUploadItem
{
	// The device's scan::{Guid} document ID, which makes the upload idempotent.
	[Required]
	public string? ScanId { get; init; }

	[Required]
	public string? TrackingNumber { get; init; }

	[Required, EnumDataType(typeof(ShipmentScanType))]
	public ShipmentScanType? ScanType { get; init; }

	[Required, MaxLength(50)]
	public string? CourierId { get; init; }

	[Required, MaxLength(200)]
	public string? Location { get; init; }

	[Required, NotInFuture]
	public DateTimeOffset? ScannedAtUtc { get; init; }

	[MaxLength(500)]
	public string? Notes { get; init; }
}
