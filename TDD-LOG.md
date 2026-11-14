# TDD log: heavy-parcel surcharge (Week 5, Task 4)

This log records one business rule built test-first, as RED → GREEN → REFACTOR. The testing strategy is in [TESTING-STRATEGY.md](TESTING-STRATEGY.md#3-tdd-the-inner-loop-m3-moq-m4-tdd).

## Requirement

**A parcel strictly over 30 kg pays a heavy-parcel surcharge.**

| Decision | Value |
|---|---|
| Surcharge | A flat **50.00 INR**, a fictional amount for tests and documentation |
| Boundary | Strictly over 30 kg. Exactly 30 kg and below pay no surcharge |
| Item types | Parcels only. Letters are exempt, as they are from the parcel weight limit (`ShippingWeightLimitRule`) |
| Where | `ShippingPricingService.CalculatePrice` (`src/ApexPost.Domain/Services/ShippingPricingService.cs`), the domain service only. The counter workflow and application configuration are not changed |
| Amount source | Supplied to the service, not hard-coded, because ADR 0005 treats prices as configured policy with no defaults |
| Missing surcharge | Fails closed: pricing a parcel over 30 kg without a surcharge throws, as a missing shipping rate already does |
| Currency | The surcharge must use the rate's currency; otherwise pricing throws |

Today's price is `base price + weight in kg × per-kg charge`, in the rate's currency. `ShippingItem.Type` (`Parcel` or `Letter`) already tells parcels from letters, so no new domain model is needed. All tests use the existing test rate of 10 INR plus 2 INR per kg.

## RED

**Baseline, before any new test (2026-11-14 10:51 UTC):** `dotnet test tests/ApexPost.Domain.Tests --filter "FullyQualifiedName~ShippingPricingServiceTests"` gave 7 passed, 0 failed, 0 skipped.

**New tests** in `tests/ApexPost.Domain.Tests/Services/ShippingPricingServiceTests.cs`. Each passes the surcharge to the service through a new constructor argument, `heavyParcelSurcharge`, by way of the helper `ServiceWithSurcharge`:

| Test | Input | Expected behaviour |
|---|---|---|
| `CalculatePrice_ParcelOfExactly30Kg_AddsNoSurcharge` | Parcel, 30 kg, surcharge 50 INR | 70.00 INR: no surcharge at the boundary |
| `CalculatePrice_ParcelBelow30Kg_AddsNoSurcharge` | Parcel, 29.99 kg, surcharge 50 INR | 69.98 INR |
| `CalculatePrice_ParcelOver30Kg_AddsTheSurcharge` (theory) | Parcel, 30.01 kg and 31 kg, surcharge 50 INR | 120.02 INR and 122.00 INR |
| `CalculatePrice_ParcelOver30KgWithNoConfiguredSurcharge_ThrowsInvalidOperationException` | Parcel, 31 kg, no surcharge | `InvalidOperationException`: "No heavy-parcel surcharge is configured for parcels over 30 kg." |
| `CalculatePrice_SurchargeInAnotherCurrency_ThrowsInvalidOperationException` | Parcel, 31 kg, surcharge 50 USD, INR rate | `InvalidOperationException`: "The heavy-parcel surcharge currency (USD) must match the shipping rate currency (INR)." |
| `CalculatePrice_LetterOver30Kg_AddsNoSurcharge` | Letter, 31 kg, surcharge 50 INR | 72.00 INR: letters are exempt |

That is 6 test methods and 7 test cases.

**Actual result (2026-11-14 10:52 UTC):** the same command fails at compile time with one error, so no test runs:

```text
tests\ApexPost.Domain.Tests\Services\ShippingPricingServiceTests.cs(206,4): error CS1739:
The best overload for 'ShippingPricingService' does not have a parameter named 'heavyParcelSurcharge'
```

**Why this is the expected failure:** the service has no way to receive a surcharge, which is the first thing the rule needs. The single error is in `ServiceWithSurcharge`, which every new test uses, so nothing else fails. No production code was changed. The 7 existing tests were not edited; the test file only gained lines.

**What the RED tests drive:**
- The over-30 kg, missing-surcharge and currency tests describe new behaviour. They will still fail on their assertions once the constructor exists, until the rule is implemented.
- The exactly-30 kg, below-30 kg and letter tests are boundary guards. The current formula already gives their expected prices, so they will pass as soon as the code compiles, and must stay green through GREEN and REFACTOR.

## GREEN

Only `src/ApexPost.Domain/Services/ShippingPricingService.cs` changed. The RED tests were not edited.

**Step 1: the input only (2026-11-14 10:56 UTC).** The constructor gained an optional parameter, `Money? heavyParcelSurcharge = null`, and nothing else. The tests compiled and failed on their assertions, exactly where RED predicted:

| Test case | Result |
|---|---|
| `CalculatePrice_ParcelOver30Kg_AddsTheSurcharge(30.01, 120.02)` | Failed: expected 120.02 INR, actual 70.02 INR |
| `CalculatePrice_ParcelOver30Kg_AddsTheSurcharge(31, 122)` | Failed: expected 122 INR, actual 72 INR |
| `CalculatePrice_ParcelOver30KgWithNoConfiguredSurcharge_ThrowsInvalidOperationException` | Failed: no exception was thrown |
| `CalculatePrice_SurchargeInAnotherCurrency_ThrowsInvalidOperationException` | Failed: no exception was thrown |

Pricing tests: **4 failed, 10 passed** (the 7 original tests and the 3 boundary guards).

**Step 2: the minimal implementation.** The constructor stores the surcharge. `CalculatePrice` then adds one branch after the existing formula, for a parcel whose weight is greater than `30m` kg:
- no configured surcharge throws `InvalidOperationException` ("No heavy-parcel surcharge is configured for parcels over 30 kg.");
- a surcharge in a currency other than the rate's throws `InvalidOperationException`;
- otherwise the surcharge amount is added.

Letters and parcels at or below 30 kg take the existing path unchanged. The 30 kg threshold is a literal for now, and is left for REFACTOR.

**Results (2026-11-14):**

| Run | Result |
|---|---|
| `dotnet test tests/ApexPost.Domain.Tests --filter "FullyQualifiedName~ShippingPricingServiceTests"` (10:56 UTC) | 14 passed, 0 failed, 0 skipped |
| `dotnet test tests/ApexPost.Domain.Tests` | 286 passed, 0 failed, 0 skipped |
| `dotnet build ApexPost.slnx --no-incremental`, then `dotnet test ApexPost.slnx --no-build` (10:57–10:59 UTC) | 0 warnings; **1,633 passed, 0 failed, 0 skipped**: Domain 286, Application 150, Courier.Sync 215, Courier.Console 15, Courier.Offline 88, Infrastructure 239, Api 337, Web 303 |

**Production code changed to make the rule testable:** the surcharge is passed to the constructor instead of being hard-coded. The amount is configured policy (ADR 0005), so the tests supply a fictional 50.00 INR, and the existing calls, which pass only the rates, compile unchanged.

**Effect on existing callers:** `MixedBasketApplicationService` and `DemoDataInitializer` still pass no surcharge. Until the surcharge is configured for the counter workflow, a counter sale of a parcel over 30 kg is refused, rather than priced without its surcharge; letters and parcels up to 30 kg are not affected. This only matters where an operator sets a parcel weight limit above 30 kg. Demo shipments weigh at most 3 kg, so the demo data is unaffected. Wiring the surcharge into the counter workflow is outside this exercise.

## REFACTOR

Only `src/ApexPost.Domain/Services/ShippingPricingService.cs` changed, and its behaviour did not. No test was edited.

| Change | Why |
|---|---|
| The literal `30m` became `HeavyParcelThreshold`, a `Weight` of 30 kg | The rule's threshold has a name, and lives in one place |
| The check uses `shippingItem.Weight.Exceeds(HeavyParcelThreshold)` | Reuses the domain's own strictly-greater comparison, the one `ShippingWeightLimitRule` uses, so the 30 kg boundary keeps its meaning |
| The surcharge branch moved into `HeavyParcelSurchargeFor(shippingItem, rate)`, which returns 0 when no surcharge applies, using early returns instead of nesting | `CalculatePrice` reads as the formula: base price + weight × per-kg charge + heavy-parcel surcharge |
| The missing-surcharge message takes the number from `HeavyParcelThreshold` | Message and rule cannot drift apart; the text is unchanged ("…over 30 kg.") |

**Deliberately not changed:**
- The constructor signature, so existing callers compile unchanged.
- The currency check still happens at pricing time. Checking in the constructor would refuse a service that never prices a heavy parcel, which would change behaviour.
- No new types or interfaces were added.

**Results (2026-11-14):**

| Run | Result |
|---|---|
| `dotnet test tests/ApexPost.Domain.Tests --filter "FullyQualifiedName~ShippingPricingServiceTests"` (11:02 UTC) | 14 passed, 0 failed, 0 skipped |
| `dotnet test tests/ApexPost.Domain.Tests` | 286 passed, 0 failed, 0 skipped |
| `dotnet build ApexPost.slnx --no-incremental`, then `dotnet test ApexPost.slnx --no-build` (11:03–11:04 UTC) | 0 warnings; **1,633 passed, 0 failed, 0 skipped**: Domain 286, Application 150, Courier.Sync 215, Courier.Console 15, Courier.Offline 88, Infrastructure 239, Api 337, Web 303 |

The same 14 pricing tests and 1,633 tests pass as after GREEN, so the refactoring kept the behaviour: the parcel-only rule, the strict boundary above 30 kg, the letter exemption, failing closed without a surcharge, and the currency check.
