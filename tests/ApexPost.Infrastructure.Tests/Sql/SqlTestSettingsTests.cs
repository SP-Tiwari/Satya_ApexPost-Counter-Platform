using ApexPost.Testing;
using Microsoft.Data.SqlClient;

namespace ApexPost.Infrastructure.Tests.Sql;

// These use no database. They pin what the SQL test settings accept, because a
// wrong answer here would point the real tests at the wrong place.
public sealed class SqlTestSettingsTests
{
	private const string OtherDirectory = @"D:\ApexPostTests\Data";
	private const string GeneratedName = "ApexPost_Test_0123456789abcdef0123456789abcdef";

	private static SqlTestSettings ForOtherMachine(string? server = null, string? directory = null) =>
		SqlTestSettings.Resolve(
			server, directory, otherMachineConfirmed: true, developmentFolderExists: false);

	private static SqlTestSettings ForDevelopmentMachine(string? directory = null) =>
		SqlTestSettings.Resolve(
			null, directory, otherMachineConfirmed: false, developmentFolderExists: true);

	[Fact]
	public void Unset_UsesTheDevelopmentDefaults()
	{
		var settings = ForDevelopmentMachine();

		Assert.Equal(".", settings.Server);
		Assert.Equal(@"E:\SqlData\ApexPost\Tests", settings.DataDirectory);
	}

	[Fact]
	public void FromEnvironment_ReadsEachVariableByName()
	{
		var variables = new Dictionary<string, string>
		{
			["APEXPOST_TEST_SQL_SERVER"] = @".\SQLEXPRESS",
			["APEXPOST_TEST_DATA_DIRECTORY"] = OtherDirectory,
			["APEXPOST_TEST_OTHER_MACHINE"] = "true"
		};

		var settings = SqlTestSettings.FromEnvironment(
			variables.GetValueOrDefault, _ => false);

		Assert.Equal(@".\SQLEXPRESS", settings.Server);
		Assert.Equal(OtherDirectory, settings.DataDirectory);
	}

	[Fact]
	public void FromEnvironment_ChecksTheDevelopmentFolder()
	{
		var variables = new Dictionary<string, string>
		{
			["APEXPOST_TEST_DATA_DIRECTORY"] = OtherDirectory,
			["APEXPOST_TEST_OTHER_MACHINE"] = "true"
		};

		var exception = Assert.Throws<InvalidOperationException>(
			() => SqlTestSettings.FromEnvironment(
				variables.GetValueOrDefault,
				path => path == @"E:\SqlData\ApexPost"));

		Assert.Contains("only approved folder", exception.Message);
	}

	[Theory]
	[InlineData("1")]
	[InlineData("yes")]
	[InlineData("")]
	[InlineData(null)]
	public void FromEnvironment_OnlyTrueConfirmsAnotherMachine(string? confirmation)
	{
		var variables = new Dictionary<string, string?>
		{
			["APEXPOST_TEST_DATA_DIRECTORY"] = OtherDirectory,
			["APEXPOST_TEST_OTHER_MACHINE"] = confirmation
		};

		var exception = Assert.Throws<InvalidOperationException>(
			() => SqlTestSettings.FromEnvironment(
				name => variables.GetValueOrDefault(name), _ => false));

		Assert.Contains(SqlTestSettings.OtherMachineVariable, exception.Message);
	}

	[Theory]
	[InlineData(".")]
	[InlineData(@".\SQLEXPRESS")]
	[InlineData("(local)")]
	[InlineData(@"(local)\SQLEXPRESS")]
	[InlineData("localhost")]
	[InlineData(@"localhost\SQLEXPRESS")]
	[InlineData("LOCALHOST")]
	[InlineData(@"(localdb)\MSSQLLocalDB")]
	public void Server_AcceptsALocalInstance(string server)
	{
		Assert.Equal(server, ForOtherMachine(server).Server);
	}

	[Theory]
	[InlineData("db-host")]
	[InlineData(@"db-host\SQLEXPRESS")]
	[InlineData("db.example.test")]
	[InlineData("127.0.0.1")]
	[InlineData("192.0.2.10")]
	[InlineData("::1")]
	[InlineData("tcp:db-host,1433")]
	[InlineData("tcp:.")]
	[InlineData("tcp:localhost,1433")]
	[InlineData("localhost,1433")]
	[InlineData(".,1433")]
	[InlineData(@"np:\\db-host\pipe\sql\query")]
	[InlineData(@"\\db-host\pipe\sql\query")]
	[InlineData("lpc:.")]
	[InlineData("localhost.example.test")]
	[InlineData(@".\")]
	[InlineData(@".\instance\extra")]
	[InlineData(@".\1instance")]
	[InlineData(@".\NameLongerThanSixteen")]
	public void Server_RefusesAnythingThatIsNotPlainlyLocal(string server)
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => ForOtherMachine(server));

		Assert.Contains(SqlTestSettings.ServerVariable, exception.Message);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData(".;Database=ApexPostDb")]
	[InlineData("Server=.;Initial Catalog=ApexPostDb")]
	[InlineData(".;User ID=sa;Password=fictional-secret")]
	[InlineData("db host")]
	[InlineData(".\n")]
	public void Server_RefusesAConnectionString_WithoutRepeatingTheValue(string server)
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => ForOtherMachine(server));

		Assert.Contains(SqlTestSettings.ServerVariable, exception.Message);
		Assert.DoesNotContain("fictional-secret", exception.Message);
		Assert.DoesNotContain("ApexPostDb", exception.Message);
	}

	[Theory]
	[InlineData(OtherDirectory, OtherDirectory)]
	[InlineData(@"D:\ApexPostTests\Data\", OtherDirectory)]
	[InlineData(@"C:\Program Files\Test Data", @"C:\Program Files\Test Data")]
	[InlineData(@"D:\Tests.v2\Data", @"D:\Tests.v2\Data")]
	public void DataDirectory_OnAnotherMachine_AcceptsALocalFolder(string value, string expected)
	{
		Assert.Equal(expected, ForOtherMachine(directory: value).DataDirectory);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("Tests")]
	[InlineData(@"..\Tests")]
	[InlineData(@"D:")]
	[InlineData(@"D:\")]
	[InlineData(@"D:Tests")]
	[InlineData(@"D:\Tests\\")]
	[InlineData(@"\\server\share\Tests")]
	[InlineData(@"D:\Tests\..\Other")]
	[InlineData(@"D:\Tests\.\Other")]
	[InlineData(@"D:\Tests\ ..\Other")]
	[InlineData(@"D:\Tests\. .\Other")]
	[InlineData(@"D:\Tests\...\Other")]
	[InlineData(@"D:\Tests.\Other")]
	[InlineData(@"D:\Tests \Other")]
	[InlineData(@"D:\Tests.")]
	[InlineData(@"D:\Tests ")]
	[InlineData(@"D:/Tests")]
	[InlineData(@"D:\Te'sts")]
	[InlineData(@"D:\Tests;Other")]
	[InlineData("D:\\Tests\nOther")]
	[InlineData("D:\\Tests\tOther")]
	public void DataDirectory_RefusesAnInvalidFolder_WithoutRepeatingTheValue(string value)
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => ForOtherMachine(directory: value));

		Assert.Contains(SqlTestSettings.DataDirectoryVariable, exception.Message);
		if (value.Trim().Length > 0)
			Assert.DoesNotContain(value, exception.Message);
	}

	[Fact]
	public void DataDirectory_AcceptsTheLongestFolderWhoseLogPathFitsQuotename()
	{
		var value = FolderOfLength(73);

		var settings = ForOtherMachine(directory: value);

		Assert.Equal(73, SqlTestSettings.MaximumDirectoryLength);
		Assert.Equal(value, settings.DataDirectory);
		Assert.Equal(128, settings.LogFilePath(GeneratedName).Length);
	}

	[Fact]
	public void DataDirectory_RefusesOneCharacterMore()
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => ForOtherMachine(directory: FolderOfLength(74)));

		Assert.Contains("at most 73 characters", exception.Message);
	}

	[Fact]
	public void DataDirectory_TrailingBackslashDoesNotCountTowardsTheLimit()
	{
		var settings = ForOtherMachine(directory: FolderOfLength(73) + @"\");

		Assert.Equal(73, settings.DataDirectory.Length);
	}

	[Fact]
	public void GeneratedName_HasTheLengthTheLimitAssumes()
	{
		var name = $"{SqlTestSettings.NamePrefix}{Guid.NewGuid():N}";

		Assert.Equal(GeneratedName.Length, name.Length);
		Assert.True(SqlTestSettings.IsTestDatabaseName(name));
	}

	[Theory]
	[InlineData(OtherDirectory)]
	[InlineData(@"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA")]
	[InlineData(@"E:\SqlData\ApexPost")]
	[InlineData(@"E:\SqlData\ApexPost\Tests\Nested")]
	public void DataDirectory_OnTheDevelopmentMachine_RefusesEveryOtherFolder(string value)
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => ForDevelopmentMachine(value));

		Assert.Contains("only approved folder", exception.Message);
	}

	[Fact]
	public void DataDirectory_OnTheDevelopmentMachine_IgnoresTheConfirmation()
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => SqlTestSettings.Resolve(
				null, OtherDirectory, otherMachineConfirmed: true, developmentFolderExists: true));

		Assert.Contains("only approved folder", exception.Message);
	}

	[Fact]
	public void DataDirectory_WhenTheDevelopmentFolderIsMissing_StillNeedsTheConfirmation()
	{
		var exception = Assert.Throws<InvalidOperationException>(
			() => SqlTestSettings.Resolve(
				null, OtherDirectory, otherMachineConfirmed: false, developmentFolderExists: false));

		Assert.Contains(SqlTestSettings.OtherMachineVariable, exception.Message);
	}

	[Theory]
	[InlineData(@"E:\SqlData\ApexPost\Tests")]
	[InlineData(@"E:\SqlData\ApexPost\Tests\")]
	[InlineData(@"e:\sqldata\apexpost\tests")]
	public void DataDirectory_ApprovedFolder_NeedsNoConfirmation(string value)
	{
		foreach (var developmentFolderExists in new[] { true, false })
		{
			var settings = SqlTestSettings.Resolve(
				null, value, otherMachineConfirmed: false, developmentFolderExists);

			Assert.Equal(value.TrimEnd('\\'), settings.DataDirectory);
		}
	}

	[Fact]
	public void ExpectedFiles_AcceptsExactlyTheTwoRequestedFiles()
	{
		var settings = ForDevelopmentMachine();

		Assert.True(settings.AreExpectedFiles(GeneratedName,
		[
			new(0, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}.mdf"),
			new(1, $@"e:\sqldata\apexpost\tests\{GeneratedName}_log.ldf")
		]));
	}

	[Fact]
	public void ExpectedFiles_RefusesNoFiles()
	{
		Assert.False(ForDevelopmentMachine().AreExpectedFiles(GeneratedName, []));
	}

	[Fact]
	public void ExpectedFiles_RefusesTheInstanceDefaultLocation()
	{
		const string defaultFolder =
			@"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA";

		Assert.False(ForDevelopmentMachine().AreExpectedFiles(GeneratedName,
		[
			new(0, $@"{defaultFolder}\{GeneratedName}.mdf"),
			new(1, $@"{defaultFolder}\{GeneratedName}_log.ldf")
		]));
	}

	[Fact]
	public void ExpectedFiles_RefusesOneFileOutsideTheFolder()
	{
		Assert.False(ForDevelopmentMachine().AreExpectedFiles(GeneratedName,
		[
			new(0, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}.mdf"),
			new(1, $@"C:\Logs\{GeneratedName}_log.ldf")
		]));
	}

	[Fact]
	public void ExpectedFiles_RefusesASubfolderAndSwappedTypes()
	{
		var settings = ForDevelopmentMachine();

		Assert.False(settings.AreExpectedFiles(GeneratedName,
		[
			new(0, $@"E:\SqlData\ApexPost\Tests\Nested\{GeneratedName}.mdf"),
			new(1, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}_log.ldf")
		]));
		Assert.False(settings.AreExpectedFiles(GeneratedName,
		[
			new(1, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}.mdf"),
			new(0, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}_log.ldf")
		]));
	}

	[Fact]
	public void ExpectedFiles_RefusesAThirdFile()
	{
		Assert.False(ForDevelopmentMachine().AreExpectedFiles(GeneratedName,
		[
			new(0, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}.mdf"),
			new(1, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}_log.ldf"),
			new(0, $@"E:\SqlData\ApexPost\Tests\{GeneratedName}_2.ndf")
		]));
	}

	[Fact]
	public void ServerConnection_IsMasterWithWindowsAuthentication()
	{
		var settings = ForOtherMachine(@".\SQLEXPRESS");

		var connection = new SqlConnectionStringBuilder(settings.ServerConnectionString);

		Assert.Equal(@".\SQLEXPRESS", connection.DataSource);
		Assert.Equal("master", connection.InitialCatalog);
		Assert.True(connection.IntegratedSecurity);
		Assert.Empty(connection.UserID);
		Assert.Empty(connection.Password);
	}

	[Fact]
	public void DatabaseConnection_TargetsOnlyTheNamedTestDatabase()
	{
		var connection = new SqlConnectionStringBuilder(
			ForDevelopmentMachine().DatabaseConnectionString("ApexPost_Test_abc123"));

		Assert.Equal("ApexPost_Test_abc123", connection.InitialCatalog);
		Assert.True(connection.IntegratedSecurity);
	}

	[Theory]
	[InlineData("ApexPostDb")]
	[InlineData("master")]
	[InlineData("")]
	[InlineData("ApexPost_Test_")]
	[InlineData("apexpost_test_abc")]
	[InlineData("ApexPost_Postman_abc")]
	[InlineData("ApexPost_Test_a;Database=ApexPostDb")]
	[InlineData("ApexPost_Test_abc\n")]
	public void DatabaseConnection_RefusesEveryOtherName(string name)
	{
		var settings = ForDevelopmentMachine();

		Assert.False(SqlTestSettings.IsTestDatabaseName(name));
		Assert.Throws<InvalidOperationException>(
			() => settings.DatabaseConnectionString(name));
	}

	private static string FolderOfLength(int length) =>
		@"D:\" + new string('a', length - 3);
}
