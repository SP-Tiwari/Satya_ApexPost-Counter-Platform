# M7 Postman evidence

A scripted Postman check of the API and error responses, last run on 2026-11-01 at 15:42 UTC with **Postman CLI 1.75.0**. It is automated, and **no Postman desktop demonstration was performed**. The test suites remain the main evidence. All data is fictional. `ApexPostDb` was not used.

**This run replaces the earlier ones.** The application was built from the working tree, including the uncommitted Milestones 1–5 changes. The original 50 requests are unchanged, and 15 were added for route tampering, refunds, stock adjustment, every administration command, the seeded reports and masked identity values. There is no environment file: the runner generates the API key and password for each run and never saves them.

| File | Shows |
|---|---|
| `ApexPost-week3.postman_collection.json` | 65 requests with their assertions. It holds no secrets, and imports into the Postman app |
| `run-postman.ps1` | Creates the disposable database, starts the application, runs the collection, then cleans up |
| `postman-run.txt` | Saved output of the last run, with secrets removed, and the database state afterwards |

## Run it

```powershell
./docs/evidence/m7/run-postman.ps1
```

It needs SQL Server, the E: drive and the Postman CLI. It builds the working tree into `out/postman`, creates `ApexPost_Postman_<guid>` in `E:\SqlData\ApexPost\Tests` from `sql/schema/ApexPostDb-idempotent.sql`, and starts the application on `http://127.0.0.1:5199` with demo data. The API key and staff password are generated for the run and never saved. Afterwards it stops only the process it started and drops that database.

## Postman desktop app

**No desktop demonstration has been performed, and this workflow is untested.** The collection has only run through the Postman CLI with `run-postman.ps1`, which is the reproducible route. This section lists what the collection needs, not steps known to work.

The `demo` profile alone is not enough, because the collection's sale includes a parcel and pays with `Cash`. It also needs:

| Setting | Value in `run-postman.ps1` | Why |
|---|---|---|
| `Transactions:PaymentMethods` | `Cash` | The sale posts this method. The `demo` profile holds slot `:0` for `DEMO Cash`, and its value outranks user-secrets |
| `Transactions:TaxRates:Standard` | `0.18` | Tax on the parcel |
| `Shipping:TaxCategory` | `Standard` | Shipping lines |
| `Shipping:MaximumParcelWeightKilograms:Standard` | `30` | The sale and its over-limit request |
| `Reports:LowStockThreshold` | `5` | The low-stock request |
| `MoneyOrders:HighValueThreshold:Amount`, `:Currency` | `10000`, `INR` | The money-order screen |
| `ApiSecurity:ApiKey` | Generated | The partner requests |
| `ApiSecurity:PartnerRateLimit:PermitLimit`, `:WindowSeconds` | `100`, `60` | Required once a key is set |
| `Authentication:SignInRateLimit:PermitLimit` | `50` | The collection makes 6 sign-in posts, and the default limit is 5 per minute |

The collection also needs `baseUrl`, `apiKey` and `password` variables, and a fresh database, because its totals and stock assertions assume the seed. Each request uses values saved by the one before it, so the requests must run in order.

**Not known:** the sign-in cookie is HTTPS-only, while `run-postman.ps1` uses HTTP. How the desktop app behaves against `https://localhost:7034`, including its certificate handling, has not been tried.

## Result

65 of 65 requests ran with 0 failed, and 188 of 188 assertions passed. No request returned a 5xx status.

| Assignment rule | Request | Result |
|---|---|---|
| Part G: not found is 404 | Unknown shipment on the partner API | 404 Problem Details, with `correlationId` equal to the header |
| Part G: not found is 404 | Unknown staff, unknown branch | 404 view |
| Part G: not found is 404 | Unknown transaction, browser route | 404 with an empty body |
| Part G: domain errors are 400 | Invalid shipment ID | 400 Problem Details |
| Part G: domain errors are 400 | Empty transaction ID, through the exception handler | 400 Problem Details |
| Part G: domain errors are 400 | Invalid branch code | 400 view with no parameter name |
| Part G: unique violations are 409 | Existing SKU | 409 view, "already exists" |
| Plan: state conflicts are 409 | Second void of a sale; a parcel over the weight limit | 409 |
| Part G: errors expose nothing internal | Every response of 400 or above | No exception names, stack traces, SQL names or paths |
| Part H: a sale persists | Clerk sale with a parcel, then the summary, label and partner status | Paid, and the partner reads the new shipment from SQL Server |
| Part H: a void requires a Branch Manager | Clerk's void | Redirect to `/Account/AccessDenied`, which is 403. The sale stays Paid |
| Part H: a void requires a Branch Manager | Branch Manager's void | Voided, the partner sees it, and stock is back to 10 |
| Part H: reports return expected totals | Daily sales before the sale, after it and after the void | Rose by the sale total of 26.455, then returned to the baseline |
| Part G: reports | Low stock | Lists `DEMO-SKU-001`, which is below the threshold of 5 |
| Part F: over-posting | A posted `transactionId` on a void of an already-voided sale | 409 on the addressed sale. The sale named in the form stays Paid, and the audit row names the addressed sale |
| Refund equals the stored payment | Refund of a seeded shipment sale, and the void above, read back by the runner | Paid 17.50, refunded 17.50; paid 26.46, refunded 26.46 |
| Part F: command-backed endpoints | Stock adjustment, and creating a branch, staff member and product, updating staff, setting branch status | 302, with a `Succeeded` audit row each, and the new records listed |
| Part G: seeded reports | Shipping volume and branch auditor | All three service levels, and all five branches |
| Part A: masked identity documents | Money orders page, and the runner's count of stored rows | Masked values only, 15 of 15 rows masked |
| Week 2: partner and sign-in security | No key, wrong key, wrong password, no antiforgery token, anonymous report | 401, 401, 401, 400, redirect to sign-in |

## What this does not cover

- **The Postman desktop app.** The collection ran through the Postman CLI, so nobody opened it in the app or watched these responses there.
- **The two-connection 409.** One sequential run cannot race itself. `TransactionsControllerConcurrencyTests` is the evidence.
- **A real 500.** Nothing on a healthy application raises one. `UnexpectedFailureOnARealRoute_ReturnsSafeProblemDetailsAndIsLogged` is the evidence.
- **The partner and sign-in rate limits.** The run raises both limits so that it isn't stopped by them, and doesn't check the 429 responses.
- **Issuing money orders.** The collection reads seeded money orders but does not issue one, because that needs more settings. Automated tests cover it.
- **Request DTO rejections.** Every administration form in this collection passes DTO validation, so that the request reaches the service. `TransactionsControllerAdministrationFormTests` covers invalid and over-posted forms.
- **The `demo` profile and `run-demo.ps1`.** The runner starts the built application with its own settings. `DemoProfileTests` and `RunDemoScriptTests` cover those.
