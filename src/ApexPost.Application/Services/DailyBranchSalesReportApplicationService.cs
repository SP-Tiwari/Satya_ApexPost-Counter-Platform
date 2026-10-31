using ApexPost.Domain.Reporting;
using ApexPost.Domain.Specifications;

namespace ApexPost.Application.Services;

public sealed class DailyBranchSalesReportApplicationService
{
	private const string DeniedMessage =
		"An active Branch Manager is required to access daily sales reports.";

	private readonly IStaffRepository _staff;
	private readonly ICounterTransactionRepository _transactions;
	private readonly IReportQueryService? _queries;
	private readonly DailySalesReportService _reportService = new();

	// Without it, the report is calculated from repository reads, which is what
	// the unit tests use with their in-memory fakes.
	public DailyBranchSalesReportApplicationService(
		IStaffRepository staff,
		ICounterTransactionRepository transactions,
		IReportQueryService? queries = null)
	{
		ArgumentNullException.ThrowIfNull(staff);
		ArgumentNullException.ThrowIfNull(transactions);
		_staff = staff;
		_transactions = transactions;
		_queries = queries;
	}

	public async Task<IReadOnlyList<DailySalesReportRow>> GetReportAsync(
		string staffId,
		DateOnly businessDate,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(staffId))
			throw new UnauthorizedAccessException(DeniedMessage);

		var staff = await _staff.FindByIdAsync(staffId, cancellationToken)
			.ConfigureAwait(false);
		if (staff is not { IsActive: true, Role: StaffRole.BranchManager })
			throw new UnauthorizedAccessException(DeniedMessage);

		var branchCode = staff.HomeBranchCode;
		if (_queries is not null)
		{
			var lines = await _queries
				.GetPaidSaleLinesAsync(branchCode, businessDate, cancellationToken)
				.ConfigureAwait(false);
			return PaidSalesSummary.From(branchCode, businessDate, lines).Totals is { } totals
				? [totals]
				: [];
		}

		var baskets = await _transactions
			.GetForBranchAsync(branchCode, cancellationToken)
			.ConfigureAwait(false);
		var paidAtBranchOnDate = new PaidTransactionSpecification()
			.And(new TransactionAtBranchSpecification(branchCode))
			.And(new TransactionOnBusinessDateSpecification(businessDate));
		var entries = baskets
			.Matching(paidAtBranchOnDate)
			.Select(basket => new DailySalesReportEntry(
				branchCode,
				businessDate,
				basket));

		return _reportService.CreateReport(entries);
	}
}
