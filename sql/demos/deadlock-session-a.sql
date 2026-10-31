-- Deadlock demo, session A: updates stock in SKU order, as the application does.
-- Run with deadlock-session-b-opposite-order.sql to cause a deadlock. Run this
-- same file in both sessions to see the consistent order prevent one: the
-- second session only waits, and both commit.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE 'ApexPost[_]Test[_]%'
BEGIN
    ;THROW 50000, N'Demo scripts run only in a disposable ApexPost_Test_* database.', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    UPDATE dbo.BranchStock SET Quantity = Quantity - 1
    WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-001';

    -- Gives the other session time to take its first lock.
    WAITFOR DELAY '00:00:03';

    UPDATE dbo.BranchStock SET Quantity = Quantity - 1
    WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-002';

    COMMIT TRANSACTION;
    SELECT @@SPID AS SessionId, N'Committed' AS Outcome;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    SELECT
        @@SPID AS SessionId,
        ERROR_NUMBER() AS ErrorNumber,
        ERROR_MESSAGE() AS ErrorMessage;
END CATCH;
