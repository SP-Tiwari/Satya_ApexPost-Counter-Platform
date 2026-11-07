using System.Text.Json.Serialization;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models.V2;

// Version 2 changes the shipment representation only: the recipient is a
// nested object, the latest scan is added and the status is a string. The
// scan endpoints use the same DTOs in both versions.

/// <summary>A tracked shipment (version 2).</summary>
/// <param name="TrackingNumber">The tracking number: APX- followed by eight digits.</param>
/// <param name="Recipient">Who receives the shipment, and where.</param>
/// <param name="WeightKg">The weight in kilograms.</param>
/// <param name="Status">
/// The current status, as its name: Created, PickedUp, InTransit,
/// OutForDelivery, Delivered or Failed.
/// </param>
/// <param name="LatestScan">The shipment's most recent scan, or null when it has none.</param>
public sealed record ShipmentResponse(
	string TrackingNumber,
	RecipientResponse Recipient,
	decimal WeightKg,
	[property: JsonConverter(typeof(JsonStringEnumConverter<ShipmentStatus>))]
	ShipmentStatus Status,
	LatestScanResponse? LatestScan);

/// <summary>The recipient of a shipment.</summary>
/// <param name="Name">The recipient's full name.</param>
/// <param name="Phone">The recipient's phone number in E.164 format.</param>
/// <param name="Address">The delivery address.</param>
/// <param name="City">The delivery city.</param>
public sealed record RecipientResponse(
	string Name,
	string Phone,
	string Address,
	string City);

/// <summary>A summary of a shipment's most recent scan.</summary>
/// <param name="ScanType">The scan type: 0 Pickup, 1 InTransit, 2 Delivered or 3 Failed, as in the scan endpoints.</param>
/// <param name="Location">Where the scan was made.</param>
/// <param name="ScannedAtUtc">When the scan was made.</param>
public sealed record LatestScanResponse(
	ShipmentScanType ScanType,
	string Location,
	DateTimeOffset ScannedAtUtc);

/// <summary>One page of shipments (version 2).</summary>
/// <param name="Items">The shipments on this page, ordered by tracking number.</param>
/// <param name="Page">The page number, starting at 1.</param>
/// <param name="PageSize">The largest number of shipments on a page.</param>
/// <param name="TotalCount">The number of shipments that match the filters, on all pages.</param>
public sealed record ShipmentPageResponse(
	IReadOnlyList<ShipmentResponse> Items,
	int Page,
	int PageSize,
	int TotalCount);
