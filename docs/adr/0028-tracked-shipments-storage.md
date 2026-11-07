# ADR 0028: Tracked shipments storage for the Week 4 API

- Status: Accepted
- Date: 2026-11-07

## Context

Week 4 Part B exposes shipments and scans through `ApexPost.Api`, reusing the Week 3 SQL Server and EF Core code. Two parts of `AssignmentWeek04.txt` pull in different directions:

| Line | Wording |
|---|---|
| 204, Constraints | "Do not modify the Week 3 data layer except to add new service methods." |
| 99, B1 | "Register the existing DbContext, repositories and services through dependency injection." |
| 105–112, B2 | Shipments are listed by `status` and `city`, and read, replaced, deleted and given scans by `trackingNumber`; the scan history is read back |
| 113, B2 | "Allowed status transitions: Created → PickedUp → InTransit → OutForDelivery → Delivered / Failed." |
| 131, 133, B4 | The tracking number matches `^APX-\d{8}$`; recipient name, phone, address and city are required |
| 143–144, B6 | "Each scan carries its Part A scan ID. Re-uploading the same scan ID must not create a duplicate." |

The Week 3 `Shipments` table (`ShippingItem`) stores an ID, type, weight, dimensions, destination zone and service level for a shipment sold at the counter. It has no tracking number in the `APX-########` form, no recipient, no city, no status and no scans. A new service method can only read and write columns that exist, so B2, B4 and B6 cannot be met with service methods alone.

## Decision

Extend the existing `ApexPostDbContext` additively, with one new migration that only creates two new tables.

| Item | Implementation |
|---|---|
| Domain | `TrackedShipment` (`src/ApexPost.Domain/Entities/TrackedShipment.cs`), `TrackedShipmentScan`, `ShipmentStatus`, `ShipmentScanType` and `ShipmentStatusTransitionRule` (`src/ApexPost.Domain/Rules/`) |
| Mapping | `TrackedShipmentConfiguration` and `TrackedShipmentScanConfiguration` in `src/ApexPost.Infrastructure/Persistence/Sql/Configurations/TrackedShipmentConfigurations.cs`, found by the context's existing `ApplyConfigurationsFromAssembly` call |
| Repository | `ITrackedShipmentRepository` (`src/ApexPost.Application/Persistence/`) and `SqlTrackedShipmentRepository` (`src/ApexPost.Infrastructure/Persistence/Sql/`), sharing the request's context and `IUnitOfWork` |
| Migration | `20261107102433_AddTrackedShipments`, also appended to `sql/schema/ApexPostDb-idempotent.sql` |

### Schema

**`TrackedShipments`**

| Column | Type | Notes |
|---|---|---|
| `TrackingNumber` | `varchar(12)` | Primary key. `CK_TrackedShipments_TrackingNumber`: `APX-` and eight digits 0–9 |
| `RecipientName` | `nvarchar(100)` | |
| `RecipientPhone` | `varchar(20)` | |
| `RecipientAddress` | `nvarchar(200)` | |
| `RecipientCity` | `nvarchar(100)` | The list filter |
| `WeightKg` | `decimal(10,3)` | `CK_TrackedShipments_Weight`: greater than 0. The 0.1–70 kg range is a request rule (B4) |
| `Status` | `varchar(20)` | `CK_TrackedShipments_Status`: the six statuses |
| `RowVersion` | `rowversion` | Two status changes at the same time cannot both pass the transition rule |
| `CreatedAtUtc`, `CreatedBy`, `ModifiedAtUtc` | | The Week 3 audit columns |

**`TrackedShipmentScans`**

| Column | Type | Notes |
|---|---|---|
| `ScanId` | `varchar(42)` | Primary key: the device's `scan::{Guid}` ID. `CK_TrackedShipmentScans_ScanId` checks the shape |
| `TrackingNumber` | `varchar(12)` | Foreign key to `TrackedShipments`, cascade delete. `IX_TrackedShipmentScans_TrackingNumber` serves the scan history and the cascade |
| `ScanType` | `varchar(20)` | `CK_TrackedShipmentScans_ScanType`: the four Part A scan types |
| `CourierId` | `varchar(50)` | |
| `Location` | `nvarchar(200)` | |
| `ScannedAtUtc` | `datetime2(3)` | UTC |
| `Notes` | `nvarchar(500)`, null | |
| `CreatedAtUtc`, `CreatedBy`, `ModifiedAtUtc` | | The Week 3 audit columns |

### Rules in the domain

- **Status transitions:** each status moves only to the next one, and `OutForDelivery` moves to `Delivered` or `Failed`. A repeated, backward or skipped change is refused.
- **Deletion:** allowed only while the status is `Created` (`TrackedShipment.CanBeDeleted`).
- **Scans are history:** adding a scan never changes the status. A scan ID already recorded for the shipment is refused, compared ignoring case as the database column is.
- **No rejection for a final status:** a scan is accepted after the shipment is `Delivered` or `Failed`. A device can record a scan offline and upload it after the status has changed, and refusing it would lose real history. The upload's rejection reasons are a malformed scan ID, a scan ID stored under another shipment, and a scan whose tracking number does not match the route. A scan time in the future fails request validation with 400.
- **Identifiers:** the tracking number uses `[0-9]`, not `\d`, because `\d` also matches non-ASCII digits that a `varchar` column cannot hold.

## Alternatives considered

- **Add columns to the Week 3 `Shipments` table.** It would change an existing table and entity. Counter sales would then need recipient and status values they never collect.
- **Only new service methods over the existing tables.** There is nowhere to store a status, a recipient or a scan ID.
- **A second DbContext with its own migrations in a separate schema.** It would leave every Week 3 file untouched, but it goes against line 99's "existing DbContext". It would also need a second unit of work, a second error mapping and a second migration history.

## Consequences

- No Week 3 table, column, constraint, index, stored procedure, entity, configuration or migration changes. Two existing files change, by addition only:
  - `ApexPostDbContextModelSnapshot.cs`: regenerated by `dotnet ef`, adding the two tables.
  - `SqlPersistenceRegistration.cs`: one line registering `ITrackedShipmentRepository`.
- Rolling the migration back runs `DROP TABLE [TrackedShipmentScans]`, `DROP TABLE [TrackedShipments]` and deletes its history row, nothing else.
- Two Week 3 tests list the whole schema and so must include the new objects: `Migrations_RevertToNothingAndReapply` (table count) and `Database_HasExactlyTheDesignedIndexes` (index list).
- No index beyond the keys and the foreign key is added. As in Week 3, more indexes need measured evidence.
- Tracked shipments are not linked to the counter's `Shipments` table. Nothing in Part B asks for that link.
