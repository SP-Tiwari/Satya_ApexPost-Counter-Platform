using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPost.Infrastructure.Persistence.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyBranchSalesRecompile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(StoredProcedureScripts.ReadSnapshotAsAlter(nameof(AddDailyBranchSalesRecompile), "usp_DailyBranchSales"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(StoredProcedureScripts.ReadSnapshotAsAlter(nameof(AddStoredProcedures), "usp_DailyBranchSales"));
        }
    }
}
