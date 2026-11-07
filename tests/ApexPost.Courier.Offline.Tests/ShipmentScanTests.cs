using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Offline.Tests;

public sealed class ShipmentScanTests
{
	[Fact]
	public void Record_CreatesAPendingScanWithAScanGuidId()
	{
		var scan = Scans.New(notes: "  Left at reception  ");

		Assert.Equal(SyncStatus.Pending, scan.SyncStatus);
		Assert.StartsWith("scan::", scan.Id);
		Assert.True(Guid.TryParseExact(scan.Id["scan::".Length..], "D", out _));
		Assert.Equal("Left at reception", scan.Notes);
	}

	[Fact]
	public void Record_GivesEveryScanADifferentId() =>
		Assert.NotEqual(Scans.New().Id, Scans.New().Id);

	[Theory]
	[InlineData("scan::not-a-guid")]
	[InlineData("scan::00000000-0000-0000-0000-000000000000")]
	[InlineData("SCAN::3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
	[InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
	[InlineData("scan::3f2504e04f8911d39a0c0305e82c3301")]
	[InlineData("")]
	public void Id_RejectsAnythingButScanGuid(string id) =>
		Assert.Throws<ArgumentException>(() => Scans.New() with { Id = id });

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void RequiredText_RejectsBlankValues(string blank)
	{
		Assert.Throws<ArgumentException>(() => Scans.New(trackingNumber: blank));
		Assert.Throws<ArgumentException>(() => Scans.New(courierId: blank));
		Assert.Throws<ArgumentException>(() => Scans.New() with { Location = blank });
	}

	[Fact]
	public void ScannedAtUtc_RejectsANonUtcOffset() =>
		Assert.Throws<ArgumentException>(() => Scans.New() with
		{
			ScannedAtUtc = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(5.5))
		});

	[Fact]
	public void Enums_RejectUndefinedValues()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => Scans.New() with { ScanType = (ScanType)42 });
		Assert.Throws<ArgumentOutOfRangeException>(() => Scans.New() with { SyncStatus = (SyncStatus)42 });
	}

	[Fact]
	public void MarkSyncedAndMarkSyncFailed_ChangeOnlyTheSyncStatus()
	{
		var scan = Scans.New();

		var synced = scan.MarkSynced();
		var failed = scan.MarkSyncFailed();

		Assert.Equal(SyncStatus.Synced, synced.SyncStatus);
		Assert.Equal(SyncStatus.Failed, failed.SyncStatus);
		Assert.Equal(scan with { SyncStatus = SyncStatus.Synced }, synced);
		Assert.Equal(SyncStatus.Pending, scan.SyncStatus);
	}

	[Fact]
	public void BlankNotes_AreStoredAsNoNotes() =>
		Assert.Null(Scans.New(notes: "   ").Notes);
}
