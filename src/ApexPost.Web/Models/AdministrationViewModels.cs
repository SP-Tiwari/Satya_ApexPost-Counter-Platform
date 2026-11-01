using System.ComponentModel.DataAnnotations;

namespace ApexPost.Web.Models;

public sealed class AdministrationViewModel
{
	public IReadOnlyList<ApexPost.Application.Persistence.AdministrationBranchRow> Branches { get; init; } = [];
	public IReadOnlyList<ApexPost.Application.Persistence.AdministrationStaffRow> Staff { get; init; } = [];
	public IReadOnlyList<ApexPost.Application.Persistence.AdministrationProductRow> Products { get; init; } = [];
}

public sealed class CreateStaffViewModel
{
	[Required, StringLength(50)] public string? Id { get; set; }
	[Required, StringLength(100)] public string? Name { get; set; }
	[Required, EmailAddress] public string? Email { get; set; }
	[Required] public StaffRole? Role { get; set; }
	[Required, StringLength(10)] public string? HomeBranchCode { get; set; }
}

public sealed class CreateBranchViewModel
{
	[Required, StringLength(10)] public string? Code { get; set; }
	[Required, StringLength(100)] public string? Name { get; set; }
	[Required, StringLength(100)] public string? Region { get; set; }
	[Required, StringLength(100)] public string? OpeningHours { get; set; }
	[Required, StringLength(50)] public string? TerminalId { get; set; }
	[Required, StringLength(100)] public string? TerminalName { get; set; }
}

public sealed class CreateProductViewModel
{
	[Required, StringLength(30)] public string? Sku { get; set; }
	[Required, StringLength(150)] public string? Name { get; set; }
	[Required] public ProductCategory? Category { get; set; }
	[Range(typeof(decimal), "0", "9999999")] public decimal? UnitPrice { get; set; }
	[Required, StringLength(3), MinLength(3)] public string? Currency { get; set; }
	[Required] public TaxCategory? TaxCategory { get; set; }
}
