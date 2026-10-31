using ApexPost.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ApexPost.Infrastructure.Persistence.Sql.Configurations;

public sealed class MoneyOrderConfiguration
	: IEntityTypeConfiguration<MoneyOrder>
{
	public void Configure(EntityTypeBuilder<MoneyOrder> builder)
	{
		builder.ToTable("MoneyOrders", table =>
		{
			table.HasCheckConstraint("CK_MoneyOrders_Amount", "[Amount] > 0");
			table.HasCheckConstraint("CK_MoneyOrders_Fee", "[Fee] >= 0");
			table.HasCheckConstraint(
				"CK_MoneyOrders_Currency", "[Currency] = [FeeCurrency]");
			table.HasCheckConstraint(
				"CK_MoneyOrders_Status",
				"[Status] IN ('Issued', 'Cancelled')");
		});
		builder.HasKey(order => order.Id);
		builder.Property(order => order.Id)
			.HasColumnName("MoneyOrderId")
			.ValueGeneratedNever();

		builder.ComplexProperty(
			order => order.Amount,
			money => money.IsMoney("Amount", "Currency"));
		builder.ComplexProperty(
			order => order.Fee,
			money => money.IsMoney("Fee", "FeeCurrency"));
		builder.Property(order => order.Sender).HasMaxLength(200);
		builder.Property(order => order.Recipient).HasMaxLength(200);
		builder.Property(order => order.ProcessingBranchCode!)
			.IsBranchCode("ProcessingBranchCode")
			.IsRequired();
		builder.Property(order => order.Status).IsEnumCode("Status");
		builder.HasAuditColumns();

		builder.HasOne<Branch>()
			.WithMany()
			.HasForeignKey(order => order.ProcessingBranchCode)
			.HasPrincipalKey(branch => branch.Code)
			.OnDelete(DeleteBehavior.Restrict);

		// Stored through IdentityVerificationRecord and its mapper.
		builder.Ignore(order => order.IdentityVerification);
	}
}

public sealed class IdentityVerificationConfiguration
	: IEntityTypeConfiguration<IdentityVerificationRecord>
{
	public void Configure(EntityTypeBuilder<IdentityVerificationRecord> builder)
	{
		builder.ToTable("IdentityVerifications", table =>
			table.HasCheckConstraint(
				"CK_IdentityVerifications_MaskedDocumentIdentifier",
				"LEN([MaskedDocumentIdentifier]) = 8 " +
				"AND [MaskedDocumentIdentifier] LIKE '[*][*][*][*]____'"));
		builder.HasKey(verification => verification.MoneyOrderId);
		builder.Property(verification => verification.MoneyOrderId)
			.ValueGeneratedNever();

		builder.Property(verification => verification.DocumentType)
			.IsCode(50, "DocumentType");
		builder.Property(verification => verification.MaskedDocumentIdentifier)
			.IsCode(
				IdentityVerificationRecord.MaskedIdentifierLength,
				"MaskedDocumentIdentifier");
		builder.HasAuditColumns();

		builder.HasOne<MoneyOrder>()
			.WithOne()
			.HasForeignKey<IdentityVerificationRecord>(
				verification => verification.MoneyOrderId)
			.OnDelete(DeleteBehavior.Restrict);
	}
}
