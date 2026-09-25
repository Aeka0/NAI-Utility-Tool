using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NAITool.Services;

/// <summary>Lossless seed text shared by metadata, settings, editors and requests.</summary>
public static class SeedValue
{
    private static bool TryInteger(string? text, out BigInteger value) =>
        BigInteger.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public static bool IsRandom(string? text)
    {
        var digits = text.AsSpan().Trim();
        if (digits.IsEmpty) return true;
        if (digits[0] is '+' or '-') digits = digits[1..];
        if (digits.IsEmpty) return false;
        // This runs on each edit; do not parse a potentially huge integer just to test for zero.
        foreach (char digit in digits)
            if (digit != '0') return false;
        return true;
    }

    public static string Resolve(string? text, bool forceRandom = false) =>
        forceRandom || IsRandom(text)
            ? Random.Shared.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture)
            : text!;

    public static string Adjust(string? text, int delta) =>
        TryInteger(text, out var value)
            ? BigInteger.Max(BigInteger.Zero, value + delta).ToString(CultureInfo.InvariantCulture)
            : "0";

    public static string? ReadJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    // Preserve JSON numbers for numeric API seeds without passing through double/int32.
    // Text seeds go to the provider unchanged; the wildcard hash below is local only.
    public static object ToRequestValue(string text) => TryInteger(text, out var value)
        ? JsonSerializer.Deserialize<JsonElement>(value.ToString(CultureInfo.InvariantCulture))
        : text;

    public static int ToWildcardSeed(string text)
    {
        // Keep existing wildcard choices for seeds that were already supported.
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return value;
        if (TryInteger(text, out var integer))
            text = integer.ToString(CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return BinaryPrimitives.ReadInt32LittleEndian(hash);
    }
}

/// <summary>Also reads numeric seeds from settings saved by earlier versions.</summary>
public sealed class SeedTextJsonConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return SeedValue.ReadJson(document.RootElement) ?? "0";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
