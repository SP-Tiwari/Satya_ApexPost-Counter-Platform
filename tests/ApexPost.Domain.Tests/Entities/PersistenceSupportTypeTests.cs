namespace ApexPost.Domain.Tests.Entities;

public class PersistenceSupportTypeTests
{
	private static readonly DateTimeOffset Now =
		new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Payment_TrimsTheMethodAndKeepsItsValues()
	{
		var transactionId = Guid.NewGuid();

		var payment = new Payment(
			Guid.NewGuid(),
			transactionId,
			PaymentEntryType.Refund,
			new Money(10m, "INR"),
			" Cash ",
			Now);

		Assert.Equal(transactionId, payment.TransactionId);
		Assert.Equal(PaymentEntryType.Refund, payment.Type);
		Assert.Equal("Cash", payment.PaymentMethod);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Payment_RequiresAMethod(string method)
	{
		Assert.Throws<ArgumentException>(() => new Payment(
			Guid.NewGuid(),
			Guid.NewGuid(),
			PaymentEntryType.Payment,
			new Money(10m, "INR"),
			method,
			Now));
	}

	[Fact]
	public void Payment_RequiresAnAmountAboveZero()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new Payment(
			Guid.NewGuid(),
			Guid.NewGuid(),
			PaymentEntryType.Payment,
			new Money(0m, "INR"),
			"Cash",
			Now));
	}

	[Fact]
	public void StockAdjustment_RequiresAChange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new StockAdjustment(
			Guid.NewGuid(),
			new BranchCode("BR01"),
			new Sku("SKU-1"),
			0,
			"Count",
			"staff-1",
			Now));
	}

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	public void StockAdjustment_RequiresAReason(string reason)
	{
		Assert.Throws<ArgumentException>(() => new StockAdjustment(
			Guid.NewGuid(),
			new BranchCode("BR01"),
			new Sku("SKU-1"),
			-1,
			reason,
			"staff-1",
			Now));
	}

	[Fact]
	public void ShippingZone_RequiresACode()
	{
		Assert.Throws<ArgumentException>(() => new ShippingZone(" ", "Zone"));
	}

	[Fact]
	public void TransactionTaxRate_RejectsANegativeRate()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TransactionTaxRate(TaxCategory.Standard, -0.1m));
	}

	[Fact]
	public void RestoreIdentityVerification_FillsAMissingVerification()
	{
		var order = CreateOrder(verification: null);
		var stored = IdentityVerification.FromMaskedIdentifier(
			"PASSPORT", "****1234", true);

		order.RestoreIdentityVerification(stored);

		Assert.Equal(stored, order.IdentityVerification);
	}

	[Fact]
	public void RestoreIdentityVerification_NeverReplacesARecordedVerification()
	{
		var recorded = new IdentityVerification("PASSPORT", "ABCDE1234", true);
		var order = CreateOrder(recorded);

		order.RestoreIdentityVerification(
			IdentityVerification.FromMaskedIdentifier("LICENCE", "****9999", false));

		Assert.Equal(recorded, order.IdentityVerification);
	}

	private static MoneyOrder CreateOrder(IdentityVerification? verification) =>
		new(
			new Money(100m, "INR"),
			"Sender",
			"Recipient",
			new Money(5m, "INR"),
			verification);
}
