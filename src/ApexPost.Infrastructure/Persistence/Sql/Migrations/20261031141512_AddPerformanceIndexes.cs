using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPost.Infrastructure.Persistence.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        // Reviewed by hand. The money-order and transaction-line includes also name
        // UnitPrice, Amount, Currency and Fee, which IncludeProperties cannot express
        // because they belong to complex properties. See ADR 0024.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransactionLines_TransactionId",
                table: "TransactionLines");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_ZoneCode",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOrders_ProcessingBranchCode",
                table: "MoneyOrders");

            migrationBuilder.DropIndex(
                name: "IX_CounterTransactions_BranchCode",
                table: "CounterTransactions");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionLines_TransactionId",
                table: "TransactionLines",
                column: "TransactionId")
                .Annotation("SqlServer:Include", new[] { "LineType", "Quantity", "UnitPrice", "TaxCategory", "ShipmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_ZoneCode_ServiceLevel",
                table: "Shipments",
                columns: new[] { "ZoneCode", "ServiceLevel" })
                .Annotation("SqlServer:Include", new[] { "ShipmentType", "WeightKg" });

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOrders_ProcessingBranchCode",
                table: "MoneyOrders",
                column: "ProcessingBranchCode")
                .Annotation("SqlServer:Include", new[] { "Status", "Amount", "Currency", "Fee", "Sender", "Recipient" });

            migrationBuilder.CreateIndex(
                name: "IX_CounterTransactions_BranchCode_OccurredAtUtc",
                table: "CounterTransactions",
                columns: new[] { "BranchCode", "OccurredAtUtc" })
                .Annotation("SqlServer:Include", new[] { "Status", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_BranchStock_Quantity",
                table: "BranchStock",
                column: "Quantity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TransactionLines_TransactionId",
                table: "TransactionLines");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_ZoneCode_ServiceLevel",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_MoneyOrders_ProcessingBranchCode",
                table: "MoneyOrders");

            migrationBuilder.DropIndex(
                name: "IX_CounterTransactions_BranchCode_OccurredAtUtc",
                table: "CounterTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BranchStock_Quantity",
                table: "BranchStock");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionLines_TransactionId",
                table: "TransactionLines",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_ZoneCode",
                table: "Shipments",
                column: "ZoneCode");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOrders_ProcessingBranchCode",
                table: "MoneyOrders",
                column: "ProcessingBranchCode");

            migrationBuilder.CreateIndex(
                name: "IX_CounterTransactions_BranchCode",
                table: "CounterTransactions",
                column: "BranchCode");
        }
    }
}
