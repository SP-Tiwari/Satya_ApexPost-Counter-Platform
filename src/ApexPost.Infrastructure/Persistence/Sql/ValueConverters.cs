using ApexPost.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace ApexPost.Infrastructure.Persistence.Sql;

public sealed class BranchCodeConverter() : ValueConverter<BranchCode, string>(
	code => code.Value,
	value => new BranchCode(value));

public sealed class SkuConverter() : ValueConverter<Sku, string>(
	sku => sku.Value,
	value => new Sku(value));

public sealed class WeightConverter() : ValueConverter<Weight, decimal>(
	weight => Math.Round(weight.Kilograms, 3, MidpointRounding.AwayFromZero),
	value => new Weight(value / DecimalScale.OneWithFullScale));

// Decimal columns return their full scale, such as 20.00. Dividing by this
// removes trailing zeros, so a stored value reads back and displays as entered.
internal static class DecimalScale
{
	public const decimal OneWithFullScale = 1.000000000000000000000000000000000m;
}

public sealed class TrailingZeroConverter() : ValueConverter<decimal, decimal>(
	value => value,
	value => value / DecimalScale.OneWithFullScale);

// The one place where a money amount meets decimal(18,2). Domain arithmetic is
// unrounded, so a shipping price can carry more decimals; the rule is explicit
// here rather than left to the driver. See ADR 0017.
public sealed class MoneyAmountConverter() : ValueConverter<decimal, decimal>(
	amount => Math.Round(amount, 2, MidpointRounding.AwayFromZero),
	value => value / DecimalScale.OneWithFullScale);

// Stores UTC in datetime2 and restores a zero offset.
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTime>(
	value => value.UtcDateTime,
	value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

public sealed class RouteValuesConverter() : ValueConverter<IReadOnlyDictionary<string, string>, string>(
	values => Serialize(values),
	json => Deserialize(json))
{
	private static string Serialize(IReadOnlyDictionary<string, string> values) =>
		JsonSerializer.Serialize(values);

	private static IReadOnlyDictionary<string, string> Deserialize(string json) =>
		JsonSerializer.Deserialize<Dictionary<string, string>>(json)
			?? new Dictionary<string, string>();
}
