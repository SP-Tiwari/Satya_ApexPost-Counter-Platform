using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models;

/// <summary>A tracked shipment.</summary>
/// <param name="TrackingNumber">The tracking number: APX- followed by eight digits.</param>
/// <param name="RecipientName">The recipient's full name.</param>
/// <param name="RecipientPhone">The recipient's phone number in E.164 format.</param>
/// <param name="RecipientAddress">The delivery address.</param>
/// <param name="RecipientCity">The delivery city.</param>
/// <param name="WeightKg">The weight in kilograms.</param>
/// <param name="Status">
/// The current status: 0 Created, 1 PickedUp, 2 InTransit, 3 OutForDelivery,
/// 4 Delivered or 5 Failed.
/// </param>
public sealed record ShipmentResponse(
	string TrackingNumber,
	string RecipientName,
	string RecipientPhone,
	string RecipientAddress,
	string RecipientCity,
	decimal WeightKg,
	ShipmentStatus Status);

/// <summary>One page of shipments.</summary>
/// <param name="Items">The shipments on this page, ordered by tracking number.</param>
/// <param name="Page">The page number, starting at 1.</param>
/// <param name="PageSize">The largest number of shipments on a page.</param>
/// <param name="TotalCount">The number of shipments that match the filters, on all pages.</param>
public sealed record ShipmentPageResponse(
	IReadOnlyList<ShipmentResponse> Items,
	int Page,
	int PageSize,
	int TotalCount);

/// <summary>A recorded scan of a shipment.</summary>
/// <param name="ScanId">The device's scan ID, <c>scan::</c> followed by a GUID.</param>
/// <param name="ScanType">The scan type: 0 Pickup, 1 InTransit, 2 Delivered or 3 Failed.</param>
/// <param name="CourierId">The courier who scanned the shipment.</param>
/// <param name="Location">Where the scan was made.</param>
/// <param name="ScannedAtUtc">When the scan was made.</param>
/// <param name="Notes">The courier's notes, or null.</param>
public sealed record ScanResponse(
	string ScanId,
	ShipmentScanType ScanType,
	string CourierId,
	string Location,
	DateTimeOffset ScannedAtUtc,
	string? Notes);

/// <summary>What happened to each scan in an upload: the three counts of B6.</summary>
/// <param name="Accepted">The number of scans stored.</param>
/// <param name="DuplicatesSkipped">The number of scans already recorded, or repeated in the batch, and not stored again.</param>
/// <param name="Rejected">The number of scans refused; <c>rejections</c> gives the reason for each.</param>
/// <param name="Rejections">Each refused scan and its reason.</param>
public sealed record ScanUploadResponse(
	int Accepted,
	int DuplicatesSkipped,
	int Rejected,
	IReadOnlyList<RejectedScanResponse> Rejections);

/// <summary>A scan that was refused, and why.</summary>
/// <param name="ScanId">The scan ID as it was uploaded.</param>
/// <param name="Reason">Why the scan was refused.</param>
public sealed record RejectedScanResponse(string ScanId, string Reason);
