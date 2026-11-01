using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Writes the fictional demo data to an empty database, in one commit. The data
// comes from DemoDataInitializer, the single seed source. Any existing branch,
// staff member or product stops it, so it never writes a record twice.
public sealed class SqlDemoDataSeeder(
	ApexPostDbContext database,
	ICounterTransactionRepository transactions,
	IUnitOfWork unitOfWork,
	TimeProvider timeProvider)
{
	// Enough history for reports and indexes to be meaningful. One sale per
	// six hours gives about seven weeks; stock of 10 per product covers it.
	private const int HistoricalSaleCount = 210;
	private const string DemoPaymentMethod = "DEMO Cash";

	public Task<bool> InitializeAsync(
		CancellationToken cancellationToken = default) =>
		unitOfWork.ExecuteInTransactionAsync(async token =>
		{
			if (await database.Branches.AnyAsync(token).ConfigureAwait(false) ||
				await database.Staff.AnyAsync(token).ConfigureAwait(false) ||
				await database.Products.AnyAsync(token).ConfigureAwait(false))
			{
				return false;
			}

			var demo = DemoDataInitializer.CreateDemoSnapshot(
				timeProvider.GetUtcNow(), HistoricalSaleCount, DemoPaymentMethod);
			database.Branches.AddRange(demo.Branches);
			database.Staff.AddRange(demo.Staff);
			database.Products.AddRange(demo.Products);
			database.BranchStock.AddRange(demo.BranchStocks);
			database.ShippingZones.AddRange(demo.ShippingRates
				.Select(rate => rate.DestinationZone)
				.Distinct()
				.Select(zone => new ShippingZone(zone, zone)));
			database.ShippingRates.AddRange(demo.ShippingRates);
			database.CounterTransactions.AddRange(demo.TransactionBaskets);

			var serviceSales = DemoDataInitializer.CreateDemoServiceSales(
				demo, timeProvider.GetUtcNow(), DemoPaymentMethod);
			database.ShippingRates.AddRange(serviceSales.ExtraShippingRates);
			// Add stores each money order's masked verification with its record.
			foreach (var sale in serviceSales.Sales)
				transactions.Add(sale);

			database.Payments.AddRange(demo.TransactionBaskets
				.Concat(serviceSales.Sales)
				.Select(basket => new Payment(
					Guid.NewGuid(),
					basket.Id,
					PaymentEntryType.Payment,
					basket.Total,
					DemoPaymentMethod,
					basket.OccurredAtUtc!.Value)));
			await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
			return true;
		},
		cancellationToken);
}
