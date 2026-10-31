-- The candidate indexes, one statement per workload. The harness runs each
-- batch on its own and records its build time and size. Runs only in
-- ApexPostDb_Perf. The chosen ones are also in the AddPerformanceIndexes
-- migration.
IF DB_NAME() <> 'ApexPostDb_Perf'
BEGIN
    ;THROW 50000, N'This script runs only in ApexPostDb_Perf.', 1;
END;
GO
-- Low stock across branches: a range on Quantity. Sku and BranchCode are the
-- clustered key, so they are already in the index.
CREATE INDEX IX_BranchStock_Quantity ON dbo.BranchStock (Quantity);
GO
-- Daily sales: equality on BranchCode first, then the range on OccurredAtUtc.
-- Status and Currency are read but never sought, so they are included.
-- This replaces IX_CounterTransactions_BranchCode, which it fully covers.
DROP INDEX IF EXISTS IX_CounterTransactions_BranchCode ON dbo.CounterTransactions;
CREATE INDEX IX_CounterTransactions_BranchCode_OccurredAtUtc
    ON dbo.CounterTransactions (BranchCode, OccurredAtUtc)
    INCLUDE (Status, Currency);
GO
-- Daily sales lines: covers the columns each line is read for, so a day's
-- lines need no key lookups.
DROP INDEX IF EXISTS IX_TransactionLines_TransactionId ON dbo.TransactionLines;
CREATE INDEX IX_TransactionLines_TransactionId
    ON dbo.TransactionLines (TransactionId)
    INCLUDE (LineType, Quantity, UnitPrice, TaxCategory, ShipmentId);
GO
-- Shipment search: both columns are equality tests, and ZoneCode is the FK, so
-- it stays first. This replaces IX_Shipments_ZoneCode.
DROP INDEX IF EXISTS IX_Shipments_ZoneCode ON dbo.Shipments;
CREATE INDEX IX_Shipments_ZoneCode_ServiceLevel
    ON dbo.Shipments (ZoneCode, ServiceLevel)
    INCLUDE (ShipmentType, WeightKg);
GO
-- Money-order search: a key on the branch alone needs a lookup per order, which
-- the optimizer rejects in favour of a full scan, so the list's columns are
-- included. Status also serves the auditor report.
DROP INDEX IF EXISTS IX_MoneyOrders_ProcessingBranchCode ON dbo.MoneyOrders;
CREATE INDEX IX_MoneyOrders_ProcessingBranchCode
    ON dbo.MoneyOrders (ProcessingBranchCode)
    INCLUDE (Status, Amount, Currency, Fee, Sender, Recipient);
GO
