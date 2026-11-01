# ADR 0005: Configured shipping pricing and limits

- Status: Accepted
- Date: 2026-10-25

## Context

Shipping needs a price for a destination zone and service level, as well as a maximum weight for parcels. Applicable prices, taxes, and parcel limits are business policy and are not supplied by the assignment or by authoritative production configuration in the application. Hard-coded values or implicit fallbacks could produce incorrect charges or accept disallowed parcels.

## Decision

Represent shipping rates using the existing `ShippingRate` value object, keyed by destination zone and service level, with a base price and per-kilogram charge in a common currency. `ShippingPricingService` calculates `base price + weight in kilograms × per-kilogram charge`. The application reads rates from the JSON snapshot and obtains tax rates, shipping tax category, and maximum parcel weights from explicit configuration:

- `Transactions:TaxRates:<TaxCategory>`
- `Shipping:TaxCategory`
- `Shipping:MaximumParcelWeightKilograms:<ShippingServiceLevel>`

There are no production defaults for these prices, tax values, or parcel limits. Missing or mismatched rates/configuration cause shipping processing to fail rather than selecting an invented value. Parcel limits apply to parcels; letters are exempt from this parcel-specific limit.

## Alternatives considered

- **A strategy class per service level (`StandardPricing`, `ExpressPricing`, …).** That bakes prices into code and needs a release for every price change. The rate table is data, and the formula is the only fixed part.
- **Pricing delegates (`Func<ShippingItem, Money>`).** Flexible, but there was no requirement for per-call custom formulas, and data-driven rates are easier to audit.
- **Defaults when configuration is missing.** Rejected: a guessed price or weight limit is worse than a disabled workflow.

## Consequences

Operators must configure the applicable tax and weight settings and provision rate records before enabling the corresponding shipping workflow. Configuration mistakes are surfaced instead of silently substituted. Demo data, if explicitly initialized, contains visibly fictional rate fixtures and is not production policy.

Relevant implementation: `src/ApexPost.Domain/ValueObjects/ShippingRate.cs`, `src/ApexPost.Domain/Services/ShippingPricingService.cs`, `src/ApexPost.Domain/Rules/ShippingWeightLimitRule.cs`, `src/ApexPost.Application/Services/MixedBasketApplicationService.cs`, `src/ApexPost.Infrastructure/Persistence/Sql/Configurations/ShippingConfigurations.cs`, and `src/ApexPost.Web/Program.cs`.
