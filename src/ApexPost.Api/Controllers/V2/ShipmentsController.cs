using System.Net.Mime;
using ApexPost.Api.Models;
using ApexPost.Api.Models.V2;
using ApexPost.Application.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ShipmentPageResponse = ApexPost.Api.Models.V2.ShipmentPageResponse;
using ShipmentResponse = ApexPost.Api.Models.V2.ShipmentResponse;
using UpdateStatusRequest = ApexPost.Api.Models.V2.UpdateStatusRequest;

namespace ApexPost.Api.Controllers.V2;

// The same actions, routes, route names, status codes and service calls as
// the version 1 controller, and the same XML comment conventions. Only the
// response DTOs, their mapping and the status request differ.

/// <summary>
/// Version 2 of tracked shipments: the paged list, one shipment, and creating,
/// replacing, changing the status of and deleting a shipment. A shipment has a
/// nested recipient, its latest scan and its status as a string.
/// </summary>
/// <param name="shipments">The application service that holds the shipment rules.</param>
[ApiController]
[Route(ShipmentRoutes.Shipments)]
[Tags("Shipments")]
[ApiVersion("2.0")]
[ProducesResponseType<ProblemDetails>(
	StatusCodes.Status500InternalServerError, MediaTypeNames.Application.ProblemJson)]
public sealed class ShipmentsController(TrackedShipmentApplicationService shipments)
	: ControllerBase
{
	/// <summary>List shipments by page, status and city</summary>
	/// <remarks>
	/// Returns one page of shipments, ordered by tracking number and optionally
	/// filtered by status and city. <c>page</c> defaults to 1 and
	/// <c>pageSize</c> to 20; the largest page size is 50.
	/// </remarks>
	/// <param name="query">The page, the page size and the optional filters.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <returns>The requested page and the total number of matching shipments.</returns>
	/// <response code="200">The page of shipments.</response>
	/// <response code="400">A query value is invalid, such as a page size above 50 or an unknown status.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpGet(Name = "ListShipments")]
	[EndpointSummary("List shipments by page, status and city")]
	[ProducesResponseType<ShipmentPageResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ValidationProblemDetails>(
		StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	public async Task<ActionResult<ShipmentPageResponse>> List(
		[FromQuery] ShipmentListQuery query,
		CancellationToken cancellationToken)
	{
		var page = await shipments.GetPageAsync(
			query.Page, query.PageSize, query.Status, query.City, cancellationToken);
		return Ok(page.ToV2Response(query));
	}

	/// <summary>Get a shipment by tracking number</summary>
	/// <remarks>Returns the shipment's recipient, weight and current status.</remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <returns>The shipment.</returns>
	/// <response code="200">The shipment.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpGet(ShipmentRoutes.TrackingNumber, Name = "GetShipment")]
	[EndpointSummary("Get a shipment by tracking number")]
	[ProducesResponseType<ShipmentResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)]
	public async Task<ActionResult<ShipmentResponse>> Get(
		string trackingNumber,
		CancellationToken cancellationToken)
	{
		var shipment = await shipments.FindAsync(trackingNumber, cancellationToken);
		return shipment is null
			? this.ShipmentNotFound(trackingNumber)
			: Ok(shipment.ToV2Response());
	}

	/// <summary>Create a shipment</summary>
	/// <remarks>
	/// Creates the shipment with the status Created. The <c>Location</c> header of the 201 response is the URL of the new
	/// shipment.
	/// </remarks>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <param name="request">The tracking number and the shipment details.</param>
	/// <returns>The created shipment.</returns>
	/// <response code="201">The shipment was created.</response>
	/// <response code="400">The request failed validation or its JSON is malformed.</response>
	/// <response code="409">A shipment with this tracking number already exists.</response>
	/// <response code="415">The body is not application/json.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpPost(Name = "CreateShipment")]
	[EndpointSummary("Create a shipment")]
	[Consumes(MediaTypeNames.Application.Json)]
	[ProducesResponseType<ShipmentResponse>(StatusCodes.Status201Created, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ValidationProblemDetails>(
		StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status415UnsupportedMediaType, MediaTypeNames.Application.ProblemJson)]
	public async Task<ActionResult<ShipmentResponse>> Create(
		CreateShipmentRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.CreateAsync(request.ToCommand(), cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? CreatedAtAction(
				nameof(Get),
				new { trackingNumber = result.Shipment!.TrackingNumber },
				result.Shipment.ToV2Response())
			: this.ShipmentProblem(result, request.TrackingNumber!);
	}

	/// <summary>Replace a shipment's editable details</summary>
	/// <remarks>
	/// The editable details are the recipient's name, phone, address and city,
	/// and the weight. The tracking number and the status do not change.
	/// </remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <param name="request">The new details.</param>
	/// <returns>The shipment with its new details.</returns>
	/// <response code="200">The details were replaced.</response>
	/// <response code="400">The request failed validation or its JSON is malformed.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="409">Another change was made to the shipment at the same time.</response>
	/// <response code="415">The body is not application/json.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpPut(ShipmentRoutes.TrackingNumber, Name = "ReplaceShipment")]
	[EndpointSummary("Replace a shipment's editable details")]
	[Consumes(MediaTypeNames.Application.Json)]
	[ProducesResponseType<ShipmentResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ValidationProblemDetails>(
		StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status415UnsupportedMediaType, MediaTypeNames.Application.ProblemJson)]
	public async Task<ActionResult<ShipmentResponse>> Replace(
		string trackingNumber,
		UpdateShipmentRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.ReplaceAsync(
			trackingNumber, request.ToDetails(), cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? Ok(result.Shipment!.ToV2Response())
			: this.ShipmentProblem(result, trackingNumber);
	}

	/// <summary>Change a shipment's status</summary>
	/// <remarks>
	/// Moves the shipment to its next status, given by name, such as
	/// <c>{ "status": "PickedUp" }</c>. The allowed path is Created, PickedUp,
	/// InTransit, OutForDelivery and then Delivered or Failed. Each status
	/// moves only to the next one, and Delivered and Failed are final.
	/// </remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <param name="request">The new status.</param>
	/// <returns>The shipment with its new status.</returns>
	/// <response code="200">The status was changed.</response>
	/// <response code="400">The status is missing or not a defined status, or the JSON is malformed.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="409">The transition is not allowed, or another change was made to the shipment at the same time.</response>
	/// <response code="415">The body is not application/json.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpPatch(ShipmentRoutes.TrackingNumber + "/status", Name = "ChangeShipmentStatus")]
	[EndpointSummary("Change a shipment's status")]
	[Consumes(MediaTypeNames.Application.Json)]
	[ProducesResponseType<ShipmentResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ValidationProblemDetails>(
		StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status415UnsupportedMediaType, MediaTypeNames.Application.ProblemJson)]
	public async Task<ActionResult<ShipmentResponse>> ChangeStatus(
		string trackingNumber,
		UpdateStatusRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.ChangeStatusAsync(
			trackingNumber, request.Status!.Value, cancellationToken);
		return result.Outcome == ShipmentChangeOutcome.Succeeded
			? Ok(result.Shipment!.ToV2Response())
			: this.ShipmentProblem(result, trackingNumber);
	}

	/// <summary>Delete a shipment in Created status</summary>
	/// <remarks>
	/// Deletes the shipment and its scans. A shipment that has moved past Created
	/// cannot be deleted.
	/// </remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <response code="204">The shipment was deleted.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="409">The shipment's status is not Created, or another change was made to it at the same time.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpDelete(ShipmentRoutes.TrackingNumber, Name = "DeleteShipment")]
	[EndpointSummary("Delete a shipment in Created status")]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)]
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
