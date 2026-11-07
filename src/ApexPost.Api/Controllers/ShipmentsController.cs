using ApexPost.Api.Models;
using ApexPost.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Controllers;

// Thin: each action binds its input, calls TrackedShipmentApplicationService
// and turns the outcome into an HTTP result. The rules live in the service and
// the domain.
[ApiController]
[Route(ShipmentRoutes.Shipments)]
public sealed class ShipmentsController(TrackedShipmentApplicationService shipments)
	: ControllerBase
{
	[HttpGet]
	public async Task<ActionResult<ShipmentPageResponse>> List(
		[FromQuery] ShipmentListQuery query,
		CancellationToken cancellationToken)
	{
		var page = await shipments.GetPageAsync(
			query.Page, query.PageSize, query.Status, query.City, cancellationToken);
		return Ok(page.ToResponse(query));
	}

	[HttpGet(ShipmentRoutes.TrackingNumber)]
	public async Task<ActionResult<ShipmentResponse>> Get(
		string trackingNumber,
		CancellationToken cancellationToken)
	{
		var shipment = await shipments.FindAsync(trackingNumber, cancellationToken);
		return shipment is null
			? this.ShipmentNotFound(trackingNumber)
			: Ok(shipment.ToResponse());
	}

	[HttpPost]
	[Consumes("application/json")]
	public async Task<ActionResult<ShipmentResponse>> Create(
		CreateShipmentRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.CreateAsync(request.ToCommand(), cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? CreatedAtAction(
				nameof(Get),
				new { trackingNumber = result.Shipment!.TrackingNumber },
				result.Shipment.ToResponse())
			: this.ShipmentProblem(result, request.TrackingNumber!);
	}

	[HttpPut(ShipmentRoutes.TrackingNumber)]
	[Consumes("application/json")]
	public async Task<ActionResult<ShipmentResponse>> Replace(
		string trackingNumber,
		UpdateShipmentRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.ReplaceAsync(
			trackingNumber, request.ToDetails(), cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? Ok(result.Shipment!.ToResponse())
			: this.ShipmentProblem(result, trackingNumber);
	}

	[HttpPatch(ShipmentRoutes.TrackingNumber + "/status")]
	[Consumes("application/json")]
	public async Task<ActionResult<ShipmentResponse>> ChangeStatus(
		string trackingNumber,
		UpdateStatusRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.ChangeStatusAsync(
			trackingNumber, request.Status!.Value, cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? Ok(result.Shipment!.ToResponse())
			: this.ShipmentProblem(result, trackingNumber);
	}

	[HttpDelete(ShipmentRoutes.TrackingNumber)]
	public async Task<IActionResult> Delete(
		string trackingNumber,
		CancellationToken cancellationToken)
	{
		var result = await shipments.DeleteAsync(trackingNumber, cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? NoContent()
			: this.ShipmentProblem(result, trackingNumber);
	}
}
