# Week 4 submission checklist

This page maps each marked criterion of the Week 4 assignment (Parts A, B, C and D, 100 marks) to its implementation, tests, documentation and evidence. The detailed traceability tables are kept where they already are, and are linked from each row.

**Verified by** says how each criterion was checked:
- **Live:** shown against the running application, and recorded in the linked evidence.
- **Tests:** covered only by automated tests. Where a criterion is partly live and partly tests, the row says which part is which.

The latest full run, on 2026-11-09 at 21:25 UTC, built with 0 warnings and 0 errors and passed 1,575 of 1,575 tests. It is recorded in [README.md](../README.md#build-and-test).

## Marks overview

| Part | Criteria | Marks |
|---|---|---|
| A: Couchbase Lite offline store | 6 | 25 |
| B: RESTful API | 5 | 25 |
| C: Swagger / OpenAPI | 6 | 25 |
| D: API security and mobile API integration | 7 | 25 |
| **Total** | **24** | **100** |

## Part A: Couchbase Lite offline store (25 marks)

Code paths are under `src/ApexPost.Courier.Offline` and tests under `tests/ApexPost.Courier.Offline.Tests`, unless a path says otherwise. The full table is in [README.md: Courier offline storage](../README.md#courier-offline-storage-week-4-part-a), and the evidence in [docs/evidence/week4-part-a](evidence/week4-part-a/README.md).

| Criterion | Marks | Implementation | Tests | Documentation and evidence | Verified by |
|---|---|---|---|---|---|
| Database lifecycle and document model | 5 | `Persistence/CourierDatabase.cs` (`apexpost_courier`, default collection, `IDisposable`); `Domain/ShipmentScan.cs`, `Domain/ShipmentScanId.cs` (`scan::{Guid}`), `Persistence/ShipmentScanDocument.cs` | `CourierDatabaseTests`, `ShipmentScanTests` | [ADR 0027](adr/0027-couchbase-lite-courier-offline-store.md), [harness session 1](evidence/week4-part-a/harness-session-1.txt) | Live and tests |
| Repository and key-value CRUD | 5 | `Application/IScanRepository.cs`, `Persistence/CouchbaseScanRepository.cs`; the domain references no Couchbase types | `CouchbaseScanRepositoryTests`, `ArchitectureTests` | [harness sessions](evidence/week4-part-a/README.md) | Live and tests |
| SQL++ queries, parameters and indexes | 6 | `Persistence/ScanQueries.cs` (three parameterized queries); value indexes on `trackingNumber` and `syncStatus` in `CourierDatabase` | `ScanQueryTests` | [query plans and tests](evidence/week4-part-a/query-plans-and-tests.txt), screenshots [01](evidence/week4-part-a/screenshots/01-query-by-tracking-number.png), [02](evidence/week4-part-a/screenshots/02-query-pending.png), [03](evidence/week4-part-a/screenshots/03-query-scans-per-type.png) | Live and tests |
| Outbox / offline persistence strategy | 4 | `ShipmentScan.Record` (saved locally as `Pending`), `Application/OutboxService.cs`, `Application/IShipmentUploader.cs`, `Uploading/FakeShipmentUploader.cs` | `OutboxServiceTests` | [OFFLINE-DESIGN.md](../OFFLINE-DESIGN.md#1-why-scans-are-saved-locally-first), [harness session 1](evidence/week4-part-a/harness-session-1.txt) | Live and tests |
| Change listener and cleanup | 3 | `CourierDatabase.StartChangeListener`, `StopChangeListener`, `Dispose` (token removed) | `ChangeListenerTests` | [screenshot 04](evidence/week4-part-a/screenshots/04-listener-log-during-sync.png), [when the listener reports](evidence/week4-part-a/README.md#when-the-listener-reports) | Live and tests |
| Design note | 2 | [OFFLINE-DESIGN.md](../OFFLINE-DESIGN.md): saving locally first, conflict handling (last-write-wins and the server deciding), why the embedded database is not shared by processes | — | [single-owner check](evidence/week4-part-a/single-owner.txt) | Document |

The console harness menu is `src/ApexPost.Courier.Harness` (`CourierHarnessTests`).

## Part B: RESTful API (25 marks)

Code paths are under `src/ApexPost.Api` and tests under `tests/ApexPost.Api.IntegrationTests`, unless a path says otherwise. The endpoint and status-code table is in [API-NOTES.md](../API-NOTES.md), and the evidence in [docs/evidence/week4-part-b](evidence/week4-part-b/README.md).

| Criterion | Marks | Implementation | Tests | Documentation and evidence | Verified by |
|---|---|---|---|---|---|
| Endpoint design and correct HTTP verbs | 6 | `Controllers/ShipmentsController.cs`, `Controllers/ShipmentScansController.cs`, `Controllers/ShipmentRoutes.cs`; status transitions in `src/ApexPost.Domain/Entities/TrackedShipment.cs` (`ChangeStatus`) | `ShipmentEndpointsTests`, `ShipmentListTests`, `ShipmentStatusTests`, `tests/ApexPost.Domain.Tests/Entities/TrackedShipmentTests.cs` | [API-NOTES.md: endpoints](../API-NOTES.md#endpoints), [status transitions](../API-NOTES.md#status-transitions), [http-run.txt](evidence/week4-part-b/http-run.txt) | Live and tests |
| Correct status codes and Location header | 5 | `CreatedAtAction` in `ShipmentsController.Create`; `Controllers/ShipmentProblems.cs` | `ShipmentEndpointsTests`, `ErrorHandlingTests` | [API-NOTES.md: status codes](../API-NOTES.md#status-codes), screenshots [201](evidence/week4-part-b/screenshots/01-create-201-location.png), [409](evidence/week4-part-b/screenshots/03-conflict-409.png) | Live and tests |
| DTOs and payload validation (including the custom rule) | 6 | `Models/ShipmentRequests.cs`, `Models/ScanUploadRequest.cs`, `Models/ShipmentMapping.cs`; custom rule `Validation/NotInFutureAttribute.cs` | `ShipmentEndpointsTests`, `ScanUploadTests` | [API-NOTES.md: request DTOs and validation](../API-NOTES.md#request-dtos-and-validation), [400 screenshot](evidence/week4-part-b/screenshots/02-validation-400.png) | Live and tests |
| ProblemDetails and global exception handler | 4 | `AddProblemDetails` and `AddExceptionHandler<ApiExceptionHandler>` in `Program.cs`; `Errors/ApiExceptionHandler.cs` (no stack trace outside Development) | `ErrorHandlingTests` | [http-run.txt](evidence/week4-part-b/http-run.txt) | Live and tests |
| Thin controllers, DI and idempotent scan upload | 4 | `src/ApexPost.Application/Services/TrackedShipmentApplicationService.cs` (`UploadScansAsync`: `accepted`, `duplicatesSkipped`, `rejected`); minimal hosting in `Program.cs` | `ScanUploadTests` | [API-NOTES.md: idempotent scan upload](../API-NOTES.md#idempotent-scan-upload), [ADR 0028](adr/0028-tracked-shipments-storage.md) | Live and tests |

The `.http` file is [src/ApexPost.Api/ApexPost.Api.http](../src/ApexPost.Api/ApexPost.Api.http).

## Part C: Swagger / OpenAPI (25 marks)

Code paths are under `src/ApexPost.Api` and tests under `tests/ApexPost.Api.IntegrationTests`, unless a path says otherwise. The evidence is in [docs/evidence/week4-part-c](evidence/week4-part-c/README.md).

| Criterion | Marks | Implementation | Tests | Documentation and evidence | Verified by |
|---|---|---|---|---|---|
| OpenAPI generation and document transformer | 4 | `AddOpenApi` and `MapOpenApi` in `Program.cs`; `OpenApi/ApiDocumentTransformer.cs` | `OpenApiDocumentationTests` | [README.md: API documentation](../README.md#api-documentation-openapi-and-swagger-ui-week-4-part-c) | Live and tests |
| Swagger UI, Development-only setup and README explanation | 3 | `UseSwaggerUI` inside `IsDevelopment()` in `Program.cs` | `OpenApiDocumentationTests` | [README.md: API documentation](../README.md#api-documentation-openapi-and-swagger-ui-week-4-part-c), [Swagger UI operations](evidence/week4-part-c/screenshots/c3-00-swagger-ui-operations.png) | Live and tests |
| XML comments, response types, tags and operationIds | 5 | `GenerateDocumentationFile` in `ApexPost.Api.csproj`; `[ProducesResponseType]`, `[Tags]`, `[EndpointSummary]` and route names in the controllers | `OpenApiDocumentationTests` | [API-NOTES.md: OpenAPI metadata](../API-NOTES.md#openapi-metadata-week-4-part-c-c3), [Try it out run](evidence/week4-part-c/tryitout-run.txt) | Live and tests |
| v1/v2 versioning with deprecation | 6 | `[ApiVersion("1.0", Deprecated = true)]` on the v1 controllers; `Controllers/V2/ShipmentsController.cs`, `Models/V2/`; `ReportApiVersions` in `Program.cs` | `ApiVersioningTests` | [API-NOTES.md: API versioning](../API-NOTES.md#api-versioning-week-4-part-c-c4), [VERSIONING.md](../VERSIONING.md), [http-run.txt](evidence/week4-part-c/http-run.txt) | Live and tests |
| Version-specific documents | 3 | One `AddOpenApi` document per API Explorer group in `Program.cs`; Swagger UI labels "v1 (deprecated)" and "v2"; [contracts/v1.json](../contracts/v1.json), [contracts/v2.json](../contracts/v2.json) | `OpenApiDocumentationTests`, `PublishedContractTests` | [API-NOTES.md: version-specific documents](../API-NOTES.md#version-specific-openapi-documents-week-4-part-c-c5), screenshots [v2](evidence/week4-part-c/screenshots/c5-00-swagger-ui-v2.png), [v1](evidence/week4-part-c/screenshots/c5-01-swagger-ui-v1-deprecated.png) | Live and tests |
| Generated SDK and console consumer | 4 | `src/ApexPost.Client` (NSwag, [nswag.json](../src/ApexPost.Client/nswag.json), `Generated/ApexPostApiClient.cs`); `src/ApexPost.Client.Consumer` (`ApexPostApiClientRegistration.cs`, `ShipmentClientDemo.cs`) | `ClientSdkTests` | [README.md: Client SDK](../README.md#client-sdk-week-4-part-c-c6), [consumer run](evidence/week4-part-c/consumer-run.txt) | Live and tests |

## Part D: API security and mobile API integration (25 marks)

API paths are under `src/ApexPost.Api`. Client paths are under `src/ApexPost.Courier.Sync`, with tests in `tests/ApexPost.Courier.Sync.Tests`, unless a path says otherwise. The detailed tables are in [SECURITY.md](../SECURITY.md#requirement-traceability), [RESILIENCE.md](../RESILIENCE.md#requirement-traceability) and [README.md: Courier console client](../README.md#courier-console-client-week-4-part-d). The evidence is in [docs/evidence/week4-part-d](evidence/week4-part-d/README.md).

### D-I. API security (10 marks)

| Criterion | Marks | Implementation | Tests | Documentation and evidence | Verified by |
|---|---|---|---|---|---|
| JWT authentication and role authorization (401/403, courierId check) | 5 | `Security/JwtAuthentication.cs`, `Security/DevelopmentTokenEndpoint.cs` (`POST /dev/token`), `Security/ApexPostClaims.cs`, `Security/RequireOwnScansAttribute.cs`; `[Authorize(Roles = …)]` on the controllers | `SecurityTests` (including `AuthorizationMatrix`) | [SECURITY.md: D1](../SECURITY.md#authentication-jwt-bearer-tokens-d1), [D2](../SECURITY.md#authorization-d2), [required checks](evidence/week4-part-d/README.md#required-checks), [http-run.txt](evidence/week4-part-d/http-run.txt) | Live and tests |
| Partner API key and HTTPS/HSTS | 3 | `Security/PartnerApiKeyMiddleware.cs`, `Controllers/V2/PartnerShipmentsController.cs`; `UseHttpsRedirection` everywhere and `UseHsts` outside Development in `Program.cs`; `OpenApi/SecuritySchemeTransformer.cs` | `SecurityTests` (including `Hsts_IsSentOutsideDevelopmentOnly`) | [SECURITY.md: D3](../SECURITY.md#partner-api-keys-d3), [D5](../SECURITY.md#transport-security-https-and-hsts-d5), [Swagger UI security schemes](evidence/week4-part-d/README.md#swagger-ui-security-schemes-and-lock-icons) | Partner key, HTTPS redirect and Swagger UI Authorize: live. HSTS: tests only, because it is off in Development |
| OAuth2 and transport security explanation in SECURITY.md | 2 | [SECURITY.md: D4](../SECURITY.md#oauth2-for-apexpost-d4) (Authorization Code + PKCE, Client Credentials, the identity provider, access and refresh tokens, why API keys are weaker, moving to a real identity provider) and [D5](../SECURITY.md#transport-security-https-and-hsts-d5) | — | — | Document |

### D-II. Mobile API integration (15 marks)

| Criterion | Marks | Implementation | Tests | Documentation and evidence | Verified by |
|---|---|---|---|---|---|
| IHttpClientFactory typed client and auth handler | 4 | `ApexApiClient.cs`, `CourierSyncRegistration.cs` (`AddHttpClient<ApexApiClient>()`, `ApexPostApi:BaseUrl`), `AuthHeaderHandler.cs` | `ApexApiClientTests`, `CourierSyncRegistrationTests`, `AuthHeaderHandlerTests` | [console session](evidence/week4-part-d/client/console-session.txt), [live run](evidence/week4-part-d/README.md#courier-console-client-live-run-d6d11) | Live and tests |
| Polly retry, circuit breaker and timeout (with logging) | 5 | `CourierResilience.cs` (retry 3, exponential with jitter; circuit breaker; 10 s per-attempt and 30 s overall timeouts; retry and state-change logging) | `ResilienceTests`; `tests/ApexPost.Api.IntegrationTests/CourierClientRetryTests.cs`; `tests/ApexPost.Courier.Console.Tests/CourierConsoleTests.cs` (banner) | [RESILIENCE.md](../RESILIENCE.md), [live run: required checks](evidence/week4-part-d/README.md#required-checks-1) | Retries, circuit opening, banner and recovery: live. Timeouts: tests only |
| JSON serialization and ProblemDetails handling | 2 | `CourierJsonContext.cs` (source-generated; camelCase, case-insensitive, enums as strings), `Contracts/ApiContracts.cs`, `ApiErrors.cs`, `ApiResult.cs` | `CourierJsonTests`, `ApexApiClientTests` (error, malformed-body and unknown-field cases) | [console session](evidence/week4-part-d/client/console-session.txt): the 404 Problem Details shown as a friendly message | Friendly 404 message: live. Null, unknown and malformed responses: tests only |
| End-to-end offline sync and SecureStorage | 4 | `ApiShipmentUploader.cs` (replaces the fake uploader), `SyncCoordinator.cs`, `NetworkStatus.cs`, `SyncState.cs`; `OutboxService.cs` in `src/ApexPost.Courier.Offline` (batches of 20); `DpapiTokenStore.cs`, `CourierSession.cs`; `src/ApexPost.Courier.Console/CourierConsole.cs` (screens) | `ApiShipmentUploaderTests`, `OfflineSyncTests`, `SyncCoordinatorTests`, `DpapiTokenStoreTests`, `SignInTests`; `OutboxServiceTests`; `tests/ApexPost.Api.IntegrationTests/CourierOfflineSyncTests.cs`; `CourierConsoleTests` | [OFFLINE-DESIGN.md: section 4](../OFFLINE-DESIGN.md#4-synchronization-with-the-api-part-d-d9), [SECURITY.md: courier console client](../SECURITY.md#courier-console-client-d6-d10), [live run](evidence/week4-part-d/README.md#courier-console-client-live-run-d6d11), [server check](evidence/week4-part-d/client/server-check.txt) | See below |

For the end-to-end offline sync row:
- **Live:** login, the DPAPI-encrypted token file, scans saved locally and synced, a 404 marked `Failed`, scans saved while the circuit is open, a 401 that clears the token and returns to Login, and Logout removing the token.
- **Tests only:**
  - The sync that starts when the network itself returns (`SyncCoordinatorTests.NetworkRestored_StartsASync_AfterTheReconnectDelay`).
  - Uploads in batches of 20 (`OutboxServiceTests.SyncPending_Uploads45Scans_InBatchesOf20_20And5_OldestFirst`).
- **Not demonstrated:** no airplane-mode run was recorded.

The `.http` file covers 401, 403, an invalid API key and a valid token: requests 43–61 in [ApexPost.Api.http](../src/ApexPost.Api/ApexPost.Api.http), recorded in [http-run.txt](evidence/week4-part-d/http-run.txt).

## Substitutions for the .NET MAUI app

Following the trainer's guidance, the courier app is a .NET console application, not a .NET MAUI app. No MAUI package or API is used. [ADR 0030](adr/0030-courier-console-client.md) records the decision.

| Assignment wording (MAUI) | Used instead | Where |
|---|---|---|
| `ApexPost.Courier` .NET MAUI app | `ApexPost.Courier.Console` (screens) and `ApexPost.Courier.Sync` (API client and sync) | `src/ApexPost.Courier.Console`, `src/ApexPost.Courier.Sync` |
| MAUI `SecureStorage` | `DpapiTokenStore`: Windows DPAPI, `CurrentUser` scope, stored in `%LOCALAPPDATA%\ApexPost\Courier\access-token.bin` | `DpapiTokenStore.cs`; [SECURITY.md](../SECURITY.md#courier-console-client-d6-d10) |
| MAUI `Connectivity.ConnectivityChanged` | `SystemNetworkStatus`, built on `NetworkChange` and `NetworkInterface`, feeding `SyncCoordinator` | `NetworkStatus.cs`, `SyncCoordinator.cs` |
| Separate base URLs for the Android emulator and Windows | One `ApexPostApi:BaseUrl` setting per environment (`appsettings.json`, the `ApexPostApi__BaseUrl` environment variable or `--ApexPostApi:BaseUrl=`). There is no Android client, so the emulator address does not apply | `CourierSyncRegistration.cs`, `src/ApexPost.Courier.Console/appsettings.json` |
| MAUI pages (Login, New Scan, Pending Scans, Sync Status) | Console menus with the same screens, plus Sync Now, Logout and Exit | `CourierConsole.cs` |

## Branches and merges

The assignment names the branches `week4-partA`, `week4-partB`, `week4-partC` and `week4-partD`, merged into `main`. The repository's default branch is `master`, and each part was developed on a `feature/` branch and merged through a pull request:

| Part | Branch | Pull request | Merge commit |
|---|---|---|---|
| A | `feature/week4-part-a-courier-offline` | #27 | `fe9d45d` |
| B | `feature/week4-part-b-rest-api` | #28 | `2b0b3c9` |
| Fix for the Part A test storage | `fix/courier-test-storage-on-e` | #29 | `28a08c4` |
| C | `feature/week4-part-c-openapi` | #30 | `2a1a8da` |
| D | `feature/week4-part-d-api-security` | #31 | `8cab515` |

This checklist and the demo-video preparation were added on `feature/week4-part-d-demo-video`, branched from `master` after #31.

## Deliverables

| Deliverable | Location |
|---|---|
| Part A: `OFFLINE-DESIGN.md`; screenshots of the query results and the listener log | [OFFLINE-DESIGN.md](../OFFLINE-DESIGN.md), [week4-part-a screenshots](evidence/week4-part-a/README.md#screenshots) |
| Parts B and C: `.http` file with success and failure requests for both versions | [ApexPost.Api.http](../src/ApexPost.Api/ApexPost.Api.http) |
| Parts B and C: `API-NOTES.md`, `README.md` (OpenAPI vs. Swagger UI), `VERSIONING.md`, `/contracts` | [API-NOTES.md](../API-NOTES.md), [README.md](../README.md#api-documentation-openapi-and-swagger-ui-week-4-part-c), [VERSIONING.md](../VERSIONING.md), [contracts/](../contracts/v2.json) |
| Parts B and C: `ApexPost.Client` and the console consumer | `src/ApexPost.Client`, `src/ApexPost.Client.Consumer` |
| Parts B and C: screenshots of Swagger UI v1 and v2, 400, 409, and 201 with Location | [week4-part-c evidence](evidence/week4-part-c/README.md), [week4-part-b evidence](evidence/week4-part-b/README.md) |
| Part D: the courier client and the updated API | See [Substitutions](#substitutions-for-the-net-maui-app) |
| Part D: `SECURITY.md` and `RESILIENCE.md` | [SECURITY.md](../SECURITY.md), [RESILIENCE.md](../RESILIENCE.md) |
| Part D: `.http` file covering 401, 403, an invalid API key and a valid token | Requests 43–61 in [ApexPost.Api.http](../src/ApexPost.Api/ApexPost.Api.http) |
| Part D: 3–5 minute demo video | Not stored in the repository. Its [script](evidence/week4-part-d/demo-video-script.md) and [checklist](evidence/week4-part-d/demo-video-checklist.md) list what it shows live and what only tests cover. It does not show airplane mode |
