namespace ApexPost.Web.Models;

// Presentation-only models for the shared chart, toast and empty-state
// partials. Views fill them from data they already received.

public sealed record ChartPoint(string Label, decimal Value, decimal? Marker = null);

// Horizontal bars. Marker draws a reference line, such as a stock threshold.
public sealed record BarChartViewModel(
	IReadOnlyList<ChartPoint> Points,
	string? ValueSuffix = null,
	string? MarkerLabel = null);

public sealed record DonutChartViewModel(
	IReadOnlyList<ChartPoint> Segments,
	string CenterValue,
	string CenterLabel);

// Tone is one of success, danger, warning or info.
public sealed record StatusToastViewModel(string Message, string Tone = "success");

public sealed record EmptyStateViewModel(string Icon, string Title, string? Text = null);
