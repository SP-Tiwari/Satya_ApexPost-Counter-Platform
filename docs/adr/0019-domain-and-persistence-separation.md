# ADR 0019: Where domain and persistence models are separated

- Status: Accepted
- Date: 2026-10-31

## Context

The brief asks for rich domain entities mapped with the Fluent API, and for a separate persistence entity with a mapper for at least one aggregate, justified in an ADR. Extra models cost mapping code on every change, so each one needs a reason.

## Decision

**Direct mapping is the default.** Domain entities are mapped with the Fluent API in `src/ApexPost.Infrastructure/Persistence/Sql/Configurations/`. Rowversion and audit columns are shadow properties, so the domain never sees them. Private parameterless constructors exist only for EF Core, where a type's constructor takes a value EF cannot bind.

**`IdentityVerification` is the separate persistence model**, through `IdentityVerificationRecord` and `IdentityVerificationMapper`:

- **It protects a privacy rule at the storage boundary.** The domain mask keeps the document's length (`********1234`). The record always stores `****` plus the last four characters, so the database reveals neither the number nor its length.
- **Storage differs from the domain shape.** The record has a money-order key that the value object does not, and it has a fixed `varchar(8)` column with a CHECK that rejects anything else.
- **The rule stays in one place.** The domain's masking and validation are unchanged, and restoring goes through `IdentityVerification.FromMaskedIdentifier`.

**Small internal Domain changes made direct mapping of `TransactionBasket` possible.** Neither changes public behaviour:

| Before | After | Why |
|---|---|---|
| `Dictionary<TaxCategory, decimal> _taxRates` | `List<TransactionTaxRate> _taxRates`; the public `TaxRates` dictionary is built from it | EF Core can store a list as rows, but not a dictionary |
| `List<DomainNotification> _notifications` | `string? _reversalReason`; `Notifications` is derived from it and the payment status | A basket holds at most one void or refund notification, so one column stores it |

`RoleRecord` and `ProductCategoryRecord` are rows of lookup tables with no domain equivalent; they do not duplicate a domain type. New domain types `Payment`, `StockAdjustment`, `ShippingZone` and `TransactionTaxRate` were needed because their tables had no domain concept yet.

## Alternatives considered

- **Separate persistence models for every aggregate.** The domain would be completely free of EF concerns, at the cost of a second class and a mapper per aggregate, and lost change tracking.
- **A separate model for `CounterTransaction`.** It was considered because of the dictionary and notifications, but the two small changes above remove the need.
- **An owned type for `IdentityVerification`.** Less code, but the domain value would be stored as it is, so the mask would keep revealing the document length.
- **A JSON column for tax rates.** Simple to map, but it hides repeating data from SQL reports and breaks 3NF.
- **A domain maximum for document length.** The plan originally proposed this to size the column. It would have added a new rejection rule to the domain, and the fixed stored mask needs no such rule.

## Consequences

- `MoneyOrder.IdentityVerification` is ignored by EF Core, so M2 must load it through the record and mapper. This is M2's main mapping risk. Reports can read `IdentityVerifications` directly.
- After reloading, a mask may have fewer asterisks than the in-memory value. Only the display changes, and the last four characters stay the same.
- Domain types carry private constructors for EF Core, a small and documented cost.
