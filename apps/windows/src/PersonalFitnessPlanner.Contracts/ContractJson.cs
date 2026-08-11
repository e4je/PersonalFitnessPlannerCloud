using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalFitnessPlanner.Contracts;

/// <summary>
/// System.Text.Json settings shared by the REST client, local snapshots and
/// outbox payloads. Readers deliberately accept common legacy representations;
/// writers always emit canonical snake_case JSON, UUID strings and UTC ISO-8601
/// timestamps.
/// </summary>
public static class ContractJson
{
    private static readonly JsonSerializerOptions SharedOptions = CreateOptions();

    /// <summary>
    /// A read-only shared instance. Call <see cref="CreateOptions"/> when callers
    /// need to append application-specific converters.
    /// </summary>
    public static JsonSerializerOptions Options => SharedOptions;

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        options.Converters.Add(new FlexibleGuidJsonConverter());
        options.Converters.Add(new IsoDateTimeOffsetJsonConverter());
        options.Converters.Add(new IsoDateOnlyJsonConverter());
        options.Converters.Add(new FlexibleBooleanJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }

    public static string Serialize<T>(T value, bool writeIndented = false)
    {
        if (!writeIndented)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        var options = new JsonSerializerOptions(Options) { WriteIndented = true };
        return JsonSerializer.Serialize(value, options);
    }

    public static T? Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}

public sealed class FlexibleGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Guid.Empty;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("UUID must be encoded as a string.");
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Guid.Empty;
        }

        if (Guid.TryParse(text, out var value))
        {
            return value;
        }

        throw new JsonException($"'{text}' is not a valid UUID.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D", CultureInfo.InvariantCulture));
}

public sealed class IsoDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private const long MillisecondThreshold = 100_000_000_000;

    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var epoch))
        {
            return FromEpoch(epoch);
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Timestamp must be an ISO-8601 string or Unix epoch number.");
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericEpoch))
        {
            return FromEpoch(numericEpoch);
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new JsonException($"'{text}' is not a valid ISO-8601 timestamp.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static DateTimeOffset FromEpoch(long epoch) =>
        Math.Abs(epoch) >= MillisecondThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
            : DateTimeOffset.FromUnixTimeSeconds(epoch);
}

public sealed class IsoDateOnlyJsonConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Local date must be an ISO-8601 date string.");
        }

        var text = reader.GetString();
        if (DateOnly.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return date;
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime))
        {
            return DateOnly.FromDateTime(dateTime.Date);
        }

        throw new JsonException($"'{text}' is not a valid local date.");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

public sealed class FlexibleBooleanJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number when reader.TryGetInt64(out var number) => number != 0,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
            JsonTokenType.String when long.TryParse(
                reader.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number) => number != 0,
            _ => throw new JsonException("Boolean must be true/false, 1/0, or an equivalent string."),
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
