-- Loads a large fictional dataset into ApexPostDb_Perf with set-based inserts.
-- Repeatable: every value is derived from a row number, so two runs give the
-- same rows. It refuses to run in any other database.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> 'ApexPostDb_Perf'
BEGIN
    ;THROW 50000, N'The performance generator runs only in ApexPostDb_Perf.', 1;
END;

DECLARE @Transactions int = 2000000;
DECLARE @Branches int = 500;
DECLARE @Products int = 2000;
DECLARE @Anchor datetime2(3) = '20261031';

-- The first 10 branches take 30% of sales, so the data is skewed like a real
-- network of large and small branches.
SELECT TOP (@Transactions)
    CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS N
INTO #Numbers
FROM sys.all_columns AS a
CROSS JOIN sys.all_columns AS b
CROSS JOIN sys.all_columns AS c;
CREATE UNIQUE CLUSTERED INDEX CX_Numbers ON #Numbers (N);

INSERT INTO dbo.Branches WITH (TABLOCK)
    (BranchCode, Name, Region, OpeningHours, CreatedBy)
SELECT
    CONCAT('P', FORMAT(N, '0000')),
    CONCAT(N'PERF Branch ', N),
    CONCAT(N'PERF Region ', FORMAT(N % 20, '00')),
    N'Demo hours only',
    'perf'
FROM #Numbers
WHERE N <= @Branches;

INSERT INTO dbo.Counters WITH (TABLOCK) (CounterId, BranchCode, Name, CreatedBy)
SELECT CONCAT('T', FORMAT(N, '0000'), '-1'), CONCAT('P', FORMAT(N, '0000')), N'PERF Counter', 'perf'
FROM #Numbers
WHERE N <= @Branches;

INSERT INTO dbo.Staff WITH (TABLOCK)
    (StaffId, Name, Email, RoleId, HomeBranchCode, CreatedBy)
SELECT
    CONCAT('perf-clerk-', FORMAT(N, '0000')),
    CONCAT(N'PERF Clerk ', N),
    CONCAT(N'perf.clerk', N, N'@example.invalid'),
    0,
    CONCAT('P', FORMAT(N, '0000')),
    'perf'
FROM #Numbers
WHERE N <= @Branches;

INSERT INTO dbo.Products WITH (TABLOCK)
    (Sku, Name, ProductCategoryId, TaxCategory, UnitPrice, Currency, CreatedBy)
SELECT
    CONCAT('PERF-SKU-', FORMAT(N, '00000')),
    CONCAT(N'PERF Product ', N),
    N % 3,
    CASE N % 3 WHEN 0 THEN 'Standard' WHEN 1 THEN 'Reduced' ELSE 'ZeroRated' END,
    10 + N % 500,
    'INR',
    'perf'
FROM #Numbers
WHERE N <= @Products;

-- One row per branch and product. About 3% of rows are at 10 or below.
INSERT INTO dbo.BranchStock WITH (TABLOCK) (BranchCode, Sku, Quantity, CreatedBy)
SELECT
    CONCAT('P', FORMAT(b.N, '0000')),
    CONCAT('PERF-SKU-', FORMAT(p.N, '00000')),
    CASE WHEN (b.N * 31 + p.N * 17) % 100 < 3
        THEN (b.N + p.N) % 11
        ELSE 11 + (b.N * 7 + p.N * 13) % 490 END,
    'perf'
FROM #Numbers AS b
CROSS JOIN #Numbers AS p
WHERE b.N <= @Branches AND p.N <= @Products;

INSERT INTO dbo.ShippingZones WITH (TABLOCK) (ZoneCode, Name, CreatedBy)
SELECT CONCAT('PERF-ZONE-', FORMAT(N, '00')), CONCAT(N'PERF Zone ', N), 'perf'
FROM #Numbers
WHERE N <= 50;

SELECT
    n.N,
    CAST(HASHBYTES('MD5', CAST(CAST(n.N AS bigint) AS binary(8))) AS uniqueidentifier) AS TransactionId,
    h.Mix,
    CASE WHEN h.Mix % 100 < 30
        THEN 1 + (h.Mix / 100) % 10
        ELSE 11 + (h.Mix / 100) % (@Branches - 10) END AS BranchNumber,
    -- Spread over the two years before the anchor date.
    DATEADD(SECOND, -((CHECKSUM(CAST(n.N AS bigint) * 104729) & 2147483647) % 63072000), @Anchor) AS OccurredAtUtc
INTO #Sales
FROM #Numbers AS n
CROSS APPLY (SELECT CHECKSUM(CAST(n.N AS bigint) * 7919) & 2147483647 AS Mix) AS h;
CREATE UNIQUE CLUSTERED INDEX CX_Sales ON #Sales (N);

-- 90% paid, 5% voided and 5% refunded.
INSERT INTO dbo.CounterTransactions WITH (TABLOCK)
    (TransactionId, Currency, OccurredAtUtc, CounterId, ClerkStaffId,
     PaymentMethod, BranchCode, Status, ReversalReason, CreatedBy)
SELECT
    TransactionId,
    'INR',
    OccurredAtUtc,
    CONCAT('T', FORMAT(BranchNumber, '0000'), '-1'),
    CONCAT('perf-clerk-', FORMAT(BranchNumber, '0000')),
    N'PERF Cash',
    CONCAT('P', FORMAT(BranchNumber, '0000')),
    CASE N % 20 WHEN 0 THEN 'Voided' WHEN 1 THEN 'Refunded' ELSE 'Paid' END,
    CASE WHEN N % 20 IN (0, 1) THEN N'PERF reversal' END,
    'perf'
FROM #Sales;

INSERT INTO dbo.TransactionTaxRates WITH (TABLOCK)
    (TransactionId, TaxCategory, Rate, CreatedBy)
SELECT s.TransactionId, r.TaxCategory, r.Rate, 'perf'
FROM #Sales AS s
CROSS JOIN (VALUES ('Standard', 0.18), ('Reduced', 0.05), ('ZeroRated', 0.00)) AS r (TaxCategory, Rate);

INSERT INTO dbo.Payments WITH (TABLOCK)
    (PaymentId, TransactionId, EntryType, PaymentMethod, RecordedAtUtc,
     Amount, Currency, CreatedBy)
SELECT
    CAST(HASHBYTES('MD5', CAST(CAST(N + 100000000 AS bigint) AS binary(8))) AS uniqueidentifier),
    TransactionId, 'Payment', N'PERF Cash', OccurredAtUtc, 100, 'INR', 'perf'
FROM #Sales;

-- Half of the sales have a shipment, and one in five a money order.
INSERT INTO dbo.Shipments WITH (TABLOCK)
    (ShipmentId, ShipmentType, WeightKg, ZoneCode, ServiceLevel,
     LengthCm, WidthCm, HeightCm, CreatedBy)
SELECT
    CONCAT('PERF-SHIP-', N),
    CASE WHEN Mix % 5 = 0 THEN 'Letter' ELSE 'Parcel' END,
    0.1 + (Mix % 200) / 10.0,
    CONCAT('PERF-ZONE-', FORMAT(1 + (Mix / 7) % 50, '00')),
    CASE Mix % 3 WHEN 0 THEN 'Standard' WHEN 1 THEN 'Express' ELSE 'Registered' END,
    20, 15, 10,
    'perf'
FROM #Sales
WHERE N % 2 = 0;

SELECT
    N,
    TransactionId,
    CAST(HASHBYTES('MD5', CAST(CAST(N + 200000000 AS bigint) AS binary(8))) AS uniqueidentifier) AS MoneyOrderId,
    BranchNumber
INTO #Orders
FROM #Sales
WHERE N % 5 = 0;

INSERT INTO dbo.MoneyOrders WITH (TABLOCK)
    (MoneyOrderId, Sender, Recipient, ProcessingBranchCode, Status,
     Amount, Currency, Fee, FeeCurrency, CreatedBy)
SELECT
    MoneyOrderId, N'PERF Sender', N'PERF Recipient',
    CONCAT('P', FORMAT(BranchNumber, '0000')),
    CASE WHEN N % 50 = 0 THEN 'Cancelled' ELSE 'Issued' END,
    500, 'INR', 25, 'INR', 'perf'
FROM #Orders;

-- Three in four money orders have a verification, with a fixed fictional mask.
INSERT INTO dbo.IdentityVerifications WITH (TABLOCK)
    (MoneyOrderId, DocumentType, MaskedDocumentIdentifier, IsSuccessful, CreatedBy)
SELECT MoneyOrderId, 'Passport', '****7654', CASE WHEN N % 35 = 0 THEN 0 ELSE 1 END, 'perf'
FROM #Orders
WHERE N % 20 <> 0;

-- Every sale has a goods line, and half have a second one.
INSERT INTO dbo.TransactionLines WITH (TABLOCK)
    (TransactionId, Description, LineType, Quantity, TaxCategory, Sku,
     UnitPrice, Currency, CreatedBy)
SELECT
    s.TransactionId, N'PERF goods', 'Goods', 1 + s.N % 3,
    CASE p.Number % 3 WHEN 0 THEN 'Standard' WHEN 1 THEN 'Reduced' ELSE 'ZeroRated' END,
    CONCAT('PERF-SKU-', FORMAT(p.Number, '00000')),
    10 + p.Number % 500, 'INR', 'perf'
FROM #Sales AS s
CROSS APPLY (VALUES (1), (2)) AS l (Position)
CROSS APPLY (SELECT 1 + (s.Mix + l.Position * 37) % @Products AS Number) AS p
WHERE l.Position = 1 OR s.N % 2 = 1;

INSERT INTO dbo.TransactionLines WITH (TABLOCK)
    (TransactionId, Description, LineType, Quantity, TaxCategory, ShipmentId,
     UnitPrice, Currency, CreatedBy)
SELECT TransactionId, N'PERF shipping', 'Service', 1, 'Standard', CONCAT('PERF-SHIP-', N), 12.26, 'INR', 'perf'
FROM #Sales
WHERE N % 2 = 0;

INSERT INTO dbo.TransactionLines WITH (TABLOCK)
    (TransactionId, Description, LineType, Quantity, TaxCategory, MoneyOrderId,
     UnitPrice, Currency, CreatedBy)
SELECT TransactionId, N'PERF money order', 'MoneyOrder', 1, 'ZeroRated', MoneyOrderId, 500, 'INR', 'perf'
FROM #Orders;

EXEC sys.sp_updatestats;
