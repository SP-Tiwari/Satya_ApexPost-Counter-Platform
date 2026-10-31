using ApexPost.Domain.Reporting;
using ApexPost.Application.Tests.TestDoubles;

namespace ApexPost.Application.Tests.Services;

public sealed class LowStockReportApplicationServiceTests
{
	private static readonly BranchCode HomeBranch = new("BR01");
	private static readonly BranchCode OtherBranch = new("BR02");
	private const string ManagerId = "manager-1";

	[Fact]
	public async Task GetReportAsync_ActiveManagerSeesOnlyLowStockInHomeBranch()
	{
		var repository = CreateRepository(
			CreateManager(),
			[
				new BranchStock(HomeBranch, new Sku("LOCAL-LOW"), 2),
				new BranchStock(HomeBranch, new Sku("LOCAL-OK"), 4),
				new BranchStock(OtherBranch, new Sku("FOREIGN-LOW"), 0)
			]);
		var service = TestServices.LowStockReport(repository, 2);

		var report = await service.GetReportAsync(ManagerId);

		var row = Assert.Single(report);
		Assert.Equal(HomeBranch, row.BranchCode);
		Assert.Equal(new Sku("LOCAL-LOW"), row.ProductSku);
		Assert.Equal(2, row.Quantity);
		Assert.Equal(2, row.Threshold);
	}

	[Fact]
	public async Task GetReportAsync_NonManagerIsDenied()
	{
		var clerk = CreateStaff(ManagerId, StaffRole.CounterClerk, HomeBranch);
		var service = CreateService(clerk);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(ManagerId));
	}

	[Fact]
	public async Task GetReportAsync_InactiveManagerIsDenied()
	{
		var manager = CreateManager();
		manager.Deactivate();
		var service = CreateService(manager);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(ManagerId));
	}

	[Fact]
	public async Task GetReportAsync_UnknownStaffIsDenied()
	{
		var service = CreateService(CreateManager());

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync("unknown-staff"));
	}

	[Fact]
	public async Task GetReportAsync_MissingStaffIdIsDenied()
	{
		var repository = new FakeApexPostRepository();
		var service = TestServices.LowStockReport(repository, 2);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(" "));

		Assert.Equal(0, repository.LoadCount);
	}

	[Fact]
	public async Task GetReportAsync_MissingThresholdFailsAfterAuthorization()
	{
		var service = TestServices.LowStockReport(
			CreateRepository(CreateManager(), []), null);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.GetReportAsync(ManagerId));
	}

	[Fact]
	public void Constructor_NegativeThresholdIsRejected()
	{
		var repository = new FakeApexPostRepository();

		Assert.Throws<ArgumentOutOfRangeException>(
			() => TestServices.LowStockReport(repository, -1));
	}

	private static LowStockReportApplicationService CreateService(
		Staff staff) =>
		TestServices.LowStockReport(CreateRepository(staff, []), 2);

	private static IApexPostRepository CreateRepository(
		Staff staff,
		IEnumerable<BranchStock> stocks) =>
		new FakeApexPostRepository(new ApexPostSnapshot(
			[],
			[staff],
			[],
			stocks,
			[],
			[],
			[],
			[],
			[]));

	private static Staff CreateManager() =>
		CreateStaff(ManagerId, StaffRole.BranchManager, HomeBranch);

	private static Staff CreateStaff(
		string id,
		StaffRole role,
		BranchCode branchCode) =>
		new(id, $"Test {id}", $"{id}@example.test", role, branchCode);
}
