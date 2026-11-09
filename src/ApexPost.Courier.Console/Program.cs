using ApexPost.Courier.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Composition root of the courier console client (Week 4 Part D). The API
// root comes from configuration: appsettings.json, then the
// ApexPostApi__BaseUrl environment variable, then --ApexPostApi:BaseUrl=...
// on the command line. The content root is the app's own folder, so
// appsettings.json is found from any working directory. It reports the
// configured API and whether a stored access token can be used; the courier
// menus are not part of this version.
try
{
	var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
	{
		Args = args,
		ContentRootPath = AppContext.BaseDirectory
	});
	builder.Services.AddCourierSync(builder.Configuration);
	using var host = builder.Build();

	var session = host.Services.GetRequiredService<CourierSession>();
	Console.WriteLine($"ApexPost courier client. API: {builder.Configuration[CourierSyncRegistration.BaseUrlKey]}");
	Console.WriteLine(session.IsSignInRequired
		? "Not signed in: no usable access token is stored."
		: "Signed in: a stored access token is available.");
	return 0;
}
catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
{
	Console.Error.WriteLine(exception.Message);
	return 1;
}
