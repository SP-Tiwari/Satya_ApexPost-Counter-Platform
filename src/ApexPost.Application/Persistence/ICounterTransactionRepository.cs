namespace ApexPost.Application.Persistence;

// Transactions load whole: lines, shipments, money orders and each money
// order's identity verification.
public interface ICounterTransactionRepository
{
	Task<bool> ExistsAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default);

	Task<TransactionBasket?> FindAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<TransactionBasket>> GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	// Stages the basket with its shipments, money orders and verifications.
	void Add(TransactionBasket basket);

	void AddPayment(Payment payment);
}
