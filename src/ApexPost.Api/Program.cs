using ApexPost.Api.Errors;
using ApexPost.Api.OpenApi;
using ApexPost.Api.Persistence;
using ApexPost.Api.Versioning;
using ApexPost.Application.Persistence;
using ApexPost.Application.Services;
using ApexPost.Infrastructure.Persistence.Sql;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;

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
	});
}

var app = builder.Build();

app.UseExceptionHandler();
// Writes Problem Details for an error status that has no body, such as a 404
// for a route that does not match.
app.UseStatusCodePages();

// The document and the UI exist in Development only. Hiding them elsewhere is
// not a security control: the endpoints are protected or not on their own.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
	// One dropdown entry per API version, newest first, so Swagger UI opens
	// on version 2: "v2" and "v1 (deprecated)".
	var apiVersions = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();
	app.UseSwaggerUI(options =>
	{
		foreach (var version in apiVersions.ApiVersionDescriptions.OrderByDescending(version => version.ApiVersion))
		{
			options.SwaggerEndpoint(
				$"/openapi/{version.GroupName}.json",
				version.IsDeprecated ? $"{version.GroupName} (deprecated)" : version.GroupName);
		}
	});
}

app.MapControllers();

app.Run();

/// <summary>
/// The API host. Declared partial so that WebApplicationFactory&lt;Program&gt; can
/// start it in the integration tests.
/// </summary>
public partial class Program;
