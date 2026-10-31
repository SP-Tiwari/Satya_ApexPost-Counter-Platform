-- Daily branch sales, split into retail and services, for one UTC date.
-- Same rules as usp_DailyBranchSales, across every branch. Parameter: @BusinessDate.
DECLARE @BusinessDate date = CAST(SYSUTCDATETIME() AS date);
DECLARE @From datetime2(3) = @BusinessDate;
DECLARE @To datetime2(3) = DATEADD(DAY, 1, @From);

SELECT
    b.BranchCode,
    b.Name AS BranchName,
    t.Currency,
    COUNT(DISTINCT t.TransactionId) AS PaidTransactions,
    CAST(ROUND(SUM(CASE WHEN l.LineType = 'Goods'
        THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END), 2)
        AS decimal(18, 2)) AS RetailSales,
    CAST(ROUND(SUM(CASE WHEN l.LineType = 'Service'
        THEN l.Quantity * l.UnitPrice * (1 + r.Rate) ELSE 0 END), 2)
        AS decimal(18, 2)) AS ServiceSales
FROM dbo.CounterTransactions AS t
INNER JOIN dbo.Branches AS b
    ON b.BranchCode = t.BranchCode
INNER JOIN dbo.TransactionLines AS l
    ON l.TransactionId = t.TransactionId
-- Money-order lines carry no tax, so they may have no rate.
LEFT JOIN dbo.TransactionTaxRates AS r
    ON r.TransactionId = l.TransactionId
    AND r.TaxCategory = l.TaxCategory
WHERE t.Status = 'Paid'
    AND t.OccurredAtUtc >= @From
    AND t.OccurredAtUtc < @To
GROUP BY b.BranchCode, b.Name, t.Currency
ORDER BY b.BranchCode;
