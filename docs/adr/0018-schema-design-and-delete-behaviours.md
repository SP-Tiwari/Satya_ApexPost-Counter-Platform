# ADR 0018: Schema design and delete behaviours

- Status: Accepted
- Date: 2026-10-31

## Context

The brief requires normalized tables with keys, constraints and audit columns, and a stated delete behaviour for each relationship. ApexPost keeps financial history, and later weeks will build on this schema. The ER diagram, normalization note and constraint list are in [DESIGN.md](../../DESIGN.md#44-sql-server-schema).

## Decision

**Keys**
- `Branches`, `Products` and `ShippingZones` have a surrogate identity primary key, plus a UNIQUE business code. Foreign keys reference the business code, because the domain refers to branches and products by `BranchCode` and `Sku`.
- `CounterTransactions` keeps its client-generated GUID as the primary key, so a repeated submission fails as a key conflict instead of creating a second sale. This also keeps a future offline path open.
- `Counters` uses `(BranchCode, CounterId)`, because counter IDs repeat across branches.

**Delete behaviour**

| Relationship | Behaviour | Why |
|---|---|---|
| All foreign keys except the one below (21) | `Restrict` (`NO ACTION`) | Branches, staff, products, sales, payments and shipments are history. Deleting a referenced row must fail, not erase records. Branches and staff are deactivated instead |
| `TransactionTaxRates` → `CounterTransactions` | `Cascade` | EF Core's rule for an owned collection. These rows have no meaning without their transaction, and no use case deletes a transaction |

**Other rules**
- `Roles` and `ProductCategories` are lookup tables seeded by the migration, referenced by foreign key. Other enums are `varchar` columns with CHECK constraints, so the stored values are readable.
- `AuditEntries` has no foreign keys. Audit history must outlive the staff and branches it mentions, and it can record IDs that never matched a row.
- One foreign key is written by hand in `InitialCreate`: `FK_CounterTransactions_Counters_BranchCode_CounterId`. EF Core cannot model it, because `BranchCode` is on the transaction and `CounterId` is inside the owned `CounterSaleDetails`.
- The upper weight limit is not a CHECK. It depends on the service level and comes from configuration, so `ShippingWeightLimitRule` enforces it. The database enforces `WeightKg > 0`.
- `Payments` records money movements (payment or refund, amount, method, time). `PaymentMethod` exists on two tables on purpose, and the two columns record different facts:
  - `CounterTransactions.PaymentMethod` is the method selected for the sale. It belongs to `CounterSaleDetails`, and it is nullable with the other sale details.
  - `Payments.PaymentMethod` is the method used for that financial movement, `nvarchar(50) NOT NULL`. A refund can then record how the money went back, even if that differs from the sale.

  For a normal sale the two values match. This is a deliberate exception to 3NF. Neither value is edited after it is written, so the update anomaly it would normally risk does not apply. Today's workflow has one method per sale and no way to choose a different refund method, so a refund copies the sale's method. Split payments and a separate refund method are not implemented.
- Two tables go beyond the brief's list: `TransactionTaxRates` (each sale's rates) and `StaffCredentials` (replacing the JSON credential file in M3).

## Alternatives considered

- **Natural primary keys on `Branches` and `Products`.** Simpler, but the brief asks for UNIQUE constraints on these codes, and an integer clustered key keeps those tables narrow.
- **Cascade deletes on transaction lines.** Convenient for cleanup, but one mistaken delete would silently remove financial history.
- **Soft-delete columns on every table.** Not needed: only branches, counters and staff have a lifecycle, and they already have `IsActive`.
- **Status lookup tables for every enum.** They add joins without adding protection beyond a CHECK constraint.
- **Payment method only on `CounterTransactions`.** No duplication, and the brief does not say where the method belongs, but a refund's method could never be recorded and `Payments` would be an incomplete ledger.
- **Payment method only on `Payments`.** The cleanest shape, but EF Core could no longer rebuild `CounterSaleDetails`, which requires the method. That would mean changing a domain type that existing tests use, or giving up direct mapping of `CounterTransaction`.

## Consequences

- Deleting history needs an explicit archival design, which is deliberate.
- If `InitialCreate` is ever regenerated, the hand-written foreign key must be added again. A comment in the migration says so.
- A random GUID as the clustered key on `CounterTransactions` fragments under load. M6 measured 0% after the ordered load and 99.1% after random inserts, and the key stays because it rejects a repeated submission of the same transaction ID ([PERFORMANCE.md](../../PERFORMANCE.md#clustered-guid-key)).
- Line rows repeat `Currency` from their transaction, because `Money` is stored as a whole value. The domain guarantees they match. See the normalization note.
