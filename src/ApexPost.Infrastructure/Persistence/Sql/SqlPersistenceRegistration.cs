using ApexPost.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPost.Infrastructure.Persistence.Sql;

public static class SqlPersistenceRegistration
{
	// Everything is scoped, so a request shares one DbContext. The caller
	// registers TimeProvider and ICurrentStaffAccessor. Nothing calls this
	// until the SQL Server cutover in M3.
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
		return services;
	}
}
