using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models.V2;

// The other version 2 requests are the version 1 DTOs, unchanged.

/// <summary>A shipment's new status (version 2).</summary>
public sealed record UpdateStatusRequest
{
	/// <summary>
	/// The next status, as its name (PickedUp, InTransit, OutForDelivery,
	/// Delivered or Failed), the same name a version 2 response returns.
	/// The version 1 number is also accepted. Required.
	/// </summary>
	/// <example>PickedUp</example>
	[Required, EnumDataType(typeof(ShipmentStatus))]
	[JsonConverter(typeof(JsonStringEnumConverter<ShipmentStatus>))]
	public ShipmentStatus? Status { get; init; }
}
