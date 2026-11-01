using ApexPost.Domain;

namespace ApexPost.Domain.Entities;

public sealed class MoneyOrder
{
	public Guid Id { get; }
	public Money Amount { get; }
	public string Sender { get; }
	public string Recipient { get; }
	public Money Fee { get; }
	public IdentityVerification? IdentityVerification { get; private set; }
	public BranchCode? ProcessingBranchCode { get; }

	// C# 14 field keyword: the locked getter needs no separate backing field.
	public MoneyOrderStatus Status
	{
		get
		{
			lock (DomainMutationLock.Gate)
				return field;
		}
		private set;
	} = MoneyOrderStatus.Issued;

	// Used by EF Core when it materializes a stored row.
	private MoneyOrder()
	{
		Amount = null!;
		Sender = null!;
		Recipient = null!;
		Fee = null!;
	}

	public MoneyOrder(
		Money amount,
		string sender,
		string recipient,
		Money fee,
		IdentityVerification? identityVerification = null,
		BranchCode? processingBranchCode = null,
		Guid? id = null)
	{
		ArgumentNullException.ThrowIfNull(amount);
		ArgumentNullException.ThrowIfNull(fee);

		if (id == Guid.Empty)
			throw new ArgumentException(
				"Money order ID cannot be empty.", nameof(id));

		if (amount.Amount <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(amount), "Money order amount must be greater than zero.");

		if (string.IsNullOrWhiteSpace(sender))
			throw new ArgumentException(
				"Money order sender is required.", nameof(sender));

		if (string.IsNullOrWhiteSpace(recipient))
			throw new ArgumentException(
				"Money order recipient is required.", nameof(recipient));

		if (!string.Equals(amount.Currency, fee.Currency, StringComparison.Ordinal))
			throw new ArgumentException(
				"Money order amount and fee must use the same currency.",
				nameof(fee));

		Id = id ?? Guid.NewGuid();
		Amount = amount;
		Sender = sender.Trim();
		Recipient = recipient.Trim();
		Fee = fee;
		IdentityVerification = identityVerification;
		ProcessingBranchCode = processingBranchCode;
	}

	// Used when loading from storage, where the verification is held apart
	// from the money order. It never replaces a recorded verification.
	public void RestoreIdentityVerification(IdentityVerification verification)
	{
		ArgumentNullException.ThrowIfNull(verification);
		IdentityVerification ??= verification;
	}

	public void Cancel()
	{
		lock (DomainMutationLock.Gate)
		{
			if (Status != MoneyOrderStatus.Issued)
				throw new InvalidOperationException(
					"Only an issued money order can be cancelled.");

			Status = MoneyOrderStatus.Cancelled;
		}
	}
}
