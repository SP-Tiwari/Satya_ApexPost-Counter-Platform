using ApexPost.Client;
using ApexPost.Client.Consumer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Calls version 2 of the ApexPost Logistics API through the generated client.
// The API root comes from ApexPostApi:BaseUrl (appsettings.json), which the
// command line can override: --ApexPostApi:BaseUrl=https://localhost:7280/
// The API needs an access token for COURIER-DEMO-07 with the Courier and Admin
// roles (Week 4 Part D): the demo creates a shipment and uploads scans. It is
// read from ApexPostApi:AccessToken, such as the ApexPostApi__AccessToken
// environment variable.
// The content root is the app's own folder, so appsettings.json is found
// from any working directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
	Args = args,
	ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddApexPostApiClient(builder.Configuration);

using var host = builder.Build();
var client = host.Services.GetRequiredService<IApexPostApiClient>();

try
{
	await ShipmentClientDemo.RunAsync(client, Console.Out, CancellationToken.None);
	return 0;
}
catch (HttpRequestException exception)
{
	Console.Error.WriteLine(
		$"The API could not be reached at {builder.Configuration[ApexPostApiClientRegistration.BaseUrlKey]}: {exception.Message}");
	return 1;
}
