using System.Data;
using ApexPost.Infrastructure.Persistence.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Testing;

// A uniquely named SQL Server database for one test run, built by the real
// migrations. Its files go in E:\SqlData\ApexPost\Tests, never the instance
// default on C:, and it is dropped when the run ends. Only names starting
// with the test prefix are accepted, so it can never touch ApexPostDb.
internal sealed class DisposableSqlDatabase : IDisposable
{
	private const string NamePrefix = "ApexPost_Test_";
	private const string DataDirectory = @"E:\SqlData\ApexPost\Tests";
	private const string ServerConnection =
		"Server=.;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";

	// In foreign-key order. Roles and ProductCategories keep their migration seed.
	private const string ResetSql =
		"""
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
		EXEC sys.sp_executesql @sql;
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

	private int _disposed;

	public string Name { get; }
	public string ConnectionString { get; }

	private DisposableSqlDatabase(string name)
	{
		Name = name;
		ConnectionString = new SqlConnectionStringBuilder(ServerConnection)
		{
			InitialCatalog = name
		}.ConnectionString;
	}

	public static DisposableSqlDatabase Create()
	{
		if (!Directory.Exists(Path.GetPathRoot(DataDirectory)))
			throw new InvalidOperationException(
				$"The test database folder {DataDirectory} is unavailable. Tests never fall back to the instance default on C:.");

		Directory.CreateDirectory(DataDirectory);
		var database = new DisposableSqlDatabase(
			$"{NamePrefix}{Guid.NewGuid():N}");
		database.RunOnServer(CreateSql, includeDirectory: true);

		using var context = database.CreateContext();
		context.Database.Migrate();
		return database;
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
			RunOnServer(DropSql, includeDirectory: false);
	}

	private void RunOnServer(string sql, bool includeDirectory)
	{
		if (!Name.StartsWith(NamePrefix, StringComparison.Ordinal))
			throw new InvalidOperationException(
				$"Refusing to run against '{Name}', which is not a test database.");

		SqlConnection.ClearAllPools();
		using var connection = new SqlConnection(ServerConnection);
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = Name;
		if (includeDirectory)
		{
			command.Parameters.Add("@directory", SqlDbType.NVarChar, 256).Value =
				DataDirectory;
		}

		command.ExecuteNonQuery();
	}
}
