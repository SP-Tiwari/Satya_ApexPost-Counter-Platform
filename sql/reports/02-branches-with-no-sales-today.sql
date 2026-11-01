-- Branches with no paid sale today (UTC). The sale conditions sit in the ON
-- clause: in WHERE they would remove the unmatched rows this report is after.
-- "Paid" is the same meaning of a sale as the daily sales report uses.
DECLARE @From datetime2(3) = CAST(SYSUTCDATETIME() AS date);
DECLARE @To datetime2(3) = DATEADD(DAY, 1, @From);

SELECT
    b.BranchCode,
    b.Name AS BranchName,
    b.Region,
    b.IsActive
FROM dbo.Branches AS b
LEFT JOIN dbo.CounterTransactions AS t
    ON t.BranchCode = b.BranchCode
    AND t.Status = 'Paid'
    AND t.OccurredAtUtc >= @From
    AND t.OccurredAtUtc < @To
WHERE t.TransactionId IS NULL
ORDER BY b.BranchCode;
