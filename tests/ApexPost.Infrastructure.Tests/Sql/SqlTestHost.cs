using ApexPost.Application.Events;
using ApexPost.Application.Persistence;
using ApexPost.Application.Services;
using ApexPost.Application.Verification;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.ValueObjects;
using ApexPost.Infrastructure.Persistence.Sql;
using ApexPost.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPost.Infrastructure.Tests.Sql;

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlTestHost>
{
	public const string Name = "SQL Server";
}

// One disposable database for the collection, with the application's own
// registrations, so these tests run what production runs.
public sealed class SqlTestHost : IDisposable
{
	public const string ClerkId = "clerk-1";
	public const string ManagerId = "manager-1";
	public const string AdministratorId = "admin-1";
	public const string PaymentMethod = "Cash";
	public static readonly BranchCode Branch = new("BR01");
	public static readonly BranchCode OtherBranch = new("BR02");
	public static readonly Sku Stationery = new("STAT-1");
	public static readonly Sku Packaging = new("PACK-1");

	private readonly DisposableSqlDatabase _database = DisposableSqlDatabase.Create();
	private readonly ServiceProvider _provider;

	public SqlTestHost()
	{
		var services = new ServiceCollection();
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<ICurrentStaffAccessor>(new FixedStaff());
		services.AddApexPostSqlPersistence(_database.ConnectionString);
		_provider = services.BuildServiceProvider();
	}

	public DomainEventPublisher Events { get; private set; } = new();

	public string ConnectionString => _database.ConnectionString;

	public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

	public async Task InRequestAsync(Func<IServiceProvider, Task> work)
	{
		await using var scope = CreateScope();
		await work(scope.ServiceProvider);
	}

	public async Task<T> InRequestAsync<T>(Func<IServiceProvider, Task<T>> work)
	{
		await using var scope = CreateScope();
		return await work(scope.ServiceProvider);
	}

	public Task<T> QueryAsync<T>(Func<ApexPostDbContext, Task<T>> read) =>
		InRequestAsync(services =>
			read(services.GetRequiredService<ApexPostDbContext>()));

	public async Task ResetAsync()
	{
		_database.Reset();
		Events = new DomainEventPublisher();
		await InRequestAsync(async services =>
		{
			var context = services.GetRequiredService<ApexPostDbContext>();
			context.Branches.Add(new Branch(
				Branch, "One", "North", "9-5", [new CounterTerminal("C1", "Counter 1")]));
			context.Branches.Add(new Branch(
				OtherBranch, "Two", "South", "9-5", [new CounterTerminal("C1", "Counter 1"), new CounterTerminal("C2", "Counter 2")]));
			context.Staff.Add(new Staff(ClerkId, "Clerk", "clerk@example.invalid", StaffRole.CounterClerk, Branch));
			context.Staff.Add(new Staff(ManagerId, "Manager", "manager@example.invalid", StaffRole.BranchManager, Branch));
			context.Staff.Add(new Staff(AdministratorId, "Admin", "admin@example.invalid", StaffRole.Administrator, Branch));
			context.Products.Add(new Product(
				Stationery, "Pen", ProductCategory.Stationery, new Money(33.33m, "INR"), TaxCategory.Standard));
			context.Products.Add(new Product(
				Packaging, "Box", ProductCategory.Packaging, new Money(10m, "INR"), TaxCategory.ZeroRated));
			context.BranchStock.Add(new BranchStock(Branch, Stationery, 10));
			context.BranchStock.Add(new BranchStock(Branch, Packaging, 10));
			context.ShippingZones.Add(new ShippingZone("ZONE-1", "Zone"));
			context.ShippingRates.Add(new ShippingRate(
				"ZONE-1", ShippingServiceLevel.Standard, new Money(10m, "INR"), new Money(1m, "INR")));
			await context.SaveChangesAsync();
		});
	}

	public MixedBasketApplicationService Sales(IServiceProvider services) => new(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IBranchRepository>(),
		services.GetRequiredService<IProductRepository>(),
		services.GetRequiredService<IStockRepository>(),
		services.GetRequiredService<ICounterTransactionRepository>(),
		services.GetRequiredService<IUnitOfWork>(),
		new Dictionary<TaxCategory, decimal>
		{
			[TaxCategory.Standard] = 0.18m,
			[TaxCategory.ZeroRated] = 0m
		},
		new Dictionary<ShippingServiceLevel, Weight>
		{
			[ShippingServiceLevel.Standard] = new Weight(30m)
		},
		TaxCategory.Standard,
		TimeProvider.System,
		new MoneyOrderBasketSettings(
			new Money(1000m, "INR"), new Money(25m, "INR"), TaxCategory.Standard),
		new ApprovingVerification(),
		Events,
		1,
		[PaymentMethod]);

	public TransactionApplicationService Transactions(IServiceProvider services) => new(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IBranchRepository>(),
		services.GetRequiredService<IStockRepository>(),
		services.GetRequiredService<ICounterTransactionRepository>(),
		services.GetRequiredService<IUnitOfWork>(),
		TimeProvider.System,
		Events,
		1);

	public static CounterBasketRequest Sale(
		Sku sku,
		int quantity,
		bool withShippingAndMoneyOrder = false,
		Guid? transactionId = null) => new(
		transactionId ?? Guid.NewGuid(),
		"C1",
		PaymentMethod,
		[new GoodsBasketRequest(sku.Value, quantity)],
		withShippingAndMoneyOrder
			? [new ShippingBasketRequest(
				ShippingItemType.Parcel, 1.255m, 10m, 20m, 30m, "ZONE-1", ShippingServiceLevel.Standard)]
			: [],
		withShippingAndMoneyOrder
			? [new MoneyOrderBasketRequest(5000m, "Sender", "Recipient", "PASSPORT", "ABCDEFGH1234")]
			: []);

	public void Dispose()
	{
		_provider.Dispose();
		_database.Dispose();
	}

	private sealed class FixedStaff : ICurrentStaffAccessor
	{
		public string? StaffId => "stamped-staff";
	}

	private sealed class ApprovingVerification : IIdentityVerificationService
	{
		public Task<bool> VerifyAsync(
			string documentType,
			string documentIdentifier,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(true);
	}
}
