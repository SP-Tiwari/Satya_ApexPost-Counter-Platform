using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ApexPost.Api.OpenApi;

// Sets each version's document title, description, version and team contact.
// The contact is fictional; the .example domain is reserved and never
// resolves. In the document of a deprecated version, every operation is
// marked deprecated and the description says which version to use instead.
internal sealed class ApiDocumentTransformer(IApiVersionDescriptionProvider apiVersions)
	: IOpenApiDocumentTransformer
{
	public const string Title = "ApexPost Logistics API";

	public Task TransformAsync(
		OpenApiDocument document,
		OpenApiDocumentTransformerContext context,
		CancellationToken cancellationToken)
	{
		var version = apiVersions.ApiVersionDescriptions
			.Single(version => version.GroupName == context.DocumentName);
		var latest = apiVersions.ApiVersionDescriptions.Max(version => version.ApiVersion);

		var description =
			"Tracked shipments and offline courier scan uploads for ApexPost Logistics. " +
			"Every error response is a Problem Details object (RFC 9457).";
		if (version.IsDeprecated)
			description += $" Version {version.ApiVersion} is deprecated; use version {latest}.";

		document.Info = new OpenApiInfo
		{
			Title = Title,
			Version = context.DocumentName,
			Description = description,
			Contact = new OpenApiContact
			{
				Name = "ApexPost Platform Team",
				Email = "platform-team@apexpost.example",
				Url = new Uri("https://developers.apexpost.example")
			}
		};

		if (version.IsDeprecated)
		{
			foreach (var operation in document.Paths.Values
				.SelectMany(path => path.Operations?.Values.AsEnumerable() ?? []))
			{
				operation.Deprecated = true;
			}
		}

		return Task.CompletedTask;
	}
}
