using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApexPost.Infrastructure.Persistence.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedShipments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedShipments",
                columns: table => new
                {
                    TrackingNumber = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    RecipientName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecipientPhone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RecipientAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecipientCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedShipments", x => x.TrackingNumber);
                    table.CheckConstraint("CK_TrackedShipments_Status", "[Status] IN ('Created', 'PickedUp', 'InTransit', 'OutForDelivery', 'Delivered', 'Failed')");
                    table.CheckConstraint("CK_TrackedShipments_TrackingNumber", "[TrackingNumber] LIKE 'APX-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'");
                    table.CheckConstraint("CK_TrackedShipments_Weight", "[WeightKg] > 0");
                });

            migrationBuilder.CreateTable(
                name: "TrackedShipmentScans",
                columns: table => new
                {
                    ScanId = table.Column<string>(type: "varchar(42)", unicode: false, maxLength: 42, nullable: false),
                    ScanType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CourierId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ScannedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    TrackingNumber = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedShipmentScans", x => x.ScanId);
                    table.CheckConstraint("CK_TrackedShipmentScans_ScanId", "[ScanId] LIKE 'scan::________-____-____-____-____________'");
                    table.CheckConstraint("CK_TrackedShipmentScans_ScanType", "[ScanType] IN ('Pickup', 'InTransit', 'Delivered', 'Failed')");
                    table.ForeignKey(
                        name: "FK_TrackedShipmentScans_TrackedShipments_TrackingNumber",
                        column: x => x.TrackingNumber,
                        principalTable: "TrackedShipments",
                        principalColumn: "TrackingNumber",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedShipmentScans_TrackingNumber",
                table: "TrackedShipmentScans",
                column: "TrackingNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackedShipmentScans");

            migrationBuilder.DropTable(
                name: "TrackedShipments");
        }
    }
}
