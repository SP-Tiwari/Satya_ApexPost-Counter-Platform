using ApexPost.Domain.Reporting;

namespace ApexPost.Web.Models;

public sealed record DailySalesRowViewModel(
	string BranchCode,
	DateOnly BusinessDate,
	decimal RetailSales,
	decimal ServiceSales,
	decimal TotalSales,
	string Currency)
{
	public static DailySalesRowViewModel From(DailySalesReportRow row) => new(
		row.BranchCode.Value,
		row.BusinessDate,
		row.RetailSales.Amount,
		row.ServiceSales.Amount,
		row.TotalSales.Amount,
		row.TotalSales.Currency);
}

public sealed record LowStockRowViewModel(
	string BranchCode,
	string Sku,
	int Quantity,
	int Threshold)
{
	public static LowStockRowViewModel From(LowStockReportRow row) => new(
		row.BranchCode.Value,
		row.ProductSku.Value,
		row.Quantity,
		row.Threshold);
}

public sealed record ShippingVolumeRowViewModel(
	string DestinationZone,
	ShippingServiceLevel ServiceLevel,
	int ShipmentCount)
{
	public static ShippingVolumeRowViewModel From(ShippingVolumeReportRow row) => new(
		row.DestinationZone,
		row.ServiceLevel,
		row.ShipmentCount);
}

public sealed record BranchAuditorRowViewModel(
	string BranchCode,
	int MoneyOrderCount,
	int IdentityVerificationCount,
	int SuccessfulIdentityVerificationCount,
	int FailedIdentityVerificationCount,
	int CancelledMoneyOrderCount)
{
	public static BranchAuditorRowViewModel From(BranchAuditorReportRow row) => new(
		row.BranchCode.Value,
		row.MoneyOrderCount,
		row.IdentityVerificationCount,
		row.SuccessfulIdentityVerificationCount,
		row.FailedIdentityVerificationCount,
		row.CancelledMoneyOrderCount);
}
