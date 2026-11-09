using ApexPost.Courier.Offline.Domain;

namespace ApexPost.Courier.Sync.Contracts;

// The client's own copies of the API's JSON shapes, written from the published
// contract (contracts/v2.json and the Development token endpoint) rather than
// shared with the API project, so the client depends only on the wire format.
// Response properties are nullable: a missing or null field is read without an
// exception, and ApexApiClient decides whether the response is usable.

// POST /dev/token request (Development only).
public sealed record DevelopmentTokenRequest(string CourierId, string Role);

// POST /dev/token response.
public sealed record DevelopmentTokenResponse(
	string? AccessToken,
	string? TokenType,
	DateTimeOffset? ExpiresAtUtc);

// POST /api/v2/shipments/{trackingNumber}/scans request: 1 to 100 scans.
public sealed record ScanUploadRequest(IReadOnlyList<ScanUploadItem> Scans);

// One scan in an upload. ScanType is written as its name ("InTransit"), which
// the API accepts as well as the number.
public sealed record ScanUploadItem(
	string ScanId,
	string TrackingNumber,
	ScanType ScanType,
	string CourierId,
	string Location,
	DateTimeOffset ScannedAtUtc,
	string? Notes);

// The upload response: accepted, duplicates skipped and rejected, with the
// reason for each rejected scan.
public sealed record ScanUploadResponse(
	int Accepted,
	int DuplicatesSkipped,
	int Rejected,
	IReadOnlyList<RejectedScan>? Rejections);

public sealed record RejectedScan(string? ScanId, string? Reason);

// RFC 9457 Problem Details, as the API writes for every error.
public class ProblemDetails
{
	public string? Type { get; init; }

	public string? Title { get; init; }

	public int? Status { get; init; }

	public string? Detail { get; init; }
}

// The API's 400 for invalid input: Problem Details plus the messages for each
// field. Error bodies are read as this type, which also reads a plain
// ProblemDetails (errors is then null).
public sealed class ValidationProblemDetails : ProblemDetails
{
	public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
