# ApexPost API versioning

How the ApexPost Logistics API (`src/ApexPost.Api`) is versioned, where each version's contract is published, which changes are breaking, and how version 1 is retired. All data and dates are fictional.

## Versions

| Version | Base URL | Status | OpenAPI document (Development) | Published contract | Generated client |
|---|---|---|---|---|---|
| 1.0 | `/api/v1` | **Deprecated** since 2026-11-07; sunset planned for 2027-05-07 | `/openapi/v1.json` | [contracts/v1.json](contracts/v1.json) | None |
| 2.0 | `/api/v2` | **Current** | `/openapi/v2.json` | [contracts/v2.json](contracts/v2.json) | [src/ApexPost.Client](src/ApexPost.Client) |

- **How a version is chosen:** by the URL segment, `/api/v{version}/shipments` (`UrlSegmentApiVersionReader`). `/api/v1.0/...` is the same version as `/api/v1/...`. An unknown version, such as `/api/v3/...`, is a 404 Problem Details. There is no default version, so a URL without one, such as `/api/shipments`, is also a 404.
- **How versions are reported:** every response from a versioned action carries `api-supported-versions: 2.0` and `api-deprecated-versions: 1.0` (`ReportApiVersions`). The only exception is a 415, which routing answers before a version is selected.
- **How deprecation is shown:** `[ApiVersion("1.0", Deprecated = true)]`. Every operation in the v1 document is `deprecated: true`, the v1 document's description names version 2, and Swagger UI lists v1 as "v1 (deprecated)".
- **What the versions share:** both use the same `TrackedShipmentApplicationService`, rules and database, so a shipment created through one version is read and changed through the other. Only the DTOs and their mapping differ.

The implementation and its tests are described in [API-NOTES.md](API-NOTES.md#api-versioning-week-4-part-c-c4).

## Published contracts

`contracts/v1.json` and `contracts/v2.json` are the OpenAPI 3.1 documents the API generates, saved byte for byte as it serves them.

- **Where they come from:** the built-in generator, one document per API Explorer group name, served by the API on its `https` profile (`https://localhost:7280`) in Development. Their `servers` entry is therefore `https://localhost:7280/`. That entry names the host that served the document, not part of the contract. Until Week 4 Part D the documents were published from the former `http` profile, `http://localhost:5280/`. Part D removed that profile and redirects all HTTP to HTTPS.
- **Line endings:** [.gitattributes](.gitattributes) marks `contracts/*.json` as `-text`, so Git never converts the line endings and the files stay byte-identical to the served documents.
- **How they are kept accurate:** `tests/ApexPost.Api.IntegrationTests/PublishedContractTests.cs` runs with every test run.
  - `PublishedContract_MatchesTheDocumentTheApiGenerates` (v1, v2) compares each file with the document the API generates now, member by member, ignoring only `servers`. Any change to an operation, schema, description or the document info fails the test, which names the differing parts.
  - `PublishedContract_IsAValidOpenApiDocumentForItsVersion` (v1, v2) parses each file with `Microsoft.OpenApi` (no errors). It also checks the OpenAPI version, title, `info.version`, `servers`, and that every path is under the file's own version.
- **What reads them:** NSwag generates the version 2 client from `contracts/v2.json` ([src/ApexPost.Client/nswag.json](src/ApexPost.Client/nswag.json)), so regenerating the client does not need the API running. Partners can generate their own clients from the same files.

### Publishing a contract change

1. **Change the API.** The test run then fails in `PublishedContractTests` for each document that changed.
2. **Classify the change** with the rules below. A breaking change never goes into an existing version; it needs a new version, such as `v3`, with its own controller, DTOs and document.
3. **Publish both documents** from the running API. The API needs `ConnectionStrings:ApexPostDb` and a JWT signing key ([SECURITY.md](SECURITY.md#development-tokens)) to start, from User Secrets, but serving the documents uses neither the database nor a token.

   ```powershell
   # Terminal 1
   dotnet run --project src/ApexPost.Api --launch-profile https

   # Terminal 2, from the repository root
   Invoke-WebRequest https://localhost:7280/openapi/v1.json -UseBasicParsing -OutFile contracts/v1.json
   Invoke-WebRequest https://localhost:7280/openapi/v2.json -UseBasicParsing -OutFile contracts/v2.json
   ```

4. **Record the change** in [Changes](#changes) below.
5. **If `v2.json` changed, regenerate the client** with `dotnet tool restore` and then `dotnet nswag run src/ApexPost.Client/nswag.json` (no API needed). Then run the tests: `ClientSdkTests` checks the client against the v2 document.
6. **Commit together:** the API change, the contracts, this file and the regenerated client.

## What counts as a breaking change

A change is **breaking** when a client written against the published contract can fail without changing its own code:
- removing or renaming an operation, a route, a field or an enum value
- moving a field, or changing its JSON type, such as a number becoming a string
- making a request stricter: a new required field, a narrower range or pattern, or a value that was accepted being refused
- changing the status codes an operation returns for the same request, or the meaning of a field

A change is **non-breaking** when every existing request stays valid and every existing response field keeps its name, place, type and meaning:
- adding an operation, or an optional response field
- accepting more input than before
- adding response headers
- improving the document's descriptions or schema details without changing the JSON on the wire

Clients should ignore response fields they do not know. Version 2 relies on that for `latestScan`. `System.Text.Json` and the generated client do so by default.

## Changes

| # | Milestone | Change | Breaking? | Why |
|---|---|---|---|---|
| 1 | C4 | The Part B URLs moved under the version segment: `/api/shipments/...` became `/api/v1/shipments/...` | **Breaking** for Part B clients | Every request URL changes, and the old URLs are 404. The assignment requires the version in every URL. A Part B client changes only its base path; its requests and responses stay the same |
| 2 | C4 | Responses carry `api-supported-versions` and `api-deprecated-versions` | Non-breaking | Response headers are added; nothing a client reads changes |
| 3 | C4 | Version 1 is marked deprecated | Non-breaking | Version 1 behaves exactly as before; deprecation only announces its retirement (see the plan below) |
| 4 | C4, v2 | The shipment's recipient fields are grouped into `recipient { name, phone, address, city }` in every shipment response (GET, POST, PUT, PATCH and the list items) | **Breaking** | `recipientName`, `recipientPhone`, `recipientAddress` and `recipientCity` no longer exist at the top level, so a version 1 client reading them gets nothing |
| 5 | C4, v2 | `latestScan { scanType, location, scannedAtUtc }`, or `null` when there is no scan, is added to every shipment response | Non-breaking on its own | An added response field: a client that ignores unknown fields is unaffected. It is released only in version 2 because it ships with changes 4 and 6 |
| 6 | C4, v2 | `status` in shipment responses is the name, such as `"PickedUp"`, instead of the number `1` | **Breaking** | The JSON type changes from number to string, so a client reading it as a number fails |
| 7 | C4, v2 | `PATCH …/status` accepts the name, `{ "status": "PickedUp" }`, as well as the number | Non-breaking | The accepted input is widened: every version 1 request body is still valid. A client sends the same value it reads |
| 8 | C4, v2 | The v2 document describes the list filter `?status=` with the status names | Non-breaking | Both versions bind the filter with the same `ShipmentListQuery`, which accepts the name or the number (`ShipmentListTests`). Only the documented form differs |
| 9 | C5 | One document per version (`/openapi/v1.json`, `/openapi/v2.json`); the v1 operations are `deprecated: true` | Non-breaking | Documentation only; no request or response changes |
| 10 | C6 | The documents state enum types and values and decimal formats: `type: string` on the v2 status, the values and `x-enumNames` of the numeric enums, and `format: decimal` on `weightKg` | Non-breaking | Documentation only: the JSON on the wire is unchanged, as the unchanged Part B and C4 tests show. It lets generated clients use enums and `decimal` ([API-NOTES.md](API-NOTES.md#schema-types-for-generated-clients-week-4-part-c-c6)) |
| 11 | C7 | The contracts are published in `contracts/`, and the client is generated from `contracts/v2.json` | Non-breaking | No API change |
| 12 | Part D (D2), v1 and v2 | `POST …/scans` requires a bearer token with the Courier role, and every scan's `courierId` must be the token's. It now also returns 401 and 403 Problem Details, declared in both documents | **Breaking** for clients that uploaded without a token | A request that succeeded anonymously is now refused (a new status code for the same request). The assignment requires it, and an exception is made to "v1 is frozen" because security fixes are allowed in a deprecated version. The scan controller serves both versions, so one rule protects both upload routes. Couriers' devices send a token from Part D on |
| 13 | Part D (D2), v1 and v2 | Every shipment and scan-history operation requires a bearer token: the list, create, replace and delete need the Admin role, the status change the Courier role, and reading one shipment or its scan history any signed-in user. Each declares 401, and those limited to a role also 403 | **Breaking** for anonymous clients and for tokens without the required role | Same reason as change 12: requests that succeeded anonymously are now refused. Version 1 gets the same rules as version 2 as a security fix, so the deprecated version cannot be used to get round them. The request and response bodies are unchanged |
| 14 | Part D (D5), v1 and v2 | The documents declare their security schemes: `Bearer` (HTTP bearer, JWT) in both, and `PartnerApiKey` (`X-Api-Key` header) in v2. Every operation lists the scheme it requires | Non-breaking | Documentation of changes 12, 13, 15 and 18. It lets Swagger UI's Authorize button and generated clients send the credentials. The assignment asks for the scheme in v2; v1 declares `Bearer` too because all its operations need a token |
| 15 | Part D (D3), v2 | New operation `GetPartnerShipment`: `GET /api/v2/partner/shipments/{trackingNumber}`, with a partner API key, returning `PartnerShipmentResponse { trackingNumber, status, destinationCity, latestScan }` | Non-breaking | An added operation and schema; no existing operation changes. It returns no recipient name, phone or address |
| 16 | Part D, v1 and v2 | A scan's `scanType` in an upload is accepted as the name, such as `"InTransit"`, as well as the number. The schema stays the numeric enum, and the property description says so | Non-breaking | The accepted input is widened: every earlier request stays valid. Responses still return the number, so the `ShipmentScanType` schema is unchanged |
| 17 | Part D (D5) | Plain HTTP is redirected (307) to HTTPS in every environment, and the contracts' `servers` entry is now `https://localhost:7280/` | Non-breaking for the contract | `servers` names the publishing host and is excluded from the contract comparison. A client configured with an `http://` base URL is redirected, and must switch to `https://` so it never sends a token over plain HTTP |
| 18 | Part D (D2) | A fallback authorization policy requires a signed-in user for every request without a rule of its own, including requests that match no route. An anonymous request to an unknown route, an unsupported version or a malformed tracking number is now a 401 instead of a 404; a signed-in caller still gets the 404. The partner route is exempt (`[AllowAnonymous]`) and still requires its API key | **Breaking** for anonymous clients that relied on a 404 | The status code for the same anonymous request changes. Every documented operation already requires a token (changes 12 and 13), so no documented anonymous request is affected. Protecting by default means a future endpoint cannot be left open by mistake |

**The same in both versions:**
- the operations and their routes (apart from the version segment), operation IDs, status codes and Problem Details errors, including the Part D authorization rules and their 401 and 403 responses (changes 12, 13 and 18); only the partner operation (change 15) is version 2 alone
- the create, replace and scan-upload request bodies and their validation rules
- the scan endpoints' responses, where `scanType` stays a number
- the business rules: status transitions, delete only while `Created`, and idempotent scan upload

## Migrating from version 1 to version 2

| Version 1 | Version 2 |
|---|---|
| `/api/v1/shipments...` | `/api/v2/shipments...` |
| `recipientName`, `recipientPhone`, `recipientAddress`, `recipientCity` | `recipient.name`, `recipient.phone`, `recipient.address`, `recipient.city` |
| `"status": 1` | `"status": "PickedUp"` (names: `Created`, `PickedUp`, `InTransit`, `OutForDelivery`, `Delivered`, `Failed`) |
| Not available | `latestScan`: the most recent scan, or `null` |
| `PATCH …/status` with `{ "status": 1 }` | `{ "status": "PickedUp" }`; the number is still accepted |
| `?status=1` | `?status=PickedUp`; the number is still accepted |
| Scan upload and scan history | Unchanged; both versions need the same bearer token (Part D) |

A .NET client can use the generated client in `src/ApexPost.Client` instead of migrating by hand. `src/ApexPost.Client.Consumer` shows it registered with `AddHttpClient`.

## Deprecation and sunset plan for version 1

The dates are the plan for this fictional service. Once a sunset date is announced it can move later, never earlier.

| When | Step | State |
|---|---|---|
| 2026-11-07 | **Deprecate.** Version 2 is released and version 1 is marked deprecated: `Deprecated = true`, `api-deprecated-versions: 1.0` on every response, `deprecated: true` on every operation in `contracts/v1.json`, and "v1 (deprecated)" in Swagger UI. Version 1 is frozen: no new operations or fields, only security and bug fixes | Done |
| 2026-11-07 | **Notify.** Give the customer-portal team and partner companies this file, the migration table and the v2 contract and client | Planned |
| 2027-02-07 (three months before sunset) | **Announce the date in the API.** Add an `Asp.Versioning` sunset policy for version 1.0, so every version 1 response also carries the `Sunset` header (RFC 8594) with the date and a `Link` to this file. Review the version 1 traffic in the request logs and contact each remaining client | Planned |
| 2027-05-07 | **Sunset.** Remove the version 1 controller and the `1.0` declaration on the scans controller, with their DTOs, mapping and tests. `/api/v1/...` then returns a 404 Problem Details like any unsupported version. `contracts/v1.json` is deleted; it stays in the Git history | Planned |

**What a version 1 client sees until the sunset:** the same requests and responses as before, plus the two version headers. After the sunset it must call `/api/v2`.

**Not implemented yet:**
- The `Sunset` header is added at the announcement step, not now. Announcing a date before it is approved would commit the service to it.
- The usage review relies on request logs. Since Week 4 Part D a courier's token identifies the caller of the secured operations, but the API does not yet log usage by client.
