using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.ValueObjects;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Web.IntegrationTests;

// Test-only: lets tests seed and inspect SQL Server with a whole snapshot, as
// they did with the JSON file. The application itself never uses this type.
internal sealed class SqlSnapshotRepository(IServiceScopeFactory scopes)
	: IApexPostRepository
{
	private const string DefaultCounterId = "TEST-COUNTER";
	private static readonly DateTimeOffset DefaultOccurredAtUtc =
		new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public async Task<ApexPostSnapshot> LoadAsync(
		CancellationToken cancellationToken = default)
	{
		await using var scope = scopes.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<ApexPostDbContext>();
		var baskets = await context.CounterTransactions
			.Include(basket => basket.LineItems).ThenInclude(line => line.ShippingItem)
			.Include(basket => basket.LineItems).ThenInclude(line => line.MoneyOrder)
			.AsSplitQuery()
			.ToArrayAsync(cancellationToken);
		var moneyOrders = await context.MoneyOrders.AsNoTracking()
			.ToArrayAsync(cancellationToken);
		var verifications = await context.IdentityVerifications.AsNoTracking()
			.ToDictionaryAsync(record => record.MoneyOrderId, cancellationToken);
		foreach (var order in moneyOrders)
		{
			if (verifications.TryGetValue(order.Id, out var record))
				order.RestoreIdentityVerification(IdentityVerificationMapper.ToDomain(record));
		}

		return new ApexPostSnapshot(
			await context.Branches.Include(branch => branch.CounterTerminals)
				.ToArrayAsync(cancellationToken),
			await context.Staff.ToArrayAsync(cancellationToken),
			await context.Products.ToArrayAsync(cancellationToken),
			await context.BranchStock.ToArrayAsync(cancellationToken),
			await context.Shipments.ToArrayAsync(cancellationToken),
			await context.ShippingRates.ToArrayAsync(cancellationToken),
			moneyOrders,
			moneyOrders
				.Select(order => order.IdentityVerification)
				.OfType<IdentityVerification>(),
			baskets);
	}

	public async Task SaveAsync(
		ApexPostSnapshot snapshot,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		TestSqlServer.Database.Reset();
		await using var scope = scopes.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<ApexPostDbContext>();

		// Legacy records with no branch cannot exist in SQL Server, because
		// the schema requires one, so a test's unattributed rows are left out.
		var baskets = snapshot.TransactionBaskets
			.Where(basket => basket.BranchCode is not null)
			.ToArray();
		var moneyOrders = snapshot.MoneyOrders
			.Concat(baskets
				.SelectMany(basket => basket.LineItems)
				.Select(line => line.MoneyOrder)
				.OfType<MoneyOrder>())
			.Where(order => order.ProcessingBranchCode is not null)
			.DistinctBy(order => order.Id)
			.ToArray();

		context.Branches.AddRange(snapshot.Branches);
		context.Branches.AddRange(MissingBranches(snapshot, baskets, moneyOrders));
		context.Staff.AddRange(snapshot.Staff);
		context.Products.AddRange(snapshot.Products);
		context.Products.AddRange(MissingProducts(snapshot, baskets));
		context.BranchStock.AddRange(snapshot.BranchStocks);
		context.ShippingZones.AddRange(ZonesOf(snapshot));
		context.ShippingRates.AddRange(snapshot.ShippingRates);
		context.Shipments.AddRange(snapshot.ShippingItems);
		context.MoneyOrders.AddRange(moneyOrders);
		context.CounterTransactions.AddRange(baskets);
		foreach (var basket in baskets.Where(basket => basket.OccurredAtUtc is null))
		{
			context.Entry(basket).Property(entry => entry.OccurredAtUtc).CurrentValue =
				DefaultOccurredAtUtc;
		}

		context.IdentityVerifications.AddRange(moneyOrders
			.Where(order => order.IdentityVerification is not null)
			.Select(order => IdentityVerificationMapper.ToRecord(
				order.Id, order.IdentityVerification!)));
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task UpdateAsync(
		Func<ApexPostSnapshot, ApexPostSnapshot> update,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(update);
		var current = await LoadAsync(cancellationToken);
		await SaveAsync(update(current), cancellationToken);
	}

	// Tests list only the records they care about, but SQL Server requires
	// every parent row, so missing ones are filled in here.
	private static IEnumerable<Branch> MissingBranches(
		ApexPostSnapshot snapshot,
		IReadOnlyCollection<TransactionBasket> baskets,
		IReadOnlyCollection<MoneyOrder> moneyOrders)
	{
		var listed = snapshot.Branches.Select(branch => branch.Code).ToHashSet();
		return snapshot.Staff.Select(staff => staff.HomeBranchCode)
			.Concat(snapshot.BranchStocks.Select(stock => stock.BranchCode))
			.Concat(baskets.Select(basket => basket.BranchCode!))
			.Concat(moneyOrders.Select(order => order.ProcessingBranchCode!))
			.Distinct()
			.Where(code => !listed.Contains(code))
			.Select(code => new Branch(
				code,
				$"Branch {code}",
				"Test region",
				"Test hours",
				baskets
					.Where(basket => basket.BranchCode == code)
					.Select(basket => basket.SaleDetails?.CounterTerminalId)
					.OfType<string>()
					.Append(DefaultCounterId)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Select(id => new CounterTerminal(id, id))));
	}

	private static IEnumerable<Product> MissingProducts(
		ApexPostSnapshot snapshot,
		IReadOnlyCollection<TransactionBasket> baskets)
	{
		var listed = snapshot.Products.Select(product => product.Sku).ToHashSet();
		return snapshot.BranchStocks.Select(stock => stock.ProductSku)
			.Concat(baskets
				.SelectMany(basket => basket.LineItems)
				.Select(line => line.ProductSku)
				.OfType<Sku>())
			.Distinct()
			.Where(sku => !listed.Contains(sku))
			.Select(sku => new Product(
				sku,
				$"Product {sku}",
				Domain.Enums.ProductCategory.Stationery,
				new Money(1m, "INR"),
				Domain.Enums.TaxCategory.ZeroRated));
	}

	private static IEnumerable<ShippingZone> ZonesOf(ApexPostSnapshot snapshot) =>
		snapshot.ShippingRates.Select(rate => rate.DestinationZone)
			.Concat(snapshot.ShippingItems.Select(item => item.DestinationZone))
			.Concat(snapshot.TransactionBaskets
				.SelectMany(basket => basket.LineItems)
				.Select(line => line.ShippingItem?.DestinationZone)
				.OfType<string>())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(zone => new ShippingZone(zone, zone));
}
