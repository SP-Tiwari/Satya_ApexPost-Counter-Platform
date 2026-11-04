using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.Rules;
using ApexPost.Domain.Services;
using ApexPost.Domain.ValueObjects;

namespace ApexPost.Infrastructure.Persistence;

// Shipment and money-order sales for the SQL demo seed, with the extra rates that
// let the shipping report show more than one service level.
internal sealed record DemoServiceSales(
	IReadOnlyList<ShippingRate> ExtraShippingRates,
	IReadOnlyList<TransactionBasket> Sales);

// SqlDemoDataSeeder writes these records to SQL Server.
public static class DemoDataInitializer
{
	// The demo seed is the only seed source. Staff IDs are exposed so demo
	// logins can be provisioned for exactly these records.
	public static IReadOnlyList<string> DemoStaffIds { get; } =
		Enumerable.Range(1, 20)
			.Select(index => $"demo-staff-{index:00}")
			.ToArray();

	private const int DemoShipmentSaleCount = 40;
	private const int DemoMoneyOrderSaleCount = 20;
	private const int DemoExpressAndRegisteredZoneCount = 3;

	// Above this fictional amount a money order needs a successful check. The rule
	// below applies it to every seeded order, so the seed cannot contradict it.
	private const decimal DemoHighValueThreshold = 10000m;
	private const decimal DemoMoneyOrderFee = 25m;
	private const string DemoCurrency = "INR";

	// With a payment method, sales record a counter and clerk like real ones.
	public static ApexPostSnapshot CreateDemoSnapshot(
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

	// Prices stay within two decimals, so these sales never depend on the column
	// rounding in ADR 0017. IDs are fixed, so the same seed gives the same records.
	internal static DemoServiceSales CreateDemoServiceSales(
		ApexPostSnapshot demo,
		DateTimeOffset seededAtUtc,
		string paymentMethod)
	{
		var extraRates = demo.ShippingRates
			.Take(DemoExpressAndRegisteredZoneCount)
			.SelectMany(rate => new[]
			{
				new ShippingRate(
					rate.DestinationZone,
					ShippingServiceLevel.Express,
					new Money(rate.BasePrice.Amount + 5m, DemoCurrency),
					rate.PerKilogramCharge),
				new ShippingRate(
					rate.DestinationZone,
					ShippingServiceLevel.Registered,
					new Money(rate.BasePrice.Amount + 8m, DemoCurrency),
					rate.PerKilogramCharge)
			})
			.ToArray();
		ShippingRate[] allRates = [.. demo.ShippingRates, .. extraRates];
		var pricing = new ShippingPricingService(allRates);
		var moneyOrderRule = new MoneyOrderIdentityVerificationRule(
			new Money(DemoHighValueThreshold, DemoCurrency));
		var fee = new Money(DemoMoneyOrderFee, DemoCurrency);
		var saleProcessing = new SaleProcessingService();

		TransactionBasket OpenSale(int index, Branch branch)
		{
			var clerk = demo.Staff.First(person =>
				person.HomeBranchCode == branch.Code &&
				person.Role == StaffRole.CounterClerk);
			return new TransactionBasket(
				Guid.Parse($"00000000-0000-0000-0001-{index:000000000000}"),
				DemoCurrency,
				new Dictionary<TaxCategory, decimal> { [TaxCategory.ZeroRated] = 0m },
				branch.Code,
				seededAtUtc.AddHours(-6 * (index - 1) - 3),
				new CounterSaleDetails(
					branch.CounterTerminals[0].Id, clerk.Id, paymentMethod));
		}

		var shipmentSales = Enumerable.Range(1, DemoShipmentSaleCount)
			.Select(index =>
			{
				var rate = allRates[(index - 1) % allRates.Length];
				var isLetter = index % 4 == 0;
				var shipment = new ShippingItem(
					$"DEMO-SHIP-{index:0000}",
					isLetter ? ShippingItemType.Letter : ShippingItemType.Parcel,
					new Weight(isLetter ? 0.25m : 1m + index % 5 * 0.5m),
					isLetter ? new Dimensions(24m, 16m, 1m) : new Dimensions(30m, 20m, 10m),
					rate.DestinationZone,
					rate.ServiceLevel);
				var branch = demo.Branches[(index - 1) % demo.Branches.Count];
				var basket = OpenSale(index, branch);
				basket.AddLineItem(new TransactionLineItem(
					$"Shipping {shipment.Type} to {shipment.DestinationZone} ({shipment.ServiceLevel})",
					TransactionItemType.Service,
					pricing.CalculatePrice(shipment),
					1,
					TaxCategory.ZeroRated,
					productSku: null,
					shippingItem: shipment));
				saleProcessing.ProcessSale(basket, branch.Code, demo.BranchStocks);
				return basket;
			});

		var moneyOrderSales = Enumerable.Range(1, DemoMoneyOrderSaleCount)
			.Select(index =>
			{
				var branch = demo.Branches[(index - 1) % demo.Branches.Count];
				// The document number is fictional, eight characters to fit the
				// masked column, and IdentityVerification keeps only its mask.
				var document = $"DEMO{index:0000}";
				var (amount, verification) = ((index - 1) % 4) switch
				{
					0 => (2000m, (IdentityVerification?)null),
					1 => (2500m, new IdentityVerification("DEMO Passport", document, true)),
					2 => (3000m, new IdentityVerification("DEMO Passport", document, false)),
					_ => (15000m, new IdentityVerification("DEMO Passport", document, true))
				};
				var moneyOrder = new MoneyOrder(
					new Money(amount, DemoCurrency),
					$"DEMO Sender {index:00}",
					$"DEMO Recipient {index:00}",
					fee,
					verification,
					branch.Code,
					Guid.Parse($"00000000-0000-0000-0002-{index:000000000000}"));
				moneyOrderRule.EnsureVerification(moneyOrder, verification);
				var basket = OpenSale(DemoShipmentSaleCount + index, branch);
				basket.AddLineItem(new TransactionLineItem(moneyOrder));
				basket.AddLineItem(new TransactionLineItem(
					"Money order fee",
					TransactionItemType.Service,
					fee,
					1,
					TaxCategory.ZeroRated));
				saleProcessing.ProcessSale(basket, branch.Code, demo.BranchStocks);
				return basket;
			});

		return new DemoServiceSales(extraRates, [.. shipmentSales, .. moneyOrderSales]);
	}
}
