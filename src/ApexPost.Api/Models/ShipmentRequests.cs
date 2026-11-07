using System.ComponentModel.DataAnnotations;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models;

// Request DTOs, never EF entities. [ApiController] validates them and answers
// a failure with a 400 ValidationProblemDetails. Maximum lengths match the
// TrackedShipments columns (ADR 0028), so an over-long value is a 400, not a
// database error.

/// <summary>The editable shipment details shared by create and replace.</summary>
public abstract record ShipmentDetailsRequest
{
	/// <summary>
	/// The E.164 phone format: a plus sign, a country code and up to 15 digits
	/// in all.
	/// </summary>
	public const string PhonePattern = @"^\+[1-9][0-9]{7,14}$";

	/// <summary>The recipient's full name. Required; at most 100 characters.</summary>
	/// <example>DEMO Recipient</example>
	[Required, MaxLength(100)]
	public string? RecipientName { get; init; }

	/// <summary>The recipient's phone number in E.164 format. Required; at most 20 characters.</summary>
	/// <example>+919876543210</example>
	[Required, MaxLength(20)]
	[RegularExpression(PhonePattern,
		ErrorMessage = "The recipient phone must be in E.164 format, such as +919876543210.")]
	public string? RecipientPhone { get; init; }

	/// <summary>The delivery address. Required; at most 200 characters.</summary>
	/// <example>1 Demo Street</example>
	[Required, MaxLength(200)]
	public string? RecipientAddress { get; init; }

	/// <summary>The delivery city. Required; at most 100 characters.</summary>
	/// <example>Pune</example>
	[Required, MaxLength(100)]
	public string? RecipientCity { get; init; }

	/// <summary>The weight in kilograms. Required; from 0.1 to 70.</summary>
	/// <example>2.5</example>
	[Required]
	[Range(typeof(decimal), "0.1", "70",
		ParseLimitsInInvariantCulture = true,
		ConvertValueInInvariantCulture = true,
		ErrorMessage = "The weight must be between 0.1 and 70 kg.")]
	public decimal? WeightKg { get; init; }
}

/// <summary>A new shipment: its tracking number and details.</summary>
public sealed record CreateShipmentRequest : ShipmentDetailsRequest
{
	/// <summary>
	/// The brief's <c>^APX-\d{8}$</c>, written with <c>[0-9]</c> because .NET's
	/// <c>\d</c> also matches non-ASCII digits.
	/// </summary>
	public const string TrackingNumberPattern = "^APX-[0-9]{8}$";

	/// <summary>The tracking number: APX- followed by eight digits. Required and unique.</summary>
	/// <example>APX-00000001</example>
	[Required]
	[RegularExpression(TrackingNumberPattern,
		ErrorMessage = "The tracking number must be APX- followed by eight digits.")]
	public string? TrackingNumber { get; init; }
}

/// <summary>
/// A shipment's new editable details. The tracking number comes from the route
/// and cannot change.
/// </summary>
public sealed record UpdateShipmentRequest : ShipmentDetailsRequest;

/// <summary>A shipment's new status.</summary>
public sealed record UpdateStatusRequest
{
	/// <summary>
	/// The next status. Required: 0 Created, 1 PickedUp, 2 InTransit,
	/// 3 OutForDelivery, 4 Delivered or 5 Failed.
	/// </summary>
	/// <example>1</example>
	[Required, EnumDataType(typeof(ShipmentStatus))]
	public ShipmentStatus? Status { get; init; }
}

/// <summary>The shipment list query: the page and the optional filters.</summary>
public sealed record ShipmentListQuery
{
	/// <summary>The page number, starting at 1. Defaults to 1.</summary>
	[Range(1, int.MaxValue)]
	public int Page { get; init; } = 1;

	/// <summary>The number of shipments per page, from 1 to 50. Defaults to 20.</summary>
	[Range(1, 50)]
	public int PageSize { get; init; } = 20;

	/// <summary>Only shipments with this status, when given.</summary>
	[EnumDataType(typeof(ShipmentStatus))]
	public ShipmentStatus? Status { get; init; }

	/// <summary>Only shipments to this city, when given; at most 100 characters.</summary>
	[MaxLength(100)]
	public string? City { get; init; }
}
