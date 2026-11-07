using Asp.Versioning;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ApexPost.Api.Versioning;

// ReportApiVersions writes the api-supported-versions and
// api-deprecated-versions headers from an action filter. [ApiController]'s
// automatic 400 answers before action filters run, so it would have no version
// headers. This global resource filter runs before it and asks the same
// reporter to write the headers when the response starts. (A 415 for a body
// that is not JSON is answered by routing before any action is selected, so
// no filter can add the headers to it.)
internal sealed class ReportApiVersionsOnEveryResponse(IReportApiVersions reporter)
	: IResourceFilter
{
	public void OnResourceExecuting(ResourceExecutingContext context)
	{
		var metadata = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ApiVersionMetadata>();
		if (metadata is null)
			return;

		var response = context.HttpContext.Response;
		var versions = metadata.Map(reporter.Mapping);
		response.OnStarting(() =>
		{
			reporter.Report(response, versions);
			return Task.CompletedTask;
		});
	}

	public void OnResourceExecuted(ResourceExecutedContext context)
	{
	}
}
