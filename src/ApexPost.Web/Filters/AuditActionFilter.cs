using System.Runtime.ExceptionServices;
using System.Diagnostics;
using ApexPost.Application.Auditing;
using ApexPost.Web.Middleware;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ApexPost.Web.Filters;

// Records [Audited] actions only. The record is written after the result has
// executed, so the status code is the one actually sent (302 for a redirect,
// 400/401/409 for a redisplayed form) rather than the 200 default that the
// response still carries while the action filter runs.
public sealed class AuditActionFilter(
	IAuditStore auditStore,
	ILogger<AuditActionFilter> logger) : IAsyncActionFilter, IAsyncAlwaysRunResultFilter
{
	private static readonly object PendingAuditKey = new();

	public async Task OnActionExecutionAsync(
		ActionExecutingContext context,
		ActionExecutionDelegate next)
	{
		if (!IsAudited(context.ActionDescriptor))
		{
			await next().ConfigureAwait(false);
			return;
		}

		var pending = new PendingAudit(
			Stopwatch.GetTimestamp(),
			GetSanitizedRouteValues(context));
		context.HttpContext.Items[PendingAuditKey] = pending;

		ActionExecutedContext? executedContext = null;
		Exception? escapedException = null;
		try
		{
			executedContext = await next().ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			escapedException = exception;
		}

		// An unhandled action exception produces no MVC result; the exception
		// handler middleware answers 500, so record that now. No exception
		// filters are registered that could turn it into another status.
		if (escapedException is not null ||
			executedContext is { Exception: not null, ExceptionHandled: false })
		{
			await AppendAsync(
				context,
				pending,
				StatusCodes.Status500InternalServerError,
				actionFailed: true).ConfigureAwait(false);
		}

		if (escapedException is not null)
		{
			ExceptionDispatchInfo.Capture(escapedException).Throw();
		}
	}

	// Lets an audited action name the record it acted on when that ID is not
	// in the route, such as a sale's transaction ID, which is posted in the
	// form. Only allow-listed, non-sensitive keys are kept; outside an
	// audited action this does nothing.
	public static void AddAuditValue(
		HttpContext httpContext,
		string key,
		string? value)
	{
		ArgumentNullException.ThrowIfNull(httpContext);
		if (httpContext.Items.TryGetValue(PendingAuditKey, out var item) &&
			item is PendingAudit { Written: false } pending &&
			IsAllowedRouteKey(key) &&
			!string.IsNullOrWhiteSpace(value))
		{
			pending.RouteValues[key] = value.Trim();
		}
	}

	public async Task OnResultExecutionAsync(
		ResultExecutingContext context,
		ResultExecutionDelegate next)
	{
		if (!context.HttpContext.Items.TryGetValue(PendingAuditKey, out var item) ||
			item is not PendingAudit { Written: false } pending)
		{
			await next().ConfigureAwait(false);
			return;
		}

		ResultExecutedContext? executedContext = null;
		Exception? escapedException = null;
		try
		{
			executedContext = await next().ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			escapedException = exception;
		}

		var resultFailed = escapedException is not null ||
			executedContext is { Exception: not null, ExceptionHandled: false };
		var response = context.HttpContext.Response;
		var statusCode = resultFailed && !response.HasStarted
			? StatusCodes.Status500InternalServerError
			: response.StatusCode;

		await AppendAsync(
			context,
			pending,
			statusCode,
			resultFailed).ConfigureAwait(false);

		if (escapedException is not null)
		{
			ExceptionDispatchInfo.Capture(escapedException).Throw();
		}
	}

	private async Task AppendAsync(
		FilterContext context,
		PendingAudit pending,
		int statusCode,
		bool actionFailed)
	{
		pending.Written = true;
		var elapsedMilliseconds =
			Stopwatch.GetElapsedTime(pending.StartedAt).TotalMilliseconds;
		var controller =
			context.ActionDescriptor.RouteValues["controller"] ?? "Unknown";
		var action =
			context.ActionDescriptor.RouteValues["action"] ?? "Unknown";
		var outcome = actionFailed || statusCode >= 500
			? "Failed"
			: statusCode >= 400
				? "Rejected"
				: "Succeeded";
		var user = context.HttpContext.User;

		var record = new AuditRecord(
			DateTimeOffset.UtcNow,
			controller,
			action,
			$"Executed {controller}.{action}",
			outcome,
			statusCode,
			elapsedMilliseconds,
			pending.RouteValues,
			user.FindFirst("StaffId")?.Value,
			user.FindFirst("Role")?.Value,
			user.FindFirst("BranchCode")?.Value,
			context.HttpContext.Items.TryGetValue(
				CorrelationIdMiddleware.HttpContextItemKey,
				out var correlationId)
					? correlationId?.ToString()
					: null);

		try
		{
			// Not RequestAborted: the business action has already run, so a
			// client disconnecting must not drop its audit record.
			await auditStore.AppendAsync(record, CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch (Exception auditException)
		{
			// The business action has already finished, so a failed audit
			// write is logged and never changes the response.
			logger.LogError(
				auditException,
				"Failed to append audit record for {Controller}.{Action}.",
				controller,
				action);
		}
	}

	private static bool IsAudited(ActionDescriptor descriptor) =>
		descriptor.EndpointMetadata.Any(metadata => metadata is AuditedAttribute);

	private static Dictionary<string, string> GetSanitizedRouteValues(
		ActionExecutingContext context)
	{
		var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, value) in context.RouteData.Values)
		{
			if (!IsAllowedRouteKey(key) || value is null)
				continue;

			var text = value.ToString();
			if (string.IsNullOrWhiteSpace(text))
				continue;

			sanitized[key] = text.Trim();
		}

		return sanitized;
	}

	private static bool IsAllowedRouteKey(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
			return false;

		if (IsSensitiveRouteKey(key))
			return false;

		return key.Equals("controller", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("action", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("id", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("transactionId", StringComparison.OrdinalIgnoreCase) ||
			key.Equals("branchCode", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsSensitiveRouteKey(string key) =>
		key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("api", StringComparison.OrdinalIgnoreCase) &&
			key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("document", StringComparison.OrdinalIgnoreCase) ||
		key.Contains("card", StringComparison.OrdinalIgnoreCase);

	// State carried from the action stage to the result stage of one request.
	private sealed class PendingAudit(
		long startedAt,
		Dictionary<string, string> routeValues)
	{
		public long StartedAt { get; } = startedAt;
		public Dictionary<string, string> RouteValues { get; } = routeValues;
		public bool Written { get; set; }
	}
}
