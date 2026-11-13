using System.Text.Json;
using ApexPost.Courier.Domain;
using ApexPost.Courier.Sync.Contracts;

namespace ApexPost.Courier.Sync.Tests;

// D8: the shared, source-generated JSON settings match the v2 API.
public sealed class CourierJsonTests
{
	private static readonly JsonSerializerOptions Options = CourierJsonContext.Default.Options;

	[Fact]
	public void Options_AreCamelCase_CaseInsensitive_AndBoundToTheGeneratedContext()
	{
		Assert.Same(JsonNamingPolicy.CamelCase, Options.PropertyNamingPolicy);
		Assert.True(Options.PropertyNameCaseInsensitive);
		Assert.Same(CourierJsonContext.Default, Options.TypeInfoResolver);
	}

	[Fact]
	public void ATypeOutsideTheContext_IsRefused_NotSerializedByReflection()
	{
		Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new Version(1, 0), Options));
	}

	[Theory]
	[InlineData(ScanType.Pickup, "Pickup")]
	[InlineData(ScanType.InTransit, "InTransit")]
	[InlineData(ScanType.Delivered, "Delivered")]
	[InlineData(ScanType.Failed, "Failed")]
	public void ScanType_IsWrittenAsItsName(ScanType scanType, string name)
	{
		var json = JsonSerializer.Serialize(Item(scanType), CourierJsonContext.Default.ScanUploadRequest);

		Assert.Contains($"\"scanType\":\"{name}\"", json);
	}

	[Theory]
	[InlineData("0", ScanType.Pickup)]
	[InlineData("1", ScanType.InTransit)]
	[InlineData("2", ScanType.Delivered)]
	[InlineData("3", ScanType.Failed)]
	[InlineData("\"InTransit\"", ScanType.InTransit)]
	[InlineData("\"intransit\"", ScanType.InTransit)]
	public void ScanType_IsReadFromANumberOrAName(string value, ScanType expected)
	{
		var json = $$"""{"scans":[{"scanId":"scan::1","trackingNumber":"APX-00000011","scanType":{{value}},"courierId":"C","location":"L","scannedAtUtc":"2026-11-07T11:10:00Z"}]}""";

		var request = JsonSerializer.Deserialize(json, CourierJsonContext.Default.ScanUploadRequest);

		Assert.Equal(expected, Assert.Single(request!.Scans).ScanType);
	}

	[Fact]
	public void PropertyNames_AreMatchedWithoutRegardToCase()
	{
		var response = JsonSerializer.Deserialize(
			"""{"ACCEPTED":2,"DuplicatesSkipped":1,"rEjEcTeD":0}""",
			CourierJsonContext.Default.ScanUploadResponse);

		Assert.Equal((2, 1, 0), (response!.Accepted, response.DuplicatesSkipped, response.Rejected));
	}

	[Fact]
	public void NullAndMissingFields_AreReadAsNull()
	{
		var token = JsonSerializer.Deserialize(
			"""{"accessToken":null,"unknown":[1,2,3]}""",
			CourierJsonContext.Default.DevelopmentTokenResponse);

		Assert.NotNull(token);
		Assert.Null(token.AccessToken);
		Assert.Null(token.TokenType);
		Assert.Null(token.ExpiresAtUtc);
	}

	[Fact]
	public void ProblemDetails_AreReadWithOrWithoutFieldErrors()
	{
		var plain = JsonSerializer.Deserialize(
			"""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"traceId":"00-x"}""",
			CourierJsonContext.Default.ValidationProblemDetails);
		var validation = JsonSerializer.Deserialize(
			"""{"title":"One or more validation errors occurred.","status":400,"errors":{"Scans[0].Location":["The Location field is required."]}}""",
			CourierJsonContext.Default.ValidationProblemDetails);

		Assert.Equal(("Forbidden", 403), (plain!.Title, plain.Status));
		Assert.Null(plain.Errors);
		Assert.Equal(["The Location field is required."], validation!.Errors!["Scans[0].Location"]);
	}

	[Fact]
	public void AccessToken_ToString_NeverShowsTheToken()
	{
		var text = TestValues.ValidToken().ToString();

		Assert.DoesNotContain(TestValues.Token, text);
		Assert.Contains("<redacted>", text);
	}

	private static ScanUploadRequest Item(ScanType scanType) => new(
	[
		new ScanUploadItem(
			"scan::5d7f9b1d-3f5b-4d7f-9b1d-3f5b7d9f1b2d",
			"APX-00000011",
			scanType,
			TestValues.CourierId,
			"Pune hub",
			TestValues.Now,
			null)
	]);
}
