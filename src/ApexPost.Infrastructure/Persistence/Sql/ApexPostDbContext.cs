using ApexPost.Application.Auditing;
using ApexPost.Application.Security;
using ApexPost.Domain.Entities;
using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Infrastructure.Persistence.Sql;

// The SQL Server system of record. Mappings live in one
// IEntityTypeConfiguration class per entity, under Configurations.
public sealed class ApexPostDbContext(
	DbContextOptions<ApexPostDbContext> options) : DbContext(options)
{
	public DbSet<Branch> Branches => Set<Branch>();
	public DbSet<CounterTerminal> Counters => Set<CounterTerminal>();
	public DbSet<Staff> Staff => Set<Staff>();
	public DbSet<StaffCredential> StaffCredentials => Set<StaffCredential>();
	public DbSet<RoleRecord> Roles => Set<RoleRecord>();
	public DbSet<Product> Products => Set<Product>();
	public DbSet<ProductCategoryRecord> ProductCategories =>
		Set<ProductCategoryRecord>();
	public DbSet<BranchStock> BranchStock => Set<BranchStock>();
	public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
	public DbSet<ShippingZone> ShippingZones => Set<ShippingZone>();
	public DbSet<ShippingRate> ShippingRates => Set<ShippingRate>();
	public DbSet<ShippingItem> Shipments => Set<ShippingItem>();
	public DbSet<MoneyOrder> MoneyOrders => Set<MoneyOrder>();
	public DbSet<IdentityVerificationRecord> IdentityVerifications =>
		Set<IdentityVerificationRecord>();
	public DbSet<TransactionBasket> CounterTransactions =>
		Set<TransactionBasket>();
	public DbSet<TransactionLineItem> TransactionLines =>
		Set<TransactionLineItem>();
	public DbSet<Payment> Payments => Set<Payment>();
	public DbSet<AuditRecord> AuditEntries => Set<AuditRecord>();

	protected override void OnModelCreating(ModelBuilder modelBuilder) =>
		modelBuilder.ApplyConfigurationsFromAssembly(
			typeof(ApexPostDbContext).Assembly);
}
