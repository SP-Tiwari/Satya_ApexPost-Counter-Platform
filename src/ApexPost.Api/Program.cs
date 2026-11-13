using ApexPost.Api.Controllers;
using ApexPost.Api.Errors;
using ApexPost.Api.OpenApi;
using ApexPost.Api.Persistence;
using ApexPost.Api.Security;
using ApexPost.Api.Versioning;
using ApexPost.Application.Persistence;
using ApexPost.Application.Services;
using ApexPost.Infrastructure.Persistence.Sql;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Rewrite;

// Minimal hosting: this file is the composition root; there is no Startup class.
var builder = WebApplication.CreateBuilder(args);

var databaseConnectionString =
	builder.Configuration.GetConnectionString("ApexPostDb");
if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
	throw new InvalidOperationException(
		"ConnectionStrings:ApexPostDb must be configured, for example in User Secrets.");
}

// The Week 3 SQL Server persistence: ApexPostDbContext, its repositories and
// the unit of work, registered exactly as the MVC application registers them.
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ICurrentStaffAccessor, NoSignedInStaff>();
builder.Services.AddApexPostSqlPersistence(databaseConnectionString);
builder.Services.AddScoped<TrackedShipmentApplicationService>();

builder.Services.AddControllers(options =>
	// The version headers also on the automatic 400; see the filter.
	options.Filters.Add<ReportApiVersionsOnEveryResponse>());
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Week 4 Part D, D1 and D2: JWT bearer tokens, validated against the issuer,
// audience and signing key in configuration, and role-based authorization on
// the actions. A missing or invalid token is a 401 and a token without the
// required role a 403; UseStatusCodePages below writes both as Problem Details.
// The fallback policy requires a signed-in user for every endpoint and request
// that states no rule of its own, so a new endpoint is protected by default.
// Only the partner route (its API key), the Development token endpoint and the
// Development OpenAPI documents opt out with AllowAnonymous.
builder.Services.AddApexPostJwtAuthentication();
builder.Services.AddAuthorizationBuilder()
	.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// URL-segment versioning: /api/v1/... (deprecated) and /api/v2/.... Every
// response reports the supported and deprecated versions in the
// api-supported-versions and api-deprecated-versions headers.
builder.Services
	.AddApiVersioning(options =>
	{
		options.ApiVersionReader = new UrlSegmentApiVersionReader();
		options.ReportApiVersions = true;
	})
	.AddMvc()
	// Groups each operation under its version (v1, v2), so the v1 document
	// lists only v1 operations, and writes the version into each path.
	.AddApiExplorer(options =>
	{
		options.GroupNameFormat = "'v'VVV";
		options.SubstituteApiVersionInUrl = true;
	});

// The built-in generator writes one OpenAPI document per API Explorer group
// name: /openapi/v1.json and /openapi/v2.json. A document includes only the
// operations whose group name is its own, so each lists one version. Swagger
// UI below only displays them, so there is one generator.
foreach (var documentName in new[] { "v1", "v2" })
{
	builder.Services.AddOpenApi(documentName, options =>
	{
		options.AddDocumentTransformer<ApiDocumentTransformer>();
		options.AddSchemaTransformer<ContractSchemaTransformer>();
		// D5: each document declares the security schemes its operations use:
		// Bearer in both, and the partner key in version 2.
		options.AddDocumentTransformer<SecuritySchemeTransformer>();
		options.AddOperationTransformer<SecuritySchemeTransformer>();
	});
}

var app = builder.Build();

app.UseExceptionHandler();
// Writes Problem Details for an error status that has no body, such as a 404
// for a route that does not match, or the 401 and 403 of the bearer scheme.
app.UseStatusCodePages();

// D5: plain HTTP is redirected to HTTPS in every environment, so a token or an
// API key is never accepted over an unencrypted connection by design. HSTS
// tells browsers to use HTTPS only; it is left out of Development, where it
// would pin localhost to HTTPS for every local site.
if (!app.Environment.IsDevelopment())
	app.UseHsts();
app.UseHttpsRedirection();

// The document and the UI exist in Development only. Hiding them elsewhere is
// not a security control: the endpoints are protected or not on their own.
// They are anonymous, so Swagger UI can load and its Authorize button can be
// used; Swagger UI is middleware, so it is placed before authorization, whose
// fallback policy would otherwise apply to it.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi().AllowAnonymous();
	// One dropdown entry per API version, newest first, so Swagger UI opens
	// on version 2: "v2" and "v1 (deprecated)".
	var apiVersions = app.DescribeApiVersions();
	app.UseSwaggerUI(options =>
	{
		foreach (var version in apiVersions.OrderByDescending(version => version.ApiVersion))
		{
			options.SwaggerEndpoint(
				$"/openapi/{version.GroupName}.json",
				version.IsDeprecated ? $"{version.GroupName} (deprecated)" : version.GroupName);
		}
	});

	// D1: development access tokens; this route does not exist elsewhere.
	app.MapDevelopmentTokenEndpoint().AllowAnonymous();
}

// Week 4 review: the Part B URLs keep working. /api/shipments and everything
// under it (one shipment, its status and its scans) is served by version 1,
// the version that keeps the Part B contract, so a Part B client that never
// learned about versioning gets the same answers, including the deprecation
// headers, instead of a 404. The rewrite is internal (no redirect), so the
// client keeps its URL and the method and body are untouched; the query string
// is carried over. It runs before routing, which is placed explicitly after it
// so that the endpoint is chosen for the rewritten path. The OpenAPI documents
// list only the versioned paths; the alias is documented in VERSIONING.md.
app.UseRewriter(new RewriteOptions()
	.AddRewrite(ShipmentRoutes.UnversionedAliasPattern, ShipmentRoutes.UnversionedAliasTarget, skipRemainingRules: true));
app.UseRouting();

app.UseAuthentication();
// D3: the partner route authenticates with its API key, after routing has
// selected the endpoint and before authorization runs.
app.UseMiddleware<PartnerApiKeyMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>
/// The API host. Declared partial so that WebApplicationFactory&lt;Program&gt; can
/// start it in the integration tests.
/// </summary>
public partial class Program;
