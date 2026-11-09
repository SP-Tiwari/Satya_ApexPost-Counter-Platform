# Courier client resilience (Week 4 Part D, D7)

How the courier console client's calls to `ApexPost.Api` survive a weak network: the chosen retry, circuit-breaker and timeout values, and the reasons for them. Everything is in [src/ApexPost.Courier.Sync/CourierResilience.cs](src/ApexPost.Courier.Sync/CourierResilience.cs). It is added to the typed `ApexApiClient` in `AddCourierSync` with `Microsoft.Extensions.Http.Resilience` (built on Polly). The client is a .NET console application instead of a .NET MAUI app ([ADR 0030](docs/adr/0030-courier-console-client.md)); the resilience pipeline is the same one a MAUI app would register.

## Requirement traceability

| D7 requirement | Implementation | Tests |
|---|---|---|
| Retry: 3 attempts with exponential backoff and jitter | `CourierResilience.CreateRetryOptions`: `MaxRetryAttempts = 3` (four attempts in all), `DelayBackoffType.Exponential`, `UseJitter = true`, base delay 1 s, maximum delay 4 s | `ResilienceTests.TransientStatus_IsRetriedThreeTimes_ThenReturned` (5 cases), `NetworkFailure_IsRetriedThreeTimes_ThenThrown`, `TransientFailures_ThenSuccess_ReturnsTheSuccess`, `Backoff_IsExponential_WithJitter` |
| Retry on network errors, HTTP 408 and HTTP 5xx only; never a 4xx | `IsTransient`: `HttpRequestException`, an attempt timeout (`TimeoutRejectedException`), 408 and any status of 500 or more | The tests above, and `ClientError_IsNeverRetried` (400, 401, 403, 404, 409, 415, 422, 429) |
| Retry only idempotent requests; why the scan upload is safe to retry | `IsRetrySafe`: a request is retried only when `MarkRetrySafe` marked it. `ApexApiClient.UploadScansAsync` marks the scan upload; `RequestCourierTokenAsync` does not | `TokenRequest_IsNotRetried_OnAServerError`, `TokenRequest_IsNotRetried_OnANetworkFailure`, `TokenRequest_IsNotRetried_AfterAnAttemptTimeout`, `RetriedUpload_SendsTheSameBodyAndToken_OnEveryAttempt`, and against the real API `CourierClientRetryTests.UploadWhoseResponseWasLost_IsRetried_AndTheApiStoresEachScanOnce` |
| Circuit breaker that opens after repeated failures | `CourierResilience.CreateCircuitBreakerOptions`: failure ratio 0.5, at least 5 calls in 30 s, open for 30 s | `RepeatedFailures_OpenTheCircuit_AndCallsThenFailFast`, `AfterTheBreak_TheCircuitIsHalfOpen_AndASuccessClosesIt`, `AFailedHalfOpenProbe_OpensTheCircuitAgain`, `ClientErrors_DoNotOpenTheCircuit` |
| A per-attempt timeout plus an overall request timeout | Attempt timeout 10 s (innermost); overall timeout 30 s (outermost), retries and delays included | `AttemptTimeout_IsRetried_AndTheNextAttemptSucceeds`, `OverallTimeout_EndsTheCall_IncludingItsRetries`, `TokenRequest_IsNotRetried_AfterAnAttemptTimeout` |
| Log every retry attempt and every circuit state change | `OnRetry`, `OnOpened`, `OnHalfOpened` and `OnClosed` write log messages; the overall timeout logs too | `Retries_AreLogged_WithoutTokenOrBody`, and the log assertions in the circuit-breaker and timeout tests |
| Never log tokens, keys or request bodies | The messages name the method, the path and the failure (status code or exception type), never a header, body or exception message | `Retries_AreLogged_WithoutTokenOrBody`, `RepeatedFailures_OpenTheCircuit_AndCallsThenFailFast` (both read every log message of every category, at Trace level) |
| While the circuit is open, keep saving scans locally | The open circuit makes every call fail at once with `BrokenCircuitException`. `ApiShipmentUploader` turns that into `RetryLater`, so the outbox leaves the scans `Pending` and stops the run. Saving a scan never needs the network. The circuit-breaker callbacks publish the state to `SyncState` | `OfflineSyncTests.WhileTheCircuitIsOpen_ScansAreStillSavedLocally_AndStayPending`, `CircuitStateChanges_ArePublishedToTheSyncState`, `ApiShipmentUploaderTests.OpenCircuit_LeavesTheScansForLater_WithoutARequest` |
| The "Server unavailable – working offline" banner while the circuit is open | `CourierConsole` (`src/ApexPost.Courier.Console`). Every screen shows `*** Server unavailable – working offline. New scans are saved on this device. ***` while `SyncState.Snapshot().CircuitState` is not closed. The banner line is also written as soon as the circuit opens, and "Server available again" when it closes. New Scan keeps working, and Sync Now lets the courier retry (the half-open test call) | `CourierConsoleTests.CircuitOpen_ShowsTheBanner_ScansAreStillSaved_AndSyncNowRecovers` |

The tests are in [tests/ApexPost.Courier.Sync.Tests/ResilienceTests.cs](tests/ApexPost.Courier.Sync.Tests/ResilienceTests.cs) and [tests/ApexPost.Api.IntegrationTests/CourierClientRetryTests.cs](tests/ApexPost.Api.IntegrationTests/CourierClientRetryTests.cs).

## The pipeline

Each `ApexApiClient` request passes, from the outside in, through:
1. `AuthHeaderHandler`, which adds the bearer token once.
2. The resilience handler, with four strategies:
   - **Overall timeout:** 30 s.
   - **Retry:** 3 retries.
   - **Circuit breaker.**
   - **Attempt timeout:** 10 s.
3. The network.

**Why the authentication handler is outside the retry:**
- Every attempt carries the same token.
- A 401 is never retried, so it clears the token exactly once.

**Why the circuit breaker is inside the retry:**
- It sees every attempt, so a server that keeps failing opens it quickly.
- An open circuit stops the remaining retries at once.

**Why the attempt timeout is innermost:** it bounds a single request, and the retry treats it like a network error.

`HttpClient.Timeout` keeps its 100 s default. That is longer than the overall timeout, so the pipeline, not `HttpClient`, decides when a call has failed.

## Chosen values and reasons

| Setting | Value | Reason |
|---|---|---|
| Retries | 3 (4 attempts) | The assignment asks for "3 attempts". This implementation reads that as three retries after the first attempt, so four attempts in all (Polly's `MaxRetryAttempts = 3`), as the project owner's Milestone 3 instruction specified. A courier's phone loses a few seconds of coverage often; three more tries spread over a few seconds ride out most of those gaps without keeping the courier waiting long |
| Backoff | Exponential, base 1 s, jitter, at most 4 s | Each wait is roughly twice the last, so a struggling server gets more room each time. Jitter spreads the retries of many couriers who lost coverage at the same moment, so they do not all hit the API together. The 4 s cap keeps one call short |
| `Retry-After` header | Not used | The waits stay as documented; the overall timeout bounds the call anyway |
| What is retried | Network errors, attempt timeouts, 408, 5xx | These may succeed if repeated. A 4xx is the server's answer to this request: repeating it gives the same answer. A 429 is a 4xx too: retrying it at once would add load to a server that asked for less |
| Attempt timeout | 10 s | A scan upload is a small JSON request. 10 s allows for a slow mobile link, while an attempt still unanswered after that is more likely a dead connection, better abandoned for a new attempt than waited on |
| Overall timeout | 30 s | The longest a courier waits for one call. It allows two full attempts and the start of a third even when every attempt hangs. When delays are short, the four attempts end sooner |
| Circuit breaker: failure ratio | 0.5 | Half the calls failing in the window is a sign of a server or network problem rather than bad luck |
| Circuit breaker: minimum throughput | 5 calls | One call that fails all four attempts (4 failures) does not open the circuit, but a second failing call does. So "repeated failures" means more than one call |
| Circuit breaker: sampling window | 30 s | At least twice the attempt timeout, so hanging attempts are counted inside the window |
| Circuit breaker: break duration | 30 s | Long enough for a restarted API or a recovered network. After it, one call tests the server (half-open): a success closes the circuit, a failure opens it for another 30 s |
| Counted by the circuit breaker | The same transient failures | A 4xx shows the server is working, so it never opens the circuit (`ClientErrors_DoNotOpenTheCircuit`) |

## Why the scan upload is safe to retry

`POST /api/v2/shipments/{trackingNumber}/scans` is not idempotent by HTTP method, but it is idempotent by design (Part B, [API-NOTES.md](API-NOTES.md#idempotent-scan-upload)):
- Every scan carries the device's `scanId` (`scan::{Guid}`).
- The API stores a scan ID once.
- A scan ID it already holds for that shipment, or one repeated in the request, is counted in `duplicatesSkipped` and not stored again.
- If two uploads store the same scan at the same moment, the database's unique key refuses the second copy and the upload is counted again as a duplicate.

So when an upload's response is lost after the server stored the scans, the retry stores nothing new. It reports the scans as duplicates, which the client treats as delivered. `CourierClientRetryTests` shows this against the real API:
1. The first attempt is processed, then its response is replaced by a connection error.
2. The retry answers `accepted 0, duplicatesSkipped 2, rejected 0`.
3. The shipment holds exactly two scans.

The retry sends the same body. `JsonContent` serializes the same request object again for each attempt, and `RetriedUpload_SendsTheSameBodyAndToken_OnEveryAttempt` compares the four bodies.

**The token request is never retried.** `POST /dev/token` issues a new token each time and is not marked safe to retry. A failure is returned to the caller after one attempt.

## What the caller sees

| Situation | Result of `ApexApiClient` |
|---|---|
| Success, or success after retries | `ApiResult` with the value |
| A 4xx, or a 408 or 5xx still failing after the retries | `ApiResult` with an `ApiError` (the last answer) |
| No answer after the retries (network error) | `HttpRequestException` |
| An attempt or the whole call timed out | `TimeoutRejectedException` |
| Circuit open | `BrokenCircuitException`, at once, without a request |
| The caller cancelled its `CancellationToken` | `OperationCanceledException` (or its subclass `TaskCanceledException`). The call stops: no further retry is made, it is not reported as a timeout, and the circuit breaker does not count it as a failure (`CallerCancellation_DuringAnAttempt_IsThrown_AndNotRetried`, `CallerCancellations_DoNotCountAsCircuitBreakerFailures`) |

A thrown exception means the server gave no usable answer. The offline sync (D9) catches it in `ApiShipmentUploader` and keeps those scans `Pending` for the next run; see [OFFLINE-DESIGN.md](OFFLINE-DESIGN.md#4-synchronization-with-the-api-part-d-d9).

## Logging

Two sources write resilience log messages:
- **The application's own messages:** written by `CourierResilience` under the `ApexPost.Courier.Sync.CourierResilience` category. They are listed below and are what D7's "log every retry attempt and every circuit state change" refers to.
- **Polly's built-in resilience telemetry:** `Microsoft.Extensions.Http.Resilience` connects Polly to the application's logging. Polly may therefore also write its own resilience-event messages (for example for each retry, circuit change or timeout) under its own logging category. They are not written or formatted by this application, so a retry can appear in the log twice. Their content is Polly's, and may include the details of an exception that caused a retry.

The application's own messages:
- **Retry:** `Retry 1 of 3 for POST /api/v2/shipments/APX-00000011/scans in 842 ms after HTTP 503.` (warning)
- **Circuit opened:** `Circuit breaker opened for 30 s after HTTP 503: API calls fail at once and scans stay on the device.` (warning)
- **Half-open:** `Circuit breaker half-open: the next API call tests whether the server has recovered.` (information)
- **Closed:** `Circuit breaker closed: the API is answering again.` (information)
- **Overall timeout:** `The API call exceeded the overall timeout of 30 s, retries included.` (warning)

What the application's own messages never contain:
- A header, the request body or the response body.
- An exception's message, which could quote the server. Only the exception type is named.

**No secrets in any log:**
- The `IHttpClientFactory` request logs redact every header value (`RedactLoggedHeaders`), so the `Authorization` header never appears in any category.
- The application never logs a request or response body.
- The tests read every message of every category at Trace level: the application's messages, the `IHttpClientFactory` request logs and Polly's telemetry. They check that neither the access token nor any part of the request body appears (`Retries_AreLogged_WithoutTokenOrBody`, `RepeatedFailures_OpenTheCircuit_AndCallsThenFailFast`).

## Testing approach

The tests replace the clock with `TestClock`, a `FakeTimeProvider` that the resilience pipeline uses through dependency injection:
- **Retry delays:** a timer of at most 4 s elapses at once. The clock moves forward by its length, which is recorded.
- **Timeouts:** the timers of the attempt and overall timeouts fire only when a test advances the clock. A test does that after the stub server reports that the attempt has started and is hanging.
- **Determinism:** the outcome of every test is decided by the fake clock, never by real time, and the suite runs in about a second.
- **Jitter:** `Backoff_IsExponential_WithJitter` gives the production retry options a fixed random value. With the same value, each delay is longer than the last; different values give different delays.
