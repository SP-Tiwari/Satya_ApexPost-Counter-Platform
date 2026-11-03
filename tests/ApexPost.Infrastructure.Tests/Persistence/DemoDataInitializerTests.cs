using ApexPost.Domain.Enums;
using ApexPost.Infrastructure.Persistence;

namespace ApexPost.Infrastructure.Tests.Persistence;

// The seed's content. SqlPersistenceTests writes it to SQL Server through
// SqlDemoDataSeeder and checks that a second run writes nothing.
public sealed class DemoDataInitializerTests
{
	private static readonly DateTimeOffset SeededAtUtc =
		new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void CreateDemoSnapshot_CreatesExpectedFictionalData()
	{
		var snapshot = DemoDataInitializer.CreateDemoSnapshot(SeededAtUtc);

		Assert.Equal(5, snapshot.Branches.Count);
		Assert.Equal(20, snapshot.Staff.Count);
		Assert.Equal(30, snapshot.Products.Count);
		Assert.Equal(10, snapshot.ShippingRates.Count);
		Assert.Equal(40, snapshot.TransactionBaskets.Count);
		var branchCodes = snapshot.Branches
			.Select(branch => branch.Code)
			.ToHashSet();
		Assert.All(snapshot.Staff, staff =>
		{
			Assert.StartsWith("DEMO ", staff.Name, StringComparison.Ordinal);
			Assert.EndsWith("@example.invalid", staff.Email, StringComparison.Ordinal);
			Assert.Contains(staff.HomeBranchCode, branchCodes);
		});
		Assert.Contains(StaffRole.CounterClerk, snapshot.Staff.Select(staff => staff.Role));
		Assert.Contains(StaffRole.BranchManager, snapshot.Staff.Select(staff => staff.Role));
		Assert.Contains(StaffRole.Auditor, snapshot.Staff.Select(staff => staff.Role));
		Assert.Contains(StaffRole.Administrator, snapshot.Staff.Select(staff => staff.Role));
		Assert.All(snapshot.TransactionBaskets, basket =>
			Assert.Equal(TransactionPaymentStatus.Paid, basket.PaymentStatus));
		Assert.All(snapshot.ShippingRates, rate =>
			Assert.StartsWith("DEMO-ZONE-", rate.DestinationZone, StringComparison.Ordinal));
	}

	[Fact]
	public void CreateDemoSnapshot_SeededSalesDeductTheirBranchStock()
	{
		var snapshot = DemoDataInitializer.CreateDemoSnapshot(SeededAtUtc);

		var unitsSold = snapshot.TransactionBaskets
			.SelectMany(basket => basket.LineItems.Select(line =>
				(BranchCode: basket.BranchCode!, Sku: line.ProductSku!, line.Quantity)))
			.GroupBy(sale => (sale.BranchCode, sale.Sku))
			.ToDictionary(
				group => group.Key,
				group => group.Sum(sale => sale.Quantity));
		Assert.Equal(40, unitsSold.Values.Sum());
		Assert.All(snapshot.BranchStocks, stock =>
			Assert.Equal(
				10 - unitsSold.GetValueOrDefault((stock.BranchCode, stock.ProductSku)),
				stock.Quantity));
	}
}
