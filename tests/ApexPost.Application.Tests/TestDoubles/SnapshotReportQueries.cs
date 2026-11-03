using ApexPost.Domain.Reporting;
using ApexPost.Domain.Specifications;

namespace ApexPost.Application.Tests.TestDoubles;

// The report queries over the in-memory store, calculated with the Domain's
// report services. In the application, SqlReportQueryService answers them from
// SQL Server, and the Infrastructure SQL tests compare the two.
internal sealed class SnapshotReportQueries(SnapshotStore store) : IReportQueryService
{
	private readonly IStockRepository _stock = store;
	private readonly ICounterTransactionRepository _transactions = store;

	public Task<IReadOnlyList<PaidSaleLine>> GetPaidSaleLinesAsync(
		BranchCode branchCode,
		DateOnly businessDate,
		CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("No unit test reads paid sale lines.");

	// Rounded like usp_DailyBranchSales.
	public async Task<DailySalesReportRow?> GetDailySalesAsync(
		BranchCode branchCode,
		DateOnly businessDate,
		CancellationToken cancellationToken = default)
	{
		var baskets = await _transactions
			.GetForBranchAsync(branchCode, cancellationToken)
			.ConfigureAwait(false);
		var paidAtBranchOnDate = new PaidTransactionSpecification()
			.And(new TransactionAtBranchSpecification(branchCode))
			.And(new TransactionOnBusinessDateSpecification(businessDate));
		var row = new DailySalesReportService()
			.CreateReport(baskets
				.Matching(paidAtBranchOnDate)
				.Select(basket => new DailySalesReportEntry(branchCode, businessDate, basket)))
			.SingleOrDefault();
		return row is null
			? null
			: row with
			{
				RetailSales = TwoPlaces(row.RetailSales),
				ServiceSales = TwoPlaces(row.ServiceSales)
			};
	}

	public async Task<IReadOnlyList<LowStockReportRow>> GetLowStockAsync(
		BranchCode branchCode,
		int threshold,
		CancellationToken cancellationToken = default)
	{
		var stocks = await _stock
			.GetForBranchAsync(branchCode, cancellationToken)
			.ConfigureAwait(false);
		return new LowStockReportService().CreateReport(stocks, threshold);
	}

	public async Task<IReadOnlyList<ShippingVolumeReportRow>> GetShippingVolumeAsync(
		CancellationToken cancellationToken = default)
	{
		var shipments = await _transactions.GetShipmentsAsync(cancellationToken)
			.ConfigureAwait(false);
		return new ShippingVolumeReportService().CreateReport(shipments);
	}

	public async Task<IReadOnlyList<BranchAuditorReportRow>> GetBranchAuditorReportAsync(
		CancellationToken cancellationToken = default)
	{
		var moneyOrders = await _transactions.GetMoneyOrdersAsync(cancellationToken)
			.ConfigureAwait(false);
		return new BranchAuditorReportService().CreateReport(moneyOrders
			.Where(order => order.ProcessingBranchCode is not null)
			.Select(order => new BranchAuditorReportEntry(
				order.ProcessingBranchCode!,
				order,
				order.IdentityVerification)));
	}

	private static Money TwoPlaces(Money money) => new(
		Math.Round(money.Amount, 2, MidpointRounding.AwayFromZero),
		money.Currency);
}
