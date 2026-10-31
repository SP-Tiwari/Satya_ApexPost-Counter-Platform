IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditEntries] (
        [AuditEntryId] bigint NOT NULL IDENTITY,
        [TimestampUtc] datetime2(3) NOT NULL,
        [Controller] varchar(100) NOT NULL,
        [Action] varchar(100) NOT NULL,
        [Description] nvarchar(300) NOT NULL,
        [Outcome] varchar(20) NOT NULL,
        [StatusCode] int NOT NULL,
        [DurationMilliseconds] float NOT NULL,
        [RouteValues] nvarchar(1000) NOT NULL,
        [StaffId] varchar(50) NULL,
        [StaffRole] varchar(50) NULL,
        [BranchCode] varchar(10) NULL,
        [CorrelationId] varchar(64) NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_AuditEntries] PRIMARY KEY ([AuditEntryId]),
        CONSTRAINT [CK_AuditEntries_DurationMilliseconds] CHECK ([DurationMilliseconds] >= 0),
        CONSTRAINT [CK_AuditEntries_Outcome] CHECK ([Outcome] IN ('Succeeded', 'Rejected', 'Failed')),
        CONSTRAINT [CK_AuditEntries_StatusCode] CHECK ([StatusCode] BETWEEN 100 AND 599)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Branches] (
        [BranchId] int NOT NULL IDENTITY,
        [BranchCode] varchar(10) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Region] nvarchar(100) NOT NULL,
        [OpeningHours] nvarchar(100) NOT NULL,
        [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_Branches] PRIMARY KEY ([BranchId]),
        CONSTRAINT [UQ_Branches_BranchCode] UNIQUE ([BranchCode])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [ProductCategories] (
        [ProductCategoryId] tinyint NOT NULL,
        [Name] nvarchar(50) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_ProductCategories] PRIMARY KEY ([ProductCategoryId]),
        CONSTRAINT [UQ_ProductCategories_Name] UNIQUE ([Name])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Roles] (
        [RoleId] tinyint NOT NULL,
        [Name] nvarchar(50) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([RoleId]),
        CONSTRAINT [UQ_Roles_Name] UNIQUE ([Name])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [ShippingZones] (
        [ShippingZoneId] int NOT NULL IDENTITY,
        [ZoneCode] varchar(50) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_ShippingZones] PRIMARY KEY ([ShippingZoneId]),
        CONSTRAINT [UQ_ShippingZones_ZoneCode] UNIQUE ([ZoneCode])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Counters] (
        [CounterId] varchar(20) NOT NULL,
        [BranchCode] varchar(10) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_Counters] PRIMARY KEY ([BranchCode], [CounterId]),
        CONSTRAINT [FK_Counters_Branches_BranchCode] FOREIGN KEY ([BranchCode]) REFERENCES [Branches] ([BranchCode]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [MoneyOrders] (
        [MoneyOrderId] uniqueidentifier NOT NULL,
        [Sender] nvarchar(200) NOT NULL,
        [Recipient] nvarchar(200) NOT NULL,
        [ProcessingBranchCode] varchar(10) NOT NULL,
        [Status] varchar(20) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] char(3) NOT NULL,
        [Fee] decimal(18,2) NOT NULL,
        [FeeCurrency] char(3) NOT NULL,
        CONSTRAINT [PK_MoneyOrders] PRIMARY KEY ([MoneyOrderId]),
        CONSTRAINT [CK_MoneyOrders_Amount] CHECK ([Amount] > 0),
        CONSTRAINT [CK_MoneyOrders_Currency] CHECK ([Currency] = [FeeCurrency]),
        CONSTRAINT [CK_MoneyOrders_Fee] CHECK ([Fee] >= 0),
        CONSTRAINT [CK_MoneyOrders_Status] CHECK ([Status] IN ('Issued', 'Cancelled')),
        CONSTRAINT [FK_MoneyOrders_Branches_ProcessingBranchCode] FOREIGN KEY ([ProcessingBranchCode]) REFERENCES [Branches] ([BranchCode]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Products] (
        [ProductId] int NOT NULL IDENTITY,
        [Sku] varchar(30) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [ProductCategoryId] tinyint NOT NULL,
        [TaxCategory] varchar(20) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [Currency] char(3) NOT NULL,
        CONSTRAINT [PK_Products] PRIMARY KEY ([ProductId]),
        CONSTRAINT [UQ_Products_Sku] UNIQUE ([Sku]),
        CONSTRAINT [CK_Products_TaxCategory] CHECK ([TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')),
        CONSTRAINT [CK_Products_UnitPrice] CHECK ([UnitPrice] >= 0),
        CONSTRAINT [FK_Products_ProductCategories_ProductCategoryId] FOREIGN KEY ([ProductCategoryId]) REFERENCES [ProductCategories] ([ProductCategoryId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Staff] (
        [StaffId] varchar(50) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Email] nvarchar(254) NOT NULL,
        [RoleId] tinyint NOT NULL,
        [HomeBranchCode] varchar(10) NOT NULL,
        [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_Staff] PRIMARY KEY ([StaffId]),
        CONSTRAINT [UQ_Staff_Email] UNIQUE ([Email]),
        CONSTRAINT [FK_Staff_Branches_HomeBranchCode] FOREIGN KEY ([HomeBranchCode]) REFERENCES [Branches] ([BranchCode]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Staff_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([RoleId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Shipments] (
        [ShipmentId] varchar(50) NOT NULL,
        [ShipmentType] varchar(20) NOT NULL,
        [WeightKg] decimal(10,3) NOT NULL,
        [ZoneCode] varchar(50) NOT NULL,
        [ServiceLevel] varchar(20) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [HeightCm] decimal(8,2) NOT NULL,
        [LengthCm] decimal(8,2) NOT NULL,
        [WidthCm] decimal(8,2) NOT NULL,
        CONSTRAINT [PK_Shipments] PRIMARY KEY ([ShipmentId]),
        CONSTRAINT [CK_Shipments_Dimensions] CHECK ([LengthCm] > 0 AND [WidthCm] > 0 AND [HeightCm] > 0),
        CONSTRAINT [CK_Shipments_ServiceLevel] CHECK ([ServiceLevel] IN ('Standard', 'Express', 'Registered')),
        CONSTRAINT [CK_Shipments_ShipmentType] CHECK ([ShipmentType] IN ('Parcel', 'Letter')),
        CONSTRAINT [CK_Shipments_Weight] CHECK ([WeightKg] > 0),
        CONSTRAINT [FK_Shipments_ShippingZones_ZoneCode] FOREIGN KEY ([ZoneCode]) REFERENCES [ShippingZones] ([ZoneCode]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [ShippingRates] (
        [ZoneCode] varchar(50) NOT NULL,
        [ServiceLevel] varchar(20) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [BasePrice] decimal(18,2) NOT NULL,
        [BasePriceCurrency] char(3) NOT NULL,
        [PerKilogramCharge] decimal(18,2) NOT NULL,
        [PerKilogramChargeCurrency] char(3) NOT NULL,
        CONSTRAINT [PK_ShippingRates] PRIMARY KEY ([ZoneCode], [ServiceLevel]),
        CONSTRAINT [CK_ShippingRates_BasePrice] CHECK ([BasePrice] >= 0),
        CONSTRAINT [CK_ShippingRates_Currency] CHECK ([BasePriceCurrency] = [PerKilogramChargeCurrency]),
        CONSTRAINT [CK_ShippingRates_PerKilogramCharge] CHECK ([PerKilogramCharge] >= 0),
        CONSTRAINT [CK_ShippingRates_ServiceLevel] CHECK ([ServiceLevel] IN ('Standard', 'Express', 'Registered')),
        CONSTRAINT [FK_ShippingRates_ShippingZones_ZoneCode] FOREIGN KEY ([ZoneCode]) REFERENCES [ShippingZones] ([ZoneCode]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [IdentityVerifications] (
        [MoneyOrderId] uniqueidentifier NOT NULL,
        [DocumentType] varchar(50) NOT NULL,
        [MaskedDocumentIdentifier] varchar(8) NOT NULL,
        [IsSuccessful] bit NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_IdentityVerifications] PRIMARY KEY ([MoneyOrderId]),
        CONSTRAINT [CK_IdentityVerifications_MaskedDocumentIdentifier] CHECK (LEN([MaskedDocumentIdentifier]) = 8 AND [MaskedDocumentIdentifier] LIKE '[*][*][*][*]____'),
        CONSTRAINT [FK_IdentityVerifications_MoneyOrders_MoneyOrderId] FOREIGN KEY ([MoneyOrderId]) REFERENCES [MoneyOrders] ([MoneyOrderId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [BranchStock] (
        [BranchCode] varchar(10) NOT NULL,
        [Sku] varchar(30) NOT NULL,
        [Quantity] int NOT NULL DEFAULT 0,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_BranchStock] PRIMARY KEY ([BranchCode], [Sku]),
        CONSTRAINT [CK_BranchStock_Quantity] CHECK ([Quantity] >= 0),
        CONSTRAINT [FK_BranchStock_Branches_BranchCode] FOREIGN KEY ([BranchCode]) REFERENCES [Branches] ([BranchCode]) ON DELETE NO ACTION,
        CONSTRAINT [FK_BranchStock_Products_Sku] FOREIGN KEY ([Sku]) REFERENCES [Products] ([Sku]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [CounterTransactions] (
        [TransactionId] uniqueidentifier NOT NULL,
        [Currency] char(3) NOT NULL,
        [OccurredAtUtc] datetime2(3) NOT NULL,
        [CounterId] varchar(20) NULL,
        [ClerkStaffId] varchar(50) NULL,
        [PaymentMethod] nvarchar(50) NULL,
        [BranchCode] varchar(10) NOT NULL,
        [Status] varchar(20) NOT NULL DEFAULT 'Open',
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        [ReversalReason] nvarchar(500) NULL,
        CONSTRAINT [PK_CounterTransactions] PRIMARY KEY ([TransactionId]),
        CONSTRAINT [CK_CounterTransactions_ReversalReason] CHECK (([Status] IN ('Voided', 'Refunded') AND [ReversalReason] IS NOT NULL) OR ([Status] IN ('Open', 'Paid') AND [ReversalReason] IS NULL)),
        CONSTRAINT [CK_CounterTransactions_SaleDetails] CHECK (([CounterId] IS NULL AND [ClerkStaffId] IS NULL AND [PaymentMethod] IS NULL) OR ([CounterId] IS NOT NULL AND [ClerkStaffId] IS NOT NULL AND [PaymentMethod] IS NOT NULL)),
        CONSTRAINT [CK_CounterTransactions_Status] CHECK ([Status] IN ('Open', 'Paid', 'Voided', 'Refunded')),
        CONSTRAINT [FK_CounterTransactions_Branches_BranchCode] FOREIGN KEY ([BranchCode]) REFERENCES [Branches] ([BranchCode]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CounterTransactions_Staff_ClerkStaffId] FOREIGN KEY ([ClerkStaffId]) REFERENCES [Staff] ([StaffId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [StaffCredentials] (
        [StaffId] varchar(50) NOT NULL,
        [Version] int NOT NULL,
        [Iterations] int NOT NULL,
        [Salt] varchar(256) NOT NULL,
        [PasswordHash] varchar(256) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_StaffCredentials] PRIMARY KEY ([StaffId]),
        CONSTRAINT [CK_StaffCredentials_Iterations] CHECK ([Iterations] > 0),
        CONSTRAINT [FK_StaffCredentials_Staff_StaffId] FOREIGN KEY ([StaffId]) REFERENCES [Staff] ([StaffId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [StockAdjustments] (
        [StockAdjustmentId] uniqueidentifier NOT NULL,
        [BranchCode] varchar(10) NOT NULL,
        [Sku] varchar(30) NOT NULL,
        [Delta] int NOT NULL,
        [Reason] nvarchar(500) NOT NULL,
        [ChangedByStaffId] varchar(50) NOT NULL,
        [OccurredAtUtc] datetime2(3) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_StockAdjustments] PRIMARY KEY ([StockAdjustmentId]),
        CONSTRAINT [CK_StockAdjustments_Delta] CHECK ([Delta] <> 0),
        CONSTRAINT [FK_StockAdjustments_BranchStock_BranchCode_Sku] FOREIGN KEY ([BranchCode], [Sku]) REFERENCES [BranchStock] ([BranchCode], [Sku]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StockAdjustments_Staff_ChangedByStaffId] FOREIGN KEY ([ChangedByStaffId]) REFERENCES [Staff] ([StaffId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [Payments] (
        [PaymentId] uniqueidentifier NOT NULL,
        [TransactionId] uniqueidentifier NOT NULL,
        [EntryType] varchar(20) NOT NULL,
        [PaymentMethod] nvarchar(50) NOT NULL,
        [RecordedAtUtc] datetime2(3) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] char(3) NOT NULL,
        CONSTRAINT [PK_Payments] PRIMARY KEY ([PaymentId]),
        CONSTRAINT [CK_Payments_Amount] CHECK ([Amount] > 0),
        CONSTRAINT [CK_Payments_EntryType] CHECK ([EntryType] IN ('Payment', 'Refund')),
        CONSTRAINT [FK_Payments_CounterTransactions_TransactionId] FOREIGN KEY ([TransactionId]) REFERENCES [CounterTransactions] ([TransactionId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [TransactionLines] (
        [TransactionLineId] bigint NOT NULL IDENTITY,
        [Description] nvarchar(300) NOT NULL,
        [LineType] varchar(20) NOT NULL,
        [Quantity] int NOT NULL,
        [TaxCategory] varchar(20) NOT NULL,
        [Sku] varchar(30) NULL,
        [ShipmentId] varchar(50) NULL,
        [MoneyOrderId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        [TransactionId] uniqueidentifier NOT NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [Currency] char(3) NOT NULL,
        CONSTRAINT [PK_TransactionLines] PRIMARY KEY ([TransactionLineId]),
        CONSTRAINT [CK_TransactionLines_LineType] CHECK ([LineType] IN ('Goods', 'Service', 'MoneyOrder')),
        CONSTRAINT [CK_TransactionLines_MoneyOrder] CHECK (([LineType] = 'MoneyOrder' AND [MoneyOrderId] IS NOT NULL) OR ([LineType] <> 'MoneyOrder' AND [MoneyOrderId] IS NULL)),
        CONSTRAINT [CK_TransactionLines_Quantity] CHECK ([Quantity] > 0),
        CONSTRAINT [CK_TransactionLines_TaxCategory] CHECK ([TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')),
        CONSTRAINT [CK_TransactionLines_UnitPrice] CHECK ([UnitPrice] >= 0),
        CONSTRAINT [FK_TransactionLines_CounterTransactions_TransactionId] FOREIGN KEY ([TransactionId]) REFERENCES [CounterTransactions] ([TransactionId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TransactionLines_MoneyOrders_MoneyOrderId] FOREIGN KEY ([MoneyOrderId]) REFERENCES [MoneyOrders] ([MoneyOrderId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TransactionLines_Products_Sku] FOREIGN KEY ([Sku]) REFERENCES [Products] ([Sku]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TransactionLines_Shipments_ShipmentId] FOREIGN KEY ([ShipmentId]) REFERENCES [Shipments] ([ShipmentId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE TABLE [TransactionTaxRates] (
        [TaxCategory] varchar(20) NOT NULL,
        [TransactionId] uniqueidentifier NOT NULL,
        [Rate] decimal(9,6) NOT NULL,
        [CreatedAtUtc] datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        [CreatedBy] varchar(128) NOT NULL DEFAULT (SUSER_SNAME()),
        [ModifiedAtUtc] datetime2(3) NULL,
        CONSTRAINT [PK_TransactionTaxRates] PRIMARY KEY ([TransactionId], [TaxCategory]),
        CONSTRAINT [CK_TransactionTaxRates_Rate] CHECK ([Rate] >= 0),
        CONSTRAINT [CK_TransactionTaxRates_TaxCategory] CHECK ([TaxCategory] IN ('Standard', 'Reduced', 'ZeroRated')),
        CONSTRAINT [FK_TransactionTaxRates_CounterTransactions_TransactionId] FOREIGN KEY ([TransactionId]) REFERENCES [CounterTransactions] ([TransactionId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ProductCategoryId', N'ModifiedAtUtc', N'Name') AND [object_id] = OBJECT_ID(N'[ProductCategories]'))
        SET IDENTITY_INSERT [ProductCategories] ON;
    EXEC(N'INSERT INTO [ProductCategories] ([ProductCategoryId], [ModifiedAtUtc], [Name])
    VALUES (CAST(0 AS tinyint), NULL, N''Packaging''),
    (CAST(1 AS tinyint), NULL, N''Stationery''),
    (CAST(2 AS tinyint), NULL, N''Office Supplies'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ProductCategoryId', N'ModifiedAtUtc', N'Name') AND [object_id] = OBJECT_ID(N'[ProductCategories]'))
        SET IDENTITY_INSERT [ProductCategories] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RoleId', N'ModifiedAtUtc', N'Name') AND [object_id] = OBJECT_ID(N'[Roles]'))
        SET IDENTITY_INSERT [Roles] ON;
    EXEC(N'INSERT INTO [Roles] ([RoleId], [ModifiedAtUtc], [Name])
    VALUES (CAST(0 AS tinyint), NULL, N''Counter Clerk''),
    (CAST(1 AS tinyint), NULL, N''Branch Manager''),
    (CAST(2 AS tinyint), NULL, N''Auditor''),
    (CAST(3 AS tinyint), NULL, N''Administrator'')');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RoleId', N'ModifiedAtUtc', N'Name') AND [object_id] = OBJECT_ID(N'[Roles]'))
        SET IDENTITY_INSERT [Roles] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_BranchStock_Sku] ON [BranchStock] ([Sku]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CounterTransactions_BranchCode] ON [CounterTransactions] ([BranchCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_CounterTransactions_ClerkStaffId] ON [CounterTransactions] ([ClerkStaffId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_MoneyOrders_ProcessingBranchCode] ON [MoneyOrders] ([ProcessingBranchCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Payments_TransactionId] ON [Payments] ([TransactionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Products_ProductCategoryId] ON [Products] ([ProductCategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Shipments_ZoneCode] ON [Shipments] ([ZoneCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Staff_HomeBranchCode] ON [Staff] ([HomeBranchCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Staff_RoleId] ON [Staff] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_StockAdjustments_BranchCode_Sku] ON [StockAdjustments] ([BranchCode], [Sku]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_StockAdjustments_ChangedByStaffId] ON [StockAdjustments] ([ChangedByStaffId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_TransactionLines_MoneyOrderId] ON [TransactionLines] ([MoneyOrderId]) WHERE [MoneyOrderId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_TransactionLines_ShipmentId] ON [TransactionLines] ([ShipmentId]) WHERE [ShipmentId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TransactionLines_Sku] ON [TransactionLines] ([Sku]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_TransactionLines_TransactionId] ON [TransactionLines] ([TransactionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    ALTER TABLE [CounterTransactions] ADD CONSTRAINT [FK_CounterTransactions_Counters_BranchCode_CounterId] FOREIGN KEY ([BranchCode], [CounterId]) REFERENCES [Counters] ([BranchCode], [CounterId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031085407_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031085407_InitialCreate', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031124843_AddStoredProcedures'
)
BEGIN
    EXEC(N'-- Retail and service totals for one branch on one UTC business date.
    -- Matches DailySalesReportService: Paid transactions only, Goods lines are
    -- retail, Service lines are services, money-order principal counts as neither,
    -- and tax is stored line amount x stored rate. A date with no paid sale
    -- returns no row. Totals are rounded only for the result set.
    CREATE OR ALTER PROCEDURE dbo.usp_DailyBranchSales
        @BranchCode varchar(10),
        @Date date
    AS
    BEGIN
        SET NOCOUNT ON;

        IF NULLIF(LTRIM(RTRIM(@BranchCode)), '''') IS NULL
        BEGIN
            ;THROW 51000, N''@BranchCode is required.'', 1;
        END;

        -- The upper bound below is the next day, which 9999-12-31 does not have.
        IF @Date IS NULL OR @Date = ''99991231''
        BEGIN
            ;THROW 51000, N''@Date must be a valid business date.'', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchCode = @BranchCode)
        BEGIN
            DECLARE @NotFound nvarchar(2048) =
                CONCAT(N''Branch '''''', @BranchCode, N'''''' was not found.'');
            ;THROW 51001, @NotFound, 1;
        END;

        DECLARE @From datetime2(3) = @Date;
        DECLARE @To datetime2(3) = DATEADD(DAY, 1, @From);
        DECLARE @Totals TABLE
        (
            Currency char(3) NOT NULL,
            RetailSales decimal(38, 6) NOT NULL,
            ServiceSales decimal(38, 6) NOT NULL
        );

        INSERT INTO @Totals (Currency, RetailSales, ServiceSales)
        SELECT
            t.Currency,
            SUM(CASE WHEN l.LineType = ''Goods''
                THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END),
            SUM(CASE WHEN l.LineType = ''Service''
                THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END)
        FROM dbo.CounterTransactions AS t
        INNER JOIN dbo.TransactionLines AS l
            ON l.TransactionId = t.TransactionId
        -- Money-order lines carry no tax, so they may have no rate.
        LEFT JOIN dbo.TransactionTaxRates AS r
            ON r.TransactionId = l.TransactionId
            AND r.TaxCategory = l.TaxCategory
        WHERE t.BranchCode = @BranchCode
            AND t.Status = ''Paid''
            AND t.OccurredAtUtc >= @From
            AND t.OccurredAtUtc < @To
        GROUP BY t.Currency;

        IF (SELECT COUNT(*) FROM @Totals) > 1
        BEGIN
            DECLARE @Incompatible nvarchar(2048) = CONCAT(
                N''Daily sales for branch '''''', @BranchCode, N'''''' on '''''',
                CONVERT(char(10), @Date, 23), N'''''' contain incompatible currencies.'');
            ;THROW 51003, @Incompatible, 1;
        END;

        SELECT
            @BranchCode AS BranchCode,
            @Date AS BusinessDate,
            Currency,
            CAST(ROUND(RetailSales, 2) AS decimal(18, 2)) AS RetailSales,
            CAST(ROUND(ServiceSales, 2) AS decimal(18, 2)) AS ServiceSales,
            CAST(ROUND(RetailSales + ServiceSales, 2) AS decimal(18, 2)) AS TotalSales
        FROM @Totals;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031124843_AddStoredProcedures'
)
BEGIN
    EXEC(N'-- Stock at or below @Threshold, for one branch or, when @BranchCode is NULL,
    -- for all branches. Matches LowStockReportService. OPTION (RECOMPILE) is
    -- explained in docs/adr/0023-stored-procedures.md.
    CREATE OR ALTER PROCEDURE dbo.usp_LowStockReport
        @BranchCode varchar(10) = NULL,
        @Threshold int
    AS
    BEGIN
        SET NOCOUNT ON;

        IF @Threshold IS NULL OR @Threshold < 0
        BEGIN
            ;THROW 51000, N''Low-stock threshold cannot be negative.'', 1;
        END;

        IF @BranchCode IS NOT NULL AND LTRIM(RTRIM(@BranchCode)) = ''''
        BEGIN
            ;THROW 51000, N''@BranchCode cannot be blank. Pass NULL for all branches.'', 1;
        END;

        IF @BranchCode IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchCode = @BranchCode)
        BEGIN
            DECLARE @NotFound nvarchar(2048) =
                CONCAT(N''Branch '''''', @BranchCode, N'''''' was not found.'');
            ;THROW 51001, @NotFound, 1;
        END;

        SELECT
            s.BranchCode,
            s.Sku,
            s.Quantity,
            @Threshold AS Threshold
        FROM dbo.BranchStock AS s
        WHERE (@BranchCode IS NULL OR s.BranchCode = @BranchCode)
            AND s.Quantity <= @Threshold
        ORDER BY s.BranchCode, s.Sku
        OPTION (RECOMPILE);
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031124843_AddStoredProcedures'
)
BEGIN
    EXEC(N'-- Changes one branch''s stock and writes its StockAdjustments history row
    -- atomically. Applies the rules of StockAdjustmentApplicationService: only an
    -- active Branch Manager of that branch may adjust, the change cannot be zero,
    -- and stock cannot go negative.
    CREATE OR ALTER PROCEDURE dbo.usp_AdjustStock
        @BranchCode varchar(10),
        @Sku varchar(30),
        @Delta int,
        -- One character over the column, so an overlong reason is rejected below
        -- rather than silently cut to fit the parameter.
        @Reason nvarchar(501),
        @ChangedBy varchar(50)
    AS
    BEGIN
        SET NOCOUNT ON;
        SET XACT_ABORT ON;

        IF NULLIF(LTRIM(RTRIM(@BranchCode)), '''') IS NULL
        BEGIN
            ;THROW 51000, N''@BranchCode is required.'', 1;
        END;

        IF NULLIF(LTRIM(RTRIM(@Sku)), '''') IS NULL
        BEGIN
            ;THROW 51000, N''@Sku is required.'', 1;
        END;

        IF @Delta IS NULL OR @Delta = 0 OR @Delta = -2147483648
        BEGIN
            ;THROW 51000, N''Stock adjustment must be a non-zero supported quantity.'', 1;
        END;

        DECLARE @TrimmedReason nvarchar(501) = LTRIM(RTRIM(@Reason));
        IF NULLIF(@TrimmedReason, N'''') IS NULL OR LEN(@TrimmedReason) > 500
        BEGIN
            ;THROW 51000, N''A reason of up to 500 characters is required to adjust stock.'', 1;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.Staff AS s
            INNER JOIN dbo.Roles AS r ON r.RoleId = s.RoleId
            WHERE s.StaffId = @ChangedBy
                AND s.IsActive = 1
                AND s.HomeBranchCode = @BranchCode
                AND r.Name = N''Branch Manager''
        )
        BEGIN
            ;THROW 51002, N''An active Branch Manager is required to adjust stock.'', 1;
        END;

        DECLARE @StockAdjustmentId uniqueidentifier = NEWID();
        DECLARE @NewQuantity int;

        BEGIN TRY
            BEGIN TRANSACTION;

            -- One statement reads and changes the row under its update lock, so
            -- two callers cannot both act on the same starting quantity.
            UPDATE dbo.BranchStock
            SET @NewQuantity = Quantity = Quantity + @Delta,
                ModifiedAtUtc = SYSUTCDATETIME()
            WHERE BranchCode = @BranchCode
                AND Sku = @Sku
                AND Quantity + @Delta >= 0;

            IF @@ROWCOUNT = 0
            BEGIN
                IF EXISTS
                (
                    SELECT 1 FROM dbo.BranchStock
                    WHERE BranchCode = @BranchCode AND Sku = @Sku
                )
                BEGIN
                    ;THROW 51003, N''Insufficient stock available.'', 1;
                END;

                DECLARE @NotFound nvarchar(2048) = CONCAT(
                    N''Stock for product '''''', @Sku, N'''''' at branch '''''',
                    @BranchCode, N'''''' was not found.'');
                ;THROW 51001, @NotFound, 1;
            END;

            INSERT INTO dbo.StockAdjustments
                (StockAdjustmentId, BranchCode, Sku, Delta, Reason,
                 ChangedByStaffId, OccurredAtUtc, CreatedBy)
            VALUES
                (@StockAdjustmentId, @BranchCode, @Sku, @Delta, @TrimmedReason,
                 @ChangedBy, SYSUTCDATETIME(), @ChangedBy);

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            -- -1 is an uncommittable transaction and 1 a committable one. Both are
            -- rolled back, so stock never changes without its history row.
            IF XACT_STATE() <> 0
                ROLLBACK TRANSACTION;
            ;THROW;
        END CATCH;

        SELECT @StockAdjustmentId AS StockAdjustmentId, @NewQuantity AS NewQuantity;
    END;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031124843_AddStoredProcedures'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031124843_AddStoredProcedures', N'10.0.14');
END;

COMMIT;
GO

