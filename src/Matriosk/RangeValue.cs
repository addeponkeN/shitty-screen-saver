using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matriosk;

/// <summary>
/// A tuning value with an optional random range. A zero-width range is a fixed value,
/// so every effect setting keeps working unchanged; a wider range is sampled per use
/// (per drop, per ripple) or per scene roll.
/// </summary>
public readonly record struct RangeValue(float Min, float Max)
{
    public bool IsFixed => Max - Min <= 1e-6f;
    public float Low => MathF.Min(Min, Max);
    public float High => MathF.Max(Min, Max);
    public float Mid => (Low + High) * 0.5f;

    public float Sample() => IsFixed ? Low : Low + Random.Shared.NextSingle() * (High - Low);

    public static RangeValue Fixed(float value) => new(value, value);
    public static RangeValue Between(float a, float b) => new(MathF.Min(a, b), MathF.Max(a, b));

    /// <summary>Lets presets and existing code assign a plain number.</summary>
    public static implicit operator RangeValue(float value) => new(value, value);
}

/// <summary>
/// Reads a float that may still be written as a [min, max] range (configs saved while
/// those fields were randomisable). The midpoint is used, so old files keep their
/// settings instead of failing to parse.
/// </summary>
public sealed class LenientFloatConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetSingle();

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0].GetSingle();
            var second = root.GetArrayLength() > 1 ? root[1].GetSingle() : first;
            return (first + second) * 0.5f;
        }

        return 0;
    }

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>
/// JSON shape: a number for a fixed value, a two-element array for a range.
/// Keeps existing config files valid and avoids brackets around fixed values.
/// </summary>
public sealed class RangeValueJsonConverter : JsonConverter<RangeValue>
{
    public override RangeValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var value = reader.GetSingle();
            return new RangeValue(value, value);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            if (!reader.Read()) return default;
            var first = reader.TokenType == JsonTokenType.Number ? reader.GetSingle() : 0;

            if (!reader.Read()) return new RangeValue(first, first);
            var second = reader.TokenType == JsonTokenType.Number ? reader.GetSingle() : first;

            reader.Read(); // consume EndArray
            return RangeValue.Between(first, second);
        }

        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, RangeValue value, JsonSerializerOptions options)
    {
        if (value.IsFixed)
        {
            writer.WriteNumberValue(value.Low);
            return;
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(value.Low);
        writer.WriteNumberValue(value.High);
        writer.WriteEndArray();
    }
}
