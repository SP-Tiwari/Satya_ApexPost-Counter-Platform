using System.ComponentModel.DataAnnotations;

namespace ApexPost.Api.Validation;

/// <summary>
/// The custom rule (B4): a time later than the server's current UTC time is
/// invalid. The clock is the registered <see cref="TimeProvider"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotInFutureAttribute : ValidationAttribute
{
	/// <summary>Creates the rule with the message "{0} cannot be in the future."</summary>
	public NotInFutureAttribute()
		: base("{0} cannot be in the future.")
	{
	}

	/// <inheritdoc/>
	protected override ValidationResult? IsValid(
		object? value,
		ValidationContext validationContext)
	{
		// A missing value is reported by [Required].
		if (value is not DateTimeOffset scannedAt)
			return ValidationResult.Success;

		var clock = validationContext.GetService(typeof(TimeProvider)) as TimeProvider
			?? TimeProvider.System;
		if (scannedAt <= clock.GetUtcNow())
			return ValidationResult.Success;

		return new ValidationResult(
			FormatErrorMessage(validationContext.DisplayName),
			validationContext.MemberName is { } member ? [member] : null);
	}
}
