using ApexPost.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ApexPost.Api.OpenApi;

// D5: each document declares how to authenticate, so Swagger UI shows an
// Authorize button and sends the credentials with "Try it out".
// - "Bearer": an HTTP bearer JWT, required by every operation that is not
//   [AllowAnonymous]: those with [Authorize], and those the fallback policy
//   covers.
// - "PartnerApiKey": the X-Api-Key header, required by the partner operation
//   (version 2 only).
// A document declares only the schemes its operations use, and each operation
// lists only the scheme it accepts. The document only describes the
// protection; the protection itself is the authorization attributes, the
// fallback policy and the partner key middleware.
internal sealed class SecuritySchemeTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
	public const string BearerScheme = "Bearer";
	public const string PartnerApiKeyScheme = "PartnerApiKey";

	public Task TransformAsync(
		OpenApiDocument document,
		OpenApiDocumentTransformerContext context,
		CancellationToken cancellationToken)
	{
		// The groups hold every version's operations; only this document's count.
		var schemes = context.DescriptionGroups
			.SelectMany(group => group.Items)
			.Where(description => description.GroupName == context.DocumentName)
			.Select(description => SchemeOf(description.ActionDescriptor.EndpointMetadata))
			.ToHashSet();

		document.Components ??= new OpenApiComponents();
		document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
		if (schemes.Contains(BearerScheme))
		{
			document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
			{
				Type = SecuritySchemeType.Http,
				Scheme = "bearer",
				BearerFormat = "JWT",
				Description =
					"A JWT access token with a Courier or Admin role claim and, for couriers, a courierId claim. " +
					"In Development, issue one with dotnet user-jwts or POST /dev/token."
			};
		}
		if (schemes.Contains(PartnerApiKeyScheme))
		{
			document.Components.SecuritySchemes[PartnerApiKeyScheme] = new OpenApiSecurityScheme
			{
				Type = SecuritySchemeType.ApiKey,
				In = ParameterLocation.Header,
				Name = PartnerApiKeyMiddleware.HeaderName,
				Description = "An approved partner's API key, for the partner operations only."
			};
		}
		return Task.CompletedTask;
	}

	public Task TransformAsync(
		OpenApiOperation operation,
		OpenApiOperationTransformerContext context,
		CancellationToken cancellationToken)
	{
		var scheme = SchemeOf(context.Description.ActionDescriptor.EndpointMetadata);
		if (scheme is null)
			return Task.CompletedTask;

		operation.Security ??= [];
		operation.Security.Add(new OpenApiSecurityRequirement
		{
			[new OpenApiSecuritySchemeReference(scheme, context.Document)] = []
		});
		return Task.CompletedTask;
	}

	// The partner key for the partner route; otherwise a bearer token unless
	// the endpoint is anonymous (the fallback policy covers the rest).
	private static string? SchemeOf(IList<object> metadata) =>
		metadata.OfType<RequirePartnerApiKeyAttribute>().Any() ? PartnerApiKeyScheme
		: metadata.OfType<IAllowAnonymous>().Any() ? null
		: BearerScheme;
}
