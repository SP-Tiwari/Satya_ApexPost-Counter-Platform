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

Nothing in the running application uses this yet: the app stays on JSON until M3. The change takes effect when sales are saved through SQL Server, and it needs automated tests in the approved testing phase.

`Payments.Amount` must hold the total the customer actually pays. M2 sets that value from the sale, and the converter applies the same rounding to it.

## Alternatives considered

- **Round inside the domain, per line.** This gives one rule for memory and storage, but it changes business calculations, which the brief and this milestone say to preserve.
- **Use `decimal(18,4)` or wider.** It would keep more precision but breaks the brief's explicit `decimal(18,2)` rule, and weight × rate can produce more decimals anyway.
- **Leave rounding to the driver.** The behaviour would be implicit and undocumented.
- **Reject any value with more than 2 decimals.** Normal shipping sales would fail.

## Consequences

- There is one documented rule in one place.
- A sale's in-memory total can differ from its reloaded total by a fraction of a currency unit. Tests in the testing phase must pin this.
- **Known defect, not yet fixed: a refund can differ from its payment.** The `Payment` entry rounds the unrounded in-memory total. A `Refund` entry rounds the total calculated after reloading, when a line's price has already been rounded.

  | Example sale | Total | Stored entry |
  |---|---|---|
  | Payment, with shipping at 11.255 | 5,160.7691 | 5,160.77 |
  | Refund, with shipping reloaded as 11.26 | 5,160.775 | 5,160.78 |

  The 5,160.77 was observed in the M2 SQL probe. The 5,160.78 is calculated from the code and has not been run. The effect is limited to sales with a price of more than 2 decimals, which in practice means shipping lines, and to about one minor currency unit. It affects `Payments` on SQL Server only, so it has no effect until the M3 cutover. The fix is held until the trainer confirms the `Refund` entries ([ADR 0020](0020-repository-and-unit-of-work-boundaries.md)), because it depends on that rule. A likely fix is to reuse the amount of the stored `Payment` entry.
- T-SQL reports in M4 must calculate tax the same way: stored line amounts × stored rates, rounded only for display.
- Dimensions use `decimal(8,2)` with no converter, because no calculation uses them.
