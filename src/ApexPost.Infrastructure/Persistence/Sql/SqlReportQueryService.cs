using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.Reporting;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// AsNoTracking projections straight from the DbContext, behind an Application
// interface (ADR 0020). Each report is one command, so none of them is N+1.
public sealed class SqlReportQueryService(
	ApexPostDbContext database,
	IStoredProcedures procedures) : IReportQueryService
{
	private const string TaxRatesField = "_taxRates";
	private const string TransactionLineId = "TransactionLineId";
	private const string ShipmentId = "ShipmentId";

	public async Task<IReadOnlyList<PaidSaleLine>> GetPaidSaleLinesAsync(
		BranchCode branchCode,
		DateOnly businessDate,
		CancellationToken cancellationToken = default) =>
		await PaidSaleLines(branchCode, businessDate)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<LowStockReportRow>> GetLowStockAsync(
		BranchCode branchCode,
		int threshold,
		CancellationToken cancellationToken = default)
	{
		var rows = await procedures
			.GetLowStockAsync(branchCode.Value, threshold, cancellationToken)
			.ConfigureAwait(false);
		return rows
			.Select(row => new LowStockReportRow(
				new BranchCode(row.BranchCode),
				new Sku(row.Sku),
				row.Quantity,
				row.Threshold))
			.OrderBy(row => row.ProductSku.Value, StringComparer.Ordinal)
			.ToArray();
	}

	public async Task<IReadOnlyList<ShippingVolumeReportRow>> GetShippingVolumeAsync(
		CancellationToken cancellationToken = default)
	{
		var rows = await ShippingVolume()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		return rows
			.OrderBy(row => row.DestinationZone, StringComparer.Ordinal)
			.ThenBy(row => row.ServiceLevel)
			.ToArray();
	}

	public async Task<IReadOnlyList<BranchAuditorReportRow>> GetBranchAuditorReportAsync(
		CancellationToken cancellationToken = default)
	{
		var rows = await BranchAuditorReport()
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);
		return rows
			.OrderBy(row => row.BranchCode.Value, StringComparer.Ordinal)
			.ToArray();
	}

	// Compares the column with a range, so an index on it stays usable.
	internal IQueryable<PaidSaleLine> PaidSaleLines(
		BranchCode branchCode,
		DateOnly businessDate)
	{
		var start = new DateTimeOffset(
			businessDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
		var end = businessDate == DateOnly.MaxValue
			? DateTimeOffset.MaxValue
			: start.AddDays(1);

		return
			from basket in database.CounterTransactions.AsNoTracking()
			where basket.BranchCode == branchCode
				&& basket.PaymentStatus == TransactionPaymentStatus.Paid
				&& basket.OccurredAtUtc >= start
				&& basket.OccurredAtUtc < end
			from line in basket.LineItems
			orderby basket.OccurredAtUtc descending,
				basket.Id,
				EF.Property<long>(line, TransactionLineId)
			select new PaidSaleLine(
				basket.Id,
				basket.Currency,
				line.Type,
				line.UnitPrice.Amount,
				line.Quantity,
				EF.Property<List<TransactionTaxRate>>(basket, TaxRatesField)
					.Where(rate => rate.Category == line.TaxCategory)
					.Select(rate => (decimal?)rate.Rate)
					.FirstOrDefault(),
				EF.Property<string?>(line, ShipmentId));
	}

	internal IQueryable<ShippingVolumeReportRow> ShippingVolume() =>
		database.Shipments
			.AsNoTracking()
			.GroupBy(shipment => new { shipment.DestinationZone, shipment.ServiceLevel })
			.Select(group => new ShippingVolumeReportRow(
				group.Key.DestinationZone,
				group.Key.ServiceLevel,
				group.Count()));

	// IsSuccessful is null for a money order with no verification.
	internal IQueryable<BranchAuditorReportRow> BranchAuditorReport() =>
		database.MoneyOrders
			.AsNoTracking()
			.Where(order => order.ProcessingBranchCode != null)
			.LeftJoin(
				database.IdentityVerifications.AsNoTracking(),
				order => order.Id,
				verification => verification.MoneyOrderId,
				(order, verification) => new
				{
					BranchCode = order.ProcessingBranchCode!,
					order.Status,
					IsSuccessful = (bool?)verification!.IsSuccessful
				})
			.GroupBy(row => row.BranchCode)
			.Select(group => new BranchAuditorReportRow(
				group.Key,
				group.Count(),
				group.Count(row => row.IsSuccessful != null),
				group.Count(row => row.IsSuccessful == true),
				group.Count(row => row.IsSuccessful == false))
			{
				CancelledMoneyOrderCount =
					group.Count(row => row.Status == MoneyOrderStatus.Cancelled)
			});
}
