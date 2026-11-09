using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace ApexPost.Api.Security;

// D1: JWT bearer authentication. AddJwtBearer() reads the issuer, the
// audiences and the signing keys from the Authentication:Schemes:Bearer
// configuration section: ValidIssuer and ValidAudiences from
// appsettings.Development.json (or the host's configuration outside
// Development), and SigningKeys from User Secrets, where dotnet user-jwts
// writes them. Nothing secret is committed. The options here turn every check
// on explicitly, and the host refuses to start without an issuer, an audience
// and a signing key, so a missing setting can never mean an unchecked token.
internal static class JwtAuthentication
{
	public const string ConfigurationSection = "Authentication:Schemes:Bearer";

	// Tolerance for clock differences between the token issuer and this host.
	public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

	public static IServiceCollection AddApexPostJwtAuthentication(this IServiceCollection services)
	{
		services
			.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
			.AddJwtBearer(options =>
			{
				// Keep the JWT claim names (role, sub, courierId) instead of
				// mapping them to the long WS-* claim type URIs.
				options.MapInboundClaims = false;
				var parameters = options.TokenValidationParameters;
				parameters.ValidateIssuer = true;
				parameters.ValidateAudience = true;
				parameters.ValidateLifetime = true;
				parameters.RequireExpirationTime = true;
				parameters.ValidateIssuerSigningKey = true;
				parameters.RequireSignedTokens = true;
				parameters.ClockSkew = ClockSkew;
				parameters.RoleClaimType = ApexPostClaims.Role;
				parameters.NameClaimType = ApexPostClaims.Subject;
			});

		services
			.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
			.Validate(
				options => HasAny(options.TokenValidationParameters.ValidIssuers)
					|| !string.IsNullOrWhiteSpace(options.TokenValidationParameters.ValidIssuer),
				$"{ConfigurationSection}:ValidIssuer must be configured.")
			.Validate(
				options => HasAny(options.TokenValidationParameters.ValidAudiences)
					|| !string.IsNullOrWhiteSpace(options.TokenValidationParameters.ValidAudience),
				$"{ConfigurationSection}:ValidAudiences must be configured.")
			.Validate(
				options => options.TokenValidationParameters.IssuerSigningKeys?.Any() == true,
				$"{ConfigurationSection}:SigningKeys must hold a key for the configured issuer. " +
				"In Development, run: dotnet user-jwts create --project src/ApexPost.Api")
			.ValidateOnStart();

		return services;
	}

	private static bool HasAny(IEnumerable<string>? values) =>
		values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

	// The validated options of the bearer scheme, for the Development token
	// endpoint, which signs with the same issuer, audience and key.
	public static TokenSettings ReadTokenSettings(IOptionsMonitor<JwtBearerOptions> monitor)
	{
		var parameters = monitor.Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
		return new TokenSettings(
			parameters.ValidIssuers?.FirstOrDefault() ?? parameters.ValidIssuer!,
			parameters.ValidAudiences?.FirstOrDefault() ?? parameters.ValidAudience!,
			parameters.IssuerSigningKeys.First());
	}

	public sealed record TokenSettings(
		string Issuer,
		string Audience,
		Microsoft.IdentityModel.Tokens.SecurityKey SigningKey);
}
