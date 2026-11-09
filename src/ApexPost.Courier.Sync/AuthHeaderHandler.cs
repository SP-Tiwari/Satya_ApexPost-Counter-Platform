using System.Net;
using System.Net.Http.Headers;

namespace ApexPost.Courier.Sync;

// D6: attaches the stored bearer token to every request of ApexApiClient, as
// "Authorization: Bearer <token>". Without a token the request goes out
// without the header. A 401 response clears the token and signals that sign-in
// is required (CourierSession); no other status touches the token. The handler
// sends no requests of its own, so it cannot loop, and it never logs the token
// or the header (the HttpClient logs redact every header; AddCourierSync).
public sealed class AuthHeaderHandler(ITokenStore tokens, CourierSession session) : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		var token = tokens.GetToken();
		if (token is not null)
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

		var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.Unauthorized)
			session.OnUnauthorized(token?.Value);

		return response;
	}
}
