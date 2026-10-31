using System.Reflection;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Reads the embedded files in sql/procedures, so the scripts in the repository
// are the only copy of each procedure's text.
internal static class StoredProcedureScripts
{
	// CREATE OR ALTER must start a batch, but the idempotent script runs each
	// migration inside an IF block, so the procedure is created through EXEC.
	public static string ReadAsExec(string procedureName) =>
		$"EXEC(N'{Read(procedureName).Replace("'", "''", StringComparison.Ordinal)}');";

	private static string Read(string procedureName)
	{
		var resource = $"ApexPost.Sql.Procedures.{procedureName}.sql";
		using var stream = Assembly.GetExecutingAssembly()
			.GetManifestResourceStream(resource)
			?? throw new InvalidOperationException(
				$"Embedded script '{resource}' was not found.");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
