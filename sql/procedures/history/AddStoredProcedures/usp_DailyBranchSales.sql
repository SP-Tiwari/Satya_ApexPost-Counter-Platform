-- Retail and service totals for one branch on one UTC business date.
-- Matches DailySalesReportService: Paid transactions only, Goods lines are
-- retail, Service lines are services, money-order principal counts as neither,
-- and tax is stored line amount x stored rate. A date with no paid sale
-- returns no row. Totals are rounded only for the result set.
CREATE OR ALTER PROCEDURE dbo.usp_DailyBranchSales
    @BranchCode varchar(10),
    @Date date
AS
BEGIN
    SET NOCOUNT ON;

    IF NULLIF(LTRIM(RTRIM(@BranchCode)), '') IS NULL
    BEGIN
        ;THROW 51000, N'@BranchCode is required.', 1;
    END;

    -- The upper bound below is the next day, which 9999-12-31 does not have.
    IF @Date IS NULL OR @Date = '99991231'
    BEGIN
        ;THROW 51000, N'@Date must be a valid business date.', 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchCode = @BranchCode)
    BEGIN
        DECLARE @NotFound nvarchar(2048) =
            CONCAT(N'Branch ''', @BranchCode, N''' was not found.');
        ;THROW 51001, @NotFound, 1;
    END;

    DECLARE @From datetime2(3) = @Date;
    DECLARE @To datetime2(3) = DATEADD(DAY, 1, @From);
    DECLARE @Totals TABLE
    (
        Currency char(3) NOT NULL,
        RetailSales decimal(38, 6) NOT NULL,
        ServiceSales decimal(38, 6) NOT NULL
    );

    INSERT INTO @Totals (Currency, RetailSales, ServiceSales)
    SELECT
        t.Currency,
        SUM(CASE WHEN l.LineType = 'Goods'
            THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END),
        SUM(CASE WHEN l.LineType = 'Service'
            THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END)
    FROM dbo.CounterTransactions AS t
    INNER JOIN dbo.TransactionLines AS l
        ON l.TransactionId = t.TransactionId
    -- Money-order lines carry no tax, so they may have no rate.
    LEFT JOIN dbo.TransactionTaxRates AS r
        ON r.TransactionId = l.TransactionId
        AND r.TaxCategory = l.TaxCategory
    WHERE t.BranchCode = @BranchCode
        AND t.Status = 'Paid'
        AND t.OccurredAtUtc >= @From
        AND t.OccurredAtUtc < @To
    GROUP BY t.Currency;

    IF (SELECT COUNT(*) FROM @Totals) > 1
    BEGIN
        DECLARE @Incompatible nvarchar(2048) = CONCAT(
            N'Daily sales for branch ''', @BranchCode, N''' on ''',
            CONVERT(char(10), @Date, 23), N''' contain incompatible currencies.');
        ;THROW 51003, @Incompatible, 1;
    END;

    SELECT
        @BranchCode AS BranchCode,
        @Date AS BusinessDate,
        Currency,
        CAST(ROUND(RetailSales, 2) AS decimal(18, 2)) AS RetailSales,
        CAST(ROUND(ServiceSales, 2) AS decimal(18, 2)) AS ServiceSales,
        CAST(ROUND(RetailSales + ServiceSales, 2) AS decimal(18, 2)) AS TotalSales
    FROM @Totals;
END;
