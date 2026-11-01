-- Blocking demo, session 1 of 3: the clerk updating stock.
-- In SSMS, open this and blocking-session-2-reader.sql in two windows, connect
-- both to a disposable database with demo-fixture.sql applied, run this one,
-- then run the reader within 20 seconds.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE 'ApexPost[_]Test[_]%'
BEGIN
    ;THROW 50000, N'Demo scripts run only in a disposable ApexPost_Test_* database.', 1;
END;

SELECT SYSDATETIME() AS WriterStarted, @@SPID AS WriterSessionId;

BEGIN TRANSACTION;

UPDATE dbo.BranchStock
SET Quantity = Quantity - 1
WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-001';

-- The exclusive row lock stays held until the transaction ends. A real sale
-- never waits here; the delay stands for a transaction left open too long.
WAITFOR DELAY '00:00:20';

ROLLBACK TRANSACTION;

SELECT SYSDATETIME() AS WriterRolledBack;
