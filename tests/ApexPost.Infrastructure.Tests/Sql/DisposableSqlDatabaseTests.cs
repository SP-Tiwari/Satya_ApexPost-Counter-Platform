using ApexPost.Testing;
using Microsoft.Data.SqlClient;

namespace ApexPost.Infrastructure.Tests.Sql;

// The checks the fixture runs before EF Core may touch a database. Migrate()
// creates a missing database in the instance's default location, so nothing
// here may leave one to it. Only the configured folder is used, and these tests
// create no database of their own.
[Collection(SqlCollection.Name)]
public sealed class DisposableSqlDatabaseTests(SqlTestHost host)
{
	private static readonly SqlTestSettings Settings = SqlTestSettings.FromEnvironment();

	[Fact]
	public void CreateStatement_WithAPathTooLongForQuotename_FailsAndCreatesNothing()
	{
		var name = UnusedName();
		var tooLong = $@"{Settings.DataDirectory}\{new string('a', 80)}";

		var exception = Assert.Throws<SqlException>(
			() => DisposableSqlDatabase.RunCreateStatement(Settings, name, tooLong));

		Assert.Contains("could not be built", exception.Message);
		Assert.Empty(DisposableSqlDatabase.ReadFiles(Settings, name));
	}

	[Fact]
	public void FileCheck_OnADatabaseThatWasNotCreated_StopsBeforeMigrating()
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => DisposableSqlDatabase.EnsureFilesInPlace(Settings, UnusedName()));

		Assert.Contains("was not created", exception.Message);
	}

	[Fact]
	public void FileCheck_OnARealTestDatabase_FindsBothFilesInTheFolder()
	{
		var name = new SqlConnectionStringBuilder(host.ConnectionString).InitialCatalog;

		var files = DisposableSqlDatabase.ReadFiles(Settings, name);

		Assert.True(Settings.AreExpectedFiles(name, files));
		DisposableSqlDatabase.EnsureFilesInPlace(Settings, name);
	}

	[Theory]
	[InlineData("ApexPostDb")]
	[InlineData("master")]
	public void FileCheck_RefusesAnyOtherDatabaseWithoutConnecting(string name)
	{
		Assert.Throws<InvalidOperationException>(
			() => DisposableSqlDatabase.ReadFiles(Settings, name));
	}

	private static string UnusedName() =>
		$"{SqlTestSettings.NamePrefix}{Guid.NewGuid():N}";
}
