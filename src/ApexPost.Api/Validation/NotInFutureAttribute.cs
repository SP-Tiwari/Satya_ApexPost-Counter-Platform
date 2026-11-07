using System.ComponentModel.DataAnnotations;

namespace ApexPost.Api.Validation;

// The custom rule (B4): a scan time later than the server's current UTC time
// is invalid. The clock is the registered TimeProvider.
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotInFutureAttribute()
	: ValidationAttribute("{0} cannot be in the future.")
{
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
