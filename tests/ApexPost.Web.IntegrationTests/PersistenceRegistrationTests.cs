using ApexPost.Application.Auditing;
using ApexPost.Application.Persistence;
using ApexPost.Application.Security;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ApexPost.Web.IntegrationTests;

public sealed class PersistenceRegistrationTests : IAsyncLifetime
{
	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_factory = new ApexPostWebApplicationFactory();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync() => await _factory.DisposeAsync();

	[Fact]
	public void Services_ResolveSqlServerPersistence()
	{
		using var scope = _factory.Services.CreateScope();
		var services = scope.ServiceProvider;

		Assert.IsType<SqlBranchRepository>(services.GetRequiredService<IBranchRepository>());
		Assert.IsType<SqlProductRepository>(services.GetRequiredService<IProductRepository>());
		Assert.IsType<SqlStockRepository>(services.GetRequiredService<IStockRepository>());
		Assert.IsType<SqlCounterTransactionRepository>(
			services.GetRequiredService<ICounterTransactionRepository>());
		Assert.IsType<SqlStaffRepository>(services.GetRequiredService<IStaffRepository>());
		Assert.IsType<SqlUnitOfWork>(services.GetRequiredService<IUnitOfWork>());
		Assert.IsType<SqlStaffCredentialRepository>(
			services.GetRequiredService<IStaffCredentialRepository>());
		Assert.IsType<SqlAuditStore>(services.GetRequiredService<IAuditStore>());
	}

	[Fact]
	public void Requests_ShareOneDbContextPerScope()
	{
		using var first = _factory.Services.CreateScope();
		using var second = _factory.Services.CreateScope();

		Assert.Same(
			first.ServiceProvider.GetRequiredService<ApexPostDbContext>(),
			first.ServiceProvider.GetRequiredService<ApexPostDbContext>());
		Assert.NotSame(
			first.ServiceProvider.GetRequiredService<ApexPostDbContext>(),
			second.ServiceProvider.GetRequiredService<ApexPostDbContext>());
	}

	[Fact]
	public void TestHost_UsesTheDisposableDatabase_NeverTheDevelopmentOne()
	{
		using var scope = _factory.Services.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<ApexPostDbContext>();
		var configured = _factory.Services.GetRequiredService<IConfiguration>()
			.GetConnectionString("ApexPostDb");

		Assert.StartsWith("ApexPost_Test_", context.Database.GetDbConnection().Database);
		Assert.DoesNotContain("=ApexPostDb;", configured, StringComparison.Ordinal);
	}
}
