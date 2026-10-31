using ApexPost.Application.Tests.TestDoubles;

namespace ApexPost.Application.Tests.Services;

// The list read path. Issuing money orders (threshold, fee, verification) is
// covered by CounterBasketApplicationServiceTests.
public sealed class MoneyOrderApplicationServiceTests
{
	private static readonly Money Threshold = new(1000m, "INR");

	[Fact]
	public async Task GetMoneyOrdersAsync_ReturnsOnlyKnownOrdersForManagersHomeBranch()
	{
		var manager = CreateStaff(
			"manager-1", StaffRole.BranchManager, "BR01");
		var otherBranchOrder = new MoneyOrder(
			new Money(200m, "INR"), "Other sender", "Recipient",
			new Money(10m, "INR"), processingBranchCode: new BranchCode("BR02"));
		var ownBranchOrder = new MoneyOrder(
			new Money(300m, "INR"), "Local sender", "Recipient",
			new Money(10m, "INR"), processingBranchCode: new BranchCode("BR01"));
		var unknownBranchOrder = new MoneyOrder(
			new Money(400m, "INR"), "Legacy sender", "Recipient",
			new Money(10m, "INR"));
		var repository = new FakeApexPostRepository(
			CreateSnapshot(
				[manager, CreateStaff(
					"manager-2", StaffRole.BranchManager, "BR02")],
				[ownBranchOrder, otherBranchOrder, unknownBranchOrder]));
		var service = TestServices.MoneyOrders(repository, Threshold);

		var orders = await service.GetMoneyOrdersAsync("manager-1");

		Assert.Equal([ownBranchOrder], orders);
	}

	[Fact]
	public async Task GetMoneyOrdersAsync_RejectsInactiveStaff()
	{
		var inactiveManager = CreateStaff(
			"manager-1", StaffRole.BranchManager, "BR01");
		inactiveManager.Deactivate();
		var service = TestServices.MoneyOrders(
			new FakeApexPostRepository(CreateSnapshot([inactiveManager])),
			Threshold);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetMoneyOrdersAsync("manager-1"));
	}

	[Theory]
	[InlineData("clerk-1")]
	[InlineData("unknown")]
	[InlineData(" ")]
	public async Task GetMoneyOrdersAsync_RejectsNonManagerOrUnknownStaff(string staffId)
	{
		var service = TestServices.MoneyOrders(
			new FakeApexPostRepository(CreateSnapshot()),
			Threshold);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetMoneyOrdersAsync(staffId));
	}

	[Fact]
	public void IsConfigured_ReflectsWhetherAThresholdIsConfigured()
	{
		var repository = new FakeApexPostRepository(CreateSnapshot());

		Assert.True(TestServices.MoneyOrders(repository, Threshold).IsConfigured);
		Assert.False(TestServices.MoneyOrders(repository, null).IsConfigured);
	}

	private static ApexPostSnapshot CreateSnapshot(
		IEnumerable<Staff>? staff = null,
		IEnumerable<MoneyOrder>? moneyOrders = null) =>
		new(
			[],
			staff ??
			[
				CreateStaff("manager-1", StaffRole.BranchManager, "BR01"),
				CreateStaff("clerk-1", StaffRole.CounterClerk, "BR01")
			],
			[],
			[],
			[],
			[],
			moneyOrders ?? [],
			[],
			[]);

	private static Staff CreateStaff(
		string id,
		StaffRole role,
		string branchCode) =>
		new(
			id,
			id,
			$"{id}@example.test",
			role,
			new BranchCode(branchCode));
}
