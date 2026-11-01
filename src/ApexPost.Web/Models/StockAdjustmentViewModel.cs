using System.ComponentModel.DataAnnotations;

namespace ApexPost.Web.Models;

public sealed class StockAdjustmentViewModel
{
	[Required]
	[StringLength(30)]
	public string? ProductSku { get; set; }

	[Required]
	public int? QuantityChange { get; set; }

	public string? BranchName { get; set; }
	public string? BranchCode { get; set; }
	public IReadOnlyList<ApexPost.Application.Persistence.StockRow> BranchStocks { get; set; } = [];
}
