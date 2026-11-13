namespace ApexPost.Api.Controllers;

internal static class ShipmentRoutes
{
	// URL-segment versioning: /api/v1/shipments and /api/v2/shipments.
	public const string Shipments = "api/v{version:apiVersion}/shipments";

	// Route constraint: a value that is not APX- and eight digits matches no
	// route, so it is a 404 like any other unknown tracking number.
	public const string TrackingNumber = @"{trackingNumber:regex(^APX-\d{{8}}$)}";

	// Week 4 review: the unversioned Part B path, /api/shipments and anything
	// below it, is an alias of version 1 (see Program.cs). Case-insensitive,
	// like routing. The pattern is matched against the path without its
	// leading slash; "(?=/|$)" stops it matching /api/shipmentsXYZ.
	public const string UnversionedAliasPattern = @"(?i)^api/shipments(?=/|$)(.*)$";
	public const string UnversionedAliasTarget = "api/v1/shipments$1";
}
