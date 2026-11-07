namespace ApexPost.Api.Controllers;

internal static class ShipmentRoutes
{
	// URL-segment versioning: /api/v1/shipments and /api/v2/shipments.
	public const string Shipments = "api/v{version:apiVersion}/shipments";

	// Route constraint: a value that is not APX- and eight digits matches no
	// route, so it is a 404 like any other unknown tracking number.
	public const string TrackingNumber = @"{trackingNumber:regex(^APX-\d{{8}}$)}";
}
