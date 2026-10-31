-- Money orders and identity verifications per branch. Matches
-- BranchAuditorReportService. The LEFT JOINs keep money orders with no
-- verification, and HAVING leaves out branches with no money orders.
-- COUNT(column) ignores the NULLs that the LEFT JOINs produce.
SELECT
    b.BranchCode,
    b.Name AS BranchName,
    COUNT(m.MoneyOrderId) AS MoneyOrderCount,
    COUNT(CASE WHEN m.Status = 'Cancelled' THEN 1 END) AS CancelledMoneyOrderCount,
    COUNT(v.MoneyOrderId) AS IdentityVerificationCount,
    COUNT(CASE WHEN v.IsSuccessful = 1 THEN 1 END) AS SuccessfulIdentityVerificationCount,
    COUNT(CASE WHEN v.IsSuccessful = 0 THEN 1 END) AS FailedIdentityVerificationCount
FROM dbo.Branches AS b
LEFT JOIN dbo.MoneyOrders AS m
    ON m.ProcessingBranchCode = b.BranchCode
LEFT JOIN dbo.IdentityVerifications AS v
    ON v.MoneyOrderId = m.MoneyOrderId
GROUP BY b.BranchCode, b.Name
HAVING COUNT(m.MoneyOrderId) > 0
ORDER BY b.BranchCode;
