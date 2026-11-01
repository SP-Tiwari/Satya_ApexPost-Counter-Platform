-- Blocking demo, session 2 of 3: a report reading the same row. Under READ
-- COMMITTED with locking (READ_COMMITTED_SNAPSHOT OFF, the ApexPostDb setting)
-- it waits for the writer, then returns only committed data.
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

DECLARE @Started datetime2 = SYSDATETIME();

SELECT Quantity AS CommittedQuantity
FROM dbo.BranchStock
WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-001';

SELECT
    @@SPID AS ReaderSessionId,
    DATEDIFF(MILLISECOND, @Started, SYSDATETIME()) AS ReaderWaitedMilliseconds;
