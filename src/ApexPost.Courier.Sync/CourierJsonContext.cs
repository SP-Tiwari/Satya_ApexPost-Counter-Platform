using System.Text.Json;
using System.Text.Json.Serialization;
using ApexPost.Courier.Sync.Contracts;

namespace ApexPost.Courier.Sync;

// D8: the client's shared JSON settings, matching the v2 API, with the
// serialization code generated at compile time for every type the client
// reads or writes:
// - camelCase names, matched case-insensitively (JsonSerializerDefaults.Web);
// - enums written as names; a number is still read, because the v2 responses
//   send scanType as a number;
// - unknown properties are ignored.
// CourierJsonContext.Default.Options are bound to this context, so serializing
// any other type with them fails instead of falling back to reflection.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DevelopmentTokenRequest))]
[JsonSerializable(typeof(DevelopmentTokenResponse))]
[JsonSerializable(typeof(ScanUploadRequest))]
[JsonSerializable(typeof(ScanUploadResponse))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(StoredToken))]
internal sealed partial class CourierJsonContext : JsonSerializerContext;

// The token store's payload, encrypted before it is written to disk.
internal sealed record StoredToken(string? AccessToken, DateTimeOffset? ExpiresAtUtc);
