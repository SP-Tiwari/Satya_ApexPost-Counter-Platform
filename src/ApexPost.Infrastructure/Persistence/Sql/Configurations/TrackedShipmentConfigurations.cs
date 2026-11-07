using ApexPost.Domain.Entities;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql.Configurations;

// The Week 4 API's shipments and scans: two new tables, added by the
// AddTrackedShipments migration without changing any Week 3 table. See ADR 0028.
public sealed class TrackedShipmentConfiguration
	: IEntityTypeConfiguration<TrackedShipment>
{
	public void Configure(EntityTypeBuilder<TrackedShipment> builder)
	{
		builder.ToTable("TrackedShipments", table =>
		{
			table.HasCheckConstraint(
				"CK_TrackedShipments_TrackingNumber",
				"[TrackingNumber] LIKE 'APX-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'");
			table.HasCheckConstraint("CK_TrackedShipments_Weight", "[WeightKg] > 0");
			table.HasCheckConstraint(
				"CK_TrackedShipments_Status",
				"[Status] IN ('Created', 'PickedUp', 'InTransit', 'OutForDelivery', 'Delivered', 'Failed')");
		});

		// The tracking number is the natural key, so a duplicate create is a
		// primary-key conflict.
		builder.HasKey(shipment => shipment.TrackingNumber);
		builder.Property(shipment => shipment.TrackingNumber)
			.IsCode(12, "TrackingNumber")
			.ValueGeneratedNever();

		builder.Property(shipment => shipment.RecipientName).HasMaxLength(100);
		builder.Property(shipment => shipment.RecipientPhone)
			.IsUnicode(false)
			.HasMaxLength(20);
		builder.Property(shipment => shipment.RecipientAddress).HasMaxLength(200);
		builder.Property(shipment => shipment.RecipientCity).HasMaxLength(100);
		builder.Property(shipment => shipment.Weight)
			.HasConversion(new WeightConverter())
			.HasColumnName("WeightKg")
			.HasPrecision(10, 3);
		builder.Property(shipment => shipment.Status).IsEnumCode("Status");

		// Two status changes at once cannot both pass the transition rule.
		builder.Property<byte[]>("RowVersion").IsRowVersion().IsRequired();
		builder.HasAuditColumns();

		builder.HasMany(shipment => shipment.Scans)
			.WithOne()
			.HasForeignKey("TrackingNumber")
			.IsRequired()
			.OnDelete(DeleteBehavior.Cascade);
	}
}

public sealed class TrackedShipmentScanConfiguration
	: IEntityTypeConfiguration<TrackedShipmentScan>
{
	public void Configure(EntityTypeBuilder<TrackedShipmentScan> builder)
	{
		builder.ToTable("TrackedShipmentScans", table =>
		{
			table.HasCheckConstraint(
				"CK_TrackedShipmentScans_ScanId",
				"[ScanId] LIKE 'scan::________-____-____-____-____________'");
			table.HasCheckConstraint(
				"CK_TrackedShipmentScans_ScanType",
				"[ScanType] IN ('Pickup', 'InTransit', 'Delivered', 'Failed')");
		});

		// The device's scan ID is the key, so the database also refuses a
		// second copy of one scan.
		builder.HasKey(scan => scan.ScanId);
		builder.Property(scan => scan.ScanId)
			.IsCode(42, "ScanId")
			.ValueGeneratedNever();
		builder.Property<string>("TrackingNumber").IsCode(12, "TrackingNumber");
		builder.Property(scan => scan.ScanType).IsEnumCode("ScanType");
		builder.Property(scan => scan.CourierId).IsCode(50, "CourierId");
		builder.Property(scan => scan.Location).HasMaxLength(200);
		builder.Property(scan => scan.ScannedAtUtc).IsUtcDateTime("ScannedAtUtc");
		builder.Property(scan => scan.Notes).HasMaxLength(500);
		builder.HasAuditColumns();
	}
}
