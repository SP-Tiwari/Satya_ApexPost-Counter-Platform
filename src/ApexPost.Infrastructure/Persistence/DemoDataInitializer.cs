using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.Services;
using ApexPost.Domain.ValueObjects;

namespace ApexPost.Infrastructure.Persistence;

public sealed class DemoDataInitializer(
	IApexPostRepository repository,
	TimeProvider? timeProvider = null)
{
	private readonly TimeProvider _timeProvider =
		timeProvider ?? TimeProvider.System;

	// The demo seed is the only seed source. Staff IDs are exposed so demo
	// logins can be provisioned for exactly these records.
	public static IReadOnlyList<string> DemoStaffIds { get; } =
		Enumerable.Range(1, 20)
			.Select(index => $"demo-staff-{index:00}")
			.ToArray();

	public async Task<bool> InitializeAsync(
		CancellationToken cancellationToken = default)
	{
		var initialized = false;
		await repository.UpdateAsync(snapshot =>
		{
			if (!snapshot.IsCompletelyEmpty)
				return snapshot;

			initialized = true;
			return CreateDemoSnapshot(_timeProvider.GetUtcNow());
		}, cancellationToken).ConfigureAwait(false);

		return initialized;
	}

	// With a payment method, sales record a counter and clerk like real ones.
	internal static ApexPostSnapshot CreateDemoSnapshot(
		DateTimeOffset seededAtUtc,
		int saleCount = 40,
		string? paymentMethod = null)
	{
		var branches = Enumerable.Range(1, 5)
			.Select(index => new Branch(
				new BranchCode($"DM{index:00}"),
				$"DEMO Branch {index:00}",
				$"DEMO Region {index:00}",
				"Demo hours only",
				[
					new CounterTerminal($"DEMO-T{index:00}-01", "DEMO Counter 1"),
					new CounterTerminal($"DEMO-T{index:00}-02", "DEMO Counter 2")
				]))
			.ToArray();

		var staff = Enumerable.Range(1, DemoStaffIds.Count)
			.Select(index =>
			{
				var branchIndex = (index - 1) / 4;
				var role = ((index - 1) % 4) switch
				{
					0 => StaffRole.CounterClerk,
					1 => StaffRole.BranchManager,
					2 => StaffRole.Auditor,
					_ => StaffRole.Administrator
				};
				return new Staff(
					DemoStaffIds[index - 1],
					$"DEMO Staff {index:00}",
					$"demo.staff{index:00}@example.invalid",
					role,
					branches[branchIndex].Code);
			})
			.ToArray();

		var products = Enumerable.Range(1, 30)
			.Select(index => new Product(
				new Sku($"DEMO-SKU-{index:000}"),
				$"DEMO Product {index:000}",
				(ProductCategory)((index - 1) % Enum.GetValues<ProductCategory>().Length),
				new Money(10m + index, "INR"),
				TaxCategory.ZeroRated))
			.ToArray();

		var branchStocks = branches
			.SelectMany(branch => products.Select(product =>
				new BranchStock(branch.Code, product.Sku, 10)))
			.ToArray();

		var shippingRates = Enumerable.Range(1, 10)
			.Select(index => new ShippingRate(
				$"DEMO-ZONE-{index:00}",
				ShippingServiceLevel.Standard,
				new Money(10m + index, "INR"),
				new Money(1m, "INR")))
			.ToArray();

		var taxRates = new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = 0m,
			[TaxCategory.Reduced] = 0m,
			[TaxCategory.ZeroRated] = 0m
		};
		var saleProcessing = new SaleProcessingService();
		var transactions = Enumerable.Range(1, saleCount)
			.Select(index =>
			{
				var product = products[(index - 1) % products.Length];
				var branch = branches[(index - 1) % branches.Length];
				var branchCode = branch.Code;
				var clerk = staff.First(person =>
					person.HomeBranchCode == branchCode &&
					person.Role == StaffRole.CounterClerk);
				// Demo history: one sale every six hours going back from seeding
				// time, so the daily report has data on recent dates.
				var basket = new TransactionBasket(
					Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
					"INR",
					taxRates,
					branchCode,
					seededAtUtc.AddHours(-6 * (index - 1)),
					paymentMethod is null
						? null
						: new CounterSaleDetails(
							branch.CounterTerminals[0].Id, clerk.Id, paymentMethod));
				basket.AddLineItem(new TransactionLineItem(product, 1));
				// Process seed sales like real sales so they deduct stock and a
				// later void or refund returns only stock that was taken.
				saleProcessing.ProcessSale(basket, branchCode, branchStocks);
				return basket;
			})
			.ToArray();

		return new ApexPostSnapshot(
			branches,
			staff,
			products,
			branchStocks,
			[],
			shippingRates,
			[],
			[],
			transactions);
	}
}

internal static class ApexPostSnapshotExtensions
{
	extension(ApexPostSnapshot snapshot)
	{
		public bool IsCompletelyEmpty =>
			snapshot.Branches.Count == 0 &&
			snapshot.Staff.Count == 0 &&
			snapshot.Products.Count == 0 &&
			snapshot.BranchStocks.Count == 0 &&
			snapshot.ShippingItems.Count == 0 &&
			snapshot.ShippingRates.Count == 0 &&
			snapshot.MoneyOrders.Count == 0 &&
			snapshot.IdentityVerifications.Count == 0 &&
			snapshot.TransactionBaskets.Count == 0;
	}
}
