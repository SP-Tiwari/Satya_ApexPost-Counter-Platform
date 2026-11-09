using System.Text.Json.Serialization;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models.V2;

/// <summary>
/// A shipment's tracking status as a partner sees it (version 2). Only what a
/// partner needs to track the shipment: no recipient name, phone number or
/// address.
/// </summary>
/// <param name="TrackingNumber">The tracking number: APX- followed by eight digits.</param>
/// <param name="Status">
/// The current status, as its name: Created, PickedUp, InTransit,
/// OutForDelivery, Delivered or Failed.
/// </param>
/// <param name="DestinationCity">The city the shipment is going to.</param>
/// <param name="LatestScan">The shipment's most recent scan, or null when it has none.</param>
public sealed record PartnerShipmentResponse(
	string TrackingNumber,
	[property: JsonConverter(typeof(JsonStringEnumConverter<ShipmentStatus>))]
	ShipmentStatus Status,
	string DestinationCity,
	LatestScanResponse? LatestScan);
