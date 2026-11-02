using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApexPost.Web.Ui;

// Shared view helpers, so every page renders icons and status badges the same
// way. They only format values the page already has.
public static class UiHtmlHelperExtensions
{
	public static IHtmlContent Icon(this IHtmlHelper html, string name, string? cssClass = null)
	{
		ArgumentNullException.ThrowIfNull(html);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var encoder = HtmlEncoder.Default;
		var href = $"{html.ViewContext.HttpContext.Request.PathBase}/img/icons.svg#{name}";
		var classes = cssClass is null ? "icon" : $"icon {cssClass}";
		return new HtmlString(
			$"<svg class=\"{encoder.Encode(classes)}\" aria-hidden=\"true\" focusable=\"false\">" +
			$"<use href=\"{encoder.Encode(href)}\"></use></svg>");
	}

	public static IHtmlContent StatusBadge(this IHtmlHelper html, object? status, string? tone = null)
	{
		ArgumentNullException.ThrowIfNull(html);

		var text = status?.ToString() ?? string.Empty;
		return new HtmlString(
			$"<span class=\"status-badge tone-{tone ?? ToneOf(text)}\">{HtmlEncoder.Default.Encode(text)}</span>");
	}

	// Colour only; the badge always shows the status text as well.
	public static string ToneOf(string status) => status switch
	{
		"Paid" or "Issued" or "Active" or "Passed" => "success",
		"Open" => "info",
		"Refunded" => "warning",
		"Voided" or "Cancelled" or "Failed" or "Closed" or "Inactive" => "danger",
		_ => "neutral"
	};
}
