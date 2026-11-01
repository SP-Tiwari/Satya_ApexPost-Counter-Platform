-- Deadlock demo, session B: updates the same rows in the opposite order, which
-- the application never does. Each session then holds the lock the other one
-- needs, and SQL Server ends one of them with error 1205.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT LIKE 'ApexPost[_]Test[_]%'
BEGIN
    ;THROW 50000, N'Demo scripts run only in a disposable ApexPost_Test_* database.', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    UPDATE dbo.BranchStock SET Quantity = Quantity - 1
    WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-002';

    WAITFOR DELAY '00:00:03';

    UPDATE dbo.BranchStock SET Quantity = Quantity - 1
    WHERE BranchCode = 'DM01' AND Sku = 'DEMO-SKU-001';

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
