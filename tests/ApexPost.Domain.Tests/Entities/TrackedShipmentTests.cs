using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;

namespace ApexPost.Domain.Tests.Entities;

public class TrackedShipmentTests
{
	private const string ScanId = "scan::3f2b8c1e-9d4a-4b6f-a1c2-5e7d9f0b1a23";

	[Fact]
	public void New_StartsCreatedWithNoScans()
	{
		var shipment = CreateShipment();

		Assert.Equal(ShipmentStatus.Created, shipment.Status);
		Assert.True(shipment.CanBeDeleted);
		Assert.Empty(shipment.Scans);
	}

	[Theory]
	[InlineData("APX-1234567")]
	[InlineData("APX-123456789")]
	[InlineData("apx-12345678")]
	[InlineData("APX-1234567A")]
	[InlineData("APX-١٢٣٤٥٦٧٨")]
	public void New_InvalidTrackingNumber_Throws(string trackingNumber)
	{
		Assert.Throws<ArgumentException>(() => CreateShipment(trackingNumber));
	}

	[Fact]
	public void ChangeStatus_AfterPickup_CanNoLongerBeDeleted()
	{
		var shipment = CreateShipment();

		shipment.ChangeStatus(ShipmentStatus.PickedUp);

		Assert.Equal(ShipmentStatus.PickedUp, shipment.Status);
		Assert.False(shipment.CanBeDeleted);
	}

	[Fact]
	public void ChangeStatus_SkippedStep_ThrowsAndKeepsTheStatus()
	{
		var shipment = CreateShipment();

		Assert.Throws<InvalidOperationException>(() =>
			shipment.ChangeStatus(ShipmentStatus.Delivered));
		Assert.Equal(ShipmentStatus.Created, shipment.Status);
	}

	[Fact]
	public void AddScan_KeepsTheStatus_AndRefusesTheSameIdInAnyCase()
	{
		var shipment = CreateShipment();

		shipment.AddScan(CreateScan(ScanId, ShipmentScanType.Delivered));

		Assert.Equal(ShipmentStatus.Created, shipment.Status);
		Assert.Throws<InvalidOperationException>(() =>
			shipment.AddScan(CreateScan(
				"scan::" + ScanId["scan::".Length..].ToUpperInvariant(), ShipmentScanType.Pickup)));
		Assert.Single(shipment.Scans);
	}

	[Theory]
	[InlineData("3f2b8c1e-9d4a-4b6f-a1c2-5e7d9f0b1a23")]
	[InlineData("scan::not-a-guid")]
	[InlineData("scan::00000000-0000-0000-0000-000000000000")]
	[InlineData("scan::3f2b8c1e9d4a4b6fa1c25e7d9f0b1a23")]
	[InlineData("SCAN::3f2b8c1e-9d4a-4b6f-a1c2-5e7d9f0b1a23")]
	public void ScanId_NotTheDeviceFormat_IsInvalid(string scanId)
	{
		Assert.False(TrackedShipmentScan.IsValidScanId(scanId));
		Assert.Throws<ArgumentException>(() => CreateScan(scanId, ShipmentScanType.Pickup));
	}

	[Fact]
	public void Scan_StoresItsTimeInUtc()
	{
		var scan = CreateScan(ScanId, ShipmentScanType.Pickup,
			new DateTimeOffset(2026, 11, 7, 15, 30, 0, TimeSpan.FromHours(5.5)));

		Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero), scan.ScannedAtUtc);
		Assert.Equal(TimeSpan.Zero, scan.ScannedAtUtc.Offset);
	}

	private static TrackedShipment CreateShipment(string trackingNumber = "APX-12345678") =>
		new(trackingNumber, "DEMO Recipient", "+919876543210", "1 Demo Street", "Pune", new Weight(1.5m));

	private static TrackedShipmentScan CreateScan(
		string scanId,
		ShipmentScanType scanType,
		DateTimeOffset? scannedAtUtc = null) =>
		new(scanId, scanType, "COURIER-DEMO-07", "Pune hub",
			scannedAtUtc ?? new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero), null);
}
