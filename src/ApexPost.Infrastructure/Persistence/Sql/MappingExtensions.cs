using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Column conventions shared by the entity configurations, so each type is
// mapped the same way on every table.
internal static class MappingExtensions
{
	private const int EnumCodeLength = 20;

	public static PropertyBuilder<BranchCode> IsBranchCode(
		this PropertyBuilder<BranchCode> property,
		string columnName = "BranchCode") =>
		property
			.HasConversion(new BranchCodeConverter())
			.HasColumnName(columnName)
			.IsUnicode(false)
			.HasMaxLength(10);

	public static PropertyBuilder<Sku> IsSku(
		this PropertyBuilder<Sku> property,
		string columnName = "Sku") =>
		property
			.HasConversion(new SkuConverter())
			.HasColumnName(columnName)
			.IsUnicode(false)
			.HasMaxLength(30);

	// Identifiers and codes hold ASCII text, so they are varchar. Matching the
	// parameter type avoids implicit conversions on indexed columns.
	public static PropertyBuilder<string> IsCode(
		this PropertyBuilder<string> property,
		int maxLength,
		string columnName) =>
		property
			.HasColumnName(columnName)
			.IsUnicode(false)
			.HasMaxLength(maxLength);

	public static PropertyBuilder<TEnum> IsEnumCode<TEnum>(
		this PropertyBuilder<TEnum> property,
		string columnName)
		where TEnum : struct, Enum =>
		property
			.HasConversion<string>()
			.HasColumnName(columnName)
			.IsUnicode(false)
			.HasMaxLength(EnumCodeLength);

	public static ComplexPropertyBuilder<Money> IsMoney(
		this ComplexPropertyBuilder<Money> money,
		string amountColumn,
		string currencyColumn)
	{
		money.Property(value => value.Amount)
			.HasConversion(new MoneyAmountConverter())
			.HasColumnName(amountColumn)
			.HasPrecision(18, 2);
		money.Property(value => value.Currency)
			.HasColumnName(currencyColumn)
			.HasColumnType("char(3)")
			.IsUnicode(false)
			.HasMaxLength(3)
			.IsFixedLength();
		return money;
	}

	public static PropertyBuilder<DateTimeOffset> IsUtcDateTime(
		this PropertyBuilder<DateTimeOffset> property,
		string columnName) =>
		property
			.HasConversion(new UtcDateTimeOffsetConverter())
			.HasColumnName(columnName)
			.HasColumnType("datetime2(3)");

	// Defaults to true while still storing a deliberate false: EF Core leaves
	// out a value equal to the sentinel, so the sentinel must be true here.
	public static PropertyBuilder<bool> IsActiveFlag(
		this PropertyBuilder<bool> property) =>
		property
			.HasColumnName("IsActive")
			.HasDefaultValue(true)
			.HasSentinel(true);

	// CreatedAtUtc, CreatedBy and ModifiedAtUtc are shadow properties: the
	// domain has no use for them, but every table carries them.
	public static EntityTypeBuilder<TEntity> HasAuditColumns<TEntity>(
		this EntityTypeBuilder<TEntity> builder)
		where TEntity : class
	{
		builder.Property<DateTime>("CreatedAtUtc")
			.HasColumnType("datetime2(3)")
			.HasDefaultValueSql("SYSUTCDATETIME()");
		builder.Property<string>("CreatedBy")
			.IsUnicode(false)
			.HasMaxLength(128)
			.HasDefaultValueSql("SUSER_SNAME()")
			.IsRequired();
		builder.Property<DateTime?>("ModifiedAtUtc")
			.HasColumnType("datetime2(3)");
		return builder;
	}

	public static OwnedNavigationBuilder<TOwner, TOwned> HasAuditColumns<TOwner, TOwned>(
		this OwnedNavigationBuilder<TOwner, TOwned> builder)
		where TOwner : class
		where TOwned : class
	{
		builder.Property<DateTime>("CreatedAtUtc")
			.HasColumnType("datetime2(3)")
			.HasDefaultValueSql("SYSUTCDATETIME()");
		builder.Property<string>("CreatedBy")
			.IsUnicode(false)
			.HasMaxLength(128)
			.HasDefaultValueSql("SUSER_SNAME()")
			.IsRequired();
		builder.Property<DateTime?>("ModifiedAtUtc")
			.HasColumnType("datetime2(3)");
		return builder;
	}
}
