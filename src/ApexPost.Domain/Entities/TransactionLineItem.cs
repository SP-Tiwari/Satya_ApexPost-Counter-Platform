
namespace ApexPost.Domain.Entities;

public sealed class TransactionLineItem
{
	public string Description { get; }
	public TransactionItemType Type { get; }
	public Money UnitPrice { get; }
	public int Quantity { get; }
	public TaxCategory TaxCategory { get; }
	public Sku? ProductSku { get; }
	public ShippingItem? ShippingItem { get; }
	public MoneyOrder? MoneyOrder { get; }

	// Money-order principal lines are untaxed; the basket ignores this nominal
	// category for them, and no tax rate needs to be configured for it.
	private const TaxCategory MoneyOrderNominalTaxCategory = TaxCategory.ZeroRated;

	// Used by EF Core when it materializes a stored row.
	private TransactionLineItem()
	{
		Description = null!;
		UnitPrice = null!;
	}

	public TransactionLineItem(MoneyOrder moneyOrder)
		: this(
			$"Money order to {Required(moneyOrder).Recipient}",
			TransactionItemType.MoneyOrder,
			moneyOrder.Amount,
			1,
			MoneyOrderNominalTaxCategory,
			productSku: null,
			shippingItem: null,
			moneyOrder: moneyOrder)
	{
	}

	private static MoneyOrder Required(MoneyOrder moneyOrder)
	{
		ArgumentNullException.ThrowIfNull(moneyOrder);
		return moneyOrder;
	}

	public TransactionLineItem(
		string description,
		TransactionItemType type,
		Money unitPrice,
		int quantity,
		TaxCategory taxCategory)
		: this(description, type, unitPrice, quantity, taxCategory, null, null)
	{
	}

	public TransactionLineItem(
		string description,
		TransactionItemType type,
		Money unitPrice,
		int quantity,
		TaxCategory taxCategory,
		Sku? productSku,
		ShippingItem? shippingItem = null,
		MoneyOrder? moneyOrder = null)
	{
		if (string.IsNullOrWhiteSpace(description))
			throw new ArgumentException(
				"Transaction line description is required.", nameof(description));

		if (!Enum.IsDefined(type))
			throw new ArgumentOutOfRangeException(nameof(type));

		ArgumentNullException.ThrowIfNull(unitPrice);

		if (quantity <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(quantity), "Transaction line quantity must be greater than zero.");

		if (!Enum.IsDefined(taxCategory))
			throw new ArgumentOutOfRangeException(nameof(taxCategory));

		Description = description.Trim();
		Type = type;
		UnitPrice = unitPrice;
		Quantity = quantity;
		TaxCategory = taxCategory;
		ProductSku = productSku;
		if (shippingItem is not null && type != TransactionItemType.Service)
			throw new ArgumentException(
				"Shipping items must be represented by a service line.",
				nameof(shippingItem));
		ShippingItem = shippingItem;

		if ((type == TransactionItemType.MoneyOrder) != (moneyOrder is not null))
			throw new ArgumentException(
				"A money-order line must reference exactly one money order.",
				nameof(moneyOrder));
		if (moneyOrder is not null &&
			(quantity != 1 ||
				unitPrice != moneyOrder.Amount ||
				productSku is not null ||
				shippingItem is not null))
		{
			throw new ArgumentException(
				"A money-order line must have quantity 1, the money-order amount as its price, and no product or shipping item.",
				nameof(moneyOrder));
		}
		MoneyOrder = moneyOrder;
	}

	public TransactionLineItem(Product product, int quantity)
	{
		ArgumentNullException.ThrowIfNull(product);

		if (quantity <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(quantity), "Transaction line quantity must be greater than zero.");

		Description = product.Name;
		Type = TransactionItemType.Goods;
		UnitPrice = product.UnitPrice;
		Quantity = quantity;
		TaxCategory = product.TaxCategory;
		ProductSku = product.Sku;
		ShippingItem = null;
	}

	public Money Subtotal =>
		new(UnitPrice.Amount * Quantity, UnitPrice.Currency);
}
