using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ApexPost.Infrastructure.Persistence.Sql;

// Used only by the dotnet-ef tool. It holds no connection string, so generating
// a migration or a script never reads secrets or touches a database. Commands
// that do connect must name their target with --connection.
public sealed class ApexPostDbContextFactory
	: IDesignTimeDbContextFactory<ApexPostDbContext>
{
	public ApexPostDbContext CreateDbContext(string[] args) =>
		new(new DbContextOptionsBuilder<ApexPostDbContext>()
			.UseSqlServer()
			.Options);
}
