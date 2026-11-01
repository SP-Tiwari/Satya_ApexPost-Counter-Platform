using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql.Configurations;

public sealed class ProductCategoryConfiguration
	: IEntityTypeConfiguration<ProductCategoryRecord>
{
	public void Configure(EntityTypeBuilder<ProductCategoryRecord> builder)
	{
		builder.ToTable("ProductCategories");
		builder.HasKey(category => category.Id);
		builder.Property(category => category.Id)
			.HasConversion<byte>()
			.HasColumnName("ProductCategoryId")
			.ValueGeneratedNever();
		builder.Property(category => category.Name).HasMaxLength(50);
		builder.HasAlternateKey(category => category.Name)
			.HasName("UQ_ProductCategories_Name");
		builder.HasAuditColumns();

		builder.HasData(
			new ProductCategoryRecord(ProductCategory.Packaging, "Packaging"),
			new ProductCategoryRecord(ProductCategory.Stationery, "Stationery"),
			new ProductCategoryRecord(
				ProductCategory.OfficeSupplies, "Office Supplies"));
	}
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
	public void Configure(EntityTypeBuilder<Product> builder)
	{
		builder.ToTable("Products", table =>
		{
			table.HasCheckConstraint(
				"CK_Products_UnitPrice", "[UnitPrice] >= 0");
			table.HasCheckConstraint(
				"CK_Products_TaxCategory",
				"[TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')");
		});
		builder.Property<int>("ProductId").UseIdentityColumn();
		builder.HasKey("ProductId");

		builder.Property(product => product.Sku).IsSku();
		builder.HasAlternateKey(product => product.Sku)
			.HasName("UQ_Products_Sku");

		builder.Property(product => product.Name).HasMaxLength(200);
		builder.ComplexProperty(
			product => product.UnitPrice,
			money => money.IsMoney("UnitPrice", "Currency"));
		builder.Property(product => product.TaxCategory)
			.IsEnumCode("TaxCategory");

		builder.Property(product => product.Category)
			.HasConversion<byte>()
			.HasColumnName("ProductCategoryId");
		builder.HasOne<ProductCategoryRecord>()
			.WithMany()
			.HasForeignKey(product => product.Category)
			.OnDelete(DeleteBehavior.Restrict);

		builder.HasAuditColumns();
	}
}

public sealed class BranchStockConfiguration
	: IEntityTypeConfiguration<BranchStock>
{
	public void Configure(EntityTypeBuilder<BranchStock> builder)
	{
		builder.ToTable("BranchStock", table =>
			table.HasCheckConstraint(
				"CK_BranchStock_Quantity", "[Quantity] >= 0"));

		builder.Property(stock => stock.BranchCode).IsBranchCode();
		builder.Property(stock => stock.ProductSku).IsSku();
		builder.HasKey(stock => new { stock.BranchCode, stock.ProductSku });

		builder.Property(stock => stock.Quantity).HasDefaultValue(0);
		builder.HasIndex(stock => stock.Quantity);
		builder.Property<byte[]>("RowVersion").IsRowVersion().IsRequired();
		builder.HasAuditColumns();

		builder.HasOne<Branch>()
			.WithMany()
			.HasForeignKey(stock => stock.BranchCode)
			.HasPrincipalKey(branch => branch.Code)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<Product>()
			.WithMany()
			.HasForeignKey(stock => stock.ProductSku)
			.HasPrincipalKey(product => product.Sku)
			.OnDelete(DeleteBehavior.Restrict);
	}
}

public sealed class StockAdjustmentConfiguration
	: IEntityTypeConfiguration<StockAdjustment>
{
	public void Configure(EntityTypeBuilder<StockAdjustment> builder)
	{
		builder.ToTable("StockAdjustments", table =>
			table.HasCheckConstraint(
				"CK_StockAdjustments_Delta", "[Delta] <> 0"));
		builder.HasKey(adjustment => adjustment.Id);
		builder.Property(adjustment => adjustment.Id)
			.HasColumnName("StockAdjustmentId")
			.ValueGeneratedNever();

		builder.Property(adjustment => adjustment.BranchCode).IsBranchCode();
		builder.Property(adjustment => adjustment.ProductSku).IsSku();
		builder.Property(adjustment => adjustment.Delta);
		builder.Property(adjustment => adjustment.Reason).HasMaxLength(500);
		builder.Property(adjustment => adjustment.ChangedByStaffId)
			.IsCode(50, "ChangedByStaffId");
		builder.Property(adjustment => adjustment.OccurredAtUtc)
			.IsUtcDateTime("OccurredAtUtc");
		builder.HasAuditColumns();

		builder.HasOne<BranchStock>()
			.WithMany()
			.HasForeignKey(adjustment =>
				new { adjustment.BranchCode, adjustment.ProductSku })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<Staff>()
			.WithMany()
			.HasForeignKey(adjustment => adjustment.ChangedByStaffId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
