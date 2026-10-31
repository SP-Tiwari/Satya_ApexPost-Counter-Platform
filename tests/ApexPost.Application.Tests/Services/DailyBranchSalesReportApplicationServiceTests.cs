using ApexPost.Application.Tests.TestDoubles;
using ApexPost.Domain.Reporting;

namespace ApexPost.Application.Tests.Services;

public sealed class DailyBranchSalesReportApplicationServiceTests
{
	private static readonly BranchCode HomeBranch = new("BR01");
	private static readonly BranchCode OtherBranch = new("BR02");
	private const string ManagerId = "manager-1";
	private static readonly DateOnly ReportDate = new(2026, 1, 15);
	private static readonly DateTimeOffset OnReportDate =
		new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

	[Fact]
	public async Task GetReportAsync_ReturnsDailyRetailAndServiceTotalsForHomeBranch()
	{
		var basket = CreatePaidBasket(
			HomeBranch,
			goodsTotal: 100m,
			serviceTotal: 30m,
			currency: "INR");
		var foreignBasket = CreatePaidBasket(
			OtherBranch,
			goodsTotal: 70m,
			serviceTotal: 0m,
			currency: "INR");
		var service = CreateService(
			CreateStaff(ManagerId, StaffRole.BranchManager, HomeBranch),
			[basket, foreignBasket]);

		var report = await service.GetReportAsync(ManagerId, ReportDate);

		Assert.Equal(
			[
				new DailySalesReportRow(
					HomeBranch,
					ReportDate,
					new Money(100m, "INR"),
					new Money(30m, "INR"))
			],
			report);
	}

	[Fact]
	public async Task GetReportAsync_IncludesOnlySalesRecordedOnTheRequestedDate()
	{
		var onDate = CreatePaidBasket(
			HomeBranch, 100m, 0m, "INR", OnReportDate);
		var dayBefore = CreatePaidBasket(
			HomeBranch, 40m, 0m, "INR", OnReportDate.AddDays(-1));
		var dayAfter = CreatePaidBasket(
			HomeBranch, 25m, 0m, "INR", OnReportDate.AddDays(1));
		var undated = CreatePaidBasket(
			HomeBranch, 60m, 0m, "INR", occurredAtUtc: null);
		var service = CreateService(
			CreateStaff(ManagerId, StaffRole.BranchManager, HomeBranch),
			[onDate, dayBefore, dayAfter, undated]);

		var row = Assert.Single(
			await service.GetReportAsync(ManagerId, ReportDate));
		Assert.Equal(new Money(100m, "INR"), row.TotalSales);
		Assert.Empty(await service.GetReportAsync(
			ManagerId, ReportDate.AddDays(-2)));
	}

	[Fact]
	public async Task GetReportAsync_WhenNoPaidHomeTransactions_ReturnsEmpty()
	{
		var openBasket = CreateOpenBasket(HomeBranch, currency: "INR");
		var service = CreateService(
			CreateStaff(ManagerId, StaffRole.BranchManager, HomeBranch),
			[openBasket]);

		Assert.Empty(await service.GetReportAsync(ManagerId, ReportDate));
	}

	[Theory]
	[InlineData(StaffRole.CounterClerk)]
	[InlineData(StaffRole.Auditor)]
	[InlineData(StaffRole.Administrator)]
	public async Task GetReportAsync_NonManagersAreDenied(StaffRole role)
	{
		var service = CreateService(
			CreateStaff(ManagerId, role, HomeBranch),
			[]);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(ManagerId, ReportDate));
	}

	[Fact]
	public async Task GetReportAsync_InactiveManagerIsDenied()
	{
		var manager = CreateStaff(ManagerId, StaffRole.BranchManager, HomeBranch);
		manager.Deactivate();
		var service = CreateService(manager, []);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(ManagerId, ReportDate));
	}

	[Fact]
	public async Task GetReportAsync_MissingStaffIdIsDeniedWithoutLoading()
	{
		var repository = new FakeApexPostRepository();
		var service = TestServices.DailySalesReport(repository);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(" ", ReportDate));

		Assert.Equal(0, repository.LoadCount);
	}

	private static DailyBranchSalesReportApplicationService CreateService(
		Staff staff,
		IEnumerable<TransactionBasket> baskets) =>
		TestServices.DailySalesReport(new FakeApexPostRepository(new ApexPostSnapshot(
			[],
			[staff],
			[],
			[],
			[],
			[],
			[],
			[],
			baskets)));

	private static Staff CreateStaff(
		string id,
		StaffRole role,
		BranchCode homeBranchCode) =>
		new(id, $"Test {id}", $"{id}@example.test", role, homeBranchCode);

	private static TransactionBasket CreatePaidBasket(
		BranchCode branchCode,
		decimal goodsTotal,
		decimal serviceTotal,
		string currency)
		=> CreatePaidBasket(
			branchCode, goodsTotal, serviceTotal, currency, OnReportDate);

	private static TransactionBasket CreatePaidBasket(
		BranchCode branchCode,
		decimal goodsTotal,
		decimal serviceTotal,
		string currency,
		DateTimeOffset? occurredAtUtc)
	{
		var taxRates = new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = 0m
		};
		var basket = new TransactionBasket(
			currency, taxRates, branchCode, occurredAtUtc);
		if (goodsTotal > 0)
		{
			basket.AddLineItem(new TransactionLineItem(
				"Goods",
				TransactionItemType.Goods,
				new Money(goodsTotal, currency),
				1,
				TaxCategory.Standard,
				new Sku("SKU-1")));
		}

		if (serviceTotal > 0)
		{
			basket.AddLineItem(new TransactionLineItem(
				"Service",
				TransactionItemType.Service,
				new Money(serviceTotal, currency),
				1,
				TaxCategory.Standard));
		}

		basket.Pay();
		return basket;
	}

	private static TransactionBasket CreateOpenBasket(
		BranchCode branchCode,
		string currency)
	{
		var taxRates = new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = 0m
		};
		return new TransactionBasket(currency, taxRates, branchCode);
	}
}