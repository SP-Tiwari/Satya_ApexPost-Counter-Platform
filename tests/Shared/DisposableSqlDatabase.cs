using System.Data;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Testing;

// A uniquely named SQL Server database for one test run, built by the real
// migrations. Its files go in the SqlTestSettings folder, never the instance
// default, and it is dropped when the run ends. Only ApexPost_Test_* names are
// accepted, so it can never touch ApexPostDb. Migrate() would create a missing
// database in the instance's default location, so it runs only after the database
// is confirmed to exist with exactly the requested files.
internal sealed class DisposableSqlDatabase : IDisposable
{
	// In foreign-key order. Roles and ProductCategories keep their migration seed.
	private const string ResetSql =
		"""
		DELETE FROM TrackedShipmentScans;
		DELETE FROM TrackedShipments;
		DELETE FROM AuditEntries;
		DELETE FROM StaffCredentials;
		DELETE FROM StockAdjustments;
		DELETE FROM Payments;
		DELETE FROM TransactionTaxRates;
		DELETE FROM TransactionLines;
		DELETE FROM IdentityVerifications;
		DELETE FROM MoneyOrders;
		DELETE FROM Shipments;
		DELETE FROM CounterTransactions;
		DELETE FROM BranchStock;
		DELETE FROM ShippingRates;
		DELETE FROM ShippingZones;
		DELETE FROM Products;
		DELETE FROM Staff;
		DELETE FROM Counters;
		DELETE FROM Branches;
		""";

	private const string CreateSql =
		"""
		DECLARE @sql nvarchar(max) =
			N'CREATE DATABASE ' + QUOTENAME(@name) +
			N' ON PRIMARY (NAME = ' + QUOTENAME(@name + N'_data', '''') +
			N', FILENAME = ' + QUOTENAME(@directory + N'\' + @name + N'.mdf', '''') +
			N', SIZE = 32MB) LOG ON (NAME = ' + QUOTENAME(@name + N'_log', '''') +
			N', FILENAME = ' + QUOTENAME(@directory + N'\' + @name + N'_log.ldf', '''') +
			N', SIZE = 16MB) COLLATE SQL_Latin1_General_CP1_CI_AS;';
		-- QUOTENAME returns NULL above 128 characters, and a NULL statement runs as nothing.
		IF @sql IS NULL
		BEGIN
			;THROW 51000, N'The CREATE DATABASE statement could not be built.', 1;
		END;
		EXEC sys.sp_executesql @sql;
		""";

	private const string FilesSql =
		"""
		SELECT f.type, f.physical_name
		FROM sys.databases AS d
		INNER JOIN sys.master_files AS f ON f.database_id = d.database_id
		WHERE d.name = @name;
		""";

	private const string DropSql =
		"""
		IF DB_ID(@name) IS NOT NULL
		BEGIN
			DECLARE @sql nvarchar(max) =
				N'ALTER DATABASE ' + QUOTENAME(@name) +
				N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' +
				QUOTENAME(@name) + N';';
			EXEC sys.sp_executesql @sql;
		END;
		""";

	private readonly SqlTestSettings _settings;
	private int _disposed;

	public string Name { get; }
	public string ConnectionString { get; }

	private DisposableSqlDatabase(SqlTestSettings settings, string name)
	{
		_settings = settings;
		Name = name;
		ConnectionString = settings.DatabaseConnectionString(name);
	}

	public static DisposableSqlDatabase Create()
	{
		var settings = SqlTestSettings.FromEnvironment();
		if (!Directory.Exists(Path.GetPathRoot(settings.DataDirectory)))
			throw new InvalidOperationException(
				$"The test database drive is unavailable. On another machine, set {SqlTestSettings.DataDirectoryVariable} and {SqlTestSettings.OtherMachineVariable}. Tests never fall back to the instance default.");

		Directory.CreateDirectory(settings.DataDirectory);
		var database = new DisposableSqlDatabase(
			settings, $"{SqlTestSettings.NamePrefix}{Guid.NewGuid():N}");
		RunCreateStatement(settings, database.Name, settings.DataDirectory);

		try
		{
			EnsureFilesInPlace(settings, database.Name);
			using var context = database.CreateContext();
			context.Database.Migrate();
			return database;
		}
		catch
		{
			database.Dispose();
			throw;
		}
	}

	public ApexPostDbContext CreateContext() =>
		new(new DbContextOptionsBuilder<ApexPostDbContext>()
			.UseSqlServer(ConnectionString)
			.Options);

	public void Reset()
	{
		using var connection = new SqlConnection(ConnectionString);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = ResetSql;
		command.ExecuteNonQuery();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			using var connection = Open(_settings, Name);
			using var command = NameCommand(connection, DropSql, Name);
			command.ExecuteNonQuery();
		}
	}

	internal static void RunCreateStatement(
		SqlTestSettings settings,
		string name,
		string directory)
	{
		using var connection = Open(settings, name);
		using var command = NameCommand(connection, CreateSql, name);
		command.Parameters.Add("@directory", SqlDbType.NVarChar, 256).Value = directory;
		command.ExecuteNonQuery();
	}

	// Reads the catalog from master, so it never opens the database itself.
	internal static IReadOnlyList<SqlTestDatabaseFile> ReadFiles(
		SqlTestSettings settings,
		string name)
	{
		using var connection = Open(settings, name);
		using var command = NameCommand(connection, FilesSql, name);
		using var reader = command.ExecuteReader();
		var files = new List<SqlTestDatabaseFile>();
		while (reader.Read())
			files.Add(new SqlTestDatabaseFile(reader.GetByte(0), reader.GetString(1)));

		return files;
	}

	internal static void EnsureFilesInPlace(SqlTestSettings settings, string name)
	{
		var files = ReadFiles(settings, name);
		if (files.Count == 0)
			throw new InvalidOperationException(
				"The test database was not created, so it will not be migrated.");

		if (!settings.AreExpectedFiles(name, files))
			throw new InvalidOperationException(
				"The test database files are not in the configured folder, so it will not be migrated.");
	}

	private static SqlConnection Open(SqlTestSettings settings, string name)
	{
		if (!SqlTestSettings.IsTestDatabaseName(name))
			throw new InvalidOperationException(
				"Refusing to run against a database that is not an ApexPost_Test_* database.");

		SqlConnection.ClearAllPools();
		var connection = new SqlConnection(settings.ServerConnectionString);
		connection.Open();
		return connection;
	}

	private static SqlCommand NameCommand(SqlConnection connection, string sql, string name)
	{
		var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
		return command;
	}
}
