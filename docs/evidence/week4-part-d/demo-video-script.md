# Week 4 Part D demo video script

A 4 min 40 s recording of the Part D work: the secured API (D1–D5) and the courier console client (D6–D11). The courier client is a .NET console application instead of a .NET MAUI app ([ADR 0030](../../adr/0030-courier-console-client.md)). All data is fictional.

Prepare the machine first with the [recording checklist](demo-video-checklist.md). It lists the windows, the demo data and how to keep every token and key off the screen.

## Sequence

Times are targets. The waits in 04 and 05 depend on the API's restart time; talk through them as written.

### 00 · 00:00–00:20 · Introduction

| | |
|---|---|
| Screen | The solution in the editor: `src/ApexPost.Api`, `src/ApexPost.Courier.Sync`, `src/ApexPost.Courier.Console`, `src/ApexPost.Courier.Offline` |
| Say | "This is Week 4 Part D of ApexPost. First, the API is secured with JWT bearer tokens, roles and a partner API key over HTTPS. Second, a courier client calls it through IHttpClientFactory with Polly resilience, and syncs scans saved offline. Following the trainer's guidance, the client is a .NET console application rather than a MAUI app. ADR 0030 lists each substitution." |

### 01 · 00:20–01:10 · JWT, roles, courier ownership and partner key (D1, D2, D3)

Run the requests in `src/ApexPost.Api/ApexPost.Api.http` one after the other. Show only the response status and body.

| Request | Expected response | Say |
|---|---|---|
| 43 | 401 Problem Details, `WWW-Authenticate: Bearer` | "No token: 401, as Problem Details." |
| 44 | 401 | "A token that is not a valid signed JWT: 401. Issuer, audience, lifetime and signing key are all validated." |
| 47 | 403 Problem Details | "A Courier token cannot delete a shipment; that needs the Admin role: 403." |
| 49 | 403, detail "Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored." | "A courier may upload scans only under their own courierId claim. Another courierId: 403, and nothing is stored." |
| 50 | 200, `accepted` 1 (or `duplicatesSkipped` 1 if it was run before) | "Their own scan is accepted." |
| 53 | 401, title "A valid partner API key is required." | "The partner route needs a valid X-Api-Key: a wrong key gets 401." |
| 51 | 200: `trackingNumber`, `status`, `destinationCity`, `latestScan`; no recipient details | "A valid key and no JWT: read-only access, without the recipient's personal data." |

### 02 · 01:10–01:35 · HTTPS, HSTS and OpenAPI security (D5)

| Action | Expected screen | Say |
|---|---|---|
| In a terminal: `curl.exe -s -i http://localhost:5280/api/v2/shipments/APX-00000011` | `HTTP/1.1 307 Temporary Redirect`, `Location: https://localhost:7280/api/v2/shipments/APX-00000011` | "Plain HTTP is never served: it is redirected to HTTPS, so a token or key is never accepted unencrypted." |
| Show `src/ApexPost.Api/Program.cs`, the `UseHsts()` / `UseHttpsRedirection()` lines | The two lines and their comment | "HSTS is on outside Development. The automated test `Hsts_IsSentOutsideDevelopmentOnly` checks both cases." |
| Browser: `https://localhost:7280/swagger`, document "v2" | The **Authorize** button and a lock on every operation | "The v2 OpenAPI document declares the Bearer and partner key schemes." |
| Select **Authorize**, then close the dialog without entering anything | `Bearer (http, Bearer)` and `PartnerApiKey (apiKey)`, header `X-Api-Key` | "Swagger UI's Authorize button uses those schemes." |

### 03 · 01:35–02:30 · Console login, secure token storage, saving and syncing scans (D6, D8, D9, D10, D11)

Console window, already started and showing the signed-out menu with `pending scans (live): 0`.

| Action | Expected screen | Say |
|---|---|---|
| `1`, then `COURIER-DEMO-07` | `Signed in as COURIER-DEMO-07.` The signed-in menu: New Scan, Pending Scans, Sync Status, Sync Now, Logout, Exit | "Login gets a development token. The client checks its Courier role, courierId and expiry before storing it." |
| Terminal 3: `[IO.File]::ReadAllBytes("$env:LOCALAPPDATA\ApexPost\Courier\access-token.bin")[0..47] \| Format-Hex` | An empty `Path:` line, then three rows of bytes starting `01 00 00 00 D0 8C 9D DF` (the DPAPI header), no readable token | "Instead of MAUI SecureStorage, the token is encrypted with Windows DPAPI for the current user; the file holds no readable token." |
| `1`, `APX-00000011`, `2`, `Pune Hub`, Enter | `Saved scan::… on this device as Pending.` The header count goes to 1, then back to 0 | "A scan is always saved on the device first, as Pending. Then it syncs in the background through the typed ApexApiClient." |
| `1`, `APX-99999999`, `1`, `Pune Hub`, Enter | After a moment: `[warn] ApiShipmentUploader: The API refused the 1 scans for APX-99999999 (HTTP 404): No shipment has tracking number APX-99999999.` | "A final refusal is read from the Problem Details and the scan is marked Failed, with the server's message." |
| `3` (Sync Status) | `Last result: 1 attempted, 0 synced, 1 failed`, circuit `closed`, the 404 under Recent failures | "Sync Status shows the last sync, the circuit state and the failures." |

### 04 · 02:30–03:40 · Retry, circuit breaker and saving while offline (D7, D9)

| Action | Expected screen | Say |
|---|---|---|
| API terminal: Ctrl+C | The API stops | "Now the API is down." |
| Console: `1`, `APX-00000011`, `1`, `Pune Hub`, Enter | `Saved … as Pending.` Then, over about 20 s: `[warn] CourierResilience: Retry 1 of 3 …`, `Retry 2 of 3 …`, `Retry 3 of 3 …`, then `Sync stopped; the scans without a final answer stay Pending: The API could not be reached.` | "The scan is saved anyway. The upload is retried 3 times with exponential backoff and jitter, and each retry is logged. It is safe to retry because the scan carries its ID and the server skips duplicates. A 4xx is never retried." |
| After the `Sync stopped` line: `4` (Sync Now) | `*** Server unavailable – working offline. New scans are saved on this device. ***`, `[warn] CourierResilience: Circuit breaker opened for 30 s …`, then `Sync finished: … Stopped: Server unavailable – working offline.` | "Sync Now's call fails too. With at least 5 calls in 30 seconds and half or more failing, the circuit breaker opens. Calls now fail at once, and the banner says the client is working offline." |
| `1`, `APX-00000011`, `2`, `Mumbai Gateway`, Enter | The banner on the menu; `Saved … as Pending.`; count 2 | "While the circuit is open, scans are still saved locally." |
| `2` (Pending Scans) | Both scans listed, oldest first; count 2 | "Pending Scans shows the list and the live count, from the Part A change listener." |
| API terminal: start the API again (same command, same key) | The API is listening again | "The API is back. The circuit stays open for its 30-second break." |

### 05 · 03:40–04:05 · Recovery and timeouts (D7)

| Action | Expected screen | Say |
|---|---|---|
| While waiting for the 30 s break to end | The console with the banner | "Each attempt has a 10-second timeout, and each call a 30-second overall timeout. Timeouts are not shown live; `ResilienceTests` covers them, including `AttemptTimeout_IsRetried_AndTheNextAttemptSucceeds` and `OverallTimeout_EndsTheCall_IncludingItsRetries`." |
| At least 30 s after the circuit opened: `4` (Sync Now) | `[info] CourierResilience: Circuit breaker half-open …`, `[info] CourierResilience: Circuit breaker closed: the API is answering again.`, `Server available again; Pending scans are being synced.`, `Sync finished: 2 attempted, 2 synced …`; the banner is gone, count 0 | "After the break, one test call goes through. It succeeds, the circuit closes, and both offline scans are synced." |

### 06 · 04:05–04:30 · 401 handling, login again and logout (D10)

| Action | Expected screen | Say |
|---|---|---|
| API terminal: Ctrl+C, then start it with the second signing key (checklist section 3) | The API is listening again | "The API now signs and checks tokens with a new key, so the stored token is no longer valid." |
| Console: `1`, `APX-00000011`, `2`, `Mumbai Gateway`, Enter | `[warn] CourierSession: The API answered 401 Unauthorized; the access token was cleared and sign-in is required.`, `Your sign-in was refused or has expired. Please log in again; scans stay on this device.` | "On a 401 the token is cleared and the courier goes back to Login. The scan stays Pending." |
| Press Enter once, then `3` | `Please choose 1 to 6.` (the answer to the menu that was on screen before the notice), then the signed-out menu. Pending Scans lists the scan; count 1 | "The scan is kept on the device." |
| `1`, `COURIER-DEMO-07`, then `3` (Sync Status) | `Signed in as COURIER-DEMO-07.` Sync Status: `1 attempted, 1 synced`, count 0 | "After signing in again, the Pending scan syncs." |
| `5` (Logout), then `5` (Exit) | `Signed out.`, `Goodbye.` | "Logout deletes the stored token." |

### 07 · 04:30–04:40 · Conclusion

| | |
|---|---|
| Screen | [docs/evidence/week4-part-d/README.md](README.md) |
| Say | "All 1,575 automated tests pass. The recorded runs are in docs/evidence/week4-part-d. Syncing when the network itself comes back is covered by automated tests, not shown in this video." |

**If the recording runs long:** shorten 01 to requests 43, 47, 49 and 53, and leave out the unknown-shipment scan in 03. Both stay covered in the [recorded evidence](README.md).

## Requirement coverage

| Requirement | In the video | Other evidence |
|---|---|---|
| D1 JWT bearer authentication; development tokens with `courierId` and role | 01 (requests 43, 44); 03 (token from `POST /dev/token`) | `SecurityTests`, [http-run.txt](http-run.txt) |
| D2 Roles, 401/403 as Problem Details, own `courierId` only | 01 (47, 49, 50) | `AuthorizationMatrix` in `SecurityTests`, [screenshots](screenshots/) |
| D3 Partner API key | 01 (53, 51) | [http-run.txt](http-run.txt) (51–54) |
| D4 OAuth2 concepts | Not shown | [SECURITY.md](../../../SECURITY.md#oauth2-for-apexpost-d4) |
| D5 HTTPS redirection, HSTS, Bearer scheme in OpenAPI | 02 (redirect, Swagger locks and Authorize) | HSTS outside Development: `Hsts_IsSentOutsideDevelopmentOnly` |
| D6 Typed `ApexApiClient`, `AuthHeaderHandler`, base URL from configuration | 03–06 (every call goes through it) | `ApexApiClientTests`, `AuthHeaderHandlerTests`, `CourierSyncRegistrationTests` |
| D7 Retry, circuit breaker, banner, logging | 04, 05 | `ResilienceTests`, [console-session.txt](client/console-session.txt) |
| D7 Per-attempt and overall timeouts | Explained in 05, not shown | `ResilienceTests` |
| D8 Source-generated JSON, Problem Details as friendly messages | 01 (Problem Details bodies), 03 (the 404 message) | `CourierJsonTests`, `ApexApiClientTests` |
| D9 Real uploader, Synced/Failed from the API answer, live pending count | 03, 04, 05 | `ApiShipmentUploaderTests`, `OfflineSyncTests`, `CourierOfflineSyncTests` |
| D9 Sync when the network returns | Not shown | `SyncCoordinatorTests` |
| D9 Batches of 20 | Not shown (the demo has at most 2 Pending scans) | `OutboxServiceTests`, `OfflineSyncTests` (45 scans) |
| D10 Encrypted token, cleared on 401, back to Login, never logged | 03, 06 | `SignInTests`, `DpapiTokenStoreTests`, `CourierConsoleTests` |
| D11 Login, New Scan, Pending Scans with count, Sync Status | 03–06 | `CourierConsoleTests` |

## Shown live and verified by tests only

**Shown live in the video** (and in the [recorded evidence](README.md)):
- 401 without a token or with an invalid one;
- 403 for the wrong role or another courier's `courierId`;
- the partner key, both refused and accepted;
- the HTTP-to-HTTPS redirect, and the OpenAPI security schemes in Swagger UI;
- console login, and the encrypted token file;
- scans saved locally first, then synced;
- a refused scan becoming `Failed` with the server's message;
- logged retries, the circuit opening, the offline banner, and saving while the circuit is open;
- recovery after the break;
- a 401 clearing the token and returning to Login;
- Logout.

**Verified by automated tests and documentation only:**
- **Sync when the network itself returns** (`SystemNetworkStatus`, `SyncCoordinator`): `SyncCoordinatorTests`. There was no airplane-mode run, and the video does not claim one.
- **Per-attempt and overall timeouts:** `ResilienceTests`.
- **HSTS:** it is off in Development, where the demo runs; `Hsts_IsSentOutsideDevelopmentOnly` covers it.
- **Batches of 20:** `OutboxServiceTests`, `OfflineSyncTests`.
- **OAuth2 concepts (D4):** [SECURITY.md](../../../SECURITY.md#oauth2-for-apexpost-d4).
