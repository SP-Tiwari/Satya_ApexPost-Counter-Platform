using ApexPost.Application.Auditing;
using ApexPost.Application.Caching;
using ApexPost.Application.Persistence;
using ApexPost.Application.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPost.Infrastructure.Persistence.Sql;

public static class SqlPersistenceRegistration
{
	// Everything that touches the DbContext is scoped, so a request shares one.
	// The audit store and reference-data source are singletons that open their
	// own scopes. The caller registers TimeProvider and ICurrentStaffAccessor.
	public static IServiceCollection AddApexPostSqlPersistence(
		this IServiceCollection services,
		string connectionString)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		services.AddScoped<AuditColumnsInterceptor>();
		services.AddDbContext<ApexPostDbContext>((provider, options) => options
			.UseSqlServer(
				connectionString,
				sql => sql.EnableRetryOnFailure())
			.AddInterceptors(provider.GetRequiredService<AuditColumnsInterceptor>()));

		services.AddScoped<IBranchRepository, SqlBranchRepository>();
		services.AddScoped<IProductRepository, SqlProductRepository>();
		services.AddScoped<IStockRepository, SqlStockRepository>();
		services.AddScoped<ICounterTransactionRepository, SqlCounterTransactionRepository>();
		services.AddScoped<IStaffRepository, SqlStaffRepository>();
		services.AddScoped<IUnitOfWork, SqlUnitOfWork>();
		services.AddScoped<IStoredProcedures, SqlStoredProcedures>();
		services.AddScoped<IReportQueryService, SqlReportQueryService>();
		services.AddScoped<IScreenQueryService, SqlScreenQueryService>();

		services.AddScoped<SqlStaffCredentialRepository>();
		services.AddScoped<IStaffCredentialRepository>(provider =>
			provider.GetRequiredService<SqlStaffCredentialRepository>());
		services.AddScoped<IStaffCredentialProvisioning>(provider =>
			provider.GetRequiredService<SqlStaffCredentialRepository>());
		services.AddSingleton<IAuditStore, SqlAuditStore>();
		services.AddSingleton<IReferenceDataSource, SqlReferenceDataSource>();
		services.AddScoped<SqlDemoDataSeeder>();
		services.AddScoped<DemoCredentialProvisioner>();
		return services;
	}
}
