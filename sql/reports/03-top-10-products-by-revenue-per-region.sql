-- Top 10 products by revenue in each region, over all recorded paid sales.
-- Revenue is quantity x unit price before tax. Currency is part of the ranking
-- because amounts in different currencies cannot be added. ROW_NUMBER ranks
-- within each region, and SKU settles ties so the result is repeatable.
WITH ProductRevenue AS
(
    SELECT
        b.Region,
        t.Currency,
        p.Sku,
        p.Name AS ProductName,
        SUM(l.Quantity) AS UnitsSold,
        SUM(l.Quantity * l.UnitPrice) AS Revenue
    FROM dbo.CounterTransactions AS t
    INNER JOIN dbo.Branches AS b
        ON b.BranchCode = t.BranchCode
    INNER JOIN dbo.TransactionLines AS l
        ON l.TransactionId = t.TransactionId
    INNER JOIN dbo.Products AS p
        ON p.Sku = l.Sku
    WHERE t.Status = 'Paid'
        AND l.LineType = 'Goods'
    GROUP BY b.Region, t.Currency, p.Sku, p.Name
    HAVING SUM(l.Quantity * l.UnitPrice) > 0
),
RankedProducts AS
(
    SELECT
        *,
        ROW_NUMBER() OVER (
            PARTITION BY Region, Currency
            ORDER BY Revenue DESC, Sku) AS RevenueRank
    FROM ProductRevenue
)
SELECT Region, Currency, RevenueRank, Sku, ProductName, UnitsSold, Revenue
FROM RankedProducts
WHERE RevenueRank <= 10
ORDER BY Region, Currency, RevenueRank;
