using System.Net;
using System.Text.Json;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Sync.Contracts;

namespace ApexPost.Courier.Sync.Tests;

// D6 and D8: the typed client builds the requests of the published contract
// and reads every kind of response without throwing.
public sealed class ApexApiClientTests
{
	private const string TrackingNumber = "APX-00000011";

	private static readonly ScanUploadRequest Upload = new(
	[
		new ScanUploadItem(
			"scan::5d7f9b1d-3f5b-4d7f-9b1d-3f5b7d9f1b2d",
			TrackingNumber,
			ScanType.InTransit,
			TestValues.CourierId,
			"Nagpur hub",
			new DateTimeOffset(2026, 11, 7, 11, 10, 0, TimeSpan.Zero),
			null)
	]);

	[Fact]
	public async Task RequestCourierToken_PostsTheCourierIdAndRole_AndReadsTheToken()
	{
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			"""{"accessToken":"issued-token","tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00+00:00"}"""));

		var result = await host.Client.RequestCourierTokenAsync(TestValues.CourierId);

		var request = Assert.Single(host.Network.Requests);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal(new Uri(TestValues.BaseUrl + "dev/token"), request.Uri);
		Assert.Equal("application/json", request.ContentType);
		Assert.Equal("""{"courierId":"COURIER-DEMO-07","role":"Courier"}""", request.Body);

		Assert.True(result.Succeeded);
		Assert.Equal("issued-token", result.Value!.Value);
		Assert.Equal(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero), result.Value.ExpiresAtUtc);
	}

	[Theory]
	[InlineData("""{"tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00Z"}""")]
	[InlineData("""{"accessToken":"  ","tokenType":"Bearer","expiresAtUtc":"2026-11-10T10:00:00Z"}""")]
	[InlineData("""{"accessToken":"t","tokenType":"Basic","expiresAtUtc":"2026-11-10T10:00:00Z"}""")]
	[InlineData("""{"accessToken":"t","tokenType":"Bearer","expiresAtUtc":null}""")]
	[InlineData("""{"accessToken":"t","tokenType":"Bearer","expiresAtUtc":"not a date"}""")]
	[InlineData("null")]
	[InlineData("[1,2]")]
	[InlineData("{\"accessToken\":")]
	[InlineData("")]
	public async Task RequestCourierToken_WithAnUnusableBody_IsAnUnexpectedResponse(string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body));

		var result = await host.Client.RequestCourierTokenAsync(TestValues.CourierId);

		Assert.False(result.Succeeded);
		Assert.Equal(ApiErrorKind.UnexpectedResponse, result.Error!.Kind);
		Assert.Equal(200, result.Error.StatusCode);
		Assert.Equal("The server sent a response this client could not read.", result.Error.Message);
	}

	[Fact]
	public async Task RequestCourierToken_WithAValidationProblem_ReturnsTheFieldErrors()
	{
		using var host = new ClientHost(_ => Responses.Problem(
			HttpStatusCode.BadRequest,
			"""{"title":"One or more validation errors occurred.","status":400,"errors":{"courierId":["The courierId must be 1 to 50 characters, without leading or trailing spaces."]}}"""));

		var result = await host.Client.RequestCourierTokenAsync("COURIER-DEMO-07");

		Assert.Equal(ApiErrorKind.Validation, result.Error!.Kind);
		Assert.Equal("One or more validation errors occurred.", result.Error.Message);
		Assert.Equal(
			["The courierId must be 1 to 50 characters, without leading or trailing spaces."],
			result.Error.FieldErrors["courierId"]);
	}

	[Fact]
	public async Task UploadScans_PostsTheBatchToTheV2Route_InCamelCaseWithTheScanTypeName()
	{
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			"""{"accepted":1,"duplicatesSkipped":0,"rejected":0,"rejections":[]}"""));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		var request = Assert.Single(host.Network.Requests);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal(new Uri(TestValues.BaseUrl + "api/v2/shipments/APX-00000011/scans"), request.Uri);
		Assert.Equal(
			"""{"scans":[{"scanId":"scan::5d7f9b1d-3f5b-4d7f-9b1d-3f5b7d9f1b2d","trackingNumber":"APX-00000011","scanType":"InTransit","courierId":"COURIER-DEMO-07","location":"Nagpur hub","scannedAtUtc":"2026-11-07T11:10:00+00:00","notes":null}]}""",
			request.Body);
		Assert.True(result.Succeeded);
		Assert.Equal(1, result.Value!.Accepted);
	}

	[Fact]
	public async Task UploadScans_EscapesTheTrackingNumberInThePath()
	{
		using var host = new ClientHost();

		await host.Client.UploadScansAsync("APX 1/2?x", Upload);

		Assert.Equal(
			TestValues.BaseUrl + "api/v2/shipments/APX%201%2F2%3Fx/scans",
			Assert.Single(host.Network.Requests).Uri.AbsoluteUri);
	}

	[Fact]
	public async Task UploadScans_ReadsRejectionsAndIgnoresUnknownFields()
	{
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			"""{"accepted":1,"duplicatesSkipped":1,"rejected":1,"newField":{"x":1},"rejections":[{"scanId":"scan::bad","reason":"The scan ID must have the format scan::{Guid}.","extra":true}]}"""));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Batch(3));

		Assert.True(result.Succeeded);
		Assert.Equal((1, 1, 1), (result.Value!.Accepted, result.Value.DuplicatesSkipped, result.Value.Rejected));
		var rejection = Assert.Single(result.Value.Rejections!);
		Assert.Equal("scan::bad", rejection.ScanId);
		Assert.Equal("The scan ID must have the format scan::{Guid}.", rejection.Reason);
	}

	[Theory]
	[InlineData("""{"accepted":1,"duplicatesSkipped":0,"rejected":0}""")]
	[InlineData("""{"accepted":1,"duplicatesSkipped":0,"rejected":0,"rejections":null}""")]
	public async Task UploadScans_WithoutRejections_ReturnsAnEmptyList(string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.True(result.Succeeded);
		Assert.Empty(result.Value!.Rejections!);
	}

	[Theory]
	[InlineData("""{"accepted":2,"duplicatesSkipped":0,"rejected":0,"rejections":[]}""")]
	[InlineData("""{"accepted":4,"duplicatesSkipped":0,"rejected":0,"rejections":[]}""")]
	[InlineData("""{"accepted":1,"duplicatesSkipped":1,"rejected":0,"rejections":[]}""")]
	[InlineData("""{"accepted":0,"duplicatesSkipped":0,"rejected":3,"rejections":[]}""")]
	[InlineData("""{"accepted":1,"duplicatesSkipped":0,"rejected":2,"rejections":[{"scanId":"scan::a","reason":"r"}]}""")]
	[InlineData("""{"accepted":1,"duplicatesSkipped":2,"rejected":0,"rejections":[{"scanId":"scan::a","reason":"r"}]}""")]
	[InlineData("""{"accepted":4,"duplicatesSkipped":-1,"rejected":0,"rejections":[]}""")]
	[InlineData("""{"accepted":2147483647,"duplicatesSkipped":2147483647,"rejected":2,"rejections":[{},{}]}""")]
	public async Task UploadScans_WithCountsThatDoNotMatchTheBatch_IsAnUnexpectedResponse(string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Batch(3));

		Assert.False(result.Succeeded);
		Assert.Equal(ApiErrorKind.UnexpectedResponse, result.Error!.Kind);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("""{"rejections":[]}""")]
	[InlineData("""{"duplicatesSkipped":0,"rejected":0,"rejections":[]}""")]
	public async Task UploadScans_WithMissingCounts_IsAnUnexpectedResponse_NotASuccess(string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.False(result.Succeeded);
		Assert.Null(result.Value);
		Assert.Equal(ApiErrorKind.UnexpectedResponse, result.Error!.Kind);
	}

	[Theory]
	[InlineData("""{"accepted":3,"duplicatesSkipped":0,"rejected":0,"rejections":[]}""")]
	[InlineData("""{"accepted":0,"duplicatesSkipped":3,"rejected":0}""")]
	[InlineData("""{"accepted":1,"duplicatesSkipped":1,"rejected":1,"rejections":[{"scanId":"scan::a","reason":"r"}]}""")]
	[InlineData("""{"accepted":0,"duplicatesSkipped":0,"rejected":3,"rejections":[{"scanId":"scan::a"},{"scanId":"scan::b"},{"scanId":"scan::c"}]}""")]
	public async Task UploadScans_WithCountsThatAddUpToTheBatch_Succeeds(string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Batch(3));

		Assert.True(result.Succeeded);
	}

	[Theory]
	[InlineData("application/json", "{\"accepted\":")]
	[InlineData("application/json", "")]
	[InlineData("application/json", "null")]
	[InlineData("application/json", """{"accepted":"one"}""")]
	[InlineData("application/json", """{"accepted":-1,"duplicatesSkipped":0,"rejected":0}""")]
	[InlineData("text/html", "<html>Gateway</html>")]
	public async Task UploadScans_WithAMalformedBody_IsAnUnexpectedResponse(string mediaType, string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body, mediaType));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Equal(ApiErrorKind.UnexpectedResponse, result.Error!.Kind);
	}

	[Fact]
	public async Task UploadScans_Forbidden_ShowsTheServersDetail()
	{
		using var host = new ClientHost(_ => Responses.Problem(
			HttpStatusCode.Forbidden,
			"""{"title":"Forbidden","status":403,"detail":"Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored."}"""));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Equal(ApiErrorKind.Forbidden, result.Error!.Kind);
		Assert.Equal(403, result.Error.StatusCode);
		Assert.Equal(
			"Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored.",
			result.Error.Message);
		Assert.Empty(result.Error.FieldErrors);
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest, ApiErrorKind.Validation, "The server refused the request because some values are invalid.")]
	[InlineData(HttpStatusCode.Forbidden, ApiErrorKind.Forbidden, "You are not allowed to do this.")]
	[InlineData(HttpStatusCode.NotFound, ApiErrorKind.NotFound, "The server could not find what was requested.")]
	[InlineData(HttpStatusCode.UnsupportedMediaType, ApiErrorKind.OtherStatus, "The server refused the request (HTTP 415).")]
	[InlineData(HttpStatusCode.Unauthorized, ApiErrorKind.Unauthorized, "Your sign-in is missing, invalid or expired. Sign in again.")]
	[InlineData(HttpStatusCode.InternalServerError, ApiErrorKind.ServerError, "The server could not complete the request. Try again later.")]
	[InlineData(HttpStatusCode.ServiceUnavailable, ApiErrorKind.ServerError, "The server could not complete the request. Try again later.")]
	public async Task UploadScans_ErrorWithoutABody_HasAStandardMessage(
		HttpStatusCode status,
		ApiErrorKind kind,
		string message)
	{
		using var host = new ClientHost(_ => Responses.Empty(status));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Equal(kind, result.Error!.Kind);
		Assert.Equal((int)status, result.Error.StatusCode);
		Assert.Equal(message, result.Error.Message);
	}

	[Theory]
	[InlineData("application/problem+json", "{\"title\":")]
	[InlineData("application/problem+json", "[]")]
	[InlineData("text/html", "<html><body>Not Found</body></html>")]
	[InlineData("text/plain", """{"title":"Looks like JSON but is labelled text"}""")]
	public async Task UploadScans_ErrorWithAnUnreadableBody_HasTheStandardMessage(string mediaType, string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.NotFound, body, mediaType));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Equal(ApiErrorKind.NotFound, result.Error!.Kind);
		Assert.Equal("The server could not find what was requested.", result.Error.Message);
	}

	[Fact]
	public async Task UploadScans_ServerError_NeverShowsTheServersText()
	{
		using var host = new ClientHost(_ => Responses.Problem(
			HttpStatusCode.InternalServerError,
			"""{"title":"System.InvalidOperationException","status":500,"detail":"at ApexPost.Api.Internal.Stack() on internal-host-db01"}"""));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.Equal(ApiErrorKind.ServerError, result.Error!.Kind);
		Assert.Equal("The server could not complete the request. Try again later.", result.Error.Message);
		Assert.DoesNotContain("internal-host", result.Error.Message);
	}

	[Fact]
	public async Task ErrorMessage_DropsControlCharacters_AndIsLimitedTo300Characters()
	{
		var detail = "Line\u001b[31m one\r\n" + new string('x', 400);
		using var host = new ClientHost(_ => Responses.Problem(
			HttpStatusCode.NotFound,
			JsonSerializer.Serialize(new Dictionary<string, string> { ["detail"] = detail })));

		var result = await host.Client.UploadScansAsync(TrackingNumber, Upload);

		Assert.StartsWith("Line[31m one" + new string('x', 10), result.Error!.Message);
		Assert.DoesNotContain(result.Error.Message, char.IsControl);
		Assert.Equal(301, result.Error.Message.Length);
		Assert.EndsWith("…", result.Error.Message);
	}

	[Fact]
	public async Task NetworkFailure_IsThrown_NotReportedAsAServerAnswer()
	{
		using var host = new ClientHost();
		host.Network.Throw = new HttpRequestException("No route to host.");

		await Assert.ThrowsAsync<HttpRequestException>(
			() => host.Client.UploadScansAsync(TrackingNumber, Upload));
	}

	private static ScanUploadRequest Batch(int count) => new(
	[
		.. Enumerable.Range(0, count).Select(i => Upload.Scans[0] with
		{
			ScanId = $"scan::{Guid.NewGuid():D}",
			Location = $"Hub {i}"
		})
	]);

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	public async Task BlankArguments_AreRefusedBeforeAnyRequest(string blank)
	{
		using var host = new ClientHost();

		await Assert.ThrowsAsync<ArgumentException>(() => host.Client.RequestCourierTokenAsync(blank));
		await Assert.ThrowsAsync<ArgumentException>(() => host.Client.UploadScansAsync(blank, Upload));
		Assert.Empty(host.Network.Requests);
	}
}
