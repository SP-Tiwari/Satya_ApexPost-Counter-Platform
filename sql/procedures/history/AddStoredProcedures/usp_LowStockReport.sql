-- Stock at or below @Threshold, for one branch or, when @BranchCode is NULL,
-- for all branches. Matches LowStockReportService. OPTION (RECOMPILE) is
-- explained in docs/adr/0023-stored-procedures.md.
CREATE OR ALTER PROCEDURE dbo.usp_LowStockReport
    @BranchCode varchar(10) = NULL,
    @Threshold int
AS
BEGIN
    SET NOCOUNT ON;

    IF @Threshold IS NULL OR @Threshold < 0
    BEGIN
        ;THROW 51000, N'Low-stock threshold cannot be negative.', 1;
    END;

    IF @BranchCode IS NOT NULL AND LTRIM(RTRIM(@BranchCode)) = ''
    BEGIN
        ;THROW 51000, N'@BranchCode cannot be blank. Pass NULL for all branches.', 1;
    END;

    IF @BranchCode IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchCode = @BranchCode)
    BEGIN
        DECLARE @NotFound nvarchar(2048) =
            CONCAT(N'Branch ''', @BranchCode, N''' was not found.');
        ;THROW 51001, @NotFound, 1;
    END;

    SELECT
        s.BranchCode,
        s.Sku,
        s.Quantity,
        @Threshold AS Threshold
    FROM dbo.BranchStock AS s
    WHERE (@BranchCode IS NULL OR s.BranchCode = @BranchCode)
        AND s.Quantity <= @Threshold
    ORDER BY s.BranchCode, s.Sku
    OPTION (RECOMPILE);
END;
