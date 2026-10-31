namespace ApexPost.Application.Persistence;

// The priced catalogue: products and the shipping rates that price a shipment.
public interface IProductRepository
{
	Task<Product?> FindBySkuAsync(
		Sku sku,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<Product>> FindBySkusAsync(
		IReadOnlyCollection<Sku> skus,
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<Product>> GetProductsAsync(
		CancellationToken cancellationToken = default);

	Task<IReadOnlyList<ShippingRate>> GetShippingRatesAsync(
		CancellationToken cancellationToken = default);

	void Add(Product product);

	// The one set-based write: it applies immediately rather than being staged
	// for SaveChangesAsync, so callers run it inside
	// IUnitOfWork.ExecuteInTransactionAsync. Returns the rows changed.
	Task<int> RevisePricesAsync(
		ProductCategory category,
		decimal multiplier,
		CancellationToken cancellationToken = default);
}
