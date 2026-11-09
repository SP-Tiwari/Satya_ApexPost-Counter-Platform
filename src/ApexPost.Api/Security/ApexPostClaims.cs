namespace ApexPost.Api.Security;

/// <summary>
/// The roles and claims an ApexPost access token carries. The claim names are
/// the JWT names as issued by <c>dotnet user-jwts</c> and the Development
/// token endpoint; inbound claims are not remapped, so they are read as is.
/// </summary>
public static class ApexPostClaims
{
	/// <summary>The role claim: <see cref="CourierRole"/> or <see cref="AdminRole"/>.</summary>
	public const string Role = "role";

	/// <summary>The subject claim, used as the user's name.</summary>
	public const string Subject = "sub";

	/// <summary>The courier the token was issued to, such as COURIER-DEMO-07.</summary>
	public const string CourierId = "courierId";

	/// <summary>A courier in the field: uploads scans and moves shipments along.</summary>
	public const string CourierRole = "Courier";

	/// <summary>An administrator: may delete shipments.</summary>
	public const string AdminRole = "Admin";
}
