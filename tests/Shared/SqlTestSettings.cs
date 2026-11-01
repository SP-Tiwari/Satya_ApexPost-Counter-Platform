using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ApexPost.Testing;

// A database file reported by sys.master_files: type 0 is data, 1 is log.
internal sealed record SqlTestDatabaseFile(byte Type, string Path);

// Where the disposable SQL test databases go, read from the environment so the
// suite can run on another machine. The defaults are this project's development
// setup. Anything invalid stops the run: it never falls back to another location,
// and validation messages name the variable, not the supplied value. Connections
// are built only for master or an ApexPost_Test_* database on a local instance,
// always with Windows authentication, so no setting can reach ApexPostDb.
internal sealed partial class SqlTestSettings
{
	public const string ServerVariable = "APEXPOST_TEST_SQL_SERVER";
	public const string DataDirectoryVariable = "APEXPOST_TEST_DATA_DIRECTORY";
	public const string OtherMachineVariable = "APEXPOST_TEST_OTHER_MACHINE";
	public const string DefaultServer = ".";
	public const string DefaultDataDirectory = @"E:\SqlData\ApexPost\Tests";
	public const string NamePrefix = "ApexPost_Test_";

	// CreateSql passes each file path through QUOTENAME, which returns NULL above
	// 128 characters. The longest path is the folder, a backslash, the 46-character
	// generated name and _log.ldf.
	public const int MaximumDirectoryLength = QuotenameLimit - 1 - DatabaseNameLength - 8;

	private const int QuotenameLimit = 128;
	private const int DatabaseNameLength = 46;

	// While this exists, the default is the only approved folder. Its absence
	// proves nothing, because a drive can be offline, so another folder also
	// needs OtherMachineVariable set to true. Code cannot identify a machine, so
	// that variable is a deliberate statement by whoever sets it.
	private const string DevelopmentRoot = @"E:\SqlData\ApexPost";

	public string Server { get; }
	public string DataDirectory { get; }

	private SqlTestSettings(string server, string dataDirectory)
	{
		Server = server;
		DataDirectory = dataDirectory;
	}

	public string ServerConnectionString => Build("master");

	public static SqlTestSettings FromEnvironment() =>
		FromEnvironment(Environment.GetEnvironmentVariable, Directory.Exists);

	public static SqlTestSettings FromEnvironment(
		Func<string, string?> readVariable,
		Func<string, bool> directoryExists) =>
		Resolve(
			readVariable(ServerVariable),
			readVariable(DataDirectoryVariable),
			string.Equals(
				readVariable(OtherMachineVariable), "true", StringComparison.OrdinalIgnoreCase),
			directoryExists(DevelopmentRoot));

	public static SqlTestSettings Resolve(
		string? server,
		string? dataDirectory,
		bool otherMachineConfirmed,
		bool developmentFolderExists)
	{
		var resolvedServer = server ?? DefaultServer;
		if (!LocalServerPattern().IsMatch(resolvedServer))
			throw new InvalidOperationException(
				$"{ServerVariable} must name a local instance: ., (local) or localhost, each with an optional \\instance, or (localdb)\\name.");

		return new SqlTestSettings(
			resolvedServer,
			dataDirectory is null
				? DefaultDataDirectory
				: ValidatedDirectory(dataDirectory, otherMachineConfirmed, developmentFolderExists));
	}

	public static bool IsTestDatabaseName(string name) =>
		TestDatabasePattern().IsMatch(name);

	public string DatabaseConnectionString(string name)
	{
		if (!IsTestDatabaseName(name))
			throw new InvalidOperationException(
				"Refusing to connect to a database that is not an ApexPost_Test_* database.");

		return Build(name);
	}

	public string DataFilePath(string name) => $@"{DataDirectory}\{name}.mdf";

	public string LogFilePath(string name) => $@"{DataDirectory}\{name}_log.ldf";

	// Exactly the two files the fixture asked for, so a database that SQL Server
	// or EF Core placed anywhere else never passes.
	public bool AreExpectedFiles(string name, IReadOnlyCollection<SqlTestDatabaseFile> files) =>
		files.Count == 2 &&
		files.Any(file => file.Type == 0 && SamePath(file.Path, DataFilePath(name))) &&
		files.Any(file => file.Type == 1 && SamePath(file.Path, LogFilePath(name)));

	private static bool SamePath(string left, string right) =>
		string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

	private static string ValidatedDirectory(
		string value,
		bool otherMachineConfirmed,
		bool developmentFolderExists)
	{
		var directory = value.EndsWith('\\') ? value[..^1] : value;
		if (directory.Length > MaximumDirectoryLength ||
			!DirectoryPattern().IsMatch(directory) ||
			directory.Split('\\').Any(segment => segment.EndsWith('.') || segment.EndsWith(' ')))
		{
			throw new InvalidOperationException(
				$"{DataDirectoryVariable} must be an absolute local folder below a drive root, of at most {MaximumDirectoryLength} characters, without quotes or semicolons, and with no part ending in a dot or space.");
		}

		if (SamePath(directory, DefaultDataDirectory))
			return directory;

		if (developmentFolderExists)
			throw new InvalidOperationException(
				$"{DataDirectoryVariable} cannot move the test databases on the development machine, where {DefaultDataDirectory} is the only approved folder.");

		if (!otherMachineConfirmed)
			throw new InvalidOperationException(
				$"{DataDirectoryVariable} needs {OtherMachineVariable} set to true, to confirm this is not the development machine.");

		return directory;
	}

	private string Build(string database) =>
		new SqlConnectionStringBuilder
		{
			DataSource = Server,
			InitialCatalog = database,
			IntegratedSecurity = true,
			Encrypt = SqlConnectionEncryptOption.Mandatory,
			TrustServerCertificate = true
		}.ConnectionString;

	// A closed list of local forms. Host names, IP addresses, ports and protocol
	// prefixes such as tcp: all fail, so a test can never reach another machine.
	[GeneratedRegex(
		@"\A(?:(?:\.|\(local\)|localhost)(?:\\[A-Za-z_][A-Za-z0-9_$#]{0,15})?|\(localdb\)\\[A-Za-z0-9_-]{1,128})\z",
		RegexOptions.IgnoreCase)]
	private static partial Regex LocalServerPattern();

	[GeneratedRegex(@"\AApexPost_Test_[A-Za-z0-9_]+\z")]
	private static partial Regex TestDatabasePattern();

	[GeneratedRegex(@"\A[A-Za-z]:(\\[^\\/:*?""<>|';\x00-\x1F]+)+\z")]
	private static partial Regex DirectoryPattern();
}
