using System.Reflection;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Reads the text a migration deployed, frozen in sql/procedures/history/<migration>.
// The current text in sql/procedures is not embedded, so a later change to a
// procedure cannot change an existing migration.
internal static class StoredProcedureScripts
{
	// CREATE OR ALTER must start a batch, but the idempotent script runs each
	// migration inside an IF block, so the procedure is created through EXEC.
	public static string ReadSnapshotAsExec(string migrationName, string procedureName) =>
		$"EXEC(N'{Escape(Read(migrationName, procedureName))}');";

	// For a later migration's Up or Down. ALTER fails if the procedure is missing,
	// where CREATE OR ALTER would hide a migration applied out of order.
	public static string ReadSnapshotAsAlter(string migrationName, string procedureName)
	{
		var script = Read(migrationName, procedureName)
			.Replace("CREATE OR ALTER PROCEDURE", "ALTER PROCEDURE", StringComparison.Ordinal);
		return $"EXEC sp_executesql N'{Escape(script)}';";
	}

	private static string Escape(string script) =>
		script.Replace("'", "''", StringComparison.Ordinal);

	private static string Read(string migrationName, string procedureName)
	{
		var resource = $"ApexPost.Sql.Procedures.History.{migrationName}.{procedureName}.sql";
		using var stream = Assembly.GetExecutingAssembly()
			.GetManifestResourceStream(resource)
			?? throw new InvalidOperationException(
				$"Embedded script '{resource}' was not found.");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
