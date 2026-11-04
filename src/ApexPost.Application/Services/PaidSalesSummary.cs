using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Services;

// The dashboard's sales totals, calculated from projected lines by the same
// rules as TransactionBasket: goods are retail, services are services, and
// money-order principal is neither. Each column is then rounded as
// usp_DailyBranchSales rounds it, so the dashboard matches the Daily Sales report.
internal sealed record PaidSalesSummary(
	int TransactionCount,
	DailySalesReportRow? Totals,
	IReadOnlyList<string> ShipmentIds)
{
	public static PaidSalesSummary From(
		BranchCode branchCode,
		DateOnly businessDate,
		IReadOnlyList<PaidSaleLine> lines)
	{
		if (lines.Count == 0)
			return new PaidSalesSummary(0, null, []);

		if (lines.Select(line => line.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
			throw new InvalidOperationException(
				$"Daily sales for branch '{branchCode}' on '{businessDate}' contain incompatible currencies.");

		var currency = lines[0].Currency;
		return new PaidSalesSummary(
			lines.Select(line => line.TransactionId).Distinct().Count(),
			new DailySalesReportRow(
				branchCode,
				businessDate,
				new Money(TotalFor(lines, TransactionItemType.Goods), currency),
				new Money(TotalFor(lines, TransactionItemType.Service), currency)),
			lines.Select(line => line.ShipmentId).OfType<string>().ToArray());
	}

	private static decimal TotalFor(
		IEnumerable<PaidSaleLine> lines,
		TransactionItemType type)
	{
		var matching = lines.Where(line => line.Type == type).ToArray();
		// SQL Server's ROUND on a decimal rounds half away from zero.
		return Math.Round(
			matching.Sum(Subtotal) + matching.Sum(Tax),
			2,
			MidpointRounding.AwayFromZero);
	}

	private static decimal Subtotal(PaidSaleLine line) =>
		line.UnitPrice * line.Quantity;

	private static decimal Tax(PaidSaleLine line) =>
		Subtotal(line) * (line.TaxRate
			?? throw new InvalidOperationException(
				$"No tax rate is stored for a line of transaction '{line.TransactionId}'."));
}
