# Performance evidence

This file grows through Week 3. M5 adds the query-count and generated SQL evidence below. Execution plans, index measurements and write costs belong to M6 and are not here yet. Every number comes from a saved run in [docs/evidence/m5](docs/evidence/m5/).

## The N+1 problem and how it is resolved

**The problem.** An N+1 runs one query for a list, then one more query for each row. The page works in a small test, but the number of database round trips grows with the data.

**Production has no N+1 to remove,** so M5 demonstrates the problem with a deliberately naive loop that exists only in a throwaway probe:

```csharp
var ids = await database.CounterTransactions.AsNoTracking()
    .Where(basket => basket.BranchCode == branch)
    .Select(basket => basket.Id).ToArrayAsync();          // 1 query
foreach (var id in ids)
{
    var lines = await /* lines of this sale */;           // 1 query per sale
    foreach (var line in lines)
        await /* verification of this money order */;     // 1 query per money order
}
```

**Measured** on a disposable database, for one branch with 42 sales and 9 verified money orders. Commands are counted from the `Microsoft.EntityFrameworkCore.Database.Command` log category, which `appsettings.Development.json` enables at Information level, so the same commands appear in the Development console.

| Approach | SQL commands | Result |
|---|---|---|
| Naive loop | **55** | 42 sales, line value 7134.12, 9 verifications |
| `GetForBranchAsync`: split query, plus one batched verification read | **4** | 42 sales, line value 7134.12, 9 verifications |

Both approaches return the same result. Neither count includes a staff lookup.

**The resolution has three parts:**

- **Eager loading with `AsSplitQuery`.** `WholeBaskets()` loads lines, shipments and money orders in a fixed number of commands, whatever the number of sales. A split query avoids repeating each transaction's columns for every joined row.
- **Batching.** `RestoreVerificationsAsync` reads all verifications with one `Contains` query, not one query per money order.
- **Projection.** Reports do not need whole aggregates, so `SqlReportQueryService` reads only the columns it needs, in one command.

## Reports before and after M5

"Old" is the M3 repository path, and "new" is `SqlReportQueryService`. Each count includes the staff authorization lookup. The dataset has 210 sales, 60 of them with a shipment and a money order. These are command counts, not timings. M6 measures reads and CPU.

| Report | Old commands | New commands | Same output |
|---|---|---|---|
| Daily sales | 5 | 2 | Yes |
| Low stock | 2 | 2 | Yes |
| Shipping volume | 2 | 2 | Yes |
| Branch auditor | 3 | 2 | Yes |

The dashboard fell from 9 commands to 6 in the same comparison, before its branch read moved to a projection. Its old repository path no longer exists.

Low stock and shipping volume keep their command count, but no longer load rows into memory to count them where SQL can do it: shipping volume is now a `GROUP BY`, and low stock is `usp_LowStockReport`. Daily sales output was also compared on three other dates, one of them with no sales, and matched.

## Screens before and after M5

"Old" is the domain-returning service path, and "new" is the DTO projection. Each count includes the staff lookup. Tracked entries are those left in the context, and the one remaining is the staff row. From [screen-parity.txt](docs/evidence/m5/screen-parity.txt).

| Screen | Rows | Old commands | New commands | Old tracked | New tracked | Same output |
|---|---|---|---|---|---|---|
| Catalogue | 30 | 5 | 5 | 4 | 1 | Yes |
| Stock adjustment | 30 | 3 | 3 | 4 | 1 | Yes |
| Money orders | 12 | 3 | 2 | 1 | 1 | Yes |
| Transaction detail | 1 | 5 | 2 | 1 | 1 | Yes |
| Transaction list | 42 | 5 | 3 | 1 | 1 | Yes |
| Administration | 55 | 4 | 4 | 1 | 1 | Yes |
| Parcel status | 1 | 4 | 1 | 0 | 0 | Yes |
| Sale form | 42 | 4 | 4 | 4 | 1 | Yes |
| Dashboard branch read only | 1 | 1 | 1 | 3 | 0 | Yes |

The transaction list compares the fields that screen shows. No requirement defines line order within a sale, so the queries do not request one. EF Core still adds `TransactionLineId` to the single-query `ORDER BY` for transaction detail, because it needs the key to group lines, but that is not a guaranteed display order.

## Generated SQL

`ToQueryString()` output for three production report queries. Screen queries are also in [generated-sql.txt](docs/evidence/m5/generated-sql.txt).

`PaidSaleLines`, used by daily sales and the dashboard. The date filter is a range on the column, so an index stays usable:

```sql
DECLARE @branchCode varchar(10) = 'DM01';
DECLARE @start datetime2 = '2026-10-31T00:00:00.0000000Z';
DECLARE @end datetime2 = '2026-11-01T00:00:00.0000000Z';
SELECT [c].[TransactionId], [c].[Currency], [t].[LineType], [t].[UnitPrice], [t].[Quantity], (
    SELECT TOP(1) [t0].[Rate]
    FROM [TransactionTaxRates] AS [t0]
    WHERE [c].[TransactionId] = [t0].[TransactionId] AND [t0].[TaxCategory] = [t].[TaxCategory]), [t].[ShipmentId]
FROM [CounterTransactions] AS [c]
INNER JOIN [TransactionLines] AS [t] ON [c].[TransactionId] = [t].[TransactionId]
WHERE [c].[BranchCode] = @branchCode AND [c].[Status] = 'Paid' AND [c].[OccurredAtUtc] >= @start AND [c].[OccurredAtUtc] < @end
ORDER BY [c].[OccurredAtUtc] DESC, [c].[TransactionId], [t].[TransactionLineId]
```

`ShippingVolume`:

```sql
SELECT [s].[ZoneCode], [s].[ServiceLevel], COUNT(*)
FROM [Shipments] AS [s]
GROUP BY [s].[ZoneCode], [s].[ServiceLevel]
```

`BranchAuditorReport`:

```sql
SELECT [m].[ProcessingBranchCode], COUNT(*), COUNT(CASE
    WHEN [i].[IsSuccessful] IS NOT NULL THEN 1
END), COUNT(CASE
    WHEN [i].[IsSuccessful] = CAST(1 AS bit) THEN 1
END), COUNT(CASE
    WHEN [i].[IsSuccessful] = CAST(0 AS bit) THEN 1
END), COUNT(CASE
    WHEN [m].[Status] = 'Cancelled' THEN 1
END)
FROM [MoneyOrders] AS [m]
LEFT JOIN [IdentityVerifications] AS [i] ON [m].[MoneyOrderId] = [i].[MoneyOrderId]
GROUP BY [m].[ProcessingBranchCode]
```
