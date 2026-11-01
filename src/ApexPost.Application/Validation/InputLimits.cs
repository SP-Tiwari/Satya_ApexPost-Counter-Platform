namespace ApexPost.Application.Validation;

// The column lengths in the SQL Server schema, checked before a value reaches
// the database so that an over-long input is a validation error (400), not a
// failed save. They are storage limits, not business rules.
public static class InputLimits
{
	public const int StaffId = 50;
	public const int StaffName = 100;
	public const int StaffEmail = 254;
	public const int BranchName = 100;
	public const int BranchRegion = 100;
	public const int OpeningHours = 100;
	public const int CounterId = 20;
	public const int CounterName = 100;
	public const int ProductName = 200;
	public const int ZoneCode = 50;
	public const int PaymentMethod = 50;
	public const int PartyName = 200;
	public const int DocumentType = 50;
	public const int ReversalReason = 500;

	// The largest values of decimal(18,2), decimal(10,3) and decimal(8,2).
	public const decimal MoneyAmount = 9_999_999_999_999_999.99m;
	public const decimal WeightKilograms = 9_999_999.999m;
	public const decimal DimensionCm = 999_999.99m;

	public static void Ensure(string? value, int maximumLength, string description)
	{
		if (value?.Trim().Length > maximumLength)
			throw new ArgumentException(
				$"{description} cannot exceed {maximumLength} characters.");
	}

	public static void Ensure(decimal value, decimal maximum, string description)
	{
		if (value > maximum)
			throw new ArgumentException(
				$"{description} cannot exceed {maximum}.");
	}
}

public sealed class MaximumLengthAttribute(int maximumLength) : RequestRuleAttribute
{
	public override string? Validate(object? value, string memberName) =>
		value is string text && text.Trim().Length > maximumLength
			? $"{memberName} cannot exceed {maximumLength} characters."
			: null;
}
