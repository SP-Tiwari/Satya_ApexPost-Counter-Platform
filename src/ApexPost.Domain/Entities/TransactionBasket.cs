using ApexPost.Domain;

namespace ApexPost.Domain.Entities;

public sealed class TransactionBasket
{
	// Held as a list rather than a dictionary so that each rate can be stored
	// as a row of its own. The public TaxRates dictionary is built from it.
	private readonly List<TransactionTaxRate> _taxRates = [];
	private readonly List<TransactionLineItem> _lineItems = [];
	private TransactionPaymentStatus _paymentStatus =
		TransactionPaymentStatus.Open;
	private BranchCode? _branchCode;

	// A basket records at most one reversal, so the void or refund notification
	// is derived from this reason and the payment status.
	private string? _reversalReason;

	public Guid Id { get; }
	public string Currency { get; }

	// Null only for transactions persisted before schema version 5, when no
	// transaction time was recorded.
	public DateTimeOffset? OccurredAtUtc { get; }

	// Counter, clerk and payment method of a counter sale. Null for
	// transactions persisted before schema version 7.
	public CounterSaleDetails? SaleDetails { get; }
	public BranchCode? BranchCode
	{
		get
		{
			lock (DomainMutationLock.Gate)
				return _branchCode;
		}
	}
	public TransactionPaymentStatus PaymentStatus
	{
		get
		{
			lock (DomainMutationLock.Gate)
				return _paymentStatus;
		}
		private set => _paymentStatus = value;
	}

	public IReadOnlyList<TransactionLineItem> LineItems =>
		GetLineItems();

	public IReadOnlyDictionary<TaxCategory, decimal> TaxRates
	{
		get
		{
			lock (DomainMutationLock.Gate)
				return CopyTaxRates();
		}
	}

	public IReadOnlyList<DomainNotification> Notifications
	{
		get
		{
			lock (DomainMutationLock.Gate)
				return GetNotifications();
		}
	}

	public TransactionBasketSnapshot GetSnapshot()
	{
		lock (DomainMutationLock.Gate)
		{
			return new TransactionBasketSnapshot(
				Id,
				Currency,
				GetSubtotal(),
				GetTax(),
				Total,
				CopyTaxRates(),
				_lineItems.ToArray(),
				_branchCode,
				_paymentStatus,
				GetNotifications(),
				OccurredAtUtc,
				SaleDetails);
		}
	}

	public Money Subtotal =>
		GetSubtotal();

	public Money Tax =>
		GetTax();

	public Money Total
	{
		get
		{
			lock (DomainMutationLock.Gate)
				return new(Subtotal.Amount + Tax.Amount, Currency);
		}
	}

	public Money TotalFor(TransactionItemType itemType)
	{
		if (!Enum.IsDefined(itemType))
			throw new ArgumentOutOfRangeException(nameof(itemType));

		lock (DomainMutationLock.Gate)
			return GetTotalFor(itemType);
	}

	internal (Money RetailSales, Money ServiceSales)? GetPaidSalesTotals()
	{
		lock (DomainMutationLock.Gate)
		{
			if (PaymentStatus != TransactionPaymentStatus.Paid)
				return null;

			return (
				GetTotalFor(TransactionItemType.Goods),
				GetTotalFor(TransactionItemType.Service));
		}
	}

	// Used by EF Core when it materializes a stored transaction.
	private TransactionBasket()
	{
		Currency = null!;
	}

	public TransactionBasket(
		string currency,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		BranchCode? branchCode = null,
		DateTimeOffset? occurredAtUtc = null)
		: this(Guid.NewGuid(), currency, taxRates, branchCode, occurredAtUtc)
	{
	}

	public TransactionBasket(
		Guid id,
		string currency,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		BranchCode? branchCode,
		DateTimeOffset? occurredAtUtc = null,
		CounterSaleDetails? saleDetails = null)
	{
		ArgumentNullException.ThrowIfNull(taxRates);

		if (id == Guid.Empty)
			throw new ArgumentException(
				"Transaction ID cannot be empty.", nameof(id));

		Id = id;
		Currency = new Money(0m, currency).Currency;
		OccurredAtUtc = occurredAtUtc?.ToUniversalTime();
		SaleDetails = saleDetails;
		_branchCode = branchCode;

		foreach (var (taxCategory, taxRate) in taxRates)
		{
			if (!Enum.IsDefined(taxCategory))
				throw new ArgumentOutOfRangeException(nameof(taxRates));

			if (taxRate < 0m)
				throw new ArgumentOutOfRangeException(
					nameof(taxRates), "Tax rates cannot be negative.");

			_taxRates.Add(new TransactionTaxRate(taxCategory, taxRate));
		}
	}

	public static TransactionBasket Reconstitute(
		string currency,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		IEnumerable<TransactionLineItem> lineItems,
		TransactionPaymentStatus paymentStatus,
		IEnumerable<DomainNotification> notifications,
		BranchCode? branchCode = null,
		DateTimeOffset? occurredAtUtc = null)
		=> Reconstitute(
			Guid.NewGuid(),
			currency,
			taxRates,
			lineItems,
			paymentStatus,
			notifications,
			branchCode,
			occurredAtUtc);

	public static TransactionBasket Reconstitute(
		Guid id,
		string currency,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		IEnumerable<TransactionLineItem> lineItems,
		TransactionPaymentStatus paymentStatus,
		IEnumerable<DomainNotification> notifications,
		BranchCode? branchCode = null,
		DateTimeOffset? occurredAtUtc = null,
		CounterSaleDetails? saleDetails = null)
	{
		ArgumentNullException.ThrowIfNull(lineItems);
		ArgumentNullException.ThrowIfNull(notifications);

		if (!Enum.IsDefined(paymentStatus))
			throw new ArgumentOutOfRangeException(nameof(paymentStatus));

		var restoredNotifications = notifications.ToArray();
		var basket = new TransactionBasket(
			id, currency, taxRates, branchCode, occurredAtUtc, saleDetails);
		foreach (var lineItem in lineItems)
			basket.AddLineItem(lineItem);

		switch (paymentStatus)
		{
			case TransactionPaymentStatus.Open:
				EnsureNoNotifications(restoredNotifications);
				break;
			case TransactionPaymentStatus.Paid:
				EnsureNoNotifications(restoredNotifications);
				basket.Pay();
				break;
			case TransactionPaymentStatus.Voided:
				basket.Void(GetRequiredReason<TransactionVoidedNotification>(
					restoredNotifications));
				break;
			case TransactionPaymentStatus.Refunded:
				basket.Pay();
				basket.Refund(GetRequiredReason<TransactionRefundedNotification>(
					restoredNotifications));
				break;
		}

		return basket;
	}

	public void AddLineItem(TransactionLineItem lineItem)
	{
		ArgumentNullException.ThrowIfNull(lineItem);

		lock (DomainMutationLock.Gate)
		{
			EnsureOpen();

			if (!string.Equals(lineItem.UnitPrice.Currency, Currency, StringComparison.Ordinal))
				throw new ArgumentException(
					"Transaction line currency must match the basket currency.",
					nameof(lineItem));

			if (lineItem.Type != TransactionItemType.MoneyOrder &&
				!_taxRates.Exists(taxRate => taxRate.Category == lineItem.TaxCategory))
				throw new InvalidOperationException(
					$"No tax rate is configured for category '{lineItem.TaxCategory}'.");

			_lineItems.Add(lineItem);
		}
	}

	internal void AssignBranch(BranchCode branchCode)
	{
		ArgumentNullException.ThrowIfNull(branchCode);
		lock (DomainMutationLock.Gate)
		{
			EnsureBranchUnderLock(branchCode);
			_branchCode = branchCode;
		}
	}

	internal void EnsureBranch(BranchCode branchCode)
	{
		ArgumentNullException.ThrowIfNull(branchCode);
		lock (DomainMutationLock.Gate)
			EnsureBranchUnderLock(branchCode);
	}

	public void Pay()
	{
		lock (DomainMutationLock.Gate)
		{
			EnsureOpen();

			if (_lineItems.Count == 0)
				throw new InvalidOperationException(
					"An empty transaction basket cannot be paid.");

			PaymentStatus = TransactionPaymentStatus.Paid;
		}
	}

	internal void Void(string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
			throw new ArgumentException(
				"A reason is required to void a transaction.", nameof(reason));

		lock (DomainMutationLock.Gate)
		{
			if (PaymentStatus is not TransactionPaymentStatus.Open and
				not TransactionPaymentStatus.Paid)
			{
				throw new InvalidOperationException(
					$"A transaction can only be voided when its status is Open or Paid; current status is '{PaymentStatus}'.");
			}

			_reversalReason = reason.Trim();
			PaymentStatus = TransactionPaymentStatus.Voided;
		}
	}

	internal void Refund(string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
			throw new ArgumentException(
				"A reason is required to refund a transaction.", nameof(reason));

		lock (DomainMutationLock.Gate)
		{
			if (PaymentStatus != TransactionPaymentStatus.Paid)
			{
				throw new InvalidOperationException(
					$"A transaction can only be refunded when its status is Paid; current status is '{PaymentStatus}'.");
			}

			_reversalReason = reason.Trim();
			PaymentStatus = TransactionPaymentStatus.Refunded;
		}
	}

	private IReadOnlyList<TransactionLineItem> GetLineItems()
	{
		lock (DomainMutationLock.Gate)
			return _lineItems.AsReadOnly();
	}

	private Money GetSubtotal()
	{
		lock (DomainMutationLock.Gate)
			return new(_lineItems.Sum(lineItem => lineItem.Subtotal.Amount), Currency);
	}

	private Money GetTax()
	{
		lock (DomainMutationLock.Gate)
			return new(_lineItems.Sum(GetLineTax), Currency);
	}

	private Money GetTotalFor(TransactionItemType itemType)
	{
		var subtotal = _lineItems
			.Where(lineItem => lineItem.Type == itemType)
			.Sum(lineItem => lineItem.Subtotal.Amount);
		var tax = _lineItems
			.Where(lineItem => lineItem.Type == itemType)
			.Sum(GetLineTax);

		return new Money(subtotal + tax, Currency);
	}

	private decimal GetLineTax(TransactionLineItem lineItem) =>
		lineItem.Type switch
		{
			TransactionItemType.MoneyOrder => 0m,
			_ => lineItem.Subtotal.Amount * _taxRates
				.First(taxRate => taxRate.Category == lineItem.TaxCategory).Rate
		};

	private Dictionary<TaxCategory, decimal> CopyTaxRates() =>
		_taxRates.ToDictionary(taxRate => taxRate.Category, taxRate => taxRate.Rate);

	private DomainNotification[] GetNotifications() =>
		_paymentStatus switch
		{
			TransactionPaymentStatus.Voided =>
				[new TransactionVoidedNotification(_reversalReason!)],
			TransactionPaymentStatus.Refunded =>
				[new TransactionRefundedNotification(_reversalReason!)],
			_ => []
		};

	private static string GetRequiredReason<TNotification>(
		IReadOnlyList<DomainNotification> notifications)
		where TNotification : DomainNotification
	{
		if (notifications.Count != 1 ||
			notifications[0] is not TNotification notification)
		{
			throw new ArgumentException(
				$"The restored payment status requires exactly one {typeof(TNotification).Name}.");
		}

		return notification switch
		{
			TransactionVoidedNotification voided => voided.Reason,
			TransactionRefundedNotification refunded => refunded.Reason,
			_ => throw new ArgumentException(
				"Restored transaction notification type is invalid.")
		};
	}

	private static void EnsureNoNotifications(
		IReadOnlyCollection<DomainNotification> notifications)
	{
		if (notifications.Count != 0)
			throw new ArgumentException(
				"Open and Paid transaction baskets cannot contain void/refund notifications.");
	}

	private void EnsureOpen()
	{
		if (PaymentStatus != TransactionPaymentStatus.Open)
		{
			throw new InvalidOperationException(
				$"This operation is only valid for an Open transaction; current status is '{PaymentStatus}'.");
		}
	}

	private void EnsureBranchUnderLock(BranchCode branchCode)
	{
		if (_branchCode is not null && _branchCode != branchCode)
			throw new InvalidOperationException(
				"A transaction cannot be processed at a different branch.");
	}
}
