namespace ApexPost.Domain.Specifications;

public sealed class PaidTransactionSpecification : Specification<TransactionBasket>
{
	public override bool IsSatisfiedBy(TransactionBasket candidate) =>
		candidate.PaymentStatus == TransactionPaymentStatus.Paid;
}

public sealed class TransactionAtBranchSpecification(BranchCode branchCode)
	: Specification<TransactionBasket>
{
	public override bool IsSatisfiedBy(TransactionBasket candidate) =>
		candidate.BranchCode == branchCode;
}

// Business dates are UTC calendar dates. Transactions saved before their time
// was recorded never satisfy a date.
public sealed class TransactionOnBusinessDateSpecification(DateOnly businessDate)
	: Specification<TransactionBasket>
{
	public override bool IsSatisfiedBy(TransactionBasket candidate) =>
		candidate.OccurredAtUtc is { } occurredAtUtc &&
		DateOnly.FromDateTime(occurredAtUtc.UtcDateTime) == businessDate;
}
