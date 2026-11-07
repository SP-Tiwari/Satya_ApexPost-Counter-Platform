using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models;

public sealed record ShipmentResponse(
	string TrackingNumber,
	string RecipientName,
	string RecipientPhone,
	string RecipientAddress,
	string RecipientCity,
	decimal WeightKg,
	ShipmentStatus Status);

public sealed record ShipmentPageResponse(
	IReadOnlyList<ShipmentResponse> Items,
	int Page,
	int PageSize,
	int TotalCount);

public sealed record ScanResponse(
	string ScanId,
	ShipmentScanType ScanType,
	string CourierId,
	string Location,
	DateTimeOffset ScannedAtUtc,
	string? Notes);

// The three counts of B6, with a reason for each rejected scan.
public sealed record ScanUploadResponse(
	int Accepted,
	int DuplicatesSkipped,
	int Rejected,
	IReadOnlyList<RejectedScanResponse> Rejections);

public sealed record RejectedScanResponse(string ScanId, string Reason);
