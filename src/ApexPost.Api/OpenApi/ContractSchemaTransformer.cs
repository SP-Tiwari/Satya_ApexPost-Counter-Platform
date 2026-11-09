using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApexPost.Api.Models;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ApexPost.Api.OpenApi;

// States in the schema what the wire format already is, so that a generated
// client (Week 4 Part C, C6) gets precise types. The JSON on the wire does not
// change.
// - An enum written as its name has only "enum": [names]; "type": "string"
//   is added, so a generator reads and writes it as a string.
// - An enum written as its number has only "type": "integer"; its values and
//   their names ("x-enumNames", read by NSwag) are added, so a generator
//   creates an enum, not an int.
// - A decimal has "format": "double"; it becomes "decimal", so a generator
//   uses decimal, not double.
internal sealed class ContractSchemaTransformer : IOpenApiSchemaTransformer
{
	public Task TransformAsync(
		OpenApiSchema schema,
		OpenApiSchemaTransformerContext context,
		CancellationToken cancellationToken)
	{
		var type = context.JsonTypeInfo.Type;
		type = Nullable.GetUnderlyingType(type) ?? type;

		if (type == typeof(decimal))
		{
			schema.Format = "decimal";
		}
		else if (type.IsEnum && IsNameInput(context.JsonPropertyInfo?.AttributeProvider))
		{
			// A request property that also accepts the name (Week 4 Part D)
			// is published as the numeric enum the responses return.
			SetNumericEnum(schema, type);
		}
		else if (type.IsEnum && schema.Type is null
			&& schema.Enum is { Count: > 0 } names
			&& names.All(name => name?.GetValueKind() == JsonValueKind.String))
		{
			schema.Type = JsonSchemaType.String;
		}
		else if (type.IsEnum && schema.Type == JsonSchemaType.Integer)
		{
			SetNumericEnum(schema, type);
		}

		return Task.CompletedTask;
	}

	// The property's [JsonConverter] attribute names the converter; the
	// serializer wraps it (in a nullable converter), so it is read from there.
	private static bool IsNameInput(ICustomAttributeProvider? property) =>
		property?.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false)
			.OfType<JsonConverterAttribute>()
			.Any(attribute => attribute.ConverterType is { IsGenericType: true } converterType
				&& converterType.GetGenericTypeDefinition() == typeof(EnumNameInputConverter<>)) == true;

	private static void SetNumericEnum(OpenApiSchema schema, Type type)
	{
		var members = Enum.GetValues(type).Cast<Enum>().ToArray();
		schema.Type = JsonSchemaType.Integer;
		schema.Enum = [.. members.Select(member => (JsonNode)Convert.ToInt64(member))];
		schema.Extensions ??= new Dictionary<string, IOpenApiExtension>();
		schema.Extensions["x-enumNames"] = new JsonNodeExtension(
			new JsonArray([.. members.Select(member => (JsonNode)member.ToString())]));
	}
}
