using ApexPost.Domain.Reporting;
using ApexPost.Application.Tests.TestDoubles;

namespace ApexPost.Application.Tests.Services;

public sealed class ShippingVolumeReportApplicationServiceTests
{
	private const string ManagerId = "staff-1";

	[Fact]
	public async Task GetReportAsync_CountsPersistedItemsByZoneAndService()
	{
		var service = CreateService(
			CreateStaff(StaffRole.Auditor),
			[
				CreateItem("ZONE-B", ShippingServiceLevel.Express),
				CreateItem("ZONE-A", ShippingServiceLevel.Standard),
				CreateItem("ZONE-A", ShippingServiceLevel.Standard),
				CreateItem("ZONE-A", ShippingServiceLevel.Express)
			]);

		var report = await service.GetReportAsync(ManagerId);

		Assert.Equal(
			[
				new ShippingVolumeReportRow(
					"ZONE-A", ShippingServiceLevel.Standard, 2),
				new ShippingVolumeReportRow(
					"ZONE-A", ShippingServiceLevel.Express, 1),
				new ShippingVolumeReportRow(
					"ZONE-B", ShippingServiceLevel.Express, 1)
			],
			report);
	}

	[Fact]
	public async Task GetReportAsync_EmptyDataReturnsNoRows()
	{
		var service = CreateService(CreateStaff(StaffRole.Administrator), []);

		Assert.Empty(await service.GetReportAsync(ManagerId));
	}

	[Theory]
	[InlineData(StaffRole.CounterClerk)]
	[InlineData(StaffRole.BranchManager)]
	public async Task GetReportAsync_UnauthorizedRolesAreDenied(StaffRole role)
	{
		var service = CreateService(
			new Staff(ManagerId, "Test", "t@example.test", role,
				new BranchCode("BR01")),
			[]);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(ManagerId));
	}

	[Fact]
	public async Task GetReportAsync_InactiveAuditorIsDenied()
	{
		var manager = CreateStaff(StaffRole.Auditor);
		manager.Deactivate();
		var service = CreateService(manager, []);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(ManagerId));
	}

	[Fact]
	public async Task GetReportAsync_UnknownStaffIsDenied()
	{
		var service = CreateService(CreateStaff(StaffRole.Administrator), []);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync("unknown-staff"));
	}

	[Fact]
	public async Task GetReportAsync_MissingStaffIdIsDeniedWithoutLoading()
	{
		var repository = new FakeApexPostRepository();
		var service = TestServices.ShippingVolumeReport(repository);

		await Assert.ThrowsAsync<UnauthorizedAccessException>(
			() => service.GetReportAsync(" "));

		Assert.Equal(0, repository.LoadCount);
	}

	private static ShippingVolumeReportApplicationService CreateService(
		Staff staff,
		IEnumerable<ShippingItem> items) =>
		TestServices.ShippingVolumeReport(new FakeApexPostRepository(new ApexPostSnapshot(
			[], [staff], [], [], items, [], [], [], [])));

	private static Staff CreateStaff(StaffRole role) =>
		new(ManagerId, "Test", "t@example.test", role,
			new BranchCode("BR01"));

	private static ShippingItem CreateItem(
		string zone,
		ShippingServiceLevel level) =>
		new(
			Guid.NewGuid().ToString(),
			ShippingItemType.Parcel,
			new Weight(1m),
			new Dimensions(1m, 1m, 1m),
			zone,
			level);
}
