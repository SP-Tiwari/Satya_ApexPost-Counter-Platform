using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Security;

/// <summary>
/// D3: marks an endpoint that only an approved partner may call, with its
/// key in the <c>X-Api-Key</c> header. <see cref="PartnerApiKeyMiddleware"/>
/// checks the key.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequirePartnerApiKeyAttribute : Attribute;

/// <summary>
/// D3: checks the partner API key of every endpoint that carries
/// <see cref="RequirePartnerApiKeyAttribute"/>. It runs after routing, so it
/// reads the selected endpoint's metadata; endpoints without the attribute are
/// not affected.
/// </summary>
/// <remarks>
/// The same approach as the Week 2 partner endpoint (ApexPost.Web's
/// ApiKeyAndIpRestrictionMiddleware): the keys come from configuration (User
/// Secrets in Development), never from the repository, and are held only as
/// SHA-256 hashes. The presented key is hashed and compared with every
/// configured hash in constant time, so neither the comparison nor the number
/// of keys checked reveals how much of a key was right. A missing or wrong key
/// is a 401 Problem Details; no configured key at all is a 500, because that is
/// a deployment fault, not the caller's. Keys are never logged.
/// </remarks>
public sealed class PartnerApiKeyMiddleware
{
	/// <summary>The request header that carries the partner's key.</summary>
	public const string HeaderName = "X-Api-Key";

	/// <summary>The configuration section: one entry per partner, name = key.</summary>
	public const string ConfigurationSection = "PartnerApiKeys";

	private readonly RequestDelegate _next;
	private readonly byte[][] _keyHashes;

	/// <summary>Reads and hashes the configured partner keys.</summary>
	/// <param name="next">The rest of the pipeline.</param>
	/// <param name="configuration">Holds the <c>PartnerApiKeys</c> section.</param>
	public PartnerApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
	{
		_next = next;
		_keyHashes =
		[
			.. configuration.GetSection(ConfigurationSection).GetChildren()
				.Select(partner => partner.Value)
				.Where(key => !string.IsNullOrWhiteSpace(key))
				.Select(key => SHA256.HashData(Encoding.UTF8.GetBytes(key!)))
		];
	}

	/// <summary>Lets the request through only with a valid partner key.</summary>
	/// <param name="context">The request.</param>
	/// <param name="problemDetails">Writes the refusal.</param>
	public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
	{
		if (context.GetEndpoint()?.Metadata.GetMetadata<RequirePartnerApiKeyAttribute>() is null)
		{
			await _next(context);
			return;
		}

		if (_keyHashes.Length == 0)
		{
			await RefuseAsync(
				context, problemDetails, StatusCodes.Status500InternalServerError,
				"The partner API is not configured.", detail: null);
			return;
		}

		if (!HasValidKey(context.Request))
		{
			await RefuseAsync(
				context, problemDetails, StatusCodes.Status401Unauthorized,
				"A valid partner API key is required.",
				$"Send the partner's key in the {HeaderName} header.");
			return;
		}

		await _next(context);
	}

	private bool HasValidKey(HttpRequest request)
	{
		if (!request.Headers.TryGetValue(HeaderName, out var values)
			|| values.Count != 1
			|| string.IsNullOrEmpty(values[0]))
		{
			return false;
		}

		var presented = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
		var matched = false;
		foreach (var keyHash in _keyHashes)
			matched |= CryptographicOperations.FixedTimeEquals(presented, keyHash);
		return matched;
	}

	private static async Task RefuseAsync(
		HttpContext context,
		IProblemDetailsService problemDetails,
		int status,
		string title,
		string? detail)
	{
		context.Response.StatusCode = status;
		await problemDetails.WriteAsync(new ProblemDetailsContext
		{
			HttpContext = context,
			ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
		});
	}
}
