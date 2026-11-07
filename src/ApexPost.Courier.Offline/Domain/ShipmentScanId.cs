namespace ApexPost.Courier.Offline.Domain;

// Document IDs use the format scan::{Guid}, with the GUID in its standard
// hyphenated form.
public static class ShipmentScanId
{
	public const string Prefix = "scan::";

	public static string New() => Prefix + Guid.NewGuid().ToString("D");

	public static bool IsValid(string? id) =>
		id is not null
		&& id.StartsWith(Prefix, StringComparison.Ordinal)
		&& Guid.TryParseExact(id[Prefix.Length..], "D", out var guid)
		&& guid != Guid.Empty;

	public static string Validate(string? id, string parameterName = "id")
	{
		if (!IsValid(id))
		{
			throw new ArgumentException(
				$"A scan ID must have the format {Prefix}{{Guid}}.",
				parameterName);
		}

		return id!;
	}
}
