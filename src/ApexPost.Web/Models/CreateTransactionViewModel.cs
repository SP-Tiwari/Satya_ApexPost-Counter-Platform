
using System.ComponentModel.DataAnnotations;

namespace ApexPost.Web.Models;

public sealed class CreateTransactionViewModel
{
	public Guid TransactionId { get; set; }

	// One entry per goods row, aligned by position (repeated form fields).
	public List<string?> GoodsSku { get; set; } = [];
	public List<int?> GoodsQuantity { get; set; } = [];

	public string? CounterTerminalId { get; set; }
	public string? PaymentMethod { get; set; }
	public bool IncludeShipping { get; set; }
	public ShippingItemType? ShippingType { get; set; }
	public decimal? WeightKilograms { get; set; }
	public decimal? LengthCm { get; set; }
	public decimal? WidthCm { get; set; }
	public decimal? HeightCm { get; set; }
	public string? DestinationZone { get; set; }
	public ShippingServiceLevel? ServiceLevel { get; set; }

	public bool IncludeMoneyOrder { get; set; }

	[Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
	public decimal? MoneyOrderAmount { get; set; }

	[StringLength(100)]
	public string? MoneyOrderSender { get; set; }

	[StringLength(100)]
	public string? MoneyOrderRecipient { get; set; }

	[StringLength(50)]
	public string? MoneyOrderDocumentType { get; set; }

	// Sent once for verification and masking; never redisplayed or stored.
	[StringLength(100)]
	public string? MoneyOrderDocumentIdentifier { get; set; }

	public IReadOnlyList<ApexPost.Application.Persistence.SaleFormProduct> Products { get; set; } = [];
	public ApexPost.Application.Persistence.SaleFormBranch? Branch { get; set; }
	public IReadOnlyList<ApexPost.Application.Persistence.SaleFormShippingRate> ShippingRates { get; set; } = [];
	public SaleFormMoneyOrderTerms? MoneyOrders { get; set; }
	public IReadOnlyList<ApexPost.Application.Persistence.SaleFormCounter> CounterTerminals { get; set; } = [];
	public IReadOnlyList<string> PaymentMethods { get; set; } = [];
	public bool CanSubmit { get; set; }
}
