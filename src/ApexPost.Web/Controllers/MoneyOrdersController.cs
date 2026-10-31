
namespace ApexPost.Web.Controllers;

// Money orders are issued as lines of a counter transaction
// (TransactionsController.Create). This controller lists branch records.
[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
[Route("[controller]/[action]")]
public sealed class MoneyOrdersController(
	MoneyOrderApplicationService moneyOrders,
	ScreenQueryApplicationService screens) : Controller
{
	[HttpGet]
	public async Task<IActionResult> Index(
		CancellationToken cancellationToken)
	{
		if (!moneyOrders.IsConfigured)
			return StatusCode(
				StatusCodes.Status503ServiceUnavailable,
				"Money-order high-value threshold is not configured.");

		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		IReadOnlyList<ApexPost.Application.Persistence.MoneyOrderRow> orders;
		try
		{
			orders = await screens.GetMoneyOrdersAsync(
				staffId, cancellationToken);
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}

		return View(orders.Select(order => new MoneyOrderListItemViewModel(
			order.Amount,
			order.Currency,
			order.Sender,
			order.Recipient,
			order.Fee,
			order.DocumentType,
			order.MaskedDocumentIdentifier,
			order.IsSuccessful,
			order.Status)).ToArray());
	}
}
