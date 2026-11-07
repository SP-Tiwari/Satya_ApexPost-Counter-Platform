using ApexPost.Client;
using ApexPost.Client.Consumer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Calls version 2 of the ApexPost Logistics API through the generated client.
// The API root comes from ApexPostApi:BaseUrl (appsettings.json), which the
// command line can override: --ApexPostApi:BaseUrl=http://localhost:5280/
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
