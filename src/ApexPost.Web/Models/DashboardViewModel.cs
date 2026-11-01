using ApexPost.Application.Courier;
using ApexPost.Application.Persistence;

namespace ApexPost.Web.Models;

public sealed record DashboardSalesViewModel(
	DateOnly BusinessDate,
	int TransactionCount,
	DailySalesRowViewModel? Totals);

public sealed record DashboardViewModel(
	string StaffName,
	StaffRole Role,
	DashboardBranch Branch,
	int ProductCount,
	int ShippingZoneCount,
	DashboardSalesViewModel? TodaySales,
	IReadOnlyList<LowStockRowViewModel>? LowStock,
	IReadOnlyList<CourierTrackingResult>? RecentShipments,
	DateTimeOffset GeneratedAtUtc)
{
	public static DashboardViewModel From(BranchDashboard dashboard) => new(
		dashboard.StaffName,
		dashboard.Role,
		dashboard.Branch,
		dashboard.ProductCount,
		dashboard.ShippingZoneCount,
		dashboard.TodaySales is { } sales
			? new DashboardSalesViewModel(
				sales.BusinessDate,
				sales.TransactionCount,
				sales.Totals is { } totals ? DailySalesRowViewModel.From(totals) : null)
			: null,
		dashboard.LowStock?.Select(LowStockRowViewModel.From).ToArray(),
		dashboard.RecentShipments,
		dashboard.GeneratedAtUtc);
}
