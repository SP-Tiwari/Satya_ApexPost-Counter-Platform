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

	// For read screens: the same transaction, not tracked for changes.
	Task<TransactionBasket?> FindForReadAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		FindAsync(transactionId, cancellationToken);

	Task<IReadOnlyList<TransactionBasket>> GetForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersForBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<MoneyOrder>> GetMoneyOrdersAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<ShippingItem>> GetShipmentsAsync(
		CancellationToken cancellationToken = default);

	// The transaction containing the shipment, or null when none does.
	Task<TransactionBasket?> FindByShipmentIdAsync(
		string shipmentId,
		CancellationToken cancellationToken = default);

	// Stages the basket with its shipments, money orders and verifications.
	void Add(TransactionBasket basket);

	void AddPayment(Payment payment);

	// The transaction's Payment entry, not a Refund, or null when it took no money.
	Task<Payment?> FindPaymentAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default);
}
