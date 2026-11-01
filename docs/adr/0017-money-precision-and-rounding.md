# ADR 0017: Money precision at the database boundary

- Status: Accepted
- Date: 2026-10-31

## Context

The brief requires `decimal(18,2)` for money. The domain does not round:

| Calculation | Source | Example |
|---|---|---|
| `Money` | `Money.cs` stores any non-negative decimal | |
| Shipping price | `ShippingPricingService.CalculatePrice`: base price + weight × per-kg charge | 10.00 + 1.255 kg × 1.00 = 11.255 |
| Line subtotal | `TransactionLineItem.Subtotal`: unit price × quantity | |
| Line tax | `TransactionBasket.GetLineTax`: subtotal × rate | 99.99 × 0.18 = 17.9982 |

Nothing in `src` called `Math.Round` before this decision. Left alone, a value with more than two decimals would reach a `decimal(18,2)` column and be changed by the driver without any rule saying how.

## Decision

- **Domain arithmetic does not change.** No calculation, rule or Domain test is altered.
- **Tax is not stored as an amount.** Each transaction keeps its rates in `TransactionTaxRates` (`decimal(9,6)`), and tax is derived from the stored lines and rates, exactly as it is today.
- **`MoneyAmountConverter` is the single rounding point.** Every money column uses it, through `MappingExtensions.IsMoney`. It rounds to 2 decimals with `MidpointRounding.AwayFromZero` when writing.
- **Weight** uses the same approach at 3 decimals (`WeightConverter`, `decimal(10,3)`).

## Behaviour change

There is one, and it is limited to values with more than 2 decimals. A shipping line priced at 11.255 is stored as 11.26, so after reloading it the basket's totals are calculated from 11.26. A value of 2 decimals or fewer is unaffected.

This has been live since the M3 cutover, and `MoneyAboveTwoDecimals_IsRoundedAtTheColumn` covers it. On reading, money, weight, dimensions and tax rates drop trailing zeros, so `20.00` from a decimal column still displays as `20`, as it did on JSON.

`Payments.Amount` must hold the total the customer actually pays. M2 sets that value from the sale, and the converter applies the same rounding to it.

## Alternatives considered

- **Round inside the domain, per line.** This gives one rule for memory and storage, but it changes business calculations, which the brief and this milestone say to preserve.
- **Use `decimal(18,4)` or wider.** It would keep more precision but breaks the brief's explicit `decimal(18,2)` rule, and weight × rate can produce more decimals anyway.
- **Leave rounding to the driver.** The behaviour would be implicit and undocumented.
- **Reject any value with more than 2 decimals.** Normal shipping sales would fail.

## Consequences

- There is one documented rule in one place.
- A sale's in-memory total can differ from its reloaded total by a fraction of a currency unit. Tests in the testing phase must pin this.
- **Fixed on 2026-11-01: a refund now equals its payment.** The project owner approved the rule that a refund must equal the amount originally paid. `RecordRefundEntryAsync` copies the amount and method of the stored `Payment` entry, read through `ICounterTransactionRepository.FindPaymentAsync`. Domain arithmetic, the schema and this ADR's rounding rule did not change.

  The earlier defect: the `Payment` entry rounded the unrounded in-memory total, and the `Refund` entry rounded the total calculated after reloading a line already rounded.

  | Example sale | Total | Stored entry |
  |---|---|---|
  | Payment, with shipping at 12.255 | 5,123.9609 | 5,123.96 |
  | Refund before the fix, with shipping reloaded as 12.26 | 5,123.9668 | 5,123.97 |
  | Refund after the fix | Not calculated | Copy of the payment |

  **Measured** on 2026-10-31 in the M3 live run against a disposable database, before the fix: a voided sale stored a `Payment` of 5,123.96 and a `Refund` of 5,123.97. `Reversal_OfASaleWithShipping_RefundsExactlyTheStoredPayment` now proves equality on a sale whose reloaded total rounds differently, for a void and a refund.
- T-SQL reports in M4 must calculate tax the same way: stored line amounts × stored rates, rounded only for display.
- **Open: the Daily Sales screen and the dashboard show unrounded sums.** They calculate in `PaidSalesSummary`, not in T-SQL, so the rule above does not cover them, and they can show three or more decimals. One sale with a parcel added 26.455 INR. `usp_DailyBranchSales` rounds the same sum to two decimals. This ADR does not settle the screen format: fixed two decimals would also turn `20` into `20.00`, against the display behaviour described above. Stored values and calculations are correct either way.
- Dimensions use `decimal(8,2)` with no converter, because no calculation uses them.
