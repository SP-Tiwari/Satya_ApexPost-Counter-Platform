using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ApexPost.Api.Security;

// D1: POST /dev/token issues a development access token, so a courier client
// or an .http file can sign in without running dotnet user-jwts. It is mapped
// in Development only (Program.cs), so it does not exist anywhere else. It
// signs with the issuer, audience and key the bearer scheme validates with,
// which dotnet user-jwts put in User Secrets, so both kinds of token are
// interchangeable. It is excluded from the OpenAPI documents: it is not part
// of the API contract.
internal static class DevelopmentTokenEndpoint
{
	public const string Route = "/dev/token";

	public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

	public static IEndpointConventionBuilder MapDevelopmentTokenEndpoint(this IEndpointRouteBuilder endpoints) =>
		endpoints.MapPost(Route, IssueToken).ExcludeFromDescription();

	private static IResult IssueToken(
		DevelopmentTokenRequest request,
		IOptionsMonitor<JwtBearerOptions> bearerOptions,
		TimeProvider timeProvider)
	{
		var errors = Validate(request, out var role);
		if (errors.Count > 0)
			return TypedResults.ValidationProblem(errors);

		var subject = request.CourierId ?? "admin";
		var claims = new List<Claim>
		{
			new(ApexPostClaims.Subject, subject),
			new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
			new(ApexPostClaims.Role, role!)
		};
		if (request.CourierId is not null)
			claims.Add(new Claim(ApexPostClaims.CourierId, request.CourierId));

		var settings = JwtAuthentication.ReadTokenSettings(bearerOptions);
		var now = timeProvider.GetUtcNow();
		var expires = now.Add(Lifetime);
		var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = settings.Issuer,
			Audience = settings.Audience,
			Subject = new ClaimsIdentity(claims),
			IssuedAt = now.UtcDateTime,
			NotBefore = now.UtcDateTime,
			Expires = expires.UtcDateTime,
			SigningCredentials = new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256)
		});

		return TypedResults.Ok(new DevelopmentTokenResponse(token, "Bearer", expires));
	}

	private static Dictionary<string, string[]> Validate(DevelopmentTokenRequest request, out string? role)
	{
		var errors = new Dictionary<string, string[]>();
		role = request.Role switch
		{
			ApexPostClaims.CourierRole => ApexPostClaims.CourierRole,
			ApexPostClaims.AdminRole => ApexPostClaims.AdminRole,
			_ => null
		};
		if (role is null)
			errors["role"] = [$"The role must be {ApexPostClaims.CourierRole} or {ApexPostClaims.AdminRole}."];

		if (role == ApexPostClaims.CourierRole && string.IsNullOrWhiteSpace(request.CourierId))
			errors["courierId"] = ["A Courier token needs a courierId."];
		else if (request.CourierId is { } courierId && (courierId.Length > 50 || courierId.Trim() != courierId || courierId.Length == 0))
			errors["courierId"] = ["The courierId must be 1 to 50 characters, without leading or trailing spaces."];

		return errors;
	}
}

/// <summary>A request for a development access token.</summary>
/// <param name="CourierId">The courier the token is for. Required for the Courier role.</param>
/// <param name="Role">Courier or Admin.</param>
internal sealed record DevelopmentTokenRequest(string? CourierId, string? Role);

/// <summary>A development access token.</summary>
/// <param name="AccessToken">The signed JWT, sent as <c>Authorization: Bearer {accessToken}</c>.</param>
/// <param name="TokenType">Always Bearer.</param>
/// <param name="ExpiresAtUtc">When the token stops being accepted.</param>
internal sealed record DevelopmentTokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAtUtc);
