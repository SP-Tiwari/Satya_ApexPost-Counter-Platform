using System.Text.RegularExpressions;
using ApexPost.Infrastructure.Persistence.Sql;
using ApexPost.Infrastructure.Persistence.Sql.Migrations;
using ApexPost.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using static ApexPost.Infrastructure.Tests.Sql.ProcedureMigrationTests;
using static ApexPost.Infrastructure.Tests.Sql.SqlScriptTests;
using static ApexPost.Infrastructure.Tests.Sql.SqlTestHost;

namespace ApexPost.Infrastructure.Tests.Sql;

// The indexes ADR 0024 chose, read back from what the migrations really built.
// The measurements in PERFORMANCE.md only hold for these exact definitions.
[Collection(SqlCollection.Name)]
public sealed class IndexDefinitionTests(SqlTestHost host) : IAsyncLifetime
{
	private const string TransactionIndex =
		"CounterTransactions.IX_CounterTransactions_BranchCode_OccurredAtUtc (BranchCode,OccurredAtUtc) INCLUDE (Currency,Status)";
	private const string LineIndex =
		"TransactionLines.IX_TransactionLines_TransactionId (TransactionId) INCLUDE (LineType,Quantity,ShipmentId,TaxCategory,UnitPrice)";
	private const string ShipmentIndex =
		"Shipments.IX_Shipments_ZoneCode_ServiceLevel (ZoneCode,ServiceLevel) INCLUDE (ShipmentType,WeightKg)";
	private const string MoneyOrderIndex =
		"MoneyOrders.IX_MoneyOrders_ProcessingBranchCode (ProcessingBranchCode) INCLUDE (Amount,Currency,Fee,Recipient,Sender,Status)";
	private const string StockIndex =
		"BranchStock.IX_BranchStock_Quantity (Quantity) INCLUDE ()";

	private static readonly string[] PerformanceIndexes =
		[TransactionIndex, LineIndex, ShipmentIndex, MoneyOrderIndex, StockIndex];

	// The same indexes before AddPerformanceIndexes. IX_BranchStock_Quantity is new.
	private static readonly string[] EarlierIndexes =
	[
		"CounterTransactions.IX_CounterTransactions_BranchCode (BranchCode) INCLUDE ()",
		"MoneyOrders.IX_MoneyOrders_ProcessingBranchCode (ProcessingBranchCode) INCLUDE ()",
		"Shipments.IX_Shipments_ZoneCode (ZoneCode) INCLUDE ()",
		"TransactionLines.IX_TransactionLines_TransactionId (TransactionId) INCLUDE ()"
	];

	private static readonly string[] OtherIndexes =
	[
		"BranchStock.IX_BranchStock_Sku (Sku) INCLUDE ()",
		"Branches.UQ_Branches_BranchCode (BranchCode) INCLUDE () UNIQUE",
		"CounterTransactions.IX_CounterTransactions_ClerkStaffId (ClerkStaffId) INCLUDE ()",
		"Payments.IX_Payments_TransactionId (TransactionId) INCLUDE ()",
		"ProductCategories.UQ_ProductCategories_Name (Name) INCLUDE () UNIQUE",
		"Products.IX_Products_ProductCategoryId (ProductCategoryId) INCLUDE ()",
		"Products.UQ_Products_Sku (Sku) INCLUDE () UNIQUE",
		"Roles.UQ_Roles_Name (Name) INCLUDE () UNIQUE",
		"ShippingZones.UQ_ShippingZones_ZoneCode (ZoneCode) INCLUDE () UNIQUE",
		"Staff.IX_Staff_HomeBranchCode (HomeBranchCode) INCLUDE ()",
		"Staff.IX_Staff_RoleId (RoleId) INCLUDE ()",
		"Staff.UQ_Staff_Email (Email) INCLUDE () UNIQUE",
		"StockAdjustments.IX_StockAdjustments_BranchCode_Sku (BranchCode,Sku) INCLUDE ()",
		"StockAdjustments.IX_StockAdjustments_ChangedByStaffId (ChangedByStaffId) INCLUDE ()",
		"TransactionLines.IX_TransactionLines_MoneyOrderId (MoneyOrderId) INCLUDE () UNIQUE WHERE ([MoneyOrderId] IS NOT NULL)",
		"TransactionLines.IX_TransactionLines_ShipmentId (ShipmentId) INCLUDE () UNIQUE WHERE ([ShipmentId] IS NOT NULL)",
		"TransactionLines.IX_TransactionLines_Sku (Sku) INCLUDE ()"
	];

	public Task InitializeAsync() => host.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task Database_HasExactlyTheDesignedIndexes()
	{
		var actual = await host.QueryAsync(ReadIndexesAsync);

		Assert.Equal(Sorted([.. PerformanceIndexes, .. OtherIndexes]), actual);
	}

	[Fact]
	public void AddPerformanceIndexes_UpCreatesExactlyTheFiveIndexes()
	{
		var operations = new AddPerformanceIndexes().UpOperations;

		Assert.Equal(
			[
				"IX_TransactionLines_TransactionId",
				"IX_Shipments_ZoneCode",
				"IX_MoneyOrders_ProcessingBranchCode",
				"IX_CounterTransactions_BranchCode"
			],
			operations.OfType<DropIndexOperation>().Select(operation => operation.Name));
		Assert.Equal(Sorted(PerformanceIndexes), Created(operations));
		Assert.All(
			operations.OfType<CreateIndexOperation>(),
			operation =>
			{
				Assert.False(operation.IsUnique);
				Assert.Null(operation.Filter);
			});
	}

	[Fact]
	public void AddPerformanceIndexes_DownRestoresTheEarlierIndexes()
	{
		var operations = new AddPerformanceIndexes().DownOperations;

		Assert.Equal(
			[
				"IX_TransactionLines_TransactionId",
				"IX_Shipments_ZoneCode_ServiceLevel",
				"IX_MoneyOrders_ProcessingBranchCode",
				"IX_CounterTransactions_BranchCode_OccurredAtUtc",
				"IX_BranchStock_Quantity"
			],
			operations.OfType<DropIndexOperation>().Select(operation => operation.Name));
		Assert.Equal(Sorted(EarlierIndexes), Created(operations));
	}

	[Fact]
	public async Task AddPerformanceIndexes_RollsBackAndReapplies_OnARealDatabase()
	{
		using var database = DisposableSqlDatabase.Create();
		await using var context = database.CreateContext();
		var migrator = context.Database.GetService<IMigrator>();
		var designed = await ReadIndexesAsync(context);

		await migrator.MigrateAsync(StoredProceduresId);
		Assert.Equal(
			Sorted([.. EarlierIndexes, .. OtherIndexes]),
			await ReadIndexesAsync(context));

		await migrator.MigrateAsync();
		Assert.Equal(designed, await ReadIndexesAsync(context));
	}

	[Fact]
	public async Task Model_DeclaresTheSameKeysUniquenessAndFiltersAsTheDatabase()
	{
		var actual = await host.QueryAsync(ReadIndexesAsync);

		await host.InRequestAsync(services =>
		{
			Assert.Equal(
				actual.Select(WithoutIncludes),
				ModelIndexes(DesignTimeModel(services)));
			return Task.CompletedTask;
		});
	}

	// IncludeProperties cannot name a complex property, so ADR 0024 keeps those
	// columns only in the migration. Nothing else may differ from the model.
	[Theory]
	[InlineData("IX_CounterTransactions_BranchCode_OccurredAtUtc", TransactionIndex, "")]
	[InlineData("IX_TransactionLines_TransactionId", LineIndex, "UnitPrice")]
	[InlineData("IX_Shipments_ZoneCode_ServiceLevel", ShipmentIndex, "")]
	[InlineData("IX_MoneyOrders_ProcessingBranchCode", MoneyOrderIndex, "Amount,Currency,Fee")]
	[InlineData("IX_BranchStock_Quantity", StockIndex, "")]
	public Task Model_IncludesAllButTheComplexColumns(
		string name,
		string definition,
		string migrationOnly) =>
		host.InRequestAsync(services =>
		{
			var (entity, index) = FindIndex(DesignTimeModel(services), name);
			var modelled = (index.GetIncludeProperties() ?? [])
				.Select(property => entity.FindProperty(property)!.GetColumnName());
			var complex = entity.GetComplexProperties()
				.SelectMany(property => property.ComplexType.GetProperties())
				.Select(property => property.GetColumnName());
			var expected = migrationOnly.Split(',', StringSplitOptions.RemoveEmptyEntries);

			Assert.Equal(
				Sorted([.. Includes(definition)]),
				Sorted([.. modelled, .. expected]));
			Assert.All(expected, column => Assert.Contains(column, complex));
			return Task.CompletedTask;
		});

	[Theory]
	[InlineData("CREATE INDEX [IX_TransactionLines_TransactionId] ON [TransactionLines] ([TransactionId]) INCLUDE ([LineType], [Quantity], [UnitPrice], [TaxCategory], [ShipmentId]);")]
	[InlineData("CREATE INDEX [IX_Shipments_ZoneCode_ServiceLevel] ON [Shipments] ([ZoneCode], [ServiceLevel]) INCLUDE ([ShipmentType], [WeightKg]);")]
	[InlineData("CREATE INDEX [IX_MoneyOrders_ProcessingBranchCode] ON [MoneyOrders] ([ProcessingBranchCode]) INCLUDE ([Status], [Amount], [Currency], [Fee], [Sender], [Recipient]);")]
	[InlineData("CREATE INDEX [IX_CounterTransactions_BranchCode_OccurredAtUtc] ON [CounterTransactions] ([BranchCode], [OccurredAtUtc]) INCLUDE ([Status], [Currency]);")]
	[InlineData("CREATE INDEX [IX_BranchStock_Quantity] ON [BranchStock] ([Quantity]);")]
	public void IdempotentScript_CreatesTheIndexInItsOwnMigration(string statement)
	{
		var script = ReadScript("schema", "ApexPostDb-idempotent.sql");

		Assert.Contains(statement, MigrationBlock(script, IndexesId), StringComparison.Ordinal);
	}

	// A line may have no shipment or money order, but each belongs to one line only.
	[Theory]
	[InlineData(
		"SELECT COUNT(*) FROM TransactionLines WHERE ShipmentId IS NULL",
		"UPDATE TransactionLines SET ShipmentId = (SELECT MIN(ShipmentId) FROM TransactionLines) WHERE ShipmentId = (SELECT MAX(ShipmentId) FROM TransactionLines)",
		"IX_TransactionLines_ShipmentId")]
	[InlineData(
		"SELECT COUNT(*) FROM TransactionLines WHERE MoneyOrderId IS NULL",
		"UPDATE TransactionLines SET MoneyOrderId = (SELECT MIN(MoneyOrderId) FROM TransactionLines) WHERE MoneyOrderId = (SELECT MAX(MoneyOrderId) FROM TransactionLines)",
		"IX_TransactionLines_MoneyOrderId")]
	public async Task FilteredIndexes_RejectASharedReference_AndAllowManyLinesWithoutOne(
		string countLinesWithoutOne,
		string shareOneReference,
		string index)
	{
		for (var sale = 0; sale < 2; sale++)
		{
			await host.InRequestAsync(services => host.Sales(services)
				.CreateAndProcessAsync(ClerkId, Sale(Stationery, 1, withShippingAndMoneyOrder: true)));
		}

		await using var connection = new SqlConnection(host.ConnectionString);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = countLinesWithoutOne;
		Assert.True((int)(await command.ExecuteScalarAsync())! > 1);

		command.CommandText = shareOneReference;
		var exception = await Assert.ThrowsAsync<SqlException>(
			() => command.ExecuteNonQueryAsync());

		Assert.Contains(index, exception.Message, StringComparison.Ordinal);
	}

	private static async Task<string[]> ReadIndexesAsync(ApexPostDbContext context) =>
		Sorted(await context.Database
			.SqlQuery<string>(
				$"""
				SELECT CONCAT(
					OBJECT_NAME(i.object_id), '.', i.name,
					' (', keyColumns.Names, ') INCLUDE (', included.Names, ')',
					IIF(i.is_unique = 1, ' UNIQUE', ''),
					IIF(i.has_filter = 1, ' WHERE ' + i.filter_definition, '')) AS Value
				FROM sys.indexes AS i
				CROSS APPLY (
					SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS Names
					FROM sys.index_columns AS ic
					JOIN sys.columns AS c
						ON c.object_id = ic.object_id AND c.column_id = ic.column_id
					WHERE ic.object_id = i.object_id
						AND ic.index_id = i.index_id
						AND ic.is_included_column = 0) AS keyColumns
				CROSS APPLY (
					SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY c.name) AS Names
					FROM sys.index_columns AS ic
					JOIN sys.columns AS c
						ON c.object_id = ic.object_id AND c.column_id = ic.column_id
					WHERE ic.object_id = i.object_id
						AND ic.index_id = i.index_id
						AND ic.is_included_column = 1) AS included
				WHERE i.type = 2
					AND i.is_primary_key = 0
					AND OBJECTPROPERTY(i.object_id, 'IsUserTable') = 1
				""")
			.ToListAsync());

	private static string[] Created(IEnumerable<MigrationOperation> operations) =>
		Sorted([.. operations.OfType<CreateIndexOperation>().Select(operation =>
			$"{operation.Table}.{operation.Name} ({string.Join(',', operation.Columns)}) " +
			$"INCLUDE ({string.Join(',', ((string[]?)operation["SqlServer:Include"] ?? []).Order(StringComparer.Ordinal))})")]);

	private static IModel DesignTimeModel(IServiceProvider services) =>
		services.GetRequiredService<ApexPostDbContext>().GetService<IDesignTimeModel>().Model;

	private static IEnumerable<string> ModelIndexes(IModel model) =>
		Sorted([.. model.GetEntityTypes()
			.Where(entity => entity.GetTableName() is not null)
			.SelectMany(entity =>
			{
				var table = StoreObjectIdentifier.Table(entity.GetTableName()!);
				var indexes = entity.GetDeclaredIndexes().Select(index => Render(
					table,
					index.GetDatabaseName(table)!,
					index.Properties,
					index.IsUnique,
					index.GetFilter()));
				var alternateKeys = entity.GetDeclaredKeys()
					.Where(key => !key.IsPrimaryKey())
					.Select(key => Render(table, key.GetName(table)!, key.Properties, true, null));
				return indexes.Concat(alternateKeys);
			})
			.Distinct()]);

	private static string Render(
		StoreObjectIdentifier table,
		string name,
		IEnumerable<IProperty> columns,
		bool unique,
		string? filter) =>
		$"{table.Name}.{name} ({string.Join(',', columns.Select(column => column.GetColumnName(table)))})" +
		(unique ? " UNIQUE" : string.Empty) +
		(filter is null ? string.Empty : $" WHERE ({filter})");

	private static (IEntityType Entity, IIndex Index) FindIndex(IModel model, string name) =>
		model.GetEntityTypes()
			.SelectMany(entity => entity.GetDeclaredIndexes().Select(index => (entity, index)))
			.Single(item => item.index.GetDatabaseName() == name);

	private static string WithoutIncludes(string definition) =>
		Regex.Replace(definition, @" INCLUDE \([^)]*\)", string.Empty);

	private static string[] Includes(string definition) =>
		Regex.Match(definition, @"INCLUDE \(([^)]*)\)").Groups[1].Value
			.Split(',', StringSplitOptions.RemoveEmptyEntries);

	private static string[] Sorted(IEnumerable<string> definitions) =>
		[.. definitions.Order(StringComparer.Ordinal)];
}
