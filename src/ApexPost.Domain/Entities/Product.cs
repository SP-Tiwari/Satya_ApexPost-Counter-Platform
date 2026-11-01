

namespace ApexPost.Domain.Entities;

public sealed class Product
{
	public Sku Sku { get; }
	public string Name { get; }
	public ProductCategory Category { get; }
	public Money UnitPrice { get; }
	public TaxCategory TaxCategory { get; }

	// Used by EF Core when it materializes a stored row.
	private Product()
	{
		Sku = null!;
		Name = null!;
		UnitPrice = null!;
	}

	public Product(
		Sku sku,
		string name,
		ProductCategory category,
		Money unitPrice,
		TaxCategory taxCategory)
	{
		ArgumentNullException.ThrowIfNull(sku);
		ArgumentNullException.ThrowIfNull(unitPrice);

		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Product name cannot be empty.", nameof(name));

		if (!Enum.IsDefined(category))
			throw new ArgumentOutOfRangeException(nameof(category));

		if (!Enum.IsDefined(taxCategory))
			throw new ArgumentOutOfRangeException(nameof(taxCategory));

		Name = name.Trim();
		Sku = sku;
		Category = category;
		UnitPrice = unitPrice;
		TaxCategory = taxCategory;
	}
}
