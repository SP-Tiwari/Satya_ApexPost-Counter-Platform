using ApexPost.Domain.ValueObjects;

namespace ApexPost.Specs.Support;

// Copilot-assisted: drafted with GitHub Copilot (Prompt B) and then reviewed;
// COPILOT-LOG.md records what was kept, changed and rejected.
//
// State shared by the steps of one heavy-parcel scenario. Reqnroll creates one
// instance per scenario and injects it into the step class, so steps never
// share state through static fields.
public sealed class HeavyParcelSurchargeContext
{
	internal ShippingRate? Rate { get; set; }

	internal Money? HeavyParcelSurcharge { get; set; }

	internal Money? LastPrice { get; set; }

	internal Exception? LastError { get; set; }
}
