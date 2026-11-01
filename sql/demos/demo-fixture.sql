-- Fictional rows for the reports, the atomic sale and the concurrency demos.
-- Runs only in a disposable ApexPost_Test_* database that has been migrated.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE 'ApexPost[_]Test[_]%'
BEGIN
    ;THROW 50000, N'Demo scripts run only in a disposable ApexPost_Test_* database.', 1;
END;

DECLARE @Now datetime2(3) = SYSUTCDATETIME();
DECLARE @Yesterday datetime2(3) = DATEADD(DAY, -1, @Now);
DECLARE @TodaySale uniqueidentifier = NEWID();
DECLARE @YesterdaySale uniqueidentifier = NEWID();
DECLARE @VerifiedOrder uniqueidentifier = NEWID();
DECLARE @FailedOrder uniqueidentifier = NEWID();

BEGIN TRANSACTION;

INSERT INTO dbo.Branches (BranchCode, Name, Region, OpeningHours)
VALUES
    ('DM01', N'DEMO Branch 01', N'DEMO Region 01', N'Demo hours only'),
    ('DM02', N'DEMO Branch 02', N'DEMO Region 01', N'Demo hours only'),
    ('DM03', N'DEMO Branch 03', N'DEMO Region 02', N'Demo hours only');

INSERT INTO dbo.Counters (CounterId, BranchCode, Name)
VALUES
    ('DEMO-T01-01', 'DM01', N'DEMO Counter 1'),
    ('DEMO-T02-01', 'DM02', N'DEMO Counter 1');

INSERT INTO dbo.Staff (StaffId, Name, Email, RoleId, HomeBranchCode)
VALUES
    ('demo-staff-01', N'DEMO Staff 01', N'demo.staff01@example.invalid', 0, 'DM01'),
    ('demo-staff-02', N'DEMO Staff 02', N'demo.staff02@example.invalid', 1, 'DM01'),
    ('demo-staff-05', N'DEMO Staff 05', N'demo.staff05@example.invalid', 0, 'DM02');

INSERT INTO dbo.Products
    (Sku, Name, ProductCategoryId, TaxCategory, UnitPrice, Currency)
VALUES
    ('DEMO-SKU-001', N'DEMO Product 001', 0, 'Standard', 11.00, 'INR'),
    ('DEMO-SKU-002', N'DEMO Product 002', 1, 'ZeroRated', 12.00, 'INR');

INSERT INTO dbo.BranchStock (BranchCode, Sku, Quantity)
VALUES
    ('DM01', 'DEMO-SKU-001', 40),
    ('DM01', 'DEMO-SKU-002', 40),
    ('DM02', 'DEMO-SKU-001', 3);

INSERT INTO dbo.ShippingZones (ZoneCode, Name)
VALUES ('DEMO-ZONE-01', N'DEMO Zone 01');

INSERT INTO dbo.Shipments
    (ShipmentId, ShipmentType, WeightKg, ZoneCode, ServiceLevel,
     LengthCm, WidthCm, HeightCm)
VALUES ('DEMO-SHIP-001', 'Parcel', 1.500, 'DEMO-ZONE-01', 'Express', 20, 15, 10);

INSERT INTO dbo.MoneyOrders
    (MoneyOrderId, Sender, Recipient, ProcessingBranchCode, Status,
     Amount, Currency, Fee, FeeCurrency)
VALUES
    (@VerifiedOrder, N'DEMO Sender', N'DEMO Recipient', 'DM01', 'Issued', 500, 'INR', 25, 'INR'),
    (@FailedOrder, N'DEMO Sender', N'DEMO Recipient', 'DM01', 'Cancelled', 900, 'INR', 25, 'INR');

INSERT INTO dbo.IdentityVerifications
    (MoneyOrderId, DocumentType, MaskedDocumentIdentifier, IsSuccessful)
VALUES
    (@VerifiedOrder, 'Passport', '****7654', 1),
    (@FailedOrder, 'Passport', '****1234', 0);

INSERT INTO dbo.CounterTransactions
    (TransactionId, Currency, OccurredAtUtc, CounterId, ClerkStaffId,
     PaymentMethod, BranchCode, Status)
VALUES
    (@TodaySale, 'INR', @Now, 'DEMO-T01-01', 'demo-staff-01', N'DEMO Cash', 'DM01', 'Paid'),
    (@YesterdaySale, 'INR', @Yesterday, 'DEMO-T02-01', 'demo-staff-05', N'DEMO Cash', 'DM02', 'Paid');

INSERT INTO dbo.TransactionTaxRates (TransactionId, TaxCategory, Rate)
VALUES
    (@TodaySale, 'Standard', 0.18),
    (@YesterdaySale, 'Standard', 0.18);

INSERT INTO dbo.TransactionLines
    (TransactionId, Description, LineType, Quantity, TaxCategory, Sku,
     ShipmentId, MoneyOrderId, UnitPrice, Currency)
VALUES
    (@TodaySale, N'DEMO Product 001', 'Goods', 2, 'Standard', 'DEMO-SKU-001', NULL, NULL, 11.00, 'INR'),
    (@TodaySale, N'DEMO shipping', 'Service', 1, 'Standard', NULL, 'DEMO-SHIP-001', NULL, 15.00, 'INR'),
    (@TodaySale, N'DEMO money order', 'MoneyOrder', 1, 'ZeroRated', NULL, NULL, @VerifiedOrder, 500.00, 'INR'),
    (@YesterdaySale, N'DEMO Product 001', 'Goods', 1, 'Standard', 'DEMO-SKU-001', NULL, NULL, 11.00, 'INR');

COMMIT TRANSACTION;
