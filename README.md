# ApexPost Counter Platform

ApexPost is a .NET 10 / C# 14 ASP.NET Core MVC application for postal branch counter operations. It uses a layered Domain, Application, Infrastructure, and Web architecture, with SQL Server through EF Core 10 as its system of record.

## Projects

- `src/ApexPost.Domain` — entities, value objects, rules, and reporting.
- `src/ApexPost.Application` — use cases and persistence/security/audit abstractions.
- `src/ApexPost.Infrastructure` — the EF Core SQL Server persistence layer (`ApexPostDbContext`, mappings, migrations, repositories and stores). The older JSON stores remain in the code, but the application no longer uses them.
- `src/ApexPost.Web` — MVC controllers and views, authentication, middleware, and composition root.
- `tests/*` — Domain, Application, Infrastructure, and Web integration tests.

## Implemented workflows and protections

- **Counter sales:** one transaction can mix several goods lines, a shipping service and money orders. Each sale records its counter terminal, clerk, payment method and time (`OccurredAtUtc`). Stock, shipping rates, parcel limits, tax and duplicate submissions are all checked inside one atomic update. Active Counter Clerks and Branch Managers sell at their home branch. After a sale, the clerk sees its details (read-only) and, for shipping lines, a printable label preview at `/Transactions/{id}/label`.
- **Catalogue:** `/Catalogue` lists every product (SKU, category, price, tax category) with stock on hand at the signed-in staff member's branch. Products come from the reference-data cache; stock is read live.
- **Money orders:** issued as basket lines, with the fee taken from configuration. Above the configured threshold, a system identity-verification service decides the result (a simulated provider with a timeout), and only the masked document number is kept.
- **Voids and refunds:** Branch Managers of the same branch can void open or paid sales and refund paid sales, with a reason. Stock is returned and money orders are cancelled exactly once.
- **Stock and reports:**
  - Branch Managers adjust stock.
  - Branch Managers see daily sales by date and low stock.
  - Auditors and Administrators see shipping volume and the money-order and identity-verification report.
  - The branch dashboard loads its data in parallel.
- **Notifications:** domain events are published only after a change is saved. Simulated notifications for low stock, high-value money orders, voids and refunds are written as warning log entries.
- **Partner API:** `GET /integration/parcel-status/{shipmentId}` is protected by `[RequireApiKey]` metadata (constant-time key check, IP allowlist, partner rate limit) and CORS for the tracking widget's origin. It returns stored shipment facts, the transaction status and a simulated courier status.
- **Security:**
  - Cookie sign-in, checked against the staff record on every request.
  - Policies: `BranchManagerOnly` and `SameBranchOnly`.
  - IP allowlist for admin pages; antiforgery on every form.
  - Security headers via `OnStarting`; HSTS outside Development.
  - Correlation IDs, Problem Details errors and maintenance mode; `/health` stays reachable.
- **Filters:** opt-in `[Audited]` business-action audit with real status codes, a validation filter for MVC forms and API endpoints, and a slow-action filter.
- **Persistence:** SQL Server through EF Core 10, with repositories and a Unit of Work. Credentials and audit entries are tables too. See [DESIGN.md](DESIGN.md#44-sql-server-schema).
- **Demo:** a one-command demo with documented logins, described in the next section.

## Run the demo

Requirements: the .NET 10 SDK and a trusted HTTPS development certificate (`dotnet dev-certs https --trust`). The sign-in cookie is HTTPS-only.

From the repository root, run:

```powershell
./scripts/run-demo.ps1
```

The script:

1. Stores a randomly generated demo password in user-secrets as `DemoData:StaffPassword`, unless one is already set, and prints it.
2. Applies the EF Core migrations to the database named in `ConnectionStrings:ApexPostDb`.
3. Starts the app with the `demo` launch profile at <https://localhost:7034>. The profile uses the Development environment and sets `DemoData:Initialize=true`.

Create the database first with `./scripts/setup-database.ps1` (see [SQL Server setup](#sql-server-setup)).

On first start the app seeds the fictional demo data into the empty database, then creates sign-in credentials for the 20 demo staff. Only PBKDF2 hashes are written to `StaffCredentials`; the password itself is never written to the repository.

Demo logins (all use the password the script prints):

| Staff ID | Role | Branch |
|---|---|---|
| `demo-staff-01` | Counter Clerk | DM01 |
| `demo-staff-02` | Branch Manager | DM01 |
| `demo-staff-03` | Auditor | DM01 |
| `demo-staff-04` | Administrator | DM01 |
| `demo-staff-05` – `demo-staff-20` | The same four roles, repeated in that order | DM02 – DM05 |

Other options:

- **Use your own password:** run `./scripts/run-demo.ps1 -Password '<your demo password>'`.
- **Start over:** `-Reset` reverts all migrations, which **deletes all data in that database**, so the seed and logins are recreated. You need this for a changed password to take effect, because existing logins are never overwritten.
- **Without the script:** run `dotnet user-secrets set DemoData:StaffPassword "<your demo password>" --project src/ApexPost.Web`, then `dotnet run --project src/ApexPost.Web --launch-profile demo`.

`DemoData:StaffPassword` is only accepted in the Development environment; startup fails if it is set anywhere else. If demo data is enabled without it, the app starts but logs a warning, and the demo staff cannot sign in.

### Workflows that need your configuration

Sign-in, the dashboard, transaction history, details, voids and refunds, stock adjustment and administration work on the seed alone. Some workflows stay disabled until you supply the business values they need. The project ships no defaults for these, so choose your own values and set them with user-secrets, for example:

```powershell
dotnet user-secrets set "Transactions:TaxRates:ZeroRated" "<rate>" --project src/ApexPost.Web
```

| Workflow | Settings |
|---|---|
| New counter sales | `Transactions:TaxRates:<TaxCategory>` for the categories you sell, and `Transactions:PaymentMethods:0`, `:1`, … |
| Shipping lines | `Shipping:TaxCategory`, `Shipping:MaximumParcelWeightKilograms:<ServiceLevel>` (the seeded zones are `Standard`) |
| Money orders | `MoneyOrders:HighValueThreshold:Amount` and `:Currency`, `MoneyOrders:Fee:Amount` and `:TaxCategory`, `MoneyOrders:Verification:AcceptedDocumentTypes:0`, … and `MoneyOrders:Verification:TimeoutSeconds` |
| Low-stock report and alerts | `Reports:LowStockThreshold` |
| Partner parcel-status API | see below |

### Partner API key

`GET /integration/parcel-status/{shipmentId}` requires the `X-API-Key` header. Keep the key in user-secrets (or an environment variable in deployment), never in source:

```powershell
dotnet user-secrets set "ApiSecurity:ApiKey" "<your partner key>" --project src/ApexPost.Web
dotnet user-secrets set "ApiSecurity:PartnerRateLimit:PermitLimit" "<requests per window>" --project src/ApexPost.Web
dotnet user-secrets set "ApiSecurity:PartnerRateLimit:WindowSeconds" "<window length>" --project src/ApexPost.Web
```

When a key is set, startup requires the partner rate limit. Without a key, the endpoint fails closed with 500.

Sample request:

```powershell
curl.exe -H "X-API-Key: <your partner key>" https://localhost:7034/integration/parcel-status/<shipmentId>
```

## SQL Server setup

The application requires SQL Server and stops at startup if `ConnectionStrings:ApexPostDb` is not set. This section sets up the local database.

Requirements: a local SQL Server instance, `sqlcmd`, and the `dotnet-ef` tool.

From the repository root, run:

```powershell
./scripts/setup-database.ps1
```

The script:

1. Creates the data folder (`E:\SqlData\ApexPost` by default; change it with `-DataDirectory`) and gives the SQL Server service account access.
2. Creates an empty `ApexPostDb` from [sql/setup/create-database.sql](sql/setup/create-database.sql). An existing database is left untouched.
3. Stores the connection string in user-secrets as `ConnectionStrings:ApexPostDb`, unless one is already set. It uses Windows authentication and trusts the local self-signed certificate, so it is for development only.

### Set up the database with SSMS instead

Use these steps if you prefer SQL Server Management Studio to the script. **These instructions have not yet been independently tested**: `ApexPostDb` was created with the script above, not through SSMS.

1. Create the folder `E:\SqlData\ApexPost`. In its **Properties** > **Security** tab, give `NT Service\MSSQLSERVER` **Modify** access.
2. In SSMS, connect to server `.` with **Windows Authentication**. If the connection fails with a certificate error, tick **Trust server certificate**.
3. Open [sql/setup/create-database.sql](sql/setup/create-database.sql), and turn on **Query** > **SQLCMD Mode**.
4. Add these two lines at the top of the query window, without saving them to the file:

   ```sql
   :setvar DatabaseName ApexPostDb
   :setvar DataDirectory "E:\SqlData\ApexPost"
   ```

5. Execute the query. The results list the data and log files, which should both be in `E:\SqlData\ApexPost`.
6. From the repository root, store the connection string:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:ApexPostDb" "Server=.;Database=ApexPostDb;Integrated Security=True;Encrypt=True;TrustServerCertificate=True" --project src/ApexPost.Web
   ```

### Create the tables

The schema comes from the EF Core migration `InitialCreate`. The application does not migrate itself at startup, so this step is required before the first run. `./scripts/run-demo.ps1` does it for you. The tool's design-time factory holds no connection string, so name the target database with `--connection`:

```powershell
dotnet ef database update --connection "Server=.;Database=ApexPostDb;Integrated Security=True;Encrypt=True;TrustServerCertificate=True" --project src/ApexPost.Infrastructure --startup-project src/ApexPost.Web --context ApexPostDbContext
```

Or apply the idempotent script, which is safe to run more than once. `sqlcmd` needs `-I` because the schema has filtered indexes:

```powershell
sqlcmd -S . -E -C -I -b -d ApexPostDb -i sql/schema/ApexPostDb-idempotent.sql
```

To regenerate the script after a schema change:

```powershell
dotnet ef migrations script --idempotent --project src/ApexPost.Infrastructure --startup-project src/ApexPost.Web --context ApexPostDbContext --output sql/schema/ApexPostDb-idempotent.sql
```

The schema, ER diagram and constraints are described in [DESIGN.md](DESIGN.md#44-sql-server-schema).

To check connectivity, start the app with `dotnet run --project src/ApexPost.Web --launch-profile https` and open <https://localhost:7034/health/database>. It answers `Healthy`, or `Unhealthy` with status 503. The endpoint exists only in Development with a connection string configured, because each request opens a database connection. `/health` does not depend on the database.

Tests never use `ApexPostDb`. They create a disposable `ApexPost_Test_*` database and drop it when the run ends.

To repeat the performance evidence on a disposable `ApexPostDb_Perf` database, run `./scripts/run-performance.ps1`. See [PERFORMANCE.md](PERFORMANCE.md).

### T-SQL scripts

The reports, atomic sale, stored procedures and concurrency demos are in [sql/](sql/), and are explained in [DESIGN.md](DESIGN.md#47-t-sql-reports-procedures-and-concurrency). The stored procedures are deployed by the migrations above.

To run them all on a disposable database and save the output to [docs/evidence/m4](docs/evidence/m4/README.md):

```powershell
./scripts/run-sql-demos.ps1
```

## Configuration

`src/ApexPost.Web/appsettings.json` documents non-secret settings. The database connection string is `ConnectionStrings:ApexPostDb`, kept in user-secrets. By default the application neither seeds data nor creates credentials. The Web project has a `UserSecretsId`, so `dotnet user-secrets` works for local secrets in Development.

Production operators must explicitly provide business configuration as applicable:

- `Transactions:TaxRates:<TaxCategory>` and `Transactions:PaymentMethods`
- `Shipping:TaxCategory` and `Shipping:MaximumParcelWeightKilograms:<ShippingServiceLevel>`
- shipping rates in the `ShippingRates` table, per destination zone and service level
- `MoneyOrders:HighValueThreshold:Amount` and `:Currency`, `MoneyOrders:Fee:Amount` and `:TaxCategory`, `MoneyOrders:Verification:AcceptedDocumentTypes` and `:TimeoutSeconds` (plus optional `:SimulatedLatencyMilliseconds`)
- `Reports:LowStockThreshold`, which is used by the low-stock report and the low-stock notification
- partner settings, supplied outside source control:
  - `ApiSecurity:ApiKey`
  - `ApiSecurity:PartnerRateLimit:PermitLimit` and `:WindowSeconds`
  - `ApiSecurity:AllowedIpAddresses`
  - `ParcelTracking:AllowedOrigins`
- `AdminSecurity:AllowedIpAddresses` for the administration pages
- optionally `Courier:SimulatedTrackingStatus`, `Courier:TimeoutSeconds` and `Courier:MaxConcurrentLookups`; without them the parcel-status response reports the courier as `NotConfigured`

No production prices, tax rates, payment methods, parcel limits, fees, or money-order and low-stock thresholds are supplied as defaults. Missing required settings fail closed for the affected workflow, and partial or invalid settings stop startup. An empty IP allowlist switches that filter off, so production must set both allowlists.

The partner endpoint is protected by `[RequireApiKey]` endpoint metadata. `ApiSecurity:ProtectedPathPrefixes` can additionally protect other paths by prefix. Requests must pass the API-key and IP checks first, then share a fixed-window bucket keyed by the SHA-256 digest of the validated key. Because there is one shared key, all partners share one bucket. Client-supplied partner identity headers are ignored. `ParcelTracking:AllowedOrigins` is an exact-origin array; it defaults to empty (no cross-origin access). Wildcards and credentialed CORS are not supported.

### Optional fictional demo data

The single seed source is `DemoDataInitializer` (`src/ApexPost.Infrastructure/Persistence/DemoDataInitializer.cs`). It contains:

- 5 branches, each with 2 counter terminals
- 20 staff, covering every role in each branch
- 30 products
- stock for each product at every branch (150 rows)
- 10 shipping zones with rates
- 210 paid historical sales with payments, one every six hours back from seeding time

The sales are processed through the same domain service as real sales, so they deduct stock and the reports have recent dates.

Set `DemoData:Initialize` to `true` to seed an empty database. The setting defaults to `false`. Seeding runs in one commit, happens only when no branches, staff or products exist, and never merges demo values into existing data.

All seeded people and business values are marked or named `DEMO`. The seed contains no passwords, hashes, secrets or identity-document numbers; demo credentials are written only to `StaffCredentials`, as described above. Demo prices, shipping rates and zero tax rates are illustrative and must not be used as production values. Parcel limits, money-order thresholds and fees, and low-stock thresholds are not seeded.

## Documentation and sample records

- [Design](DESIGN.md): architecture and domain diagrams, pipeline order, middleware versus filters, threats and mitigations, auditing, concurrency, C# 14 features, SOLID mapping
- [Request flow](docs/architecture.md): a counter sale end to end
- [Architecture Decision Records](docs/adr/)
- [Audit sample](data/AUDIT-SAMPLE.json) — the 12 lines of `audit-events.jsonl` written during a local diagnostic run against seeded demo data (sign-in, stock adjustment, a manager's and a clerk's sale with their transaction IDs, void and refund, including rejected attempts), exported unchanged as a JSON array; not application state.

## Build and test

From the repository root:

```powershell
dotnet build ApexPost.slnx
dotnet test ApexPost.slnx
```

The SQL tests need the local default SQL Server instance (`.`), Windows authentication with permission to create databases, and the `E:\SqlData\ApexPost` folder, because disposable test databases keep their files in `E:\SqlData\ApexPost\Tests`.
