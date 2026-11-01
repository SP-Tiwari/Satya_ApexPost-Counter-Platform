using ApexPost.Application.Security;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql.Configurations;

public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
	public void Configure(EntityTypeBuilder<Branch> builder)
	{
		builder.ToTable("Branches");
		builder.Property<int>("BranchId").UseIdentityColumn();
		builder.HasKey("BranchId");

		builder.Property(branch => branch.Code).IsBranchCode();
		builder.HasAlternateKey(branch => branch.Code)
			.HasName("UQ_Branches_BranchCode");

		builder.Property(branch => branch.Name).HasMaxLength(100);
		builder.Property(branch => branch.Region).HasMaxLength(100);
		builder.Property(branch => branch.OpeningHours).HasMaxLength(100);
		builder.Property(branch => branch.IsActive).IsActiveFlag();
		builder.HasAuditColumns();

		builder.HasMany(branch => branch.CounterTerminals)
			.WithOne()
			.HasForeignKey("BranchCode")
			.HasPrincipalKey(branch => branch.Code)
			.OnDelete(DeleteBehavior.Restrict);
	}
}

public sealed class CounterTerminalConfiguration
	: IEntityTypeConfiguration<CounterTerminal>
{
	public void Configure(EntityTypeBuilder<CounterTerminal> builder)
	{
		builder.ToTable("Counters");

		// Counter IDs are unique only within a branch.
		builder.Property<BranchCode>("BranchCode").IsBranchCode();
		builder.Property(counter => counter.Id).IsCode(20, "CounterId");
		builder.HasKey("BranchCode", nameof(CounterTerminal.Id));

		builder.Property(counter => counter.Name).HasMaxLength(100);
		builder.Property(counter => counter.IsActive).IsActiveFlag();
		builder.HasAuditColumns();
	}
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<RoleRecord>
{
	public void Configure(EntityTypeBuilder<RoleRecord> builder)
	{
		builder.ToTable("Roles");
		builder.HasKey(role => role.Id);
		builder.Property(role => role.Id)
			.HasConversion<byte>()
			.HasColumnName("RoleId")
			.ValueGeneratedNever();
		builder.Property(role => role.Name).HasMaxLength(50);
		builder.HasAlternateKey(role => role.Name).HasName("UQ_Roles_Name");
		builder.HasAuditColumns();

		builder.HasData(
			new RoleRecord(StaffRole.CounterClerk, "Counter Clerk"),
			new RoleRecord(StaffRole.BranchManager, "Branch Manager"),
			new RoleRecord(StaffRole.Auditor, "Auditor"),
			new RoleRecord(StaffRole.Administrator, "Administrator"));
	}
}

public sealed class StaffConfiguration : IEntityTypeConfiguration<Staff>
{
	public void Configure(EntityTypeBuilder<Staff> builder)
	{
		builder.ToTable("Staff");
		builder.HasKey(staff => staff.Id);
		builder.Property(staff => staff.Id)
			.IsCode(50, "StaffId")
			.ValueGeneratedNever();

		builder.Property(staff => staff.Name).HasMaxLength(100);
		builder.Property(staff => staff.Email).HasMaxLength(254);
		builder.HasAlternateKey(staff => staff.Email).HasName("UQ_Staff_Email");

		builder.Property(staff => staff.Role)
			.HasConversion<byte>()
			.HasColumnName("RoleId");
		builder.HasOne<RoleRecord>()
			.WithMany()
			.HasForeignKey(staff => staff.Role)
			.OnDelete(DeleteBehavior.Restrict);

		builder.Property(staff => staff.HomeBranchCode)
			.IsBranchCode("HomeBranchCode");
		builder.HasOne<Branch>()
			.WithMany()
			.HasForeignKey(staff => staff.HomeBranchCode)
			.HasPrincipalKey(branch => branch.Code)
			.OnDelete(DeleteBehavior.Restrict);

		builder.Property(staff => staff.IsActive).IsActiveFlag();
		builder.HasAuditColumns();
	}
}

public sealed class StaffCredentialConfiguration
	: IEntityTypeConfiguration<StaffCredential>
{
	public void Configure(EntityTypeBuilder<StaffCredential> builder)
	{
		builder.ToTable("StaffCredentials", table =>
			table.HasCheckConstraint(
				"CK_StaffCredentials_Iterations", "[Iterations] > 0"));
		builder.HasKey(credential => credential.StaffId);
		builder.Property(credential => credential.StaffId)
			.IsCode(50, "StaffId")
			.ValueGeneratedNever();
		builder.Property(credential => credential.Salt).IsCode(256, "Salt");
		builder.Property(credential => credential.PasswordHash)
			.IsCode(256, "PasswordHash");
		builder.HasAuditColumns();

		builder.HasOne<Staff>()
			.WithOne()
			.HasForeignKey<StaffCredential>(credential => credential.StaffId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
