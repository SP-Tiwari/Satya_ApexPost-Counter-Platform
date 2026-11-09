using System.Net.Http.Headers;

namespace ApexPost.Client.Consumer;

// Week 4 Part D: sends the configured access token on every request as
// "Authorization: Bearer ...". The token comes from configuration
// (ApexPostApi:AccessToken, for example the ApexPostApi__AccessToken
// environment variable), never from a committed file, and is never logged.
// Extending the generated client's pipeline this way leaves the generated
// file untouched.
public sealed class BearerTokenHandler(string accessToken) : DelegatingHandler
{
	protected override Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		return base.SendAsync(request, cancellationToken);
	}
}
