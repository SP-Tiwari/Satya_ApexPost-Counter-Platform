using System.Text.Json.Serialization;

namespace ApexPost.Api.Models;

/// <summary>
/// For a request property whose enum the contract publishes as a number:
/// reads the number or the name, such as <c>1</c> or <c>"InTransit"</c>.
/// Only for request DTOs, which are never written, so responses keep the
/// number. ContractSchemaTransformer publishes the property as the numeric
/// enum, so the shared enum schema that the responses use does not change.
/// </summary>
/// <typeparam name="TEnum">The enum.</typeparam>
public sealed class EnumNameInputConverter<TEnum> : JsonStringEnumConverter<TEnum>
	where TEnum : struct, Enum;
