using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace IncidentLab;

public static class J
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false
    };
    public static JsonObject Obj(object value) => JsonSerializer.SerializeToNode(value, Options)!.AsObject();
    public static JsonArray Arr<T>(IEnumerable<T> values) => JsonSerializer.SerializeToNode(values, Options)!.AsArray();
    public static JsonObject Copy(this JsonObject value) => (JsonObject)value.DeepClone();
    public static string S(this JsonNode? value, string key, string fallback = "") => value?[key]?.GetValue<string>() ?? fallback;
    public static double N(this JsonNode? value, string key, double fallback = 0) => value?[key] is { } n
        ? double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : fallback;
    public static bool B(this JsonNode? value, string key) => value?[key]?.GetValue<bool>() ?? false;
    public static JsonArray A(this JsonNode? value, string key) => value?[key] as JsonArray ?? [];
    public static JsonObject O(this JsonNode? value, string key) => value?[key] as JsonObject ?? new();
    public static string Id() => Guid.NewGuid().ToString("N");
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    public static string Clip(string value, int length) => value[..Math.Min(value.Length, length)];
    public static bool SecretEquals(string supplied, string expected) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    public static string Canonical(JsonNode? value) => value switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => value.ToJsonString()
    };
}

public sealed class LabException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
