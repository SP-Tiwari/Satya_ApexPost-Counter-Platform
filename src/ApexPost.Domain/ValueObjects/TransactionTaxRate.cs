namespace ApexPost.Domain.ValueObjects;

// One tax rate captured on a transaction when it was created, so later rate
// changes never alter an existing sale.
public sealed record TransactionTaxRate
{
	public TaxCategory Category { get; }
	public decimal Rate { get; }

	public TransactionTaxRate(TaxCategory category, decimal rate)
	{
		if (!Enum.IsDefined(category))
			throw new ArgumentOutOfRangeException(nameof(category));

		if (rate < 0m)
			throw new ArgumentOutOfRangeException(
				nameof(rate), "Tax rates cannot be negative.");

		Category = category;
		Rate = rate;
	}
}
