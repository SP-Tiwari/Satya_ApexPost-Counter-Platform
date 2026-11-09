# Week 4 Part D evidence

- **API security (D1–D5):** this page, down to [Automated tests](#automated-tests).
- **Courier console client (D6–D11):** [Courier console client: live run](#courier-console-client-live-run-d6d11).

Evidence for the JWT authentication, role authorization, courier ownership rule, partner API key, HTTPS and OpenAPI security schemes of `src/ApexPost.Api`. The design is in [SECURITY.md](../../../SECURITY.md) and the per-endpoint rules in [API-NOTES.md](../../../API-NOTES.md#security-week-4-part-d). Everything was captured on 2026-11-09 against the running API, on the `feature/week4-part-d-api-security` branch. All data is fictional.

**No secret is in this folder.**
- **Tokens:** every access token was a Development token from `POST /dev/token`, signed with a random key that existed only in the API process's environment for the run.
- **Partner key:** random as well, and it is not recorded.
- **Text files:** tokens and the key are replaced by placeholders such as `Bearer <courierToken: redacted>`.
- **Screenshots:** before each screenshot, token and key text on the page was replaced with `<access token redacted>` or `<partner key redacted>`. The Authorize dialog shows Swagger UI's own `******` mask.

## Files

| File | Shows |
|---|---|
| [http-run.txt](http-run.txt) | All 63 requests in [ApexPost.Api.http](../../../src/ApexPost.Api/ApexPost.Api.http), sent in order to the running API. It starts with a summary of each request's status against the status in its title (all 63 match), followed by each request and its real response: status, content type, `WWW-Authenticate`, `Location`, version headers and body |
| [swagger-ui-run.txt](swagger-ui-run.txt) | The Swagger UI run: each Authorize step (which token or key was entered), each Try it out execution with its status and response body, and the screenshot taken |
| [screenshots/](screenshots/) | The 16 screenshots listed below |

## Required checks

| Check | `.http` request | Result | Swagger UI screenshot |
|---|---|---|---|
| Missing JWT → 401 | 43 (v2 GET), 48 (v2 upload), 56 (v1 list), 61 (v2 scan history) | 401 Problem Details with `WWW-Authenticate: Bearer` | [d2-01](screenshots/d2-01-v2-get-without-token-401.png) |
| Invalid JWT → 401 | 44 | 401 | — |
| Insufficient role → 403 | 46 (v2 PATCH as Admin), 47 (v2 DELETE as Courier), 57 (v1 list as Courier), 58 (v1 PATCH as Admin), 59 (v1 DELETE as Courier), 60 (v2 create as Courier) | 403 Problem Details | [d2-02](screenshots/d2-02-v2-delete-as-courier-403.png), [d2-04](screenshots/d2-04-v1-patch-as-admin-403.png), [d2-05](screenshots/d2-05-v1-list-as-courier-403.png) |
| Courier token with another `courierId` → 403 | 49 | 403 "Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored." | [d2-03](screenshots/d2-03-v2-upload-other-courier-403.png) |
| Courier token with the correct `courierId` → success | 50 (scan type sent as the name `"InTransit"`), 17, 37 | 200, `accepted` 1 | [d2-07](screenshots/d2-07-v2-upload-own-courier-200.png) |
| Invalid partner API key → 401 | 53 (wrong key), 52 (no key) | 401 "A valid partner API key is required." | [d3-01](screenshots/d3-01-partner-invalid-key-401.png) |
| Valid partner API key without a JWT → success | 51 (and 54: valid key, unknown tracking number → 404) | 200 `{ trackingNumber, status, destinationCity, latestScan }`, without the recipient's name, phone or address | [d3-02](screenshots/d3-02-partner-valid-key-no-jwt-200.png): the request sends only `X-Api-Key`, with no `Authorization` header |
| Allowed roles succeed, v1 and v2 | 1–42 (each with the token its rule requires), 45 | As in Parts B and C: 200, 201, 204, and the same 400, 404, 409 and 415 errors | [d2-06](screenshots/d2-06-v2-create-as-admin-201.png) (v2 create as Admin), [d2-08](screenshots/d2-08-v2-get-as-courier-200.png) (v2 GET as Courier), [d2-09](screenshots/d2-09-v1-list-as-admin-200.png) (v1 list as Admin), [d2-10](screenshots/d2-10-v1-patch-as-courier-200.png) (v1 PATCH as Courier) |
| Development token endpoint | 0a, 0b (200), 55 (400 for an unknown role) | Tokens issued; response bodies recorded with `accessToken` redacted | — |

## Swagger UI security schemes and lock icons

| Screenshot | Shows |
|---|---|
| [d5-01-swagger-v2-locks.png](screenshots/d5-01-swagger-v2-locks.png) | "v2": the **Authorize** button and a lock icon on all nine operations, including the partner operation |
| [d5-04-swagger-v1-locks.png](screenshots/d5-04-swagger-v1-locks.png) | "v1 (deprecated)": the Authorize button and a lock icon on all eight deprecated operations |
| [d5-02-swagger-v2-authorize-dialog.png](screenshots/d5-02-swagger-v2-authorize-dialog.png) | The v2 Authorize dialog: `Bearer (http, Bearer)` with its description, and `PartnerApiKey (apiKey)`, header `X-Api-Key` |
| [d5-03-swagger-v2-authorized-courier.png](screenshots/d5-03-swagger-v2-authorized-courier.png) | After entering a Courier token and selecting Authorize: "Authorized", value `******` |

Swagger UI then sent the token with every bearer operation and the key with the partner operation. The curl line in each execution screenshot shows the header, redacted.

## How the run was done

1. **API without a database** (lock icons, Authorize and the 401/403 executions, which are refused before any database access): `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`, trusted development certificate). The signing key, the partner key and an unreachable connection string were passed as environment variables, so no User Secrets were written.
2. **Database for the success cases:** a disposable database, `ApexPost_Test_`, was created on `.` with its files in `E:\SqlData\ApexPost\Tests`, the folder the test fixture uses. `dotnet ef database update --connection …` applied the five migrations (22 tables). The API was restarted against it with new random keys. No developer database, User Secrets or LocalDB setting was used or changed.
3. **`.http` run:** a script outside the repository parsed `ApexPost.Api.http` and sent every request in order with its variables resolved, as an `.http` client does. The tokens came from requests 0a and 0b, and the partner key from the `APEXPOST_PARTNER_API_KEY` environment variable. Before the database run, the 17 requests that never reach the database (43, 44, 46–49, 52, 53, 55–61) were also sent against the database-less API, with the same results.
4. **Swagger UI:** a script outside the repository drove Microsoft Edge (headless, through `puppeteer-core`) on `https://localhost:7280/swagger/index.html`. It used the Authorize dialog and, for each operation, Try it out and Execute.
5. **Cleanup:** the API was stopped, the database was dropped and its files were removed, and the random keys were discarded.

## Automated tests

[tests/ApexPost.Api.IntegrationTests/SecurityTests.cs](../../../tests/ApexPost.Api.IntegrationTests/SecurityTests.cs) holds 105 security tests, including `AuthorizationMatrix`: 8 operations × 2 versions × no token, Courier and Admin. The full run is recorded in the [README](../../../README.md).

## Courier console client: live run (D6–D11)

One console session, recorded on 2026-11-09 between 21:08 and 21:11 UTC on the `feature/week4-part-d-api-security` branch. It used the courier console client (`src/ApexPost.Courier.Console`, built in Debug) against the running API. All data is fictional.

### Files

| File | Shows |
|---|---|
| [client/setup.txt](client/setup.txt) | Before the session: an Admin token creates the shipments `APX-10000001` to `APX-10000003` through `POST /api/v2/shipments` (201) |
| [client/console-session.txt](client/console-session.txt) | The whole console session: every line the console wrote, the input sent to it, and the numbered steps |
| [client/server-check.txt](client/server-check.txt) | After the session: the scan the server stored for each scan ID the console saved, read from `GET /api/v2/shipments/{trackingNumber}/scans` |

### Reading the transcript

- **Step headers:** a line `=== [time UTC] n. … ===` was written by the run script just before that step's input. Each header describes what the step was meant to show; the lines after it are the real output.
- **Input:** the menu choices and answers sent to the console appear on their own lines, after the prompt. Input was piped, so the console did not echo it.
- **Background output:** sync results, `[warn]` log lines, the banner and notices come from the background sync. They can appear before the input that a step header introduces.
- **`API stopped` and `up`:** printed by the run script when it stopped the API, and when it found the restarted API answering.

### Required checks

| Requirement | Step in [console-session.txt](client/console-session.txt) | Result |
|---|---|---|
| D10/D11 Login: obtain a development token | 2 | `Signed in as COURIER-DEMO-07.` The menu changes to the signed-in screens |
| D10 token stored encrypted, not as text | Token file check after step 2 | `access-token.bin`, 662 bytes, starts with the DPAPI blob header `01 00 00 00 d0 8c 9d df …`. It contains no JWT text (`eyJ`: 0 occurrences). No token appears anywhere in the transcript |
| D11 New Scan, saved locally first | 3, 4, 6, 9, 11, 17 | `Saved scan::… on this device as Pending.` The live pending count in the header goes up |
| D9 synced from the API answer | 3–5 | Sync Status: `1 attempted, 1 synced`. [server-check.txt](client/server-check.txt) shows both scans stored once, under `COURIER-DEMO-07` |
| D8/D9 refused scan marked `Failed`, with the server's Problem Details message | 6–7 | `The API refused the 1 scans for APX-99999999 (HTTP 404): No shipment has tracking number APX-99999999.` The scan leaves the Pending list and is listed under Recent failures. The server stored nothing for it |
| D11 Pending Scans with the count; Sync Status with last sync, circuit state and failures | 1, 5, 7, 12, 15, 18, 20 | As listed |
| D7 retries logged | 9 | `Retry 1 of 3 … Retry 3 of 3 for POST /api/v2/shipments/APX-10000003/scans … after HttpRequestException` |
| D7 circuit breaker opens while the API is stopped; banner; scans still saved | 10–12 | `Circuit breaker opened for 30 s …`. The banner `*** Server unavailable – working offline. New scans are saved on this device. ***` appears on every screen. A new scan is saved. Sync Status shows `Circuit breaker: open`, and both scans stay `Pending` |
| D7 recovery | 13–15 | After the 30 s break, Sync Now: `Server available again; Pending scans are being synced.` `2 attempted, 2 synced`. Circuit `closed`, banner gone. Both scans are on the server |
| D10 on a 401: token cleared, Login shown, scans kept | 16–18 | The API restarted with a new signing key. The next upload got 401: `The API answered 401 Unauthorized; the access token was cleared and sign-in is required.` The token file no longer exists, the signed-out menu is shown, and the scan stays `Pending` |
| Sync after signing in again | 19–20 | `1 attempted, 1 synced`; the scan is on the server |
| Logout removes the stored token | 20 | Token file check: `access-token.bin does not exist` |

The client-side 403 (another courier's `courierId`) is not in this session: the console always records scans under the signed-in courier's `courierId`. The API's 403 is recorded in [http-run.txt](http-run.txt) (request 49) and [d2-03](screenshots/d2-03-v2-upload-other-courier-403.png). On the client, a 403 makes the scans `Failed` (`ApiShipmentUploaderTests`).

### How the run was done

1. **Database:** a temporary database, `ApexPost_Test_<guid>`, was created on `.` with its files in `E:\SqlData\ApexPost\Tests`. `dotnet ef database update --connection …` applied the migrations (22 tables).
2. **API:** started with `dotnet run --project src/ApexPost.Api --launch-profile https` (Development, `https://localhost:7280`). The connection string, a random signing key and a random partner key were passed as process environment variables only. No User Secrets were written, and no developer or LocalDB database was used.
3. **Console:** `ApexPost.Courier.Console.exe --Courier:DataDirectory=<a temporary folder>`, so the default device store was not used. The token went to the normal DPAPI store, `%LOCALAPPDATA%\ApexPost\Courier\access-token.bin`, which was empty before the run.
4. **Session:** a script outside the repository sent the input with pauses. It stopped the API (step 8), started it again with the same key (step 13), and restarted it with a second random key (step 16). The run was rehearsed twice before this recording, against the same database, to adjust the pauses, so the server also holds those rehearsal scans. [server-check.txt](client/server-check.txt) lists only the scans of this session.
5. **Cleanup:** the API was stopped, the database was dropped and its files were removed, the temporary device store and both keys were deleted, and the token file was already removed by Logout.

Synchronization triggered by the network itself returning (`SystemNetworkStatus`, `SyncCoordinator`) was not demonstrated live; no airplane-mode run was recorded. It is verified by the automated tests in `SyncCoordinatorTests` (`tests/ApexPost.Courier.Sync.Tests`): sync after the reconnect delay, debouncing of repeated or flapping events, no sync when the network is gone again, one run at a time, and no run while sign-in is required.
