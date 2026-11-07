namespace ApexPost.Api.Controllers;

internal static class ShipmentRoutes
{
	public const string Shipments = "api/shipments";

	// Route constraint: a value that is not APX- and eight digits matches no
	// route, so it is a 404 like any other unknown tracking number.
	public const string TrackingNumber = @"{trackingNumber:regex(^APX-\d{{8}}$)}";
}
