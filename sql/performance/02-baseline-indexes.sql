-- Puts the indexes back to their state before AddPerformanceIndexes, so the
-- baseline is measured without them. Runs only in ApexPostDb_Perf.
SET NOCOUNT ON;

IF DB_NAME() <> 'ApexPostDb_Perf'
BEGIN
    ;THROW 50000, N'This script runs only in ApexPostDb_Perf.', 1;
END;

DROP INDEX IF EXISTS IX_BranchStock_Quantity ON dbo.BranchStock;
DROP INDEX IF EXISTS IX_CounterTransactions_BranchCode_OccurredAtUtc ON dbo.CounterTransactions;
DROP INDEX IF EXISTS IX_MoneyOrders_ProcessingBranchCode ON dbo.MoneyOrders;
DROP INDEX IF EXISTS IX_Shipments_ZoneCode_ServiceLevel ON dbo.Shipments;
DROP INDEX IF EXISTS IX_TransactionLines_TransactionId ON dbo.TransactionLines;

IF INDEXPROPERTY(OBJECT_ID(N'dbo.CounterTransactions'), N'IX_CounterTransactions_BranchCode', 'IndexID') IS NULL
    CREATE INDEX IX_CounterTransactions_BranchCode ON dbo.CounterTransactions (BranchCode);
IF INDEXPROPERTY(OBJECT_ID(N'dbo.MoneyOrders'), N'IX_MoneyOrders_ProcessingBranchCode', 'IndexID') IS NULL
    CREATE INDEX IX_MoneyOrders_ProcessingBranchCode ON dbo.MoneyOrders (ProcessingBranchCode);
IF INDEXPROPERTY(OBJECT_ID(N'dbo.Shipments'), N'IX_Shipments_ZoneCode', 'IndexID') IS NULL
    CREATE INDEX IX_Shipments_ZoneCode ON dbo.Shipments (ZoneCode);
IF INDEXPROPERTY(OBJECT_ID(N'dbo.TransactionLines'), N'IX_TransactionLines_TransactionId', 'IndexID') IS NULL
    CREATE INDEX IX_TransactionLines_TransactionId ON dbo.TransactionLines (TransactionId);
