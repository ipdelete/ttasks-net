using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Ttasks.Core;

internal static class MetadataValues
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?>? metadata)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata is null)
            return new ReadOnlyDictionary<string, object?>(normalized);

        foreach (var (key, value) in metadata)
            normalized[ValidateKey(key)] = NormalizeValue(value);

        return new ReadOnlyDictionary<string, object?>(normalized);
    }

    public static object? NormalizeValue(object? value)
    {
        if (value is null or string or bool)
            return value;

        if (value is DateTime or DateTimeOffset or TimeSpan)
            throw new ArgumentException("Metadata values must be JSON-compatible.");

        if (value is byte or sbyte or short or ushort or int or uint or long)
            return Convert.ToInt64(value);

        if (value is ulong ulongValue)
        {
            if (ulongValue > long.MaxValue)
                throw new ArgumentException("Metadata integer values must fit in Int64.");
            return (long)ulongValue;
        }

        if (value is float floatValue)
            return ValidateFinite(floatValue);
        if (value is double doubleValue)
            return ValidateFinite(doubleValue);
        if (value is decimal decimalValue)
            return ValidateFinite((double)decimalValue);

        if (value is JsonElement element)
            return NormalizeJsonElement(element);

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            return Normalize(readOnlyDictionary);

        if (value is IDictionary<string, object?> dictionary)
            return Normalize(new Dictionary<string, object?>(dictionary, StringComparer.Ordinal));

        if (value is IEnumerable enumerable && value is not string)
        {
            var values = new List<object?>();
            foreach (var item in enumerable)
                values.Add(NormalizeValue(item));
            return values.AsReadOnly();
        }

        throw new ArgumentException("Metadata values must be JSON-compatible.");
    }

    public static string ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Metadata keys must be non-empty.", nameof(key));
        return key;
    }

    public static string Serialize(IReadOnlyDictionary<string, object?> metadata) =>
        JsonSerializer.Serialize(metadata, JsonOptions);

    public static IReadOnlyDictionary<string, object?> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Normalize(null);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Metadata JSON must be an object.");

        return (IReadOnlyDictionary<string, object?>)NormalizeJsonElement(document.RootElement)!;
    }

    private static double ValidateFinite(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("Metadata number values must be finite.");
        return value;
    }

    private static object? NormalizeJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => Normalize(element.EnumerateObject().ToDictionary(property => property.Name, property => NormalizeJsonElement(property.Value), StringComparer.Ordinal)),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToList().AsReadOnly(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : ValidateFinite(element.GetDouble()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new ArgumentException("Metadata values must be JSON-compatible.")
        };
}
