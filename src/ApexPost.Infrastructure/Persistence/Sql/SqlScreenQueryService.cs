using System.Linq.Expressions;
using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// AsNoTracking projections for the working screens, behind an Application
// interface (ADR 0020). Stored codes are ordered as the tables store them.
public sealed class SqlScreenQueryService(ApexPostDbContext database)
	: IScreenQueryService
{
	private const string TaxRatesField = "_taxRates";
	private const string ReversalReasonField = "_reversalReason";
	private const string TransactionId = "TransactionId";
	private const string ShipmentId = "ShipmentId";

	// One expression for one transaction and for a branch's list. Tax rates are
	// a scalar subquery per line, so they add no rows.
	private static readonly Expression<Func<TransactionBasket, TransactionDetail>> ToDetail =
		basket => new TransactionDetail(
			basket.Id,
			basket.Currency,
			basket.BranchCode == null ? null : basket.BranchCode.Value,
			basket.PaymentStatus,
			EF.Property<string?>(basket, ReversalReasonField),
			basket.OccurredAtUtc,
			basket.SaleDetails == null ? null : basket.SaleDetails.CounterTerminalId,
			basket.SaleDetails == null ? null : basket.SaleDetails.ClerkStaffId,
			basket.SaleDetails == null ? null : basket.SaleDetails.PaymentMethod,
			basket.LineItems
				.Select(line => new TransactionLineDetail(
					line.Description,
					line.Type,
					line.UnitPrice.Amount,
					line.UnitPrice.Currency,
					line.Quantity,
					EF.Property<List<TransactionTaxRate>>(basket, TaxRatesField)
						.Where(rate => rate.Category == line.TaxCategory)
						.Select(rate => (decimal?)rate.Rate)
						.FirstOrDefault(),
					line.ShippingItem == null
						? null
						: new ShipmentDetail(
							line.ShippingItem.Id,
							line.ShippingItem.Type,
							line.ShippingItem.ServiceLevel,
							line.ShippingItem.Weight.Kilograms,
							line.ShippingItem.Dimensions.LengthCm,
							line.ShippingItem.Dimensions.WidthCm,
							line.ShippingItem.Dimensions.HeightCm,
							line.ShippingItem.DestinationZone)))
				.ToArray());

	public Task<BranchHeading?> FindBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		database.Branches
			.AsNoTracking()
			.Where(branch => branch.Code == branchCode)
			.Select(branch => new BranchHeading(branch.Code.Value, branch.Name))
			.SingleOrDefaultAsync(cancellationToken);

	public Task<DashboardBranch?> FindDashboardBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		database.Branches
			.AsNoTracking()
			.Where(branch => branch.Code == branchCode)
			.Select(branch => new DashboardBranch(
				branch.Code.Value,
				branch.Name,
				branch.Region,
				branch.OpeningHours,
				branch.IsActive,
				branch.CounterTerminals.Count(),
				branch.CounterTerminals.Count(terminal => terminal.IsActive)))
			.SingleOrDefaultAsync(cancellationToken);

	public Task<SaleFormBranch?> FindSaleBranchAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		database.Branches
			.AsNoTracking()
			.Where(branch => branch.Code == branchCode)
			.Select(branch => new SaleFormBranch(
				branch.Code.Value,
				branch.Name,
				branch.IsActive,
				branch.CounterTerminals
					.Where(terminal => terminal.IsActive)
					.Select(terminal => new SaleFormCounter(terminal.Id, terminal.Name))
					.ToArray()))
			.SingleOrDefaultAsync(cancellationToken);

	public async Task<IReadOnlyList<SaleFormProduct>> GetSaleProductsAsync(
		CancellationToken cancellationToken = default) =>
		await database.Products
			.AsNoTracking()
			.Select(product => new SaleFormProduct(
				product.Sku.Value,
				product.Name,
				product.TaxCategory,
				product.UnitPrice.Amount,
				product.UnitPrice.Currency))
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<SaleFormShippingRate>> GetSaleShippingRatesAsync(
		CancellationToken cancellationToken = default) =>
		await database.ShippingRates
			.AsNoTracking()
			.Select(rate => new SaleFormShippingRate(
				rate.DestinationZone,
				rate.ServiceLevel,
				rate.BasePrice.Amount,
				rate.BasePrice.Currency,
				rate.PerKilogramCharge.Amount,
				rate.PerKilogramCharge.Currency))
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<StockRow>> GetStockAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		await Stock(branchCode)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<MoneyOrderRow>> GetMoneyOrdersAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		await MoneyOrders(branchCode)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public Task<TransactionDetail?> FindTransactionAsync(
		Guid transactionId,
		CancellationToken cancellationToken = default) =>
		Transaction(transactionId).SingleOrDefaultAsync(cancellationToken);

	public async Task<IReadOnlyList<TransactionDetail>> GetTransactionsAsync(
		BranchCode branchCode,
		CancellationToken cancellationToken = default) =>
		await Transactions(branchCode)
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public Task<ParcelDetail?> FindParcelAsync(
		string shipmentId,
		CancellationToken cancellationToken = default) =>
		Parcel(shipmentId).SingleOrDefaultAsync(cancellationToken);

	public async Task<IReadOnlyList<AdministrationBranchRow>> GetBranchesAsync(
		CancellationToken cancellationToken = default) =>
		await database.Branches
			.AsNoTracking()
			.OrderBy(branch => EF.Property<int>(branch, "BranchId"))
			.Select(branch => new AdministrationBranchRow(
				branch.Code.Value, branch.Name, branch.Region, branch.IsActive))
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<AdministrationStaffRow>> GetStaffAsync(
		CancellationToken cancellationToken = default) =>
		await database.Staff
			.AsNoTracking()
			.OrderBy(person => person.Id)
			.Select(person => new AdministrationStaffRow(
				person.Id,
				person.Name,
				person.Role,
				person.HomeBranchCode.Value,
				person.IsActive))
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	public async Task<IReadOnlyList<AdministrationProductRow>> GetProductsAsync(
		CancellationToken cancellationToken = default) =>
		await database.Products
			.AsNoTracking()
			.OrderBy(product => EF.Property<int>(product, "ProductId"))
			.Select(product => new AdministrationProductRow(
				product.Sku.Value,
				product.Name,
				product.Category,
				product.UnitPrice.Amount,
				product.UnitPrice.Currency))
			.ToArrayAsync(cancellationToken)
			.ConfigureAwait(false);

	internal IQueryable<StockRow> Stock(BranchCode branchCode) =>
		database.BranchStock
			.AsNoTracking()
			.Where(stock => stock.BranchCode == branchCode)
			.OrderBy(stock => stock.ProductSku)
			.Select(stock => new StockRow(stock.ProductSku.Value, stock.Quantity));

	internal IQueryable<MoneyOrderRow> MoneyOrders(BranchCode branchCode) =>
		database.MoneyOrders
			.AsNoTracking()
			.Where(order => order.ProcessingBranchCode == branchCode)
			.LeftJoin(
				database.IdentityVerifications.AsNoTracking(),
				order => order.Id,
				verification => verification.MoneyOrderId,
				(order, verification) => new { order, verification })
			.OrderBy(row => row.order.Id)
			.Select(row => new MoneyOrderRow(
				row.order.Amount.Amount,
				row.order.Amount.Currency,
				row.order.Sender,
				row.order.Recipient,
				row.order.Fee.Amount,
				row.verification == null ? null : row.verification.DocumentType,
				row.verification == null ? null : row.verification.MaskedDocumentIdentifier,
				row.verification == null ? null : row.verification.IsSuccessful,
				row.order.Status));

	internal IQueryable<TransactionDetail> Transaction(Guid transactionId) =>
		database.CounterTransactions
			.AsNoTracking()
			.Where(basket => basket.Id == transactionId)
			.Select(ToDetail);

	// A split query: a list repeats each transaction for every line otherwise.
	internal IQueryable<TransactionDetail> Transactions(BranchCode branchCode) =>
		database.CounterTransactions
			.AsNoTracking()
			.AsSplitQuery()
			.Where(basket => basket.BranchCode == branchCode)
			.OrderBy(basket => basket.Id)
			.Select(ToDetail);

	internal IQueryable<ParcelDetail> Parcel(string shipmentId) =>
		from basket in database.CounterTransactions.AsNoTracking()
		from line in basket.LineItems
		where EF.Property<string?>(line, ShipmentId) == shipmentId
		select new ParcelDetail(
			line.ShippingItem!.Id,
			line.ShippingItem.Type,
			line.ShippingItem.ServiceLevel,
			line.ShippingItem.DestinationZone,
			basket.OccurredAtUtc,
			basket.PaymentStatus);
}
