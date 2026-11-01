-- Changes one branch's stock and writes its StockAdjustments history row
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

    IF NULLIF(LTRIM(RTRIM(@BranchCode)), '') IS NULL
    BEGIN
        ;THROW 51000, N'@BranchCode is required.', 1;
    END;

    IF NULLIF(LTRIM(RTRIM(@Sku)), '') IS NULL
    BEGIN
        ;THROW 51000, N'@Sku is required.', 1;
    END;

    IF @Delta IS NULL OR @Delta = 0 OR @Delta = -2147483648
    BEGIN
        ;THROW 51000, N'Stock adjustment must be a non-zero supported quantity.', 1;
    END;

    DECLARE @TrimmedReason nvarchar(501) = LTRIM(RTRIM(@Reason));
    IF NULLIF(@TrimmedReason, N'') IS NULL OR LEN(@TrimmedReason) > 500
    BEGIN
        ;THROW 51000, N'A reason of up to 500 characters is required to adjust stock.', 1;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.Staff AS s
        INNER JOIN dbo.Roles AS r ON r.RoleId = s.RoleId
        WHERE s.StaffId = @ChangedBy
            AND s.IsActive = 1
            AND s.HomeBranchCode = @BranchCode
            AND r.Name = N'Branch Manager'
    )
    BEGIN
        ;THROW 51002, N'An active Branch Manager is required to adjust stock.', 1;
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
                ;THROW 51003, N'Insufficient stock available.', 1;
            END;

            DECLARE @NotFound nvarchar(2048) = CONCAT(
                N'Stock for product ''', @Sku, N''' at branch ''',
                @BranchCode, N''' was not found.');
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
