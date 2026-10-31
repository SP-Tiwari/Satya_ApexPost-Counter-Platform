using ApexPost.Application.Caching;
using ApexPost.Application.Events;
using ApexPost.Application.Verification;

namespace ApexPost.Application.Tests.TestDoubles;

// Builds services over the in-memory store, so each test needs only its data.
internal static class TestServices
{
	public static TransactionApplicationService Transactions(
		IApexPostRepository repository,
		TimeProvider? timeProvider = null,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null)
	{
		var store = new SnapshotStore(repository);
		return new TransactionApplicationService(
			store, store, store, store, store, timeProvider, events, lowStockThreshold);
	}

	public static StockAdjustmentApplicationService StockAdjustment(
		IApexPostRepository repository,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null,
		TimeProvider? timeProvider = null)
	{
		var store = new SnapshotStore(repository);
		return new StockAdjustmentApplicationService(
			store, store, store, store, store, events, lowStockThreshold, timeProvider);
	}

	public static MoneyOrderApplicationService MoneyOrders(
		IApexPostRepository repository,
		Money? highValueThreshold)
	{
		var store = new SnapshotStore(repository);
		return new MoneyOrderApplicationService(store, store, highValueThreshold);
	}

	public static ProductCatalogueApplicationService ProductCatalogue(
		IApexPostRepository repository,
		ReferenceDataCache referenceData)
	{
		var store = new SnapshotStore(repository);
		return new ProductCatalogueApplicationService(
			store, store, store, referenceData);
	}

	public static LowStockReportApplicationService LowStockReport(
		IApexPostRepository repository,
		int? threshold)
	{
		var store = new SnapshotStore(repository);
		return new LowStockReportApplicationService(store, store, threshold);
	}

	public static DailyBranchSalesReportApplicationService DailySalesReport(
		IApexPostRepository repository)
	{
		var store = new SnapshotStore(repository);
		return new DailyBranchSalesReportApplicationService(store, store);
	}

	public static ShippingVolumeReportApplicationService ShippingVolumeReport(
		IApexPostRepository repository)
	{
		var store = new SnapshotStore(repository);
		return new ShippingVolumeReportApplicationService(store, store);
	}

	public static BranchAuditorReportApplicationService BranchAuditorReport(
		IApexPostRepository repository)
	{
		var store = new SnapshotStore(repository);
		return new BranchAuditorReportApplicationService(store, store);
	}

	public static MixedBasketApplicationService MixedBasket(
		IApexPostRepository repository,
		IReadOnlyDictionary<TaxCategory, decimal> taxRates,
		IReadOnlyDictionary<ShippingServiceLevel, Weight> maximumParcelWeights,
		TaxCategory? shippingTaxCategory = null,
		TimeProvider? timeProvider = null,
		MoneyOrderBasketSettings? moneyOrderSettings = null,
		IIdentityVerificationService? identityVerification = null,
		DomainEventPublisher? events = null,
		int? lowStockThreshold = null,
		IReadOnlyCollection<string>? paymentMethods = null)
	{
		var store = new SnapshotStore(repository);
		return new MixedBasketApplicationService(
			store,
			store,
			store,
			store,
			store,
			store,
			taxRates,
			maximumParcelWeights,
			shippingTaxCategory,
			timeProvider,
			moneyOrderSettings,
			identityVerification,
			events,
			lowStockThreshold,
			paymentMethods);
	}
}
