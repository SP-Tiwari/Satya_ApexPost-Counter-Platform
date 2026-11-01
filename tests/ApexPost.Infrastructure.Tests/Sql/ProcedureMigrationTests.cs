using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApexPost.Infrastructure.Persistence.Sql.Migrations;
using ApexPost.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using static ApexPost.Infrastructure.Tests.Sql.SqlScriptTests;

namespace ApexPost.Infrastructure.Tests.Sql;

// A migration keeps deploying the procedure text it was written with, whatever
// happens to the current files in sql/procedures. See ADR 0023.
[Collection(SqlCollection.Name)]
public sealed class ProcedureMigrationTests
{
	internal const string RecompileId = "20261031184702_AddDailyBranchSalesRecompile";

	internal const string StoredProceduresId = "20261031124843_AddStoredProcedures";
	internal const string IndexesId = "20261031141512_AddPerformanceIndexes";
	private const string Recompile = "OPTION (RECOMPILE)";
	private const string DailySales = "usp_DailyBranchSales";
	private const string LowStock = "usp_LowStockReport";
	private const string AdjustStock = "usp_AdjustStock";
	private const string SnapshotPrefix = "ApexPost.Sql.Procedures.History.";

	// A snapshot records what a migration deployed, so it must never be edited.
	// A procedure change needs a new migration with its own snapshot.
	[Theory]
	[InlineData(nameof(AddStoredProcedures), DailySales, "ae4816235a4579506b38c42f4c31e7a498f2e16d74a69fccacbbe0b4ba8057fe")]
	[InlineData(nameof(AddStoredProcedures), LowStock, "e8d85a70a4559894adb824f75450e43f60aa71dffe28acb9aa8d04b526062e19")]
	[InlineData(nameof(AddStoredProcedures), AdjustStock, "9ead25484ddd7d218653cdb7bb24acecdfb9a412556dce2e01f3c1632782eff3")]
	[InlineData(nameof(AddDailyBranchSalesRecompile), DailySales, "f8d745f69f39086ffeb917de3b6d066aa54530a5d949c555b28a249e6bf79735")]
	public void Snapshot_HasNotChanged(string migration, string procedure, string expectedHash)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Snapshot(migration, procedure)));

		Assert.Equal(expectedHash, Convert.ToHexStringLower(hash));
	}

	[Fact]
	public void OnlySnapshotsAreEmbedded_AndEverySnapshotIsEmbedded()
	{
		var resources = typeof(AddStoredProcedures).Assembly
			.GetManifestResourceNames()
			.Where(name => name.StartsWith("ApexPost.Sql.", StringComparison.Ordinal))
			.Order(StringComparer.Ordinal);
		var history = Path.Combine(AppContext.BaseDirectory, "sql", "procedures", "history");
		var files = Directory
			.EnumerateFiles(history, "*.sql", SearchOption.AllDirectories)
			.Select(file => SnapshotPrefix + Path.GetRelativePath(history, file).Replace(Path.DirectorySeparatorChar, '.'))
			.Order(StringComparer.Ordinal);

		Assert.Equal(files, resources);
		Assert.Equal(4, resources.Count());
	}

	[Fact]
	public void AddStoredProcedures_DeploysOnlyItsSnapshots()
	{
		var deployed = Sql(new AddStoredProcedures().UpOperations);

		Assert.Equal(
			[DailySales, LowStock, AdjustStock],
			[.. deployed.Select(Procedure)]);
		Assert.Equal(
			[
				AsExec(Snapshot(nameof(AddStoredProcedures), DailySales)),
				AsExec(Snapshot(nameof(AddStoredProcedures), LowStock)),
				AsExec(Snapshot(nameof(AddStoredProcedures), AdjustStock))
			],
			deployed);
	}

	[Fact]
	public void AddDailyBranchSalesRecompile_UpAndDownUseOnlyTheirSnapshots()
	{
		var migration = new AddDailyBranchSalesRecompile();

		Assert.Equal(
			AsAlter(Snapshot(nameof(AddDailyBranchSalesRecompile), DailySales)),
			Assert.Single(Sql(migration.UpOperations)));
		Assert.Equal(
			AsAlter(Snapshot(nameof(AddStoredProcedures), DailySales)),
			Assert.Single(Sql(migration.DownOperations)));
	}

	[Fact]
	public void IdempotentScript_ContainsEachSnapshotInItsOwnMigration()
	{
		var script = Flatten(ReadScript("schema", "ApexPostDb-idempotent.sql"));
		var original = MigrationBlock(script, StoredProceduresId);
		var recompile = MigrationBlock(script, RecompileId);

		foreach (var procedure in new[] { DailySales, LowStock, AdjustStock })
			Assert.Contains(Flatten(AsExec(Snapshot(nameof(AddStoredProcedures), procedure))), original);

		Assert.Contains(
			Flatten(AsAlter(Snapshot(nameof(AddDailyBranchSalesRecompile), DailySales))),
			recompile);
		Assert.DoesNotContain(
			Recompile,
			Snapshot(nameof(AddStoredProcedures), DailySales));
	}

	[Fact]
	public async Task StoppingAtAddStoredProcedures_DeploysTheOriginalProcedures()
	{
		using var database = DisposableSqlDatabase.Create();
		await using var context = database.CreateContext();
		var migrator = context.Database.GetService<IMigrator>();

		await migrator.MigrateAsync(Migration.InitialDatabase);
		await migrator.MigrateAsync(StoredProceduresId);

		var daily = await DefinitionAsync(context, DailySales);
		Assert.DoesNotContain(Recompile, daily);
		Assert.Equal(Normalize(Snapshot(nameof(AddStoredProcedures), DailySales)), Normalize(daily));
		foreach (var procedure in new[] { LowStock, AdjustStock })
		{
			Assert.Equal(
				Normalize(Snapshot(nameof(AddStoredProcedures), procedure)),
				Normalize(await DefinitionAsync(context, procedure)));
		}
	}

	[Fact]
	public async Task AddDailyBranchSalesRecompile_UpgradesRollsBackAndReupgrades_KeepingData()
	{
		using var database = DisposableSqlDatabase.Create();
		await using var context = database.CreateContext();
		var migrator = context.Database.GetService<IMigrator>();
		var original = Normalize(Snapshot(nameof(AddStoredProcedures), DailySales));
		var recompiled = Normalize(Snapshot(nameof(AddDailyBranchSalesRecompile), DailySales));

		await migrator.MigrateAsync(IndexesId);
		await context.Database.ExecuteSqlAsync(
			$"INSERT INTO Branches (BranchCode, Name, Region, OpeningHours) VALUES ('MIG1', 'Marker', 'Test', '9-5')");
		Assert.Equal(original, Normalize(await DefinitionAsync(context, DailySales)));

		await migrator.MigrateAsync(RecompileId);
		Assert.Equal(recompiled, Normalize(await DefinitionAsync(context, DailySales)));
		Assert.Contains(Recompile, recompiled);

		await migrator.MigrateAsync(IndexesId);
		Assert.Equal(original, Normalize(await DefinitionAsync(context, DailySales)));

		await migrator.MigrateAsync(RecompileId);
		Assert.Equal(recompiled, Normalize(await DefinitionAsync(context, DailySales)));
		Assert.Equal(1, await context.Branches.CountAsync(branch => branch.Name == "Marker"));
	}

	// A migration's statements in the idempotent script: its own IF blocks, from
	// the first mention of its id to the insert into the history table.
	internal static string MigrationBlock(string script, string migrationId)
	{
		var id = $"N'{migrationId}'";
		var first = script.IndexOf(id, StringComparison.Ordinal);
		var last = script.LastIndexOf(id, StringComparison.Ordinal);
		Assert.True(first >= 0, $"{migrationId} is not in the script.");
		return script[first..last];
	}

	private static string Snapshot(string migration, string procedure) =>
		ReadScript("procedures", "history", migration, $"{procedure}.sql").ReplaceLineEndings("\n");

	private static string AsExec(string script) => $"EXEC(N'{Escape(script)}');";

	private static string AsAlter(string script) =>
		$"EXEC sp_executesql N'{Escape(script.Replace("CREATE OR ALTER PROCEDURE", "ALTER PROCEDURE", StringComparison.Ordinal))}';";

	private static string Escape(string script) => script.Replace("'", "''", StringComparison.Ordinal);

	private static string[] Sql(IEnumerable<MigrationOperation> operations) =>
		[.. operations.Cast<SqlOperation>().Select(operation => operation.Sql.ReplaceLineEndings("\n"))];

	private static string Procedure(string sql) =>
		Regex.Match(sql, @"PROCEDURE dbo\.(\w+)").Groups[1].Value;

	// EF Core indents each script block, including inside the EXEC string.
	private static string Flatten(string sql) =>
		Regex.Replace(sql.ReplaceLineEndings("\n"), @"^[ \t]+", "", RegexOptions.Multiline);

	private static Task<string> DefinitionAsync(DbContext context, string procedure) =>
		context.Database
			.SqlQuery<string>($"SELECT OBJECT_DEFINITION(OBJECT_ID({"dbo." + procedure})) AS Value")
			.SingleAsync();
}
