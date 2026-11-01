-- The write cost of the indexes: 20,000 sales with their lines and stock
-- changes, all rolled back so each run starts from the same rows. It reports
-- the transaction log used, which grows with every index a write must change.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> 'ApexPostDb_Perf'
BEGIN
    ;THROW 50000, N'This script runs only in ApexPostDb_Perf.', 1;
END;

DECLARE @Started datetime2 = SYSDATETIME();

BEGIN TRANSACTION;

SELECT TOP (20000)
    CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS N
INTO #Batch
FROM sys.all_columns AS a
CROSS JOIN sys.all_columns AS b;

SELECT
    N,
    CAST(HASHBYTES('MD5', CAST(CAST(N + 300000000 AS bigint) AS binary(8))) AS uniqueidentifier) AS TransactionId,
    CONCAT('P', FORMAT(1 + N % 500, '0000')) AS BranchCode,
    CONCAT('T', FORMAT(1 + N % 500, '0000'), '-1') AS CounterId,
    CONCAT('perf-clerk-', FORMAT(1 + N % 500, '0000')) AS ClerkStaffId,
    CONCAT('PERF-SKU-', FORMAT(1 + N % 2000, '00000')) AS Sku
INTO #Sales
FROM #Batch;

DECLARE @Writing datetime2 = SYSDATETIME();

UPDATE s
SET s.Quantity = s.Quantity + 1, s.ModifiedAtUtc = SYSUTCDATETIME()
FROM dbo.BranchStock AS s
INNER JOIN #Sales AS b ON b.BranchCode = s.BranchCode AND b.Sku = s.Sku;

INSERT INTO dbo.CounterTransactions
    (TransactionId, Currency, OccurredAtUtc, CounterId, ClerkStaffId,
     PaymentMethod, BranchCode, Status, CreatedBy)
SELECT TransactionId, 'INR', SYSUTCDATETIME(), CounterId, ClerkStaffId, N'PERF Cash', BranchCode, 'Paid', 'perf'
FROM #Sales;

INSERT INTO dbo.TransactionLines
    (TransactionId, Description, LineType, Quantity, TaxCategory, Sku,
     UnitPrice, Currency, CreatedBy)
SELECT TransactionId, N'PERF goods', 'Goods', 1, 'Standard', Sku, 20, 'INR', 'perf'
FROM #Sales;

DECLARE @Finished datetime2 = SYSDATETIME();

SELECT
    DATEDIFF(MILLISECOND, @Writing, @Finished) AS WriteMilliseconds,
    t.database_transaction_log_bytes_used AS LogBytes,
    t.database_transaction_log_record_count AS LogRecords
FROM sys.dm_tran_database_transactions AS t
INNER JOIN sys.dm_tran_current_transaction AS c ON c.transaction_id = t.transaction_id
WHERE t.database_id = DB_ID();

ROLLBACK TRANSACTION;
