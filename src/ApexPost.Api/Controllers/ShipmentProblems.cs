using ApexPost.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Controllers;

// The Problem Details answers the shipment controllers share.
internal static class ShipmentProblems
{
	public static ObjectResult ShipmentNotFound(
		this ControllerBase controller,
		string trackingNumber) =>
		controller.Problem(
			statusCode: StatusCodes.Status404NotFound,
			title: "Shipment not found.",
			detail: $"No shipment has tracking number {trackingNumber}.");

	// A duplicate tracking number, a refused status change or a delete after
	// Created: the request is valid but the current state refuses it.
	public static ObjectResult ShipmentProblem(
		this ControllerBase controller,
		ShipmentChangeResult result,
		string trackingNumber) =>
		result.Outcome == ShipmentChangeOutcome.NotFound
			? controller.ShipmentNotFound(trackingNumber)
			: controller.Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "The shipment's current state refuses this request.",
				detail: result.Problem);
}
