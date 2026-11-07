using ApexPost.Api.Errors;
using ApexPost.Api.Persistence;
using ApexPost.Application.Persistence;
using ApexPost.Application.Services;
using ApexPost.Infrastructure.Persistence.Sql;

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

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
// Writes Problem Details for an error status that has no body, such as a 404
// for a route that does not match.
app.UseStatusCodePages();
app.MapControllers();

app.Run();

// Lets WebApplicationFactory<Program> start this host in the integration tests.
public partial class Program;
