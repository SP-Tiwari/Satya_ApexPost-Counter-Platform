using System.Text;
using ApexPost.Application.Validation;

namespace ApexPost.Application.Services;

public sealed record MoneyOrderBasketSettings
{
	public Money HighValueThreshold { get; }
	public Money Fee { get; }
	public TaxCategory FeeTaxCategory { get; }

	public MoneyOrderBasketSettings(
		Money highValueThreshold,
		Money fee,
		TaxCategory feeTaxCategory)
	{
		ArgumentNullException.ThrowIfNull(highValueThreshold);
		ArgumentNullException.ThrowIfNull(fee);
		if (!string.Equals(
			highValueThreshold.Currency, fee.Currency, StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"The money-order fee must use the high-value threshold currency.",
				nameof(fee));
		}
		if (!Enum.IsDefined(feeTaxCategory))
			throw new ArgumentOutOfRangeException(nameof(feeTaxCategory));

		HighValueThreshold = highValueThreshold;
		Fee = fee;
		FeeTaxCategory = feeTaxCategory;
	}
}

public sealed record MoneyOrderBasketRequest(
	[property: GreaterThanZero] decimal Amount,
	[property: NotBlank, MaximumLength(InputLimits.PartyName)] string Sender,
	[property: NotBlank, MaximumLength(InputLimits.PartyName)] string Recipient,
	[property: MaximumLength(InputLimits.DocumentType)] string? DocumentType,
	string? DocumentIdentifier)
{
	// Keep the full document identifier out of logs and exception messages.
	private bool PrintMembers(StringBuilder builder)
	{
		builder.Append($"Amount = {Amount}, Sender = {Sender}, Recipient = {Recipient}, DocumentType = {DocumentType}, DocumentIdentifier = [redacted]");
		return true;
	}
}
