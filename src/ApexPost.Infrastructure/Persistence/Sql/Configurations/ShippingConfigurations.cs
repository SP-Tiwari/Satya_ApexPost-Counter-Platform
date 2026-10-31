using ApexPost.Domain.Entities;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql.Configurations;

public sealed class ShippingZoneConfiguration
	: IEntityTypeConfiguration<ShippingZone>
{
	public void Configure(EntityTypeBuilder<ShippingZone> builder)
	{
		builder.ToTable("ShippingZones");
		builder.Property<int>("ShippingZoneId").UseIdentityColumn();
		builder.HasKey("ShippingZoneId");

		builder.Property(zone => zone.Code).IsCode(50, "ZoneCode");
		builder.HasAlternateKey(zone => zone.Code)
			.HasName("UQ_ShippingZones_ZoneCode");
		builder.Property(zone => zone.Name).HasMaxLength(100);
		builder.HasAuditColumns();
	}
}

public sealed class ShippingRateConfiguration
	: IEntityTypeConfiguration<ShippingRate>
{
	public void Configure(EntityTypeBuilder<ShippingRate> builder)
	{
		builder.ToTable("ShippingRates", table =>
		{
			table.HasCheckConstraint(
				"CK_ShippingRates_BasePrice", "[BasePrice] >= 0");
			table.HasCheckConstraint(
				"CK_ShippingRates_PerKilogramCharge",
				"[PerKilogramCharge] >= 0");
			table.HasCheckConstraint(
				"CK_ShippingRates_Currency",
				"[BasePriceCurrency] = [PerKilogramChargeCurrency]");
			table.HasCheckConstraint(
				"CK_ShippingRates_ServiceLevel",
				"[ServiceLevel] IN ('Standard', 'Express', 'Registered')");
		});

		builder.Property(rate => rate.DestinationZone).IsCode(50, "ZoneCode");
		builder.Property(rate => rate.ServiceLevel).IsEnumCode("ServiceLevel");
		builder.HasKey(rate => new { rate.DestinationZone, rate.ServiceLevel });

		builder.ComplexProperty(
			rate => rate.BasePrice,
			money => money.IsMoney("BasePrice", "BasePriceCurrency"));
		builder.ComplexProperty(
			rate => rate.PerKilogramCharge,
			money => money.IsMoney(
				"PerKilogramCharge", "PerKilogramChargeCurrency"));
		builder.HasAuditColumns();

		builder.HasOne<ShippingZone>()
			.WithMany()
			.HasForeignKey(rate => rate.DestinationZone)
			.HasPrincipalKey(zone => zone.Code)
			.OnDelete(DeleteBehavior.Restrict);
	}
}

public sealed class ShipmentConfiguration
	: IEntityTypeConfiguration<ShippingItem>
{
	public void Configure(EntityTypeBuilder<ShippingItem> builder)
	{
		builder.ToTable("Shipments", table =>
		{
			// The upper weight limit depends on the service level and comes
			// from configuration, so ShippingWeightLimitRule enforces it.
			table.HasCheckConstraint("CK_Shipments_Weight", "[WeightKg] > 0");
			table.HasCheckConstraint(
				"CK_Shipments_Dimensions",
				"[LengthCm] > 0 AND [WidthCm] > 0 AND [HeightCm] > 0");
			table.HasCheckConstraint(
				"CK_Shipments_ShipmentType",
				"[ShipmentType] IN ('Parcel', 'Letter')");
			table.HasCheckConstraint(
				"CK_Shipments_ServiceLevel",
				"[ServiceLevel] IN ('Standard', 'Express', 'Registered')");
		});
		builder.HasKey(shipment => shipment.Id);
		builder.Property(shipment => shipment.Id)
			.IsCode(50, "ShipmentId")
			.ValueGeneratedNever();

		builder.Property(shipment => shipment.Type).IsEnumCode("ShipmentType");
		builder.Property(shipment => shipment.Weight)
			.HasConversion(new WeightConverter())
			.HasColumnName("WeightKg")
			.HasPrecision(10, 3);
		builder.ComplexProperty(shipment => shipment.Dimensions, dimensions =>
		{
			dimensions.Property(value => value.LengthCm)
				.HasColumnName("LengthCm")
				.HasPrecision(8, 2);
			dimensions.Property(value => value.WidthCm)
				.HasColumnName("WidthCm")
				.HasPrecision(8, 2);
			dimensions.Property(value => value.HeightCm)
				.HasColumnName("HeightCm")
				.HasPrecision(8, 2);
		});
		builder.Property(shipment => shipment.DestinationZone)
			.IsCode(50, "ZoneCode");
		builder.Property(shipment => shipment.ServiceLevel)
			.IsEnumCode("ServiceLevel");
		builder.HasAuditColumns();

		builder.HasOne<ShippingZone>()
			.WithMany()
			.HasForeignKey(shipment => shipment.DestinationZone)
			.HasPrincipalKey(zone => zone.Code)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
