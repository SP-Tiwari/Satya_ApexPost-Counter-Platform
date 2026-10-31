# Performance evidence

Execution plans, index measurements and conclusions from M6 come first, followed by the query-count and generated SQL evidence from M5. Every number comes from a saved run in [docs/evidence/m6](docs/evidence/m6/README.md) or [docs/evidence/m5](docs/evidence/m5/).

## Index optimization and execution plans

### Test setup

| Item | Value |
|---|---|
| Database | Disposable `ApexPostDb_Perf`, files in `E:\SqlData\ApexPost\Perf`, dropped after the run. `ApexPostDb` was not used |
| Server | SQL Server 2025 (17.0.1000.7), Enterprise Evaluation, compatibility level 170, 4 CPUs |
| Data | Set-based and repeatable: [01-generate-data.sql](sql/performance/01-generate-data.sql), loaded in 2.9 minutes |
| Rows | 2,000,000 sales, 4,400,000 lines, 6,000,000 tax rates, 2,000,000 payments, 1,000,000 stock rows, 1,000,000 shipments, 400,000 money orders, 300,000 verifications |
| Skew | 500 branches. The first 10 take 30% of sales, so a busy branch has about 60,000 and a small one about 2,900 |
| Method | Each workload's first run warms the cache and is not counted. Then 5 runs: logical reads from the last, and median CPU and elapsed time. The application's own SQL, with the same parameter types |
| Repeat | `./scripts/run-performance.ps1` |

Enterprise can use batch mode on rowstore tables, which appears in two baseline plans. Express cannot, so its timings may differ. Logical reads are the steadier measure here, because these small queries run from memory and time is close to the measurement limit.

### Before and after

The indexes are in [ADR 0024](docs/adr/0024-index-choices.md). Plans are saved in [docs/evidence/m6/plans](docs/evidence/m6/plans) as `<workload>-before.sqlplan` and `<workload>-after.sqlplan`, and open in SSMS. All figures come from [measurements-before.csv](docs/evidence/m6/measurements-before.csv) and [measurements-after.csv](docs/evidence/m6/measurements-after.csv).

| Workload | Rows | Reads before | Reads after | CPU ms | Elapsed ms | Plan before | Plan after |
|---|---|---|---|---|---|---|---|
| Low stock, one branch | 60 | 22 | 22 | 0 → 0 | 0 → 0 | Clustered seek | Unchanged |
| Low stock, all branches | 30,000 | 8,562 | 133 | 48 → 47 | 36 → 41 | Parallel clustered scan | Index seek, sort |
| Daily sales lines, busy branch | 156 | 35,332 | 1,815 | 188 → 0 | 51 → 2 | Parallel clustered scan, key lookups | Seeks only |
| Daily sales lines, small branch | 10 | 33,089 | 121 | 187 → 0 | 49 → 0 | Parallel clustered scan, key lookups | Seeks only |
| `usp_DailyBranchSales` | 1 | 33,301 | 750 | 172 → 0 | 167 → 1 | Clustered scan, key lookups | Seeks only |
| Shipment search | 6,744 | 13,771 | 61 | 110 → 0 | 59 → 3 | Parallel clustered scan | Index seek |
| Shipping volume report | 150 | 13,771 | 8,703 | 140 → 249 | 40 → 65 | Clustered scan, batch hash | Index scan, stream aggregate |
| Money orders, branch list | 11,899 | 9,597 | 2,704 | 94 → 47 | 96 → 47 | Two clustered scans, merge join | Index seek, clustered scan, merge join |
| Money orders, auditor report | 500 | 10,098 | 8,694 | 249 → 220 | 58 → 61 | Two clustered scans, batch hash | Index scan, clustered scan, batch hash |

**What the numbers show:**

- **Daily sales and shipment search are the clear wins.** Reads fall by about 95% to 99.6%, and time falls to almost nothing. Before, even a 10-row day scanned every sale.
- **Low stock for one branch needs no index.** `PK_BranchStock` already starts with `BranchCode`, so 22 reads is the right answer.
- **Low stock across branches reads 64 times fewer pages but is not faster.** 30,000 rows still have to be sorted, and the data was in memory. This is the weakest index in the set.
- **Shipping volume is slower despite fewer reads.** The new index gives a narrower scan, but the plan moved from a batch-mode hash to a row-mode stream aggregate, so CPU rose from 140 to 249 ms. This report must count every shipment, so a scan remains correct. It is a cost of the index built for shipment search, accepted because that search falls from 13,771 reads to 61.
- **Remaining scans are explained.** The shipping and auditor reports aggregate whole tables. The branch list still scans `PK_IdentityVerifications` for its merge join, because 11,899 orders is about 3% of all orders and the optimizer judged a scan cheaper than that many seeks.

### Write overhead and storage

[04-write-workload.sql](sql/performance/04-write-workload.sql) writes 20,000 sales, 20,000 lines and 20,000 stock updates, then rolls back. Each state ran three times, and this table shows the median.

| State | Write ms | Log bytes | Log records |
|---|---|---|---|
| Before | 1,074 | 14,389,120 | 102,545 |
| After, without `IX_BranchStock_Quantity` | 1,014 | 15,349,120 | 102,545 |
| After, all indexes | 1,038 | 15,937,992 | 106,925 |

The indexes add **10.8% more log per write**. Write time shows no measurable change, because the differences are within the variation between runs. `IX_BranchStock_Quantity` is 3.8% of that, because every sale changes the column it indexes.

| Index | Before MB | After MB | Build seconds |
|---|---|---|---|
| `IX_BranchStock_Quantity` | none | 34 | 0.7 |
| `IX_CounterTransactions_BranchCode_OccurredAtUtc` (replaces `_BranchCode`) | 60 | 92 | 1.9 |
| `IX_TransactionLines_TransactionId` | 128 | 287 | 3.4 |
| `IX_Shipments_ZoneCode_ServiceLevel` (replaces `_ZoneCode`) | 39 | 66 | 1.5 |
| `IX_MoneyOrders_ProcessingBranchCode` | 12 | 44 | 0.5 |
| **Total** | **239** | **523** | **8.0** |

The indexes add 284 MB. The covering include on `TransactionLines` is more than half of that, and it is what removes the key lookups from daily sales.

### Non-SARGable predicate

`YEAR(OccurredAtUtc) = 2026` hides the column inside a function, so the index cannot seek on it. Rewriting it as a range keeps the column bare. Both queries return the same count, and both use the new index. From [studies.txt](docs/evidence/m6/studies.txt).

| Predicate | Logical reads | CPU ms | Elapsed ms | Plan file |
|---|---|---|---|---|
| `YEAR([OccurredAtUtc]) = 2026` | 357 | 16 | 8 | `sarg-year-function.sqlplan` |
| `[OccurredAtUtc] >= '20260101' AND < '20270101'` | 151 | 0 | 2 | `sarg-date-range.sqlplan` |

Both seek on `BranchCode`. With `YEAR()`, the date is then tested on every row of that branch, about 60,000. With the range, the seek stops at the date boundary. The application's own date filter (`PaidSaleLines`) already uses a range.

### Estimated and actual rows

Two mismatches were investigated. Both are in [studies.txt](docs/evidence/m6/studies.txt).

**Local variables, in `usp_DailyBranchSales`.** This one was found while measuring. The procedure's dates are local variables, so at compile time the optimizer cannot see their values and uses a fixed guess. With the new indexes in place, it still chose merge joins that scanned about 5.7 million tax rates to return one row.

| Version | Estimated rows | Actual rows | Logical reads | Elapsed ms | Plan file |
|---|---|---|---|---|---|
| Local variables | 24,612 | 71 | 93,836 | 1,308 | `estimate-local-variables.sqlplan` |
| `OPTION (RECOMPILE)` | 503 | 71 | 829 | 1 | `estimate-local-variables-recompile.sqlplan` |

**No index could fix this.** `usp_DailyBranchSales` now uses `OPTION (RECOMPILE)` on that statement, so the optimizer sees the actual dates. Its results do not change. The `AddDailyBranchSalesRecompile` migration deploys that change. The estimate is still above the actual count, because 71 rows is a small part of one day's histogram step, but it is close enough to choose the right plan.

**Stale statistics.** With automatic updates off, 200,000 rows were added after the histogram's last date.

| Statistics | Estimated rows | Actual rows | Plan | Logical reads | Elapsed ms |
|---|---|---|---|---|---|
| Stale | 3,157 | 200,000 | Index seek with a key lookup per row | 613,688 | 674 |
| After `UPDATE STATISTICS ... WITH FULLSCAN` | 67,354 | 200,000 | Parallel clustered scan | 64,911 | 123 |

The low estimate made key lookups look cheap, and each one cost several reads. Fresh statistics led to a scan with about a tenth of the reads. Automatic updates are on by default, so this needs a large load between updates, and a bulk load should be followed by a statistics update.

### Implicit conversion

Why every code column is `varchar`, and why the application sends `varchar` parameters. Both queries look up one SKU in `BranchStock`, which has `IX_BranchStock_Sku`. The collation is the same as `ApexPostDb`.

| Parameter | Plan | Logical reads | CPU ms | Elapsed ms | Warning |
|---|---|---|---|---|---|
| `varchar(30)`, as EF Core sends | Index seek on `IX_BranchStock_Sku` | 1,537 | 0 | 2 | None |
| `nvarchar(30)` | Parallel index scan | 5,055 | 171 | 45 | `PlanAffectingConvert`: `CONVERT_IMPLICIT(nvarchar(30),[b].[Sku],0)=[@sku]`, Seek Plan |

An `nvarchar` value has higher type precedence, so SQL Server converts every stored value, and the index can no longer seek.

### Query Store

Query Store ran on `ApexPostDb_Perf` only, and `ApexPostDb` settings did not change. It tracked each query across the index change without any extra instrumentation. In [query-store.txt](docs/evidence/m6/query-store.txt), each query has one ID with a separate plan ID before and after. For example:

| Query | Plan | Executions | Average reads | Average duration ms |
|---|---|---|---|---|
| Shipment search | Before | 7 | 13,771 | 53.1 |
| Shipment search | After | 7 | 61 | 3.9 |
| `usp_DailyBranchSales` statement | Before | 7 | 33,397.6 | 168.3 |
| `usp_DailyBranchSales` statement | After | 7 | 809 | 2.3 |

These averages include the plan-capture run, so they differ slightly from the table above.

### Clustered GUID key

ADR 0018 left this for measurement. `CounterTransactions` is clustered on a client-generated GUID, which ADR 0018 keeps so that offline terminals can create IDs.

| State | `PK_CounterTransactions` fragmentation | Pages |
|---|---|---|
| After the ordered bulk load | 0.0% | 31,856 |
| After 60,000 rolled-back random inserts | 62.2% | 46,394 |
| After 200,000 more committed inserts | 99.1% | 63,836 |

Random inserts split pages, and a rollback does not undo the split. The first write run produced 248,609,396 log bytes, against about 14,400,000 for later runs on the same pages. The impact is contained because the workloads above no longer scan this table. The cost remains for writes, and the index would need regular maintenance in production. The key stays, because the offline design depends on it.

### Conclusions

1. The workloads that return a small part of a large table gained most, because a seek replaced a scan. The daily sales path went from over 33,000 reads to 121 for a small branch.
2. Reads and time did not always move together. Two workloads read less without running faster, and one ran slower. An index is justified by the whole table above, not by logical reads alone.
3. Not every problem was an index problem. The slowest query found, 1.3 seconds for one row, needed a query hint. Stale statistics and `nvarchar` parameters can undo a good index.
4. The cost is 284 MB and 10.8% more log per write, with no measurable change in write time at this load.

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
