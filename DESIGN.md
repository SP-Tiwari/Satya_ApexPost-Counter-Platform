# ApexPost Counter Platform — Design

This document describes the platform as implemented on the `feature/week3-sql-persistence` branch. Where something is planned but not built, it says so. The application still runs on JSON persistence; the SQL Server schema and mapping from Week 3 M1 are described in [section 4.4](#44-sql-server-schema), and the application moves onto them in M2 and M3 ([plan](docs/week-3-plan.md)). Decisions are recorded as ADRs in [docs/adr/](docs/adr/); a short request-flow walkthrough is in [docs/architecture.md](docs/architecture.md).

## 1. Architecture

### Layers

| Project | Responsibility | Depends on |
|---|---|---|
| `ApexPost.Domain` | Entities, value objects, business rules, domain services, events, specifications and report calculations. No I/O. | — |
| `ApexPost.Application` | Use cases (application services), persistence/security/audit/courier/verification abstractions, the domain-event publisher, the reference-data cache and request validation. | Domain |
| `ApexPost.Infrastructure` | JSON snapshot repository, JSON credential store, JSON Lines audit store, demo seed, and the simulated courier and identity-verification services. Also `ApexPostDbContext`, its EF Core mappings and migrations, which the running application does not use until M3. | Application, Domain |
| `ApexPost.Web` | MVC controllers and views, middleware, filters, authentication, authorization policies, the partner API and the composition root (`Program.cs`). | Application, Infrastructure |

```mermaid
flowchart LR
    Web["ApexPost.Web<br/>MVC, middleware, filters, composition root"]
    App["ApexPost.Application<br/>use cases and abstractions"]
    Infra["ApexPost.Infrastructure<br/>JSON stores, simulated services"]
    Domain["ApexPost.Domain<br/>entities, rules, events"]
    Web --> App
    Web --> Infra
    Infra --> App
    Infra --> Domain
    App --> Domain
```

Dependencies point inward. Application owns the interfaces (`IApexPostRepository`, `IStaffCredentialRepository`, `IAuditStore`, `ICourierTrackingService`, `IIdentityVerificationService`); Infrastructure implements them; Web wires them together. Controllers translate HTTP to application calls and back. They hold no business rules, and `Program.cs` only reads configuration, registers services and builds the pipeline.

**Did the Week 1 architecture survive?** This repository starts at the ApexPost solution (first commit: "initialize ApexPost solution structure"), so the Week 1 TechNova code is not in its history. The four-layer split shown above was chosen for ApexPost from the start, with the domain rebuilt around branches, counters, a mixed basket and postal/financial services. *Author to confirm against the Week 1 submission.*

### Domain model

```mermaid
classDiagram
    direction LR
    class Branch {
        BranchCode Code
        string Name
        string Region
        string OpeningHours
        bool IsActive
    }
    class CounterTerminal {
        string Id
        string Name
        bool IsActive
    }
    class Staff {
        string Id
        string Name
        string Email
        StaffRole Role
        BranchCode HomeBranchCode
        bool IsActive
    }
    class Product {
        Sku Sku
        string Name
        ProductCategory Category
        Money UnitPrice
        TaxCategory TaxCategory
    }
    class BranchStock {
        BranchCode BranchCode
        Sku ProductSku
        int Quantity
    }
    class TransactionBasket {
        Guid Id
        BranchCode BranchCode
        string Currency
        TransactionPaymentStatus PaymentStatus
        DateTimeOffset OccurredAtUtc
        Money Subtotal
        Money Tax
        Money Total
    }
    class CounterSaleDetails {
        string CounterTerminalId
        string ClerkStaffId
        string PaymentMethod
    }
    class TransactionLineItem {
        TransactionItemType Type
        string Description
        Money UnitPrice
        int Quantity
        TaxCategory TaxCategory
    }
    class ShippingItem {
        string Id
        ShippingItemType Type
        Weight Weight
        Dimensions Dimensions
        string DestinationZone
        ShippingServiceLevel ServiceLevel
    }
    class MoneyOrder {
        Guid Id
        Money Amount
        string Sender
        string Recipient
        Money Fee
        MoneyOrderStatus Status
    }
    class IdentityVerification {
        string DocumentType
        string MaskedDocumentIdentifier
        bool IsSuccessful
    }
    Branch "1" *-- "1..*" CounterTerminal
    Staff --> Branch : home branch
    BranchStock --> Branch
    BranchStock --> Product
    TransactionBasket "1" *-- "1..*" TransactionLineItem
    TransactionBasket o-- "0..1" CounterSaleDetails
    TransactionLineItem --> "0..1" Product : goods, by Sku
    TransactionLineItem o-- "0..1" ShippingItem : shipping
    TransactionLineItem o-- "0..1" MoneyOrder : money order
    MoneyOrder o-- "0..1" IdentityVerification
```

- **Entities** have identity and a lifecycle: `Branch`, `Staff`, `Product`, `BranchStock`, `TransactionBasket`, `TransactionLineItem`, `ShippingItem`, `MoneyOrder`.
- **Value objects** are immutable `sealed record`s that validate on construction: `Money`, `Sku`, `BranchCode`, `Weight`, `Dimensions`, `ShippingRate`, `IdentityVerification` (holds only the masked identifier), and `CounterSaleDetails`.
- **The mixed basket uses composition, not inheritance.** A basket holds `TransactionLineItem`s, each with a `TransactionItemType` (`Goods`, `Service`, `MoneyOrder`) and at most one attached shipping item or money order. Rules that differ by line type are `switch` expressions on that type, for example money-order lines carry no tax. This keeps one totals calculation and one lifecycle for every kind of sale.
- **Basket lifecycle:** Open → Paid, Open → Voided, Paid → Voided, and Paid → Refunded. Voiding or refunding a paid sale returns its stock and cancels its money orders exactly once (`SaleReversalService`, [ADR 0011](docs/adr/0011-void-and-refund-of-completed-sales.md)).

### Pricing and tax

- **Shipping** uses `ShippingPricingService`, a rule object built from persisted `ShippingRate` records keyed by `(destination zone, service level)`. The price is `base price + weight in kg × per-kg charge`. A missing rate is an error, never a fallback ([ADR 0005](docs/adr/0005-configured-shipping-pricing-and-limits.md)). `ShippingWeightLimitRule` rejects parcels over the configured maximum for their service level; letters are exempt.
- **Tax** is a per-category rate dictionary (`Transactions:TaxRates:<TaxCategory>`) passed into the basket. Each line is taxed by its `TaxCategory`. Shipping lines use `Shipping:TaxCategory`, and money-order principal is not taxed. The money-order fee is a separate line with `MoneyOrders:Fee:TaxCategory`.
- **No values in code.** No price, tax rate, weight limit, threshold or fee has a default. If one is missing, the affected workflow is disabled (or fails closed) rather than guessing.

## 2. Business rules

| Rule | Enforced by |
|---|---|
| Branch stock never goes negative; a sale fails if any line lacks stock (all lines are checked before any stock is deducted) | `SaleProcessingService`, `BranchStock` |
| Inactive staff cannot process; clerks and managers transact only at their home branch; the clerk on the sale must be the signed-in staff member | `TransactionProcessingRule` |
| A closed branch, or an inactive or unknown counter, cannot take transactions | `TransactionProcessingRule` |
| A money order above the configured threshold needs a successful identity verification in the same transaction, decided by the verification service, not the user | `MoneyOrderIdentityVerificationRule`, `MixedBasketApplicationService` |
| Parcels over the configured maximum for their service level are rejected | `ShippingWeightLimitRule` |
| Voids and refunds need an active Branch Manager of the same branch and a reason | `TransactionVoidRefundRule` |
| Full identity-document numbers are never stored; only the last four characters are kept | `IdentityVerification` |
| A transaction ID can be processed once; repeated submissions do not deduct stock twice | Repository `UpdateAsync` callback in the application services |

## 3. Reports, events and notifications

### Reports

| Report | Who | Notes |
|---|---|---|
| Daily branch sales (goods vs. services) | Branch Manager | Filters by business date using `OccurredAtUtc` (UTC) |
| Low stock per branch | Branch Manager | Threshold is `Reports:LowStockThreshold`; the page returns 503 until it is configured |
| Shipping volume by zone and service level | Auditor, Administrator | |
| Money orders and identity verifications per branch | Auditor, Administrator | Includes cancelled money orders |

Report filters are composable specifications (`PaidTransactionSpecification`, `TransactionAtBranchSpecification`, `TransactionOnBusinessDateSpecification`, `StockAtBranchSpecification`), combined with `And`/`Or`/`Not` and applied with the `Matching` extension member.

### Dashboard

`/` shows a branch dashboard for signed-in staff. `BranchDashboardApplicationService` starts the reference-data load and the courier lookups for recent shipments at the same time and awaits them with `Task.WhenAll`. Sales and stock panels are shown to Branch Managers only.

### Domain events and simulated notifications

| Event | Raised when |
|---|---|
| `TransactionCompletedEvent` | A sale is processed |
| `StockLowEvent` | A sale or adjustment takes a product from above the threshold to at or below it (crossing only, so no repeats) |
| `MoneyOrderIssuedEvent` | A money order is issued |
| `HighValueMoneyOrderIssuedEvent` | The issued order is above the threshold |
| `TransactionVoidedEvent` | A sale is voided |
| `TransactionRefundedEvent` | A sale is refunded |

Events are collected inside the repository update and published by `DomainEventPublisher` only after the update has committed, so a rejected or rolled-back change never notifies anyone. `SimulatedNotificationSender` subscribes to the publisher's C# events and writes the four required notifications (low stock, high-value money order, void, refund) as structured warning logs prefixed `Simulated notification:`. A failing subscriber is logged and cannot undo the committed change ([ADR 0012](docs/adr/0012-committed-domain-events-and-simulated-notifications.md)).

## 4. Persistence

- **Store:** `JsonApexPostRepository` keeps one versioned `apexpost.json` snapshot in `Storage:Directory`. Infrastructure maps it through DTOs and rebuilds domain objects through their validating constructors; it never deserializes straight into entities.
- **Concurrency:** `UpdateAsync(Func<snapshot, snapshot>)` holds a `SemaphoreSlim` across load, change and save. It writes to a temporary file in the same folder and then moves it into place. If the callback throws, nothing is saved. This is what makes concurrent sales safe within one process ([ADR 0001](docs/adr/0001-json-snapshot-persistence.md), [ADR 0004](docs/adr/0004-atomic-transaction-processing.md)).
- **Schema migration:** the current schema is 7.

  | Version | Change |
  |---|---|
  | 2–4 | Transaction IDs, money-order verification links, basket branch and shipping-line links |
  | 5 | Transaction `OccurredAtUtc` |
  | 6 | Money-order `Id` and `Status`, and line-to-money-order links |
  | 7 | Sale details: counter, clerk and payment method |

  Older files are migrated on load without inventing business data; for example, undated legacy sales stay undated. A file without a version that contains newer fields is rejected.
- **Failure behaviour:** a missing file is an empty snapshot. A malformed, unsupported or domain-invalid file fails loudly and is never overwritten.
- **Other stores:** credentials live in a separate file (`Authentication:CredentialFile`, [ADR 0007](docs/adr/0007-separate-staff-credential-store.md)), and audit records are appended to a JSON Lines file (`Audit:FilePath`).
- **Next step:** the SQL Server schema below replaces these stores in M2 and M3. The lock is per process; it does not coordinate several processes sharing one folder.

### 4.4 SQL Server schema

Week 3 M1 adds the schema and mapping. **No screen or service uses them yet.** Decisions are in [ADR 0017](docs/adr/0017-money-precision-and-rounding.md) (money precision), [ADR 0018](docs/adr/0018-schema-design-and-delete-behaviours.md) (schema and deletes) and [ADR 0019](docs/adr/0019-domain-and-persistence-separation.md) (domain and persistence models).

#### ER diagram

Every table also has `CreatedAtUtc`, `CreatedBy` and `ModifiedAtUtc`, left out here for space.

```mermaid
erDiagram
    Branches ||--o{ Counters : has
    Branches ||--o{ Staff : "home branch"
    Branches ||--o{ BranchStock : holds
    Branches ||--o{ CounterTransactions : records
    Branches ||--o{ MoneyOrders : processes
    Roles ||--o{ Staff : assigns
    Staff ||--o| StaffCredentials : "signs in with"
    Staff ||--o{ CounterTransactions : "clerk of"
    Staff ||--o{ StockAdjustments : makes
    ProductCategories ||--o{ Products : groups
    Products ||--o{ BranchStock : "stocked as"
    Products ||--o{ TransactionLines : "sold as"
    BranchStock ||--o{ StockAdjustments : "changed by"
    ShippingZones ||--o{ ShippingRates : prices
    ShippingZones ||--o{ Shipments : "destination of"
    Counters ||--o{ CounterTransactions : takes
    CounterTransactions ||--|{ TransactionLines : contains
    CounterTransactions ||--o{ TransactionTaxRates : captures
    CounterTransactions ||--o{ Payments : "paid by"
    Shipments ||--o| TransactionLines : "billed on"
    MoneyOrders ||--o| TransactionLines : "billed on"
    MoneyOrders ||--o| IdentityVerifications : "verified by"

    Branches {
        int BranchId PK
        varchar BranchCode UK
        nvarchar Name
        nvarchar Region
        nvarchar OpeningHours
        bit IsActive
    }
    Counters {
        varchar BranchCode PK,FK
        varchar CounterId PK
        nvarchar Name
        bit IsActive
    }
    Staff {
        varchar StaffId PK
        nvarchar Name
        nvarchar Email UK
        tinyint RoleId FK
        varchar HomeBranchCode FK
        bit IsActive
    }
    StaffCredentials {
        varchar StaffId PK,FK
        int Version
        int Iterations
        varchar Salt
        varchar PasswordHash
    }
    Roles {
        tinyint RoleId PK
        nvarchar Name UK
    }
    Products {
        int ProductId PK
        varchar Sku UK
        nvarchar Name
        tinyint ProductCategoryId FK
        varchar TaxCategory
        decimal UnitPrice
        char Currency
    }
    ProductCategories {
        tinyint ProductCategoryId PK
        nvarchar Name UK
    }
    BranchStock {
        varchar BranchCode PK,FK
        varchar Sku PK,FK
        int Quantity
        rowversion RowVersion
    }
    StockAdjustments {
        uniqueidentifier StockAdjustmentId PK
        varchar BranchCode FK
        varchar Sku FK
        int Delta
        nvarchar Reason
        varchar ChangedByStaffId FK
        datetime2 OccurredAtUtc
    }
    ShippingZones {
        int ShippingZoneId PK
        varchar ZoneCode UK
        nvarchar Name
    }
    ShippingRates {
        varchar ZoneCode PK,FK
        varchar ServiceLevel PK
        decimal BasePrice
        char BasePriceCurrency
        decimal PerKilogramCharge
        char PerKilogramChargeCurrency
    }
    Shipments {
        varchar ShipmentId PK
        varchar ShipmentType
        decimal WeightKg
        decimal LengthCm
        decimal WidthCm
        decimal HeightCm
        varchar ZoneCode FK
        varchar ServiceLevel
    }
    MoneyOrders {
        uniqueidentifier MoneyOrderId PK
        decimal Amount
        char Currency
        decimal Fee
        char FeeCurrency
        nvarchar Sender
        nvarchar Recipient
        varchar ProcessingBranchCode FK
        varchar Status
    }
    IdentityVerifications {
        uniqueidentifier MoneyOrderId PK,FK
        varchar DocumentType
        varchar MaskedDocumentIdentifier
        bit IsSuccessful
    }
    CounterTransactions {
        uniqueidentifier TransactionId PK
        char Currency
        varchar BranchCode FK
        varchar CounterId FK
        varchar ClerkStaffId FK
        nvarchar PaymentMethod
        datetime2 OccurredAtUtc
        varchar Status
        nvarchar ReversalReason
        rowversion RowVersion
    }
    TransactionLines {
        bigint TransactionLineId PK
        uniqueidentifier TransactionId FK
        varchar LineType
        nvarchar Description
        decimal UnitPrice
        char Currency
        int Quantity
        varchar TaxCategory
        varchar Sku FK
        varchar ShipmentId FK
        uniqueidentifier MoneyOrderId FK
    }
    TransactionTaxRates {
        uniqueidentifier TransactionId PK,FK
        varchar TaxCategory PK
        decimal Rate
    }
    Payments {
        uniqueidentifier PaymentId PK
        uniqueidentifier TransactionId FK
        varchar EntryType
        decimal Amount
        char Currency
        nvarchar PaymentMethod
        datetime2 RecordedAtUtc
    }
    AuditEntries {
        bigint AuditEntryId PK
        datetime2 TimestampUtc
        varchar Controller
        varchar Action
        nvarchar Description
        varchar Outcome
        int StatusCode
        float DurationMilliseconds
        nvarchar RouteValues
        varchar StaffId
        varchar StaffRole
        varchar BranchCode
        varchar CorrelationId
    }
```

The table count is 19: the 17 the brief lists, plus `TransactionTaxRates` and `StaffCredentials`.

#### Normalization note (3NF)

Every non-key column depends only on its table's key. Three concrete anomalies this design prevents:

| Anomaly | Without this design | How the schema prevents it |
|---|---|---|
| **Update** | If each sale copied the branch's name and region, renaming a region would mean updating every sale, and a missed row would leave reports disagreeing | Name and region exist once, in `Branches`. `CounterTransactions` holds only `BranchCode` |
| **Insertion** | If stock and product details shared a table, a product could not be added to the catalogue until a branch stocked it. If zones existed only as text on rates, a zone could not exist before its first rate, and a typo would create a new zone | `Products` is separate from `BranchStock`. `ShippingZones` is separate, and `ShippingRates` and `Shipments` must reference a real zone |
| **Deletion** | If product details lived only on transaction lines, losing the last line for a product would lose the product | `Products` stands alone, and `Restrict` stops a referenced product being deleted |

**Deliberate exceptions:**
- `TransactionLines.Description`, `UnitPrice` and `TaxCategory` are copied from the product at sale time. They are not redundant: they record what was charged then, and they must not change when a price changes.
- `TransactionLines.Currency` repeats the transaction's currency, because `Money` is stored as a whole value. The domain rejects a line in another currency (`TransactionBasket.AddLineItem`).
- `PaymentMethod` is on both `CounterTransactions` and `Payments`, recording different facts. The transaction column is the method selected for the sale; the payment column is the method used for that financial movement, so a refund can record how the money went back. For a normal sale the two match. Neither is edited after it is written ([ADR 0018](docs/adr/0018-schema-design-and-delete-behaviours.md)).

#### Mapping reference

Code: `src/ApexPost.Infrastructure/Persistence/Sql/`.

| Concern | Implementation |
|---|---|
| DbContext | `ApexPostDbContext`, scoped use from M2; the configurations are applied with `ApplyConfigurationsFromAssembly` |
| Design-time creation | `ApexPostDbContextFactory`: holds no connection string, so generating a migration or script never reads secrets or touches a database |
| Fluent configuration | `Configurations/`: one `IEntityTypeConfiguration` class per entity, 18 in all. The 19th table, `TransactionTaxRates`, is an owned collection configured inside `CounterTransactionConfiguration` |
| Shared column rules | `MappingExtensions`: `IsBranchCode`, `IsSku`, `IsCode`, `IsEnumCode`, `IsMoney`, `IsUtcDateTime`, `IsActiveFlag`, `HasAuditColumns` |
| Value converters | `ValueConverters.cs`: `BranchCodeConverter`, `SkuConverter`, `WeightConverter`, `MoneyAmountConverter`, `UtcDateTimeOffsetConverter`, `RouteValuesConverter` |
| Complex types | `Money` (two columns, so a converter handles its amount and the type maps as a complex property, safely shared between objects) and `Dimensions` |
| Owned types | `CounterSaleDetails` (optional, stored on `CounterTransactions`) and `TransactionTaxRate` (owned collection, stored in `TransactionTaxRates`) |
| Shadow properties | `RowVersion`; `CreatedAtUtc`, `CreatedBy`, `ModifiedAtUtc`; surrogate keys such as `BranchId`; foreign keys such as `TransactionId` on lines |
| Backing fields | `_lineItems`, `_counterTerminals`, `_taxRates`, `_reversalReason`, `_branchCode`, `_paymentStatus` |
| Separate persistence model | `IdentityVerificationRecord` and `IdentityVerificationMapper` |
| `varchar` codes | `IsUnicode(false)` with a maximum length on every code column. Verified: EF sends `@sku varchar(30)`, so an indexed code column is not compared through an implicit conversion |
| Health check | `SqlServerConnectivityHealthCheck` uses `Database.CanConnectAsync`, is mapped only in Development, and connects only when `/health/database` is requested |

`Address` on `Branch` was not added: the brief offers it as an example, nothing in ApexPost records one, and `Dimensions` and `CounterSaleDetails` already cover "complex and owned types where they fit".

#### Constraints

| Type | Count | Examples |
|---|---|---|
| UNIQUE | 6 | `UQ_Branches_BranchCode`, `UQ_Products_Sku`, `UQ_Staff_Email`, `UQ_ShippingZones_ZoneCode`, `UQ_Roles_Name`, `UQ_ProductCategories_Name` |
| CHECK | 33 | `CK_BranchStock_Quantity` (≥ 0), `CK_MoneyOrders_Amount` and `CK_Payments_Amount` (> 0), `CK_Shipments_Weight` (> 0), `CK_CounterTransactions_Status`, `CK_IdentityVerifications_MaskedDocumentIdentifier` |
| DEFAULT | 43 | Three audit defaults per table (`SYSUTCDATETIME()`, `SUSER_SNAME()`) are the main group, plus `IsActive`, `Quantity` and `Status` |
| Foreign key | 22 | 21 `NO ACTION`, 1 `CASCADE`; see [ADR 0018](docs/adr/0018-schema-design-and-delete-behaviours.md) |
| Rowversion | 2 | `BranchStock.RowVersion`, `CounterTransactions.RowVersion`, both `NOT NULL` |

Identity documents are stored only as `****` plus the last four characters, in `varchar(8)` with a CHECK that also rejects any other format. The domain's `IdentityVerification` masks first, so a full number never reaches the mapper.

#### Migrations

| Item | Location |
|---|---|
| Migration | `src/ApexPost.Infrastructure/Persistence/Sql/Migrations/20261031085407_InitialCreate.cs` |
| Idempotent script | `sql/schema/ApexPostDb-idempotent.sql` |
| Follow-up migration | `AddPerformanceIndexes`, planned for M6 |

The application never calls `EnsureCreated()` or migrates at startup. `InitialCreate` was reviewed by hand. The review made `CreatedBy`, `RowVersion` and `TransactionLines.TransactionId` non-nullable, and added the composite counter foreign key by hand.

**`ApexPostDb` itself has not been migrated.** It is still empty; verification used a separate disposable database.

#### M1 traceability

Verification ran on 2026-10-31 on SQL Server 2025 (17.0) Enterprise Evaluation, in a disposable database in `E:\SqlData\ApexPost\Tests`, since dropped. The EF round-trip checks were a throwaway probe, not repository tests; automated tests follow in the approved testing phase.

| Assignment requirement | Implementation | Documentation | Verification evidence |
|---|---|---|---|
| Part A: required tables | `Configurations/*.cs` | ER diagram above | 19 user tables after applying the script |
| A: PKs, FKs, delete behaviour | `Configurations/*.cs`; hand-written FK in `InitialCreate` | ADR 0018 | 22 FKs. Deleting a referenced branch and product was rejected. A counter from another branch was rejected |
| A: UNIQUE constraints | `HasAlternateKey` in `BranchConfiguration`, `ProductConfiguration`, `StaffConfiguration` | Constraints table | Duplicate `BranchCode`, `Sku` and email each rejected |
| A: CHECK constraints | `HasCheckConstraint` in each configuration | Constraints table | Negative stock, invalid status, zero amount, zero weight, unmasked document and a reason on a paid sale each rejected |
| A: DEFAULT and audit columns | `MappingExtensions.HasAuditColumns`, `IsActiveFlag` | Constraints table | Every table has all three audit columns; 43 defaults |
| A: rowversion | `BranchStockConfiguration`, `CounterTransactionConfiguration` | Constraints table | Both `NOT NULL`. Two contexts selling the same stock: the second got `DbUpdateConcurrencyException`, and stock stayed at 0 |
| A: data types | `MappingExtensions` | Mapping reference, ADR 0017 | Catalog query: 70 `varchar`/`char` columns, money `decimal(18,2)`; the 18 `nvarchar` columns are all names or free text |
| A: masked documents | `IdentityVerification`, `IdentityVerificationMapper`, `IdentityVerificationConfiguration` | ADR 0019 | `ABCDEFGH1234` stored as `****1234`; an unmasked value and an over-long value were rejected |
| A: ER diagram, three anomalies | This section | This section | Review |
| A: idempotent script | `sql/schema/ApexPostDb-idempotent.sql` | Migrations table | Applied twice to one database: both runs exited 0, and one history row was recorded |
| E: `ApexPostDbContext` | `ApexPostDbContext.cs` | Mapping reference | Round trip of a sale with goods, shipping and money-order lines |
| E: Fluent configuration per entity | `Configurations/*.cs` | Mapping reference | `has-pending-model-changes` reports no changes |
| E: value converters | `ValueConverters.cs` | Mapping reference, ADR 0017 | `BranchCode`, `Sku`, `Weight` and UTC times round-tripped; 11.255 stored as 11.26 |
| E: owned and complex types | `TransactionConfigurations.cs`, `ShippingConfigurations.cs` | Mapping reference | `CounterSaleDetails`, tax rates, `Money` and `Dimensions` round-tripped; null sale details stayed null |
| E: `InitialCreate` | `Migrations/20261031085407_InitialCreate.cs` | Migrations table | Applied by script and by `dotnet ef database update`; reverted to 0 tables and re-applied to 19 |
| A: `Payments` with its payment method | `Payment.cs`, `PaymentConfiguration` in `TransactionConfigurations.cs` | Normalization note, ADR 0018 | `Payments.PaymentMethod` is `nvarchar(50) NOT NULL`, and a NULL insert was rejected. `CounterTransactions.PaymentMethod` stays `nvarchar(50) NULL`. A sale selected `Card` while its refund entry recorded `Cash`, and both round-tripped |
| E: never `EnsureCreated()` | No call in `src` | Migrations table | `grep` of `src` found no `EnsureCreated` or `Migrate` call |
| F: rich entities, shadow properties | Domain entities; `HasAuditColumns`, `RowVersion` | ADR 0019 | Round trip through private constructors and backing fields |
| F: separate persistence entity and mapper | `IdentityVerificationRecord.cs` | ADR 0019 | Mapper round trip to `IdentityVerification.FromMaskedIdentifier` |
| Plan: `varchar` with `IsUnicode(false)` | `MappingExtensions.IsCode`, `IsBranchCode`, `IsSku`, `IsEnumCode` | Mapping reference | `ToQueryString()` shows `DECLARE @sku varchar(30)` |
| Plan: health check through EF Core | `SqlServerConnectivityHealthCheck.cs` | Mapping reference, README | `/health/database` returned `200 Healthy`; the existing health check tests passed |
| Week 2 rules preserved | Domain changes in ADR 0019 | ADR 0019 | All 558 existing tests passed after the `Payments.PaymentMethod` correction: Domain 213, Application 110, Infrastructure 38, Web 197. No test file was modified. These tests verify existing behaviour on JSON persistence, not the new SQL persistence. SQL schema and mapping were verified separately with throwaway probes on disposable databases, which are not repeatable automated tests |

**Not implemented in M1:** repositories and the Unit of Work, stamping of `ModifiedAtUtc` and the real `CreatedBy`, seed data, and automated SQL tests. Those belong to later milestones.

## 5. HTTP pipeline

```mermaid
flowchart TD
    A[Request] --> B[CorrelationIdMiddleware<br/>ID, log scope, request timing]
    B --> C[UseExceptionHandler<br/>Problem Details]
    C --> D[UseHsts<br/>outside Development]
    D --> E[SecurityHeadersMiddleware<br/>headers set in OnStarting]
    E --> F[MaintenanceModeMiddleware<br/>503 + Retry-After, /health open]
    F --> G[UseHttpsRedirection]
    G --> H[UseRouting]
    H --> I[UseCors ParcelTracking]
    I --> J[ApiKeyAndIpRestrictionMiddleware<br/>RequireApiKey endpoints]
    J --> K[AdminIpRestrictionMiddleware<br/>RequireAdminNetwork endpoints]
    K --> L[PartnerRateLimitingMiddleware]
    L --> M[UseRateLimiter<br/>sign-in policy]
    M --> N[UseAuthentication]
    N --> O[UseAuthorization<br/>roles and policies]
    O --> P[Endpoints: /health, static assets, MVC]
    P --> Q[MVC filters: Audit, Validation, Performance]
```

| Position | Why it is there |
|---|---|
| Correlation ID first | Every later log line, error response and audit record can carry the ID. It wraps everything, so its `try/finally` timing covers failed requests too. |
| Exception handler second | Catches anything downstream and returns Problem Details with a trace ID and no stack trace (no developer exception page is registered). It sits inside the correlation scope so errors are correlated. |
| HSTS, then security headers | Headers are added in `Response.OnStarting`, so they are present on every response, including errors, maintenance 503s and redirects. |
| Maintenance before routing | Shuts the app quickly without running authentication or MVC; `/health` stays reachable for monitors. |
| HTTPS redirection before routing | Plain-HTTP requests are redirected before any endpoint work. |
| Routing before CORS and endpoint checks | `[RequireApiKey]` and `[RequireAdminNetwork]` are endpoint metadata, which exists only after routing. CORS also needs the matched endpoint. |
| API key/IP, then admin IP, then partner limit | Cheap rejections happen before authentication. The partner limit counts only callers that have proven the key, so anonymous floods cannot use up a partner's quota. |
| Sign-in rate limiter before authentication | Brute-force attempts are refused before any password hashing work. |
| Authentication, then authorization | Claims must exist before policies (`BranchManagerOnly`, `SameBranchOnly`) are evaluated. |

## 6. Middleware versus filters

| Concern | Implemented as | Why |
|---|---|---|
| Correlation ID, request logging and timing | Middleware | Applies to every request, including static files, `/health` and errors that never reach MVC. |
| Exception handling | Built-in middleware | Must catch failures from anywhere in the pipeline. |
| Security headers, HTTPS, HSTS | Middleware | Response-wide, endpoint-independent. |
| Maintenance mode | Middleware | Must stop requests before routing, authentication or MVC. |
| API key, IP allowlists, rate limits | Middleware | Must reject before authentication and MVC run. It reads endpoint metadata after routing, so marking an endpoint is still per-action. |
| CORS | Built-in middleware | Preflight requests never reach an action. |
| Auditing | `AuditActionFilter` (global, `ServiceFilter`, opt-in through `[Audited]`) | Needs the selected controller, action and route values, plus the action's final result. Middleware sees only URLs. |
| Validation | `ValidationActionFilter` (global) | Needs bound `ModelState`, which exists only inside MVC. It redisplays MVC forms through the controller's `IInvalidModelStateHandler`, and returns `ValidationProblemDetails` for `[ApiController]` endpoints. |
| Action performance | `PerformanceActionFilter` (global) | Times only the action, separate from whole-request timing, and logs a warning above `Performance:SlowActionThresholdMilliseconds`. |
| Same-branch access | `SameBranchOnly` resource-based authorization policy | Needs the loaded transaction's branch, which is known only inside the action. |

## 7. Security

### Authentication and authorization

- **Sign-in:** cookie authentication with claims `StaffId`, `Role` and `BranchCode`. The cookie is HttpOnly, always Secure and SameSite=Lax, with an 8-hour sliding lifetime.
- **Revalidation:** every request rechecks the cookie against the current staff record (`StaffCookieAuthenticationEvents`), so deactivated or reassigned staff lose access immediately.
- **Passwords:** PBKDF2-SHA256 with at least 600,000 iterations and a random 16-byte salt, compared in constant time. A dummy hash runs for unknown staff IDs. Credentials are kept in a separate file from business data.
- **Policies:**
  - `BranchManagerOnly` (role) protects transaction history, voids, refunds, stock and manager reports. A Counter Clerk can open the details and shipping label of a sale they processed themselves, read-only (`TransactionAccessRule.CanView`).
  - `SameBranchOnly` (resource-based) compares the transaction's branch with the staff member's saved home branch, not the cookie claim ([ADR 0002](docs/adr/0002-authoritative-branch-scoped-transactions.md)).
  - Administration requires the Administrator role.
- **Forms:** antiforgery is required on every POST, and return URLs must be local.

### Partner and admin endpoints

- **Partner API:** `GET /integration/parcel-status/{shipmentId}` is marked `[RequireApiKey]`. The `X-API-Key` header is compared in constant time against a SHA-256 digest of `ApiSecurity:ApiKey`, which comes from user-secrets or the environment, never source.
  - 401 for a missing or wrong key, and 500 if no key is configured.
  - 403 from an IP outside `ApiSecurity:AllowedIpAddresses`.
  - 429 above `ApiSecurity:PartnerRateLimit`.
  - The response contains stored shipment facts, the existing transaction status and the simulated courier result. It has no prices, people or identity data ([ADR 0013](docs/adr/0013-partner-parcel-status-api-and-endpoint-security.md)).
- **Admin pages:** `[RequireAdminNetwork]` pages return 403 outside `AdminSecurity:AllowedIpAddresses`.
- **Empty allowlists:** an empty list switches that IP filter off; production must set both lists.

### Threats and mitigations

| Threat | Mitigation | Residual risk |
|---|---|---|
| Password guessing | Sign-in rate limit (429), PBKDF2, generic error message | Limit is per IP; shared NAT users share it |
| Account enumeration by timing | Dummy hash for unknown IDs, at the same 600,000 iterations that every created credential uses | A credential provisioned out of band with more iterations would still take longer than the dummy |
| Stolen or stale cookie | HttpOnly, Secure, SameSite cookie; per-request staff revalidation | Theft of a live cookie within its lifetime |
| CSRF | Antiforgery on every POST; SameSite=Lax | — |
| XSS and clickjacking | CSP `default-src 'self'` (no inline script), `X-Frame-Options: DENY`, `frame-ancestors 'none'`, nosniff, no-referrer | — |
| Cross-branch data access (IDOR) | Branch scoping from persisted staff in the services, plus `SameBranchOnly`; other branches' IDs return 404 or 403 | — |
| Partner key leakage or abuse | Key from secrets only; digest compare; IP allowlist; partner rate limit; minimal response | One shared key means one rate bucket for all partners |
| Spoofed client IP | `RemoteIpAddress` only; forwarded headers are not trusted | Behind a proxy, configure forwarded headers before relying on IP checks |
| CORS misuse | Exact configured origin, GET only, no credentials | CORS only tells browsers what to allow. It does not stop scripts or servers calling the API, so it is not a security boundary; the API key is. |
| Sensitive data in logs or audit | Audit records only IDs, roles, route keys and status; passwords, document numbers, keys and form values are never recorded; identity documents are masked in the domain | Void/refund notification logs include the staff-entered reason |
| Secrets in the repository | User-secrets (`UserSecretsId` set), `.gitignore` for data folders, demo password never committed | — |
| Lost updates under concurrency | Locked read-modify-write and atomic file replace | Single process only |
| Error detail disclosure | Problem Details with a trace ID only | — |

## 8. Auditing and privacy

- **What is audited:** actions marked `[Audited]`:
  - sign-in and sign-out
  - sales, including money orders and their identity checks, which happen inside the sale
  - voids, refunds and stock adjustments
  - all administration changes

  Page views, reports and partner lookups are not audited ([ADR 0009](docs/adr/0009-mvc-cross-cutting-filters.md)).
- **What a record holds:**
  - UTC time
  - controller and action
  - outcome: `Succeeded` below 400, `Rejected` for 4xx, `Failed` for 5xx or an exception
  - the status code actually sent: the filter writes after the result runs, so redirects are 302
  - duration and allow-listed route values (`id`, `transactionId`, `branchCode`); a new sale adds its posted transaction ID through `AuditActionFilter.AddAuditValue`, which accepts only the same allow-listed keys
  - staff ID, role and branch
  - correlation ID
- **Where it is stored:** `IAuditStore` is implemented by `JsonAuditStore`, an append-only JSON Lines file serialized by a `SemaphoreSlim`. A write is not cancelled if the client disconnects. [data/AUDIT-SAMPLE.json](data/AUDIT-SAMPLE.json) contains real lines from a local run.
- **Personal data:** no passwords, API keys, full document numbers, card data or form values are written. Route keys that look sensitive (password, key, token, secret, credential, document, card) are dropped.
- **Integrity:** the file is not tamper-evident; protect it with file permissions and backups.
- **Gaps:** requests rejected before MVC (authorization, antiforgery, rate limit, IP) are logged by the framework but not audited.

## 9. Asynchrony, concurrency and caching

| Technique | Where | Why |
|---|---|---|
| `async`/`await` with `CancellationToken` | Controllers → application services → repositories and simulated services | No blocking I/O; abandoned requests stop work. There is no `.Result`, `.Wait()` or `Thread.Sleep`. |
| `Task.WhenAll` | `BranchDashboardApplicationService` (reference data plus courier lookups); demo credential hashing | Independent work runs at the same time, so the page waits for the slowest call rather than the sum |
| `SemaphoreSlim` | JSON repository and audit store (one writer); simulated courier (`Courier:MaxConcurrentLookups`) | Safe file writes; bounded load on the external courier |
| `ValueTask` plus cache | `ReferenceDataCache` (products and shipping rates, `Caching:ReferenceDataSeconds` = 30), read by the dashboard and the catalogue page | A cache hit returns synchronously without reading and deserializing the JSON file. Sale pricing always uses the authoritative snapshot inside the locked update, never the cache. |
| Timeouts, separate from cancellation | Simulated courier and identity verification use `CancellationTokenSource(timeout, TimeProvider)` | A timeout becomes `TimedOut` or a 503 "verification unavailable"; caller cancellation propagates as cancellation. |

**Measurement:** no benchmark has been recorded yet. The cache's benefit is explained above rather than measured; adding a timing comparison is listed under known gaps.

**DI lifetimes:**
- **Singletons:** repository, credential and audit stores, cache, event publisher, notification sender, `TimeProvider`, simulated services. They hold locks or shared state.
- **Scoped:** application services, filters and `StaffCookieAuthenticationEvents`, which are per request.
- **Middleware** is created once, so its constructors take only singletons (`IConfiguration`, `ILogger`, the partner limiter). Nothing scoped is captured.

## 10. C# 14 and language features

| Feature | Where | Problem it solves |
|---|---|---|
| Records and value objects | `Money`, `Sku`, `BranchCode`, `Weight`, `Dimensions`, `ShippingRate`, `IdentityVerification`, `CounterSaleDetails` | Validated, immutable values with value equality |
| Pattern matching | Line tax by item type, basket status transitions, notification text, event dispatch | One readable rule per case |
| Extension members (`extension` blocks) | `SpecificationExtensions.Matching`, `ApexPostSnapshotExtensions.IsCompletelyEmpty` | Query helpers without changing the types |
| `field` keyword | `BranchStock.Quantity`, `MoneyOrder.Status`, `CounterSaleDetails` | Validation or locking in accessors without a hand-written backing field |
| Generics with constraints | `Specification<T> where T : class`, `RequestValidator.Validate<T>` | Reusable report filters and validation |
| Delegates (`Func`, `Predicate`, `Action`) | `UpdateAsync(Func<…>)`, `PredicateSpecification<T>`, `SaleReversalService` status callback, subscriber error callback | Caller-supplied behaviour inside a locked or validated operation |
| Events | `DomainEventPublisher` C# events | Observer-style notifications |
| Custom attributes with cached reflection | `NotBlank`, `GreaterThanZero`, `DefinedEnum` plus `RequestValidator` (`ConcurrentDictionary` cache); `[Audited]`, `[RequireApiKey]`, `[RequireAdminNetwork]` | Declarative rules without reflecting on every request |
| async, `Task.WhenAll`, `SemaphoreSlim`, `ValueTask`, timeouts | See section 9 | |

## 11. SOLID mapping

| Principle | Where it shows |
|---|---|
| Single responsibility | One rule class per business rule; pricing, sale processing and reversal are separate domain services; storage, audit and credentials are separate stores |
| Open/closed | New report filters are new specifications; new notification channels subscribe to the publisher; a database repository can replace JSON behind `IApexPostRepository` |
| Liskov substitution | Simulated courier and verification services are interchangeable with real ones behind their interfaces; tests substitute fakes for the repository and stores |
| Interface segregation | Credential reads (`IStaffCredentialRepository`) are separate from provisioning (`IStaffCredentialProvisioning`); `IAuditStore` has one method |
| Dependency inversion | Application depends on its own interfaces; Infrastructure implements them; Web composes them |

## 12. Configuration and demo

`appsettings.json` holds only non-secret operational settings. Business values (tax rates, payment methods, shipping tax category and weight limits, money-order threshold, fee and accepted documents, low-stock threshold) and secrets (partner API key, demo password) must be supplied through user-secrets or the environment; there are no defaults. Startup rejects partial or invalid settings.

`./scripts/run-demo.ps1` runs the opt-in demo seed with demo logins whose password comes from user-secrets ([ADR 0003](docs/adr/0003-opt-in-demo-data.md), [ADR 0010](docs/adr/0010-demo-logins-from-user-secrets.md)). The README lists the accounts and settings.

## 13. Testing

There are four xUnit projects: Domain (rules and value objects), Application (use cases), Infrastructure (JSON persistence, migration, concurrency, demo seed) and Web integration (`WebApplicationFactory`: security headers, API key and IP, rate limits, CORS, maintenance, authentication, filters and controllers).

Only the assignment's allowed libraries are used: xUnit, `Microsoft.AspNetCore.Mvc.Testing` and the Microsoft test SDK. Test doubles are hand-written (`FakeApexPostRepository`, a fixed `TimeProvider`, a fake identity-verification service), and every integration test host uses `ApexPostWebApplicationFactory`, which drops the developer's user-secrets so local settings cannot change results.

| Project | Result | Main coverage |
|---|---|---|
| Domain | 213/213 | Stock, weight-limit, money-order verification and void/refund rules, pricing, transaction access (`CanView`) |
| Application | 110/110 | Multi-line counter sales, payment-method configuration, money orders as basket lines (fee, threshold, verification success, failure and timeout), dated reports, catalogue, clerk read access, password hashing and the dummy-hash timing |
| Infrastructure | 38/38 | JSON persistence, migration from each schema version 1–6 to 7, rejection of fields newer than the file's version, concurrency, demo seed, database health check failure and cancellation paths |
| Web | 197/197 | Database health endpoint isolation, the six required checks (security headers, partner 401 and 403, sign-in 429, sign-in redirect, clerk 403 on void, correlation ID), the access-denied page answering 403, audit opt-in, status codes and transaction ID, catalogue, label preview and its print wiring, clerk sale flow, partner parcel-status API and CORS, invalid startup configuration |

The dummy-hash timing test compares the fastest of three runs and accepts a ratio of 0.6–1.6 against a real check; the old 210,000-iteration dummy measured about 0.35.

## 14. Known gaps

- **Shipping label content:** the label preview prints only what a sale records (shipment ID, zone, service level, weight, dimensions, origin branch, date). The domain holds no sender or recipient address and no barcode format is specified, so neither is printed.
- **Browser-only behaviour:** the label's print button is wired in `site.js` (the CSP blocks inline handlers). Tests check that wiring (the button's hook, the script served from the site's origin, the `window.print()` binding), but no browser engine is allowed, so the click itself is not exercised. A forbidden browser request redirects to `/Account/AccessDenied`, which answers 403.
- **Single-process persistence:** the persistence lock and partner rate limiting are single-process, and there is one shared partner key.
- **Audit gaps:** the audit omits pre-MVC rejections (authorization, antiforgery, rate limit, IP checks); the framework logs them instead.
- **No measurements:** none are recorded for the cache or dashboard.

## 15. Decision records

| ADR | Decision |
|---|---|
| [0001](docs/adr/0001-json-snapshot-persistence.md) | Versioned JSON snapshot persistence |
| [0002](docs/adr/0002-authoritative-branch-scoped-transactions.md) | Branch scope from persisted staff |
| [0003](docs/adr/0003-opt-in-demo-data.md) | Opt-in idempotent demo data |
| [0004](docs/adr/0004-atomic-transaction-processing.md) | Atomic transaction processing |
| [0005](docs/adr/0005-configured-shipping-pricing-and-limits.md) | Configured shipping pricing and limits |
| [0006](docs/adr/0006-money-order-verification-and-privacy.md) | Money orders as basket lines with system verification |
| [0007](docs/adr/0007-separate-staff-credential-store.md) | Separate staff credential store |
| [0008](docs/adr/0008-http-security-pipeline-order.md) | HTTP pipeline order |
| [0009](docs/adr/0009-mvc-cross-cutting-filters.md) | MVC filters: opt-in audit, validation, performance |
| [0010](docs/adr/0010-demo-logins-from-user-secrets.md) | Demo logins from user-secrets |
| [0011](docs/adr/0011-void-and-refund-of-completed-sales.md) | Void and refund of completed sales |
| [0012](docs/adr/0012-committed-domain-events-and-simulated-notifications.md) | Committed-only domain events and notifications |
| [0013](docs/adr/0013-partner-parcel-status-api-and-endpoint-security.md) | Partner parcel-status API and endpoint security |
| [0014](docs/adr/0014-transaction-dates-and-sale-details.md) | Transaction dates and sale details |
| [0015](docs/adr/0015-caching-concurrency-and-timeouts.md) | Caching, concurrency and timeouts |
| [0016](docs/adr/0016-patched-cryptography-dependency.md) | Pin the patched cryptography dependency |
| [0017](docs/adr/0017-money-precision-and-rounding.md) | Money precision at the database boundary |
| [0018](docs/adr/0018-schema-design-and-delete-behaviours.md) | Schema design and delete behaviours |
| [0019](docs/adr/0019-domain-and-persistence-separation.md) | Where domain and persistence models are separated |
