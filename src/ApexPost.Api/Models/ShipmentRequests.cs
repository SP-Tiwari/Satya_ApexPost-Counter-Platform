using System.ComponentModel.DataAnnotations;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models;

// Request DTOs, never EF entities. [ApiController] validates them and answers
// a failure with a 400 ValidationProblemDetails. Maximum lengths match the
// TrackedShipments columns (ADR 0028), so an over-long value is a 400, not a
// database error.

// The editable details shared by create and replace.
public abstract record ShipmentDetailsRequest
{
	// E.164: a plus sign, a country code and up to 15 digits in all.
	public const string PhonePattern = @"^\+[1-9][0-9]{7,14}$";

	[Required, MaxLength(100)]
	public string? RecipientName { get; init; }

	[Required, MaxLength(20)]
	[RegularExpression(PhonePattern,
		ErrorMessage = "The recipient phone must be in E.164 format, such as +919876543210.")]
	public string? RecipientPhone { get; init; }

	[Required, MaxLength(200)]
	public string? RecipientAddress { get; init; }

	[Required, MaxLength(100)]
	public string? RecipientCity { get; init; }

	[Required]
	[Range(typeof(decimal), "0.1", "70",
		ParseLimitsInInvariantCulture = true,
		ConvertValueInInvariantCulture = true,
		ErrorMessage = "The weight must be between 0.1 and 70 kg.")]
	public decimal? WeightKg { get; init; }
}

public sealed record CreateShipmentRequest : ShipmentDetailsRequest
{
	// ^APX-\d{8}$ from the brief, written with [0-9] because .NET's \d also
	// matches non-ASCII digits.
	public const string TrackingNumberPattern = "^APX-[0-9]{8}$";

	[Required]
	[RegularExpression(TrackingNumberPattern,
		ErrorMessage = "The tracking number must be APX- followed by eight digits.")]
	public string? TrackingNumber { get; init; }
}

public sealed record UpdateShipmentRequest : ShipmentDetailsRequest;

public sealed record UpdateStatusRequest
{
	[Required, EnumDataType(typeof(ShipmentStatus))]
	public ShipmentStatus? Status { get; init; }
}

// The list query. pageSize defaults to 20, and more than 50 is a 400.
public sealed record ShipmentListQuery
{
	[Range(1, int.MaxValue)]
	public int Page { get; init; } = 1;

	[Range(1, 50)]
	public int PageSize { get; init; } = 20;

	[EnumDataType(typeof(ShipmentStatus))]
	public ShipmentStatus? Status { get; init; }

	[MaxLength(100)]
	public string? City { get; init; }
}
