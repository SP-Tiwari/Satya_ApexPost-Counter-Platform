using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ApexPost.Infrastructure.Persistence.Sql.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    AuditEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Controller = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    DurationMilliseconds = table.Column<double>(type: "float", nullable: false),
                    RouteValues = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    StaffId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    StaffRole = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    BranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.AuditEntryId);
                    table.CheckConstraint("CK_AuditEntries_DurationMilliseconds", "[DurationMilliseconds] >= 0");
                    table.CheckConstraint("CK_AuditEntries_Outcome", "[Outcome] IN ('Succeeded', 'Rejected', 'Failed')");
                    table.CheckConstraint("CK_AuditEntries_StatusCode", "[StatusCode] BETWEEN 100 AND 599");
                });

            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    BranchId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OpeningHours = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.BranchId);
                    table.UniqueConstraint("UQ_Branches_BranchCode", x => x.BranchCode);
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    ProductCategoryId = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.ProductCategoryId);
                    table.UniqueConstraint("UQ_ProductCategories_Name", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    RoleId = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.RoleId);
                    table.UniqueConstraint("UQ_Roles_Name", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "ShippingZones",
                columns: table => new
                {
                    ShippingZoneId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ZoneCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingZones", x => x.ShippingZoneId);
                    table.UniqueConstraint("UQ_ShippingZones_ZoneCode", x => x.ZoneCode);
                });

            migrationBuilder.CreateTable(
                name: "Counters",
                columns: table => new
                {
                    CounterId = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    BranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Counters", x => new { x.BranchCode, x.CounterId });
                    table.ForeignKey(
                        name: "FK_Counters_Branches_BranchCode",
                        column: x => x.BranchCode,
                        principalTable: "Branches",
                        principalColumn: "BranchCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MoneyOrders",
                columns: table => new
                {
                    MoneyOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sender = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProcessingBranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyOrders", x => x.MoneyOrderId);
                    table.CheckConstraint("CK_MoneyOrders_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_MoneyOrders_Currency", "[Currency] = [FeeCurrency]");
                    table.CheckConstraint("CK_MoneyOrders_Fee", "[Fee] >= 0");
                    table.CheckConstraint("CK_MoneyOrders_Status", "[Status] IN ('Issued', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_MoneyOrders_Branches_ProcessingBranchCode",
                        column: x => x.ProcessingBranchCode,
                        principalTable: "Branches",
                        principalColumn: "BranchCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Sku = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProductCategoryId = table.Column<byte>(type: "tinyint", nullable: false),
                    TaxCategory = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.ProductId);
                    table.UniqueConstraint("UQ_Products_Sku", x => x.Sku);
                    table.CheckConstraint("CK_Products_TaxCategory", "[TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')");
                    table.CheckConstraint("CK_Products_UnitPrice", "[UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_Products_ProductCategories_ProductCategoryId",
                        column: x => x.ProductCategoryId,
                        principalTable: "ProductCategories",
                        principalColumn: "ProductCategoryId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Staff",
                columns: table => new
                {
                    StaffId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    RoleId = table.Column<byte>(type: "tinyint", nullable: false),
                    HomeBranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Staff", x => x.StaffId);
                    table.UniqueConstraint("UQ_Staff_Email", x => x.Email);
                    table.ForeignKey(
                        name: "FK_Staff_Branches_HomeBranchCode",
                        column: x => x.HomeBranchCode,
                        principalTable: "Branches",
                        principalColumn: "BranchCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Staff_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "RoleId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    ShipmentId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    ShipmentType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: false),
                    ZoneCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    ServiceLevel = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    HeightCm = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    LengthCm = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    WidthCm = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.ShipmentId);
                    table.CheckConstraint("CK_Shipments_Dimensions", "[LengthCm] > 0 AND [WidthCm] > 0 AND [HeightCm] > 0");
                    table.CheckConstraint("CK_Shipments_ServiceLevel", "[ServiceLevel] IN ('Standard', 'Express', 'Registered')");
                    table.CheckConstraint("CK_Shipments_ShipmentType", "[ShipmentType] IN ('Parcel', 'Letter')");
                    table.CheckConstraint("CK_Shipments_Weight", "[WeightKg] > 0");
                    table.ForeignKey(
                        name: "FK_Shipments_ShippingZones_ZoneCode",
                        column: x => x.ZoneCode,
                        principalTable: "ShippingZones",
                        principalColumn: "ZoneCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShippingRates",
                columns: table => new
                {
                    ZoneCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    ServiceLevel = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    BasePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BasePriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PerKilogramCharge = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PerKilogramChargeCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingRates", x => new { x.ZoneCode, x.ServiceLevel });
                    table.CheckConstraint("CK_ShippingRates_BasePrice", "[BasePrice] >= 0");
                    table.CheckConstraint("CK_ShippingRates_Currency", "[BasePriceCurrency] = [PerKilogramChargeCurrency]");
                    table.CheckConstraint("CK_ShippingRates_PerKilogramCharge", "[PerKilogramCharge] >= 0");
                    table.CheckConstraint("CK_ShippingRates_ServiceLevel", "[ServiceLevel] IN ('Standard', 'Express', 'Registered')");
                    table.ForeignKey(
                        name: "FK_ShippingRates_ShippingZones_ZoneCode",
                        column: x => x.ZoneCode,
                        principalTable: "ShippingZones",
                        principalColumn: "ZoneCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdentityVerifications",
                columns: table => new
                {
                    MoneyOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    MaskedDocumentIdentifier = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    IsSuccessful = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityVerifications", x => x.MoneyOrderId);
                    table.CheckConstraint("CK_IdentityVerifications_MaskedDocumentIdentifier", "LEN([MaskedDocumentIdentifier]) = 8 AND [MaskedDocumentIdentifier] LIKE '[*][*][*][*]____'");
                    table.ForeignKey(
                        name: "FK_IdentityVerifications_MoneyOrders_MoneyOrderId",
                        column: x => x.MoneyOrderId,
                        principalTable: "MoneyOrders",
                        principalColumn: "MoneyOrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BranchStock",
                columns: table => new
                {
                    BranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Sku = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchStock", x => new { x.BranchCode, x.Sku });
                    table.CheckConstraint("CK_BranchStock_Quantity", "[Quantity] >= 0");
                    table.ForeignKey(
                        name: "FK_BranchStock_Branches_BranchCode",
                        column: x => x.BranchCode,
                        principalTable: "Branches",
                        principalColumn: "BranchCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchStock_Products_Sku",
                        column: x => x.Sku,
                        principalTable: "Products",
                        principalColumn: "Sku",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CounterTransactions",
                columns: table => new
                {
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CounterId = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    ClerkStaffId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    PaymentMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "Open"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    ReversalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CounterTransactions", x => x.TransactionId);
                    table.CheckConstraint("CK_CounterTransactions_ReversalReason", "([Status] IN ('Voided', 'Refunded') AND [ReversalReason] IS NOT NULL) OR ([Status] IN ('Open', 'Paid') AND [ReversalReason] IS NULL)");
                    table.CheckConstraint("CK_CounterTransactions_SaleDetails", "([CounterId] IS NULL AND [ClerkStaffId] IS NULL AND [PaymentMethod] IS NULL) OR ([CounterId] IS NOT NULL AND [ClerkStaffId] IS NOT NULL AND [PaymentMethod] IS NOT NULL)");
                    table.CheckConstraint("CK_CounterTransactions_Status", "[Status] IN ('Open', 'Paid', 'Voided', 'Refunded')");
                    table.ForeignKey(
                        name: "FK_CounterTransactions_Branches_BranchCode",
                        column: x => x.BranchCode,
                        principalTable: "Branches",
                        principalColumn: "BranchCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CounterTransactions_Staff_ClerkStaffId",
                        column: x => x.ClerkStaffId,
                        principalTable: "Staff",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffCredentials",
                columns: table => new
                {
                    StaffId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Iterations = table.Column<int>(type: "int", nullable: false),
                    Salt = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffCredentials", x => x.StaffId);
                    table.CheckConstraint("CK_StaffCredentials_Iterations", "[Iterations] > 0");
                    table.ForeignKey(
                        name: "FK_StaffCredentials_Staff_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Staff",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustments",
                columns: table => new
                {
                    StockAdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchCode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Sku = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Delta = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ChangedByStaffId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustments", x => x.StockAdjustmentId);
                    table.CheckConstraint("CK_StockAdjustments_Delta", "[Delta] <> 0");
                    table.ForeignKey(
                        name: "FK_StockAdjustments_BranchStock_BranchCode_Sku",
                        columns: x => new { x.BranchCode, x.Sku },
                        principalTable: "BranchStock",
                        principalColumns: new[] { "BranchCode", "Sku" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockAdjustments_Staff_ChangedByStaffId",
                        column: x => x.ChangedByStaffId,
                        principalTable: "Staff",
                        principalColumn: "StaffId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.PaymentId);
                    table.CheckConstraint("CK_Payments_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_Payments_EntryType", "[EntryType] IN ('Payment', 'Refund')");
                    table.ForeignKey(
                        name: "FK_Payments_CounterTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "CounterTransactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransactionLines",
                columns: table => new
                {
                    TransactionLineId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LineType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    TaxCategory = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Sku = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    ShipmentId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    MoneyOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionLines", x => x.TransactionLineId);
                    table.CheckConstraint("CK_TransactionLines_LineType", "[LineType] IN ('Goods', 'Service', 'MoneyOrder')");
                    table.CheckConstraint("CK_TransactionLines_MoneyOrder", "([LineType] = 'MoneyOrder' AND [MoneyOrderId] IS NOT NULL) OR ([LineType] <> 'MoneyOrder' AND [MoneyOrderId] IS NULL)");
                    table.CheckConstraint("CK_TransactionLines_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_TransactionLines_TaxCategory", "[TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')");
                    table.CheckConstraint("CK_TransactionLines_UnitPrice", "[UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_TransactionLines_CounterTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "CounterTransactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionLines_MoneyOrders_MoneyOrderId",
                        column: x => x.MoneyOrderId,
                        principalTable: "MoneyOrders",
                        principalColumn: "MoneyOrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionLines_Products_Sku",
                        column: x => x.Sku,
                        principalTable: "Products",
                        principalColumn: "Sku",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransactionLines_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "ShipmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransactionTaxRates",
                columns: table => new
                {
                    TaxCategory = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, defaultValueSql: "SUSER_SNAME()"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionTaxRates", x => new { x.TransactionId, x.TaxCategory });
                    table.CheckConstraint("CK_TransactionTaxRates_Rate", "[Rate] >= 0");
                    table.CheckConstraint("CK_TransactionTaxRates_TaxCategory", "[TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')");
                    table.ForeignKey(
                        name: "FK_TransactionTaxRates_CounterTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "CounterTransactions",
                        principalColumn: "TransactionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ProductCategories",
                columns: new[] { "ProductCategoryId", "ModifiedAtUtc", "Name" },
                values: new object[,]
                {
                    { (byte)0, null, "Packaging" },
                    { (byte)1, null, "Stationery" },
                    { (byte)2, null, "Office Supplies" }
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "RoleId", "ModifiedAtUtc", "Name" },
                values: new object[,]
                {
                    { (byte)0, null, "Counter Clerk" },
                    { (byte)1, null, "Branch Manager" },
                    { (byte)2, null, "Auditor" },
                    { (byte)3, null, "Administrator" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchStock_Sku",
                table: "BranchStock",
                column: "Sku");

            migrationBuilder.CreateIndex(
                name: "IX_CounterTransactions_BranchCode",
                table: "CounterTransactions",
                column: "BranchCode");

            migrationBuilder.CreateIndex(
                name: "IX_CounterTransactions_ClerkStaffId",
                table: "CounterTransactions",
                column: "ClerkStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyOrders_ProcessingBranchCode",
                table: "MoneyOrders",
                column: "ProcessingBranchCode");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TransactionId",
                table: "Payments",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_ProductCategoryId",
                table: "Products",
                column: "ProductCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_ZoneCode",
                table: "Shipments",
                column: "ZoneCode");

            migrationBuilder.CreateIndex(
                name: "IX_Staff_HomeBranchCode",
                table: "Staff",
                column: "HomeBranchCode");

            migrationBuilder.CreateIndex(
                name: "IX_Staff_RoleId",
                table: "Staff",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_BranchCode_Sku",
                table: "StockAdjustments",
                columns: new[] { "BranchCode", "Sku" });

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_ChangedByStaffId",
                table: "StockAdjustments",
                column: "ChangedByStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionLines_MoneyOrderId",
                table: "TransactionLines",
                column: "MoneyOrderId",
                unique: true,
                filter: "[MoneyOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionLines_ShipmentId",
                table: "TransactionLines",
                column: "ShipmentId",
                unique: true,
                filter: "[ShipmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionLines_Sku",
                table: "TransactionLines",
                column: "Sku");

            migrationBuilder.CreateIndex(
                name: "IX_TransactionLines_TransactionId",
                table: "TransactionLines",
                column: "TransactionId");

            // Added by hand: a sale's counter must belong to the sale's branch.
            // EF Core cannot model this key because BranchCode is on the
            // transaction and CounterId is inside the owned CounterSaleDetails.
            // Keep this if InitialCreate is ever regenerated. See ADR 0018.
            migrationBuilder.AddForeignKey(
                name: "FK_CounterTransactions_Counters_BranchCode_CounterId",
                table: "CounterTransactions",
                columns: new[] { "BranchCode", "CounterId" },
                principalTable: "Counters",
                principalColumns: new[] { "BranchCode", "CounterId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Added by hand, with the foreign key at the end of Up.
            migrationBuilder.DropForeignKey(
                name: "FK_CounterTransactions_Counters_BranchCode_CounterId",
                table: "CounterTransactions");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "Counters");

            migrationBuilder.DropTable(
                name: "IdentityVerifications");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "ShippingRates");

            migrationBuilder.DropTable(
                name: "StaffCredentials");

            migrationBuilder.DropTable(
                name: "StockAdjustments");

            migrationBuilder.DropTable(
                name: "TransactionLines");

            migrationBuilder.DropTable(
                name: "TransactionTaxRates");

            migrationBuilder.DropTable(
                name: "BranchStock");

            migrationBuilder.DropTable(
                name: "MoneyOrders");

            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropTable(
                name: "CounterTransactions");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "ShippingZones");

            migrationBuilder.DropTable(
                name: "Staff");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropTable(
                name: "Branches");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
