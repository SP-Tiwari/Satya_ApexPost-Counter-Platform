# Copilot test generation log (Week 5, Task 6)

GitHub Copilot drafted unit tests for one service and one Gherkin feature with its step definitions. Every draft was built, run and reviewed before it was kept. This log records each prompt word for word, what Copilot produced, what was kept, changed or rejected and why, and the proof that generated tests fail when the production code is broken.

| Task 6 requirement | Evidence |
|---|---|
| Unit tests for at least one service | [Prompt A](#prompt-a-unit-tests-for-parcelstatusapplicationservice): `tests/ApexPost.Application.Tests/Services/ParcelStatusApplicationServiceTests.cs` |
| One Gherkin feature with its step definitions | [Prompt B](#prompt-b-heavy-parcel-surcharge-feature-and-step-definitions): `tests/ApexPost.Specs/Features/HeavyParcelSurcharge.feature`, `StepDefinitions/HeavyParcelSurchargeSteps.cs`, `Support/HeavyParcelSurchargeContext.cs` |
| Each prompt, what Copilot produced, what was kept, changed or rejected, and why | The prompt, output and review tables under each prompt |
| At least two generated tests proven meaningful | [Break-and-restore](#break-and-restore-proof-that-generated-tests-can-fail): two unchanged Copilot tests failed against broken production code and passed again after the restore |
| Copilot-assisted classes marked | A `// Copilot-assisted:` comment at the top of `ParcelStatusApplicationServiceTests`, `HeavyParcelSurchargeSteps` and `HeavyParcelSurchargeContext` |

## Session

- **Tool:** GitHub Copilot Chat in Visual Studio Community 2026 (18.11.12315.225), **Agent** mode: Copilot created the files and built the projects; in Prompt A it also ran the tests, and in Prompt B it only listed them.
- **Model:** `gpt-5.3-codex`.
- **Date:** 2026-11-14. Prompt A was sent at 15:12:25 UTC and Prompt B at 15:28:16 UTC.
- **Clock note:** all 2026-11-14 times in this document were read from a system clock that VMware Tools had set 5 h 28 min 48 s fast at about 09:15 IST; Windows Time corrected it at 18:23 IST (Windows System log, Kernel-General event 1). The true times are 5 h 28 min 48 s earlier, for example 15:12:25 UTC was 09:43:37 UTC. Dates, order, durations and results are unaffected, and the dates of the Week 5 commits up to `4fed488` carry the same offset.
- **Source:** both prompts below are copied from the Visual Studio Copilot chat log of that session. Copilot's files were preserved unchanged, outside the repository, before any review change, and every finding below was checked against them.

## Prompt A: unit tests for ParcelStatusApplicationService

**Why this service:** `ParcelStatusApplicationService` (`src/ApexPost.Application/Services/ParcelStatusApplicationService.cs`) had no unit tests; only the partner endpoint was tested end to end, in `CounterScreensIntegrationTests`. Its three dependencies are interfaces, so Moq can isolate it.

### Prompt

```text
#ParcelStatusApplicationService.cs #ICounterTransactionRepository.cs #ICourierTrackingService.cs #IScreenQueryService.cs #TransactionBasket.cs #TrackedShipmentApplicationServiceTests.cs

Generate xUnit unit tests for ParcelStatusApplicationService using Moq.

Cover:
- null, empty and whitespace shipment ID
- shipment not found through ICounterTransactionRepository
- shipment found through ICounterTransactionRepository (check every ParcelStatus field)
- shipment found through IScreenQueryService, which is used instead of the repository when it is supplied
- IScreenQueryService returns a parcel whose ShipmentId differs only by letter case
- courier tracking configured: result included and GetTrackingAsync called once with the shipment ID
- courier tracking not configured: Courier is null
- shipment not found: GetTrackingAsync is never called

Requirements:
- xUnit 2.9.3 ([Fact], [Theory] with [InlineData]) and Moq 4.21 only; no other packages
- Arrange / Act / Assert comments and Method_Scenario_ExpectedResult names
- fixed dates, no DateTime.Now; fictional data only
- one complete file, ParcelStatusApplicationServiceTests.cs, namespace ApexPost.Application.Tests.Services
- do not modify ParcelStatusApplicationService or any production code
```

### What Copilot produced

One new file, `ParcelStatusApplicationServiceTests.cs`, with 6 test methods (8 test cases): a `[Theory]` for null, empty and whitespace IDs, and facts for not found, found through the repository (all seven fields), the screen-query path, the case-differing ID and the courier lookup. It built with 0 warnings and **8 of 8 passed**, both in Copilot's run and when re-run independently. Copilot's reply:

```text
Implemented.
Created one new test file:
•	tests/ApexPost.Application.Tests/Services/ParcelStatusApplicationServiceTests.cs
What it covers (xUnit 2.9.3 + Moq 4.21 only):
•	null/empty/whitespace shipment ID ([Theory] + [InlineData])
•	shipment not found through ICounterTransactionRepository
•	shipment found through repository with full ParcelStatus field assertions
•	IScreenQueryService path used instead of repository when supplied
•	screen query shipment ID mismatch by letter case returns null
•	courier tracking configured: included in result and GetTrackingAsync called once with shipment ID
•	courier not configured: Courier is null
•	shipment not found: GetTrackingAsync never called
Style constraints applied:
•	Method_Scenario_ExpectedResult naming
•	// Arrange, // Act, // Assert comments
•	fixed fictional dates (no DateTime.Now)
•	no production code changes
Validation:
•	Built ApexPost.Application.Tests successfully
•	Ran ParcelStatusApplicationServiceTests: 8/8 passed
```

### Review decisions

Each test was checked against the review questions: does it compile and pass, does it fail when it should, does it assert behaviour rather than "no exception", does it match the requirement rather than the current implementation, are the expected values independent of the production code, and are the mock setups and verifications correct.

| Decision | Item | Reason |
|---|---|---|
| **Kept** | `GetStatusAsync_ShipmentNotFoundInRepository_ReturnsNullAndDoesNotCallCourier` | Asserts the null result and that the courier is never asked; covers "not found: GetTrackingAsync is never called" |
| **Kept** | `GetStatusAsync_ShipmentFoundInRepository_ReturnsEveryParcelStatusField` | All seven fields against hand-written literals (Parcel, Express, `ZONE-NORTH`, the date, Paid); its `Assert.Null(status.Courier)` covers "courier not configured" |
| **Kept** | `GetStatusAsync_ScreenQueryShipmentIdCaseDiffers_ReturnsNull` | Models the real risk: the database ID column ignores case, so a query can return a differently cased ID. Proven by break-and-restore |
| **Kept** | `GetStatusAsync_CourierTrackingConfigured_IncludesCourierAndCallsTrackingOnce` | Argument-matched setup and `Times.Once`. Proven by break-and-restore |
| **Kept** | Test data, the two helpers, names, AAA comments, fixed date | Fictional, deterministic and in the project's style |
| **Rejected and replaced** | `Assert.ThrowsAnyAsync<ArgumentException>` in `GetStatusAsync_InvalidShipmentId_ThrowsArgumentException` (null, `""`, `"   "`) | **Weak assertion:** `ThrowsAnyAsync` accepts any subclass. The service throws `ArgumentNullException` for null and `ArgumentException` for empty or whitespace, and the strategy requires exact exception types. Replaced by `GetStatusAsync_NullShipmentId_ThrowsArgumentNullException` and `GetStatusAsync_EmptyOrWhitespaceShipmentId_ThrowsArgumentException`, each with exact `ThrowsAsync`, `ParamName` `"shipmentId"` and the repository never called |
| **Rejected and replaced** | The expected values in `GetStatusAsync_ScreenQueryServiceSupplied_UsesScreenQueryInsteadOfRepository` | **Expected values not independent:** each assertion read the expected value back from the same `ParcelDetail` it had passed in (`Assert.Equal(parcel.Type, status.Type)`), and `Courier` was not checked. Now literal values and `Assert.Null(status.Courier)`. Its check that the query is used and the repository never called is kept |
| **Added** (missing edge case) | `GetStatusAsync_RepositoryBasketHasNoMatchingShipment_ReturnsNullAndDoesNotCallCourier` (`APX-SHIP-2002`, `apx-ship-1001`) | The service's own exact match of the basket's shipping items after the repository lookup was untested |
| **Added** (missing edge case) | `GetStatusAsync_ShipmentNotFoundByScreenQuery_ReturnsNullAndDoesNotCallCourier` | Not found on the screen-query path |
| **Added** (missing edge case) | `GetStatusAsync_ScreenQueryWithCourierTracking_IncludesCourierAndCallsTrackingOnce` | The courier lookup on the screen-query path |
| **Added** | The marker comment and a note on what the mocks do not prove | The SQL behind `FindByShipmentIdAsync` and `FindParcelAsync` is covered by `CounterScreensIntegrationTests` |
| **Not added** | Cancellation-token forwarding, the constructor's null check | Wiring, not behaviour |

**Rejected Copilot test (for the viva):** no whole test was wrong enough to delete. The one rejected as written is `GetStatusAsync_InvalidShipmentId_ThrowsArgumentException`: it passed, but `ThrowsAnyAsync<ArgumentException>` would also pass if the service threw the wrong exception type, so it is replaced by the two exact tests above.

**Result:** 10 test methods, 12 test cases, 0 warnings, **12 of 12 passed**.

### Break-and-restore: proof that generated tests can fail

One temporary change at a time to `src/ApexPost.Application/Services/ParcelStatusApplicationService.cs`. The file was backed up first, restored from that backup after each run, and confirmed identical to the committed version (same SHA-256, `git diff` clean). Nothing was committed.

| | Mutation 1 | Mutation 2 |
|---|---|---|
| Change | Screen-query path: `StringComparison.Ordinal` → `StringComparison.OrdinalIgnoreCase` in the ID check | Both `await courier.GetTrackingAsync(...)` calls → `(CourierTrackingResult?)null` |
| Run (UTC) | 15:23:18: **11 passed, 1 failed** | 15:24:08: **10 passed, 2 failed** |
| Failed | `GetStatusAsync_ScreenQueryShipmentIdCaseDiffers_ReturnsNull` (**Copilot, unchanged**): `Assert.Null() Failure: Value is not null`, actual `ParcelStatus { ShipmentId = apx-ship-1001, … }` | `GetStatusAsync_CourierTrackingConfigured_IncludesCourierAndCallsTrackingOnce` (**Copilot, unchanged**) and `GetStatusAsync_ScreenQueryWithCourierTracking_IncludesCourierAndCallsTrackingOnce` (added in review): `Assert.Equal() Failure`, expected `CourierTrackingResult { … }`, actual `null` |
| After restore (UTC) | 15:23:33: 12 passed, 0 failed | 15:24:21: 12 passed, 0 failed |

The first attempt at mutation 2, replacing the whole lookup with `CourierTrackingResult? courierResult = null;`, did not compile (`CS9113: Parameter 'courier' is unread`, because warnings are errors). No test ran, so it is not counted; the file was restored and the compiling form above was used.

## Prompt B: heavy-parcel surcharge feature and step definitions

**Why this rule:** the heavy-parcel surcharge was built test first in `ShippingPricingService` ([TDD-LOG.md](TDD-LOG.md)). The overlap with `ShippingPricingServiceTests` is intentional: TDD built the rule case by case, and this feature states the same rule as business examples in Gherkin. It runs at service level and does not cover counter-workflow pricing, because the surcharge is not wired into `MixedBasketApplicationService` or the application configuration.

### Prompt

```text
#ShippingPricingService.cs #ShippingRate.cs #ShippingItem.cs #Weight.cs #Money.cs #ShippingPricingServiceTests.cs #ShipmentCreation.feature #ScanSync.feature #ScanSyncSteps.cs #CourierScanContext.cs #ApexPost.Specs.csproj

Generate one Reqnroll feature file and its step definitions for this business rule, calling ShippingPricingService.CalculatePrice directly (service level, no API, no database).

Business rule:
- A shipment's price is the rate's base price plus its weight in kg times the rate's per-kg charge.
- A parcel strictly heavier than 30 kg also pays a configured heavy-parcel surcharge of 50.00 INR.
- A parcel of exactly 30 kg or less pays no surcharge. A letter never pays it, whatever its weight.
- If no surcharge is configured, pricing a parcel over 30 kg is refused.

Use this fictional rate: destination zone "ZONE-A", Standard service, base price 100.00 INR, 10.00 INR per kg.

Include:
- a Background with the rate and the 50.00 INR surcharge
- a Scenario Outline with Examples for parcels of 29.99, 30, 30.01 and 31 kg, giving the expected total price of each
- a scenario for a letter over 30 kg
- a scenario for a parcel over 30 kg when no surcharge is configured
- business language, as in ShipmentCreation.feature

Step definitions:
- Cucumber expressions ({decimal}, {string}) and xUnit asserts
- share state between steps through an injected context class, as ScanSyncSteps uses CourierScanContext; no static fields
- no hooks are needed, because the feature uses no external resources
- work out each expected price from the business rule, not from the production code

Create tests/ApexPost.Specs/Features/HeavyParcelSurcharge.feature and tests/ApexPost.Specs/StepDefinitions/HeavyParcelSurchargeSteps.cs, plus a context class under tests/ApexPost.Specs/Support/ if you need one. Do not modify production code, the project file or any other file. Do not add packages.
```

The prompt gives the rule in business words and a fixed fictional rate, so Copilot had to work out the expected prices itself and the review could check them by hand.

### What Copilot produced

Three new files: the feature (Background, a Scenario Outline with four Examples rows, a letter scenario and a refusal scenario), `HeavyParcelSurchargeSteps.cs` and `HeavyParcelSurchargeContext.cs`. Copilot's reply:

```text
Implemented exactly the requested files:
•	tests/ApexPost.Specs/Features/HeavyParcelSurcharge.feature
•	tests/ApexPost.Specs/StepDefinitions/HeavyParcelSurchargeSteps.cs
•	tests/ApexPost.Specs/Support/HeavyParcelSurchargeContext.cs
What’s included:
•	Background with:
•	rate for "ZONE-A" + "Standard" + 100.00 INR base + 10.00 INR/kg
•	heavy-parcel surcharge 50.00 INR
•	Scenario Outline for parcel weights:
•	29.99 -> 399.90
•	30 -> 400.00
•	30.01 -> 450.10
•	31 -> 460.00
•	Scenario for a letter over 30 kg (31 -> 410.00, no surcharge)
•	Scenario for parcel over 30 kg with no surcharge configured (pricing refused)
Step definitions use:
•	Reqnroll Cucumber expressions with {decimal} and {string}
•	injected context class (no static fields)
•	direct call to ShippingPricingService.CalculatePrice
•	xUnit assertions
•	no hooks
Validation:
•	tests/ApexPost.Specs builds successfully.
Note: the existing tests/ApexPost.Specs/ApexPost.Specs.csproj currently contains: <ReqnrollFeatureFile Remove="Features\HeavyParcelSurcharge.feature" /> I did not modify it per your instruction, so this new feature is excluded from test discovery until that project setting is changed.
```

**Expected values, checked by hand** (100.00 + kg × 10.00, plus 50.00 for a parcel over 30 kg): 29.99 kg → 399.90, 30 kg → 400.00, 30.01 kg → 450.10, 31 kg → 460.00, 31 kg letter → 410.00. All five were correct.

### The project-file side effect (rejected)

The note at the end of Copilot's reply was not accurate: the line was not in the committed project file. During the agent run, `ApexPost.Specs.csproj` gained `<ReqnrollFeatureFile Remove="Features\HeavyParcelSurcharge.feature" />`, which removed the new feature from the build. The scenarios were not compiled, and only the existing 9 Specs tests ran, so "builds successfully" proved nothing about the feature.

Evidence from the session: when Copilot created the `.feature` file (15:28:55 UTC), its automatic add-to-project step reported `Success: False` (the two `.cs` files reported `Success: True`); the file is already included by Reqnroll's `**\*.feature` pattern. No Copilot edit targeted the project file. The project file was written at 15:31:38.962 UTC, 0.26 s after the agent's build was confirmed, with the new element written in Windows line endings inside an otherwise LF file. This points to Visual Studio's project system, as a side effect of the agent's file creation; its internal reason is not logged.

**Correction:** with Visual Studio closed, the project file was restored to its committed version (`git restore`), the solution was reopened, and the line did not return. Discovery then found 15 Specs tests, and Copilot's six scenarios, still exactly as generated, **passed 6 of 6** (15:49:22 UTC).

### Review decisions

| Decision | Item | Reason |
|---|---|---|
| **Kept** | Feature structure: Background, Scenario Outline (29.99, 30, 30.01, 31 kg), letter scenario, refusal scenario; scenario names; When and Then step wording | Matches the business rule and the requested boundaries |
| **Kept** | All five expected prices | Correct by hand calculation, not copied from the code |
| **Kept** | Context class injected into the step class, no static fields, no hooks | Meets the assignment's context-injection rule; nothing needs setting up or cleaning up |
| **Kept** | Then steps: exact amount, currency and no error; for the refusal, exact exception type, reason and no price | Asserts behaviour, not "no exception" |
| **Rejected** | The project-file exclusion | Excluded the feature from the build; restored to the committed file |
| **Rejected and replaced** | The When step hard-coded `"ZONE-A"` and `ShippingServiceLevel.Standard` | If the Background changed, the shipment would no longer match the rate and would fail as "no rate configured" instead of testing the surcharge. The step now uses the Background rate's zone and service level |
| **Rejected and replaced** | `catch (Exception)` in the When step | Swallowed every error, so an unexpected failure surfaced only in a later Then step. Now `catch (InvalidOperationException)`, the refusal under test; any other exception fails the When step |
| **Rejected** | The context's `CurrentItem` property and `CreatePricingService` helper | `CurrentItem` was written and never read; the helper was no longer needed once the When step builds the service from the single rate. The rates list became one `Rate` |
| **Changed** | Shipment ID `SHIP-HEAVY-001` → `APX-SHIP-3001` | The repository's fictional `APX-` prefix |
| **Changed** | Background wording → `Given the Standard rate to zone "ZONE-A" is 100.00 INR plus 10.00 INR per kg` | Business language instead of "shipping uses destination zone … at service level … per-kilogram charge" |
| **Changed** | Feature layout | Background, Scenario and Examples at column 0, a description line and a final newline, as in the other feature files |
| **Added** | Marker comments; a note on the deliberate overlap with the TDD-built rule and the counter-workflow limitation | Task 6 marking and an honest scope statement |
| **Not added** | A 31 kg letter priced with no surcharge configured | Optional; left out to keep the requested scope |

**Result:** 6 scenario runs, **6 of 6 passed** (15:52:17 UTC), and the whole Specs project 15 of 15 (15:52:20 UTC), with 0 warnings. Two Copilot tests had already been proven by break-and-restore in Prompt A, so none was run for this feature.

## Lessons

- **Copilot's summary is not evidence.** Both replies said the work was built and validated; only `git status`, `git diff` and an independent test run showed that one feature was not being run at all.
- **Typical draft weaknesses found:** a weak exception assertion, expected values read back from the test's own input, missing edge cases, hard-coded data that duplicated the Background, and catch-all error handling.
- **Agent mode changes more than it reports.** After any agent run, check every changed file, including project files.

## Verification

Full suite after M6, 2026-11-14: `dotnet build ApexPost.slnx --no-incremental` (15:56:08–15:56:34 UTC) gave 0 warnings and 0 errors; `dotnet test ApexPost.slnx --no-build` (15:56:34–15:58:11 UTC) gave **1,660 passed, 0 failed, 0 skipped**, including Application 162 and Specs 15.
