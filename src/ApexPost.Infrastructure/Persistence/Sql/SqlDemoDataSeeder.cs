using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Writes the fictional demo data to an empty database, in one commit. The data
// comes from DemoDataInitializer, the single seed source.
public sealed class SqlDemoDataSeeder(
	ApexPostDbContext database,
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
			database.Payments.AddRange(demo.TransactionBaskets.Select(basket =>
				new Payment(
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
