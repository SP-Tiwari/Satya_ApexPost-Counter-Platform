namespace ApexPost.Domain.Entities;

// One movement of money for a transaction: the payment taken, or a refund of
// it. PaymentMethod is how this movement happened; the sale's own
// CounterSaleDetails.PaymentMethod is the method selected for the sale.
public sealed class Payment
{
	public Guid Id { get; }
	public Guid TransactionId { get; }
	public PaymentEntryType Type { get; }
	public Money Amount { get; }
	public string PaymentMethod { get; }
	public DateTimeOffset RecordedAtUtc { get; }

	// Used by EF Core when it materializes a stored row.
	private Payment()
	{
		Amount = null!;
		PaymentMethod = null!;
	}

	public Payment(
		Guid id,
		Guid transactionId,
		PaymentEntryType type,
		Money amount,
		string paymentMethod,
		DateTimeOffset recordedAtUtc)
	{
		ArgumentNullException.ThrowIfNull(amount);

		if (string.IsNullOrWhiteSpace(paymentMethod))
			throw new ArgumentException(
				"Payment method is required.", nameof(paymentMethod));

		if (id == Guid.Empty)
			throw new ArgumentException("Payment ID cannot be empty.", nameof(id));

		if (transactionId == Guid.Empty)
			throw new ArgumentException(
				"Transaction ID cannot be empty.", nameof(transactionId));

		if (!Enum.IsDefined(type))
			throw new ArgumentOutOfRangeException(nameof(type));

		if (amount.Amount <= 0m)
			throw new ArgumentOutOfRangeException(
				nameof(amount), "Payment amount must be greater than zero.");

		Id = id;
		TransactionId = transactionId;
		Type = type;
		Amount = amount;
		PaymentMethod = paymentMethod.Trim();
		RecordedAtUtc = recordedAtUtc.ToUniversalTime();
	}
}
