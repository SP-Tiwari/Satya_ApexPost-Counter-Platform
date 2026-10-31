using ApexPost.Application.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql.Configurations;

public sealed class AuditEntryConfiguration
	: IEntityTypeConfiguration<AuditRecord>
{
	public void Configure(EntityTypeBuilder<AuditRecord> builder)
	{
		builder.ToTable("AuditEntries", table =>
		{
			table.HasCheckConstraint(
				"CK_AuditEntries_Outcome",
				"[Outcome] IN ('Succeeded', 'Rejected', 'Failed')");
			table.HasCheckConstraint(
				"CK_AuditEntries_StatusCode",
				"[StatusCode] BETWEEN 100 AND 599");
			table.HasCheckConstraint(
				"CK_AuditEntries_DurationMilliseconds",
				"[DurationMilliseconds] >= 0");
		});
		builder.Property<long>("AuditEntryId").UseIdentityColumn();
		builder.HasKey("AuditEntryId");

		builder.Property(entry => entry.TimestampUtc)
			.IsUtcDateTime("TimestampUtc");
		builder.Property(entry => entry.Controller)
			.IsCode(100, "Controller");
		builder.Property(entry => entry.Action).IsCode(100, "Action");
		builder.Property(entry => entry.Description).HasMaxLength(300);
		builder.Property(entry => entry.Outcome).IsCode(20, "Outcome");
		builder.Property(entry => entry.RouteValues)
			.HasConversion(
				new RouteValuesConverter(),
				new ValueComparer<IReadOnlyDictionary<string, string>>(
					(left, right) => ReferenceEquals(left, right),
					values => values.Count,
					values => values))
			.HasMaxLength(1000);

		// Audit history outlives the staff and branches it mentions, and may
		// name an ID that never matched a record, so these are not foreign keys.
		builder.Property(entry => entry.StaffId)
			.IsUnicode(false)
			.HasMaxLength(50);
		builder.Property(entry => entry.StaffRole)
			.IsUnicode(false)
			.HasMaxLength(50);
		builder.Property(entry => entry.BranchCode)
			.IsUnicode(false)
			.HasMaxLength(10);
		builder.Property(entry => entry.CorrelationId)
			.IsUnicode(false)
			.HasMaxLength(64);
		builder.HasAuditColumns();
	}
}
