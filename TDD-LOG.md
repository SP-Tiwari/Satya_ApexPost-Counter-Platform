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

Not started.

## REFACTOR

Not started.
