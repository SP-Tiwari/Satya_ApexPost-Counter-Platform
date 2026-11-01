namespace ApexPost.Application.Caching;

// Where the singleton cache loads from. It has no request of its own, so an
// implementation must not rely on a request's scoped services.
public interface IReferenceDataSource
{
	Task<(IReadOnlyList<Product> Products, IReadOnlyList<ShippingRate> ShippingRates)>
		LoadAsync(CancellationToken cancellationToken = default);
}
