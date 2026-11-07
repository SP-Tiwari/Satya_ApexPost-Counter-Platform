using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ApexPost.Api.IntegrationTests;

// C7: contracts/v1.json and contracts/v2.json are the documents the API
// generates, published as it serves them. If a change to the API changes a
// document, these tests fail until the contract is published again and the
// change is recorded in VERSIONING.md.
public sealed class PublishedContractTests
{
	// The host the contracts were published from: the API's http profile.
	private const string PublishedFrom = "http://localhost:5280/";

	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public async Task PublishedContract_MatchesTheDocumentTheApiGenerates(string version)
	{
		var published = ReadPublished(version);
		using var factory = new ApiFactory("Development");
		var generated = JsonNode.Parse(
			await factory.CreateClient().GetStringAsync($"/openapi/{version}.json"))!.AsObject();

		// "servers" is the host that served the document, so it differs here
		// (http://localhost/); everything else is the contract.
		published.Remove("servers");
		generated.Remove("servers");
		var differences = generated.Select(member => member.Key)
			.Union(published.Select(member => member.Key))
			.Where(key => !JsonNode.DeepEquals(generated[key], published[key]))
			.ToArray();
		Assert.True(
			differences.Length == 0,
			$"contracts/{version}.json differs from /openapi/{version}.json in: {string.Join(", ", differences)}. " +
			"Publish the contract again and record the change in VERSIONING.md.");
	}

	[Theory]
	[InlineData("v1")]
	[InlineData("v2")]
	public void PublishedContract_IsAValidOpenApiDocumentForItsVersion(string version)
	{
		var json = File.ReadAllText(ContractPath(version));

		var read = OpenApiDocument.Parse(json, OpenApiConstants.Json);
		Assert.NotNull(read.Document);
		Assert.Empty(read.Diagnostic?.Errors ?? []);
		var contract = JsonNode.Parse(json)!.AsObject();
		Assert.StartsWith("3.1.", contract["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
		Assert.Equal("ApexPost Logistics API", contract["info"]!["title"]!.GetValue<string>());
		Assert.Equal(version, contract["info"]!["version"]!.GetValue<string>());
		Assert.Equal(PublishedFrom, Assert.Single(contract["servers"]!.AsArray())!["url"]!.GetValue<string>());
		Assert.All(
			contract["paths"]!.AsObject(),
			path => Assert.StartsWith($"/api/{version}/", path.Key, StringComparison.Ordinal));
	}

	private static string ContractPath(string version) =>
		Path.Combine(AppContext.BaseDirectory, "contracts", $"{version}.json");

	private static JsonObject ReadPublished(string version) =>
		JsonNode.Parse(File.ReadAllText(ContractPath(version)))!.AsObject();
}
