using ApexPost.Application.Persistence;
using ApexPost.Application.Verification;
using ApexPost.Domain.Notifications;
using ApexPost.Web.Filters;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ApexPost.Web.Controllers;

[Authorize(Roles =
	nameof(StaffRole.BranchManager) + "," + nameof(StaffRole.CounterClerk))]
[Route("[controller]")]
public sealed class TransactionsController : Controller, IInvalidModelStateHandler
{
	private readonly TransactionApplicationService _transactions;
	private readonly ScreenQueryApplicationService _screens;
	private readonly MixedBasketApplicationService _mixedBaskets;
	private readonly IAuthorizationService _authorization;

	public TransactionsController(
		TransactionApplicationService transactions,
		ScreenQueryApplicationService screens,
		MixedBasketApplicationService mixedBaskets,
		IAuthorizationService authorization)
	{
		ArgumentNullException.ThrowIfNull(transactions);
		ArgumentNullException.ThrowIfNull(screens);
		ArgumentNullException.ThrowIfNull(mixedBaskets);
		ArgumentNullException.ThrowIfNull(authorization);
		_transactions = transactions;
		_screens = screens;
		_mixedBaskets = mixedBaskets;
		_authorization = authorization;
	}

	// SameBranchOnly, applied once the transaction's branch is known. The
	// application service already scopes reads to the staff member's branch;
	// this is a second, policy-based check at the web boundary.
	private async Task<bool> IsSameBranchAsync(string? branchCode) =>
		branchCode is not null &&
		(await _authorization.AuthorizeAsync(
			User,
			new BranchCode(branchCode),
			ApexPostPolicies.SameBranchOnly)).Succeeded;

	[HttpGet]
	[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
	public async Task<IActionResult> Index(CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		IReadOnlyList<ApexPost.Application.Persistence.TransactionDetail> transactions;
		try
		{
			transactions = await _screens.GetTransactionsAsync(
				staffId, cancellationToken);
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (InvalidOperationException)
		{
			return Forbid();
		}
		var model = transactions
			.Select(basket => new TransactionListViewModel(
				basket.Id,
				basket.PaymentStatus,
				basket.Total,
				basket.Currency,
				basket.BranchCode))
			.ToArray();
		return View(model);
	}

	[HttpGet("Create")]
	public async Task<IActionResult> Create(
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();
		try
		{
			var model = await BuildCreateViewModelAsync(
				new CreateTransactionViewModel
				{
					TransactionId = Guid.NewGuid()
				},
				staffId,
				cancellationToken);
			return View(model);
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (InvalidOperationException)
		{
			return Conflict(
				"Transactions cannot be processed at the assigned branch.");
		}
	}

	[HttpPost("Create")]
	[ValidateAntiForgeryToken]
	[Audited]
	public async Task<IActionResult> Create(
		CreateTransactionViewModel model,
		CancellationToken cancellationToken)
	{
		if (model.TransactionId == Guid.Empty)
			ModelState.AddModelError(
				nameof(model.TransactionId), "A transaction ID is required.");
		else
			AuditActionFilter.AddAuditValue(
				HttpContext, "transactionId", model.TransactionId.ToString());

		// Goods rows arrive as parallel GoodsSku/GoodsQuantity lists; rows with
		// neither a product nor a quantity are blank and ignored.
		var goodsLines = new List<GoodsBasketRequest>();
		var hasGoods = false;
		var goodsRowCount = Math.Max(
			model.GoodsSku.Count, model.GoodsQuantity.Count);
		for (var row = 0; row < goodsRowCount; row++)
		{
			var sku = row < model.GoodsSku.Count ? model.GoodsSku[row] : null;
			var quantity = row < model.GoodsQuantity.Count
				? model.GoodsQuantity[row]
				: null;
			if (string.IsNullOrWhiteSpace(sku) && quantity is null)
				continue;

			hasGoods = true;
			if (string.IsNullOrWhiteSpace(sku))
				ModelState.AddModelError(
					nameof(model.GoodsSku),
					$"Goods line {row + 1}: select a product.");
			else if (quantity is not > 0)
				ModelState.AddModelError(
					nameof(model.GoodsQuantity),
					$"Goods line {row + 1}: quantity must be greater than zero.");
			else
				goodsLines.Add(new GoodsBasketRequest(sku, quantity.Value));
		}

		if (string.IsNullOrWhiteSpace(model.CounterTerminalId))
			ModelState.AddModelError(
				nameof(model.CounterTerminalId), "Select a counter terminal.");
		if (string.IsNullOrWhiteSpace(model.PaymentMethod))
			ModelState.AddModelError(
				nameof(model.PaymentMethod), "Select a payment method.");

		ShippingBasketRequest? shippingRequest = null;
		if (model.IncludeShipping)
		{
			if (model.ShippingType is null ||
				!Enum.IsDefined(model.ShippingType.Value))
				ModelState.AddModelError(
					nameof(model.ShippingType), "Select a valid shipping type.");
			if (model.ServiceLevel is null ||
				!Enum.IsDefined(model.ServiceLevel.Value))
				ModelState.AddModelError(
					nameof(model.ServiceLevel), "Select a valid service level.");
			if (string.IsNullOrWhiteSpace(model.DestinationZone))
				ModelState.AddModelError(
					nameof(model.DestinationZone),
					"Select a destination zone.");
			if (model.WeightKilograms is not > 0m)
				ModelState.AddModelError(
					nameof(model.WeightKilograms),
					"Weight must be greater than zero.");
			if (model.LengthCm is not > 0m)
				ModelState.AddModelError(
					nameof(model.LengthCm),
					"Length must be greater than zero.");
			if (model.WidthCm is not > 0m)
				ModelState.AddModelError(
					nameof(model.WidthCm),
					"Width must be greater than zero.");
			if (model.HeightCm is not > 0m)
				ModelState.AddModelError(
					nameof(model.HeightCm),
					"Height must be greater than zero.");

			if (ModelState.IsValid)
			{
				shippingRequest = new ShippingBasketRequest(
					model.ShippingType!.Value,
					model.WeightKilograms!.Value,
					model.LengthCm!.Value,
					model.WidthCm!.Value,
					model.HeightCm!.Value,
					model.DestinationZone!,
					model.ServiceLevel!.Value);
			}
		}

		MoneyOrderBasketRequest? moneyOrderRequest = null;
		if (model.IncludeMoneyOrder)
		{
			if (model.MoneyOrderAmount is not > 0m)
				ModelState.AddModelError(
					nameof(model.MoneyOrderAmount),
					"Enter a money-order amount greater than zero.");
			if (string.IsNullOrWhiteSpace(model.MoneyOrderSender))
				ModelState.AddModelError(
					nameof(model.MoneyOrderSender), "Enter the sender.");
			if (string.IsNullOrWhiteSpace(model.MoneyOrderRecipient))
				ModelState.AddModelError(
					nameof(model.MoneyOrderRecipient), "Enter the recipient.");
			if (string.IsNullOrWhiteSpace(model.MoneyOrderDocumentType) !=
				string.IsNullOrWhiteSpace(model.MoneyOrderDocumentIdentifier))
				ModelState.AddModelError(
					nameof(model.MoneyOrderDocumentType),
					"Enter both the identity document type and number, or neither.");

			if (ModelState.IsValid)
			{
				moneyOrderRequest = new MoneyOrderBasketRequest(
					model.MoneyOrderAmount!.Value,
					model.MoneyOrderSender!,
					model.MoneyOrderRecipient!,
					string.IsNullOrWhiteSpace(model.MoneyOrderDocumentType)
						? null
						: model.MoneyOrderDocumentType,
					string.IsNullOrWhiteSpace(model.MoneyOrderDocumentIdentifier)
						? null
						: model.MoneyOrderDocumentIdentifier);
			}
		}

		if (!hasGoods && !model.IncludeShipping && !model.IncludeMoneyOrder)
			ModelState.AddModelError(
				string.Empty,
				"Add at least one goods item, shipping service, or money order.");

		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		if (!_mixedBaskets.HasTaxRates)
			ModelState.AddModelError(
				string.Empty,
				"Configure transaction tax rates before processing transactions.");

		if (!ModelState.IsValid)
		{
			Response.StatusCode = StatusCodes.Status400BadRequest;
			return View(await BuildCreateViewModelAsync(
				model, staffId, cancellationToken));
		}

		try
		{
			IReadOnlyList<ShippingBasketRequest> shipments = shippingRequest is null
				? []
				: [shippingRequest];
			IReadOnlyList<MoneyOrderBasketRequest> moneyOrders =
				moneyOrderRequest is null ? [] : [moneyOrderRequest];
			var transactionId =
				await _mixedBaskets.CreateAndProcessAsync(
					staffId,
					new CounterBasketRequest(
						model.TransactionId,
						model.CounterTerminalId!,
						model.PaymentMethod!,
						goodsLines,
						shipments,
						moneyOrders),
					cancellationToken);
			TempData["TransactionActionStatus"] =
				"Transaction processed.";
			return RedirectToAction(
				nameof(Details), new { transactionId });
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (IdentityVerificationUnavailableException)
		{
			ModelState.AddModelError(
				string.Empty,
				"Identity verification is unavailable. The transaction was not processed; try again later.");
			Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			return View(await BuildCreateViewModelAsync(
				model, staffId, cancellationToken));
		}
		catch (ArgumentException)
		{
			ModelState.AddModelError(
				string.Empty,
				"One or more transaction values are invalid.");
			Response.StatusCode = StatusCodes.Status400BadRequest;
			return View(await BuildCreateViewModelAsync(
				model, staffId, cancellationToken));
		}
		// Derives from InvalidOperationException, so it must stay above that catch.
		catch (ConcurrencyConflictException)
		{
			ModelState.AddModelError(
				string.Empty,
				"The transaction was not processed because another change was made at the same time. Review the form and submit it again.");
			Response.StatusCode = StatusCodes.Status409Conflict;
			return View(await BuildCreateViewModelAsync(
				model, staffId, cancellationToken));
		}
		catch (InvalidOperationException)
		{
			ModelState.AddModelError(
				string.Empty,
				"The transaction could not be processed. Check the counter terminal, configured rates and payment methods, weight limits, stock, money-order identity verification, and submission status.");
			Response.StatusCode = StatusCodes.Status409Conflict;
			return View(await BuildCreateViewModelAsync(
				model, staffId, cancellationToken));
		}
	}

	// Managers see every transaction at their branch; a clerk sees the sales
	// they processed (TransactionAccessRule.CanView), so a completed sale
	// redirects here for both roles. Void and refund stay manager-only.
	[HttpGet("{transactionId:guid}")]
	public async Task<IActionResult> Details(
		Guid transactionId,
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var model = await BuildDetailsViewModelAsync(
				transactionId, staffId, cancellationToken);
			if (!await IsSameBranchAsync(model.BranchCode))
				return Forbid();
			return View(model);
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	// Printable label preview for each shipping line, for the same audience
	// as Details. It shows only what the sale recorded; the domain holds no
	// recipient address, so none is printed.
	[HttpGet("{transactionId:guid}/label")]
	public async Task<IActionResult> Label(
		Guid transactionId,
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var basket = await _screens.GetTransactionAsync(
				transactionId, staffId, cancellationToken);
			if (!await IsSameBranchAsync(basket.BranchCode))
				return Forbid();

			var labels = basket.Lines
				.Select(item => item.Shipment)
				.OfType<ApexPost.Application.Persistence.ShipmentDetail>()
				.Select(item => new ShippingLabelItemViewModel(
					item.ShipmentId,
					item.Type,
					item.ServiceLevel,
					item.WeightKilograms,
					item.LengthCm,
					item.WidthCm,
					item.HeightCm,
					item.DestinationZone))
				.ToArray();
			if (labels.Length == 0)
				return NotFound();

			return View(new ShippingLabelViewModel(
				basket.Id,
				basket.PaymentStatus,
				basket.BranchCode!,
				basket.OccurredAtUtc,
				labels));
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	// JSON basket summary for the same audience as Details.
	[HttpGet("{transactionId:guid}/summary")]
	public async Task<IActionResult> Summary(
		Guid transactionId,
		CancellationToken cancellationToken)
	{
		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		try
		{
			var details = await BuildDetailsViewModelAsync(
				transactionId, staffId, cancellationToken);
			if (!await IsSameBranchAsync(details.BranchCode))
				return Forbid();
			return Json(new
			{
				details.Id,
				Status = details.PaymentStatus.ToString(),
				details.BranchCode,
				details.CounterTerminalId,
				details.ClerkStaffId,
				details.PaymentMethod,
				details.OccurredAtUtc,
				details.Currency,
				details.Subtotal,
				details.Tax,
				details.Total,
				Lines = details.LineItems.Select(line => new
				{
					line.Description,
					Type = line.Type.ToString(),
					line.UnitPrice,
					line.Quantity,
					line.Subtotal
				})
			});
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	[HttpPost("{transactionId:guid}/void")]
	[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
	[ValidateAntiForgeryToken]
	[Audited]
	public Task<IActionResult> Void(
		Guid transactionId,
		TransactionActionViewModel model,
		CancellationToken cancellationToken) =>
		ChangeStatusAsync(
			transactionId,
			model,
			"Transaction voided.",
			_transactions.VoidAsync,
			cancellationToken);

	[HttpPost("{transactionId:guid}/refund")]
	[Authorize(Policy = ApexPostPolicies.BranchManagerOnly)]
	[ValidateAntiForgeryToken]
	[Audited]
	public Task<IActionResult> Refund(
		Guid transactionId,
		TransactionActionViewModel model,
		CancellationToken cancellationToken) =>
		ChangeStatusAsync(
			transactionId,
			model,
			"Transaction refunded.",
			_transactions.RefundAsync,
			cancellationToken);

	private async Task<IActionResult> ChangeStatusAsync(
		Guid transactionId,
		TransactionActionViewModel model,
		string successMessage,
		Func<Guid, string, string, CancellationToken, Task> changeStatus,
		CancellationToken cancellationToken)
	{
		if (transactionId == Guid.Empty)
			return BadRequest();

		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		if (!ModelState.IsValid)
		{
			return await RenderDetailsWithErrorAsync(
				transactionId,
				staffId,
				StatusCodes.Status400BadRequest,
				"Enter a reason of no more than 500 characters.",
				cancellationToken);
		}

		try
		{
			await changeStatus(
				transactionId,
				staffId,
				model.Reason,
				cancellationToken);
			TempData["TransactionActionStatus"] = successMessage;
			return RedirectToAction(
				nameof(Details), new { transactionId });
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
		catch (ArgumentException)
		{
			return await RenderDetailsWithErrorAsync(
				transactionId,
				staffId,
				StatusCodes.Status400BadRequest,
				"Enter a valid reason for this transaction action.",
				cancellationToken);
		}
		catch (InvalidOperationException)
		{
			return await RenderDetailsWithErrorAsync(
				transactionId,
				staffId,
				StatusCodes.Status409Conflict,
				"This transaction cannot be changed in its current state.",
				cancellationToken);
		}
	}

	// Void/refund forms are redisplayed on the details page. Create is left to
	// the action (null): it merges binding errors with its cross-line basket
	// checks so the clerk sees every problem in one redisplay, and it already
	// rebuilds the form and clears the identity document number.
	async Task<IActionResult?> IInvalidModelStateHandler.HandleInvalidModelStateAsync(
		ActionExecutingContext context)
	{
		if (!context.ActionArguments.Values.OfType<TransactionActionViewModel>().Any())
			return null;

		if (!context.ActionArguments.TryGetValue("transactionId", out var value) ||
			value is not Guid transactionId ||
			transactionId == Guid.Empty)
			return BadRequest();

		var staffId = User.FindFirst("StaffId")?.Value;
		if (string.IsNullOrWhiteSpace(staffId))
			return Forbid();

		return await RenderDetailsWithErrorAsync(
			transactionId,
			staffId,
			StatusCodes.Status400BadRequest,
			"Enter a reason of no more than 500 characters.",
			context.HttpContext.RequestAborted);
	}

	private async Task<IActionResult> RenderDetailsWithErrorAsync(
		Guid transactionId,
		string staffId,
		int statusCode,
		string errorMessage,
		CancellationToken cancellationToken)
	{
		try
		{
			var model = await BuildDetailsViewModelAsync(
				transactionId, staffId, cancellationToken);
			ViewData["TransactionActionError"] = errorMessage;
			Response.StatusCode = statusCode;
			return View("Details", model);
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
		catch (UnauthorizedAccessException)
		{
			return Forbid();
		}
	}

	private async Task<TransactionDetailsViewModel> BuildDetailsViewModelAsync(
		Guid transactionId,
		string staffId,
		CancellationToken cancellationToken)
	{
		var basket = await _screens.GetTransactionAsync(
			transactionId,
			staffId,
			cancellationToken);
		return new TransactionDetailsViewModel(
			basket.Id,
			basket.PaymentStatus,
			basket.Subtotal,
			basket.Tax,
			basket.Total,
			basket.Currency,
			basket.BranchCode,
			basket.Lines.Select(item => new TransactionLineViewModel(
				item.Description,
				item.Type,
				item.UnitPrice,
				item.Quantity,
				item.Subtotal,
				item.Currency,
				item.Shipment is null
					? null
					: $"{item.Shipment.Type}; {item.Shipment.WeightKilograms} kg; " +
						$"{item.Shipment.LengthCm} x " +
						$"{item.Shipment.WidthCm} x " +
						$"{item.Shipment.HeightCm} cm; " +
						$"{item.Shipment.DestinationZone}; " +
						$"{item.Shipment.ServiceLevel}")).ToArray(),
			basket.ReversalReason is { } reason
				? [new TransactionNotificationViewModel(
					basket.PaymentStatus.ToString(), reason)]
				: [],
			basket.OccurredAtUtc,
			basket.CounterTerminalId,
			basket.ClerkStaffId,
			basket.PaymentMethod);

	}

	private async Task<CreateTransactionViewModel> BuildCreateViewModelAsync(
		CreateTransactionViewModel model,
		string staffId,
		CancellationToken cancellationToken)
	{
		var catalog = await _mixedBaskets.GetSaleFormAsync(
			staffId, cancellationToken);
		model.Products = catalog.Products;
		model.Branch = catalog.Branch;
		model.ShippingRates = catalog.ShippingRates;
		model.MoneyOrders = catalog.MoneyOrders;
		model.CounterTerminals = catalog.Branch.ActiveCounters;
		model.PaymentMethods = _mixedBaskets.PaymentMethods;
		model.CanSubmit = _mixedBaskets.HasTaxRates &&
			model.CounterTerminals.Count > 0 &&
			model.PaymentMethods.Count > 0 &&
			(catalog.Products.Count > 0 ||
				catalog.ShippingRates.Count > 0 ||
				catalog.MoneyOrders is not null);
		// Always offer at least one goods row to fill in.
		if (model.GoodsSku.Count == 0)
		{
			model.GoodsSku = [null];
			model.GoodsQuantity = [null];
		}
		// Never echo a full identity document number back to the browser.
		model.MoneyOrderDocumentIdentifier = null;
		ModelState.Remove(nameof(model.MoneyOrderDocumentIdentifier));
		return model;
	}
}
