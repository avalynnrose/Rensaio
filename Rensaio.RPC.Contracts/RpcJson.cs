using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rensaio.RPC.Contracts;

/// <summary>
/// Central JSON settings for every model payload that crosses the RPC boundary.
///
/// The shared <c>Mihon.ExtensionsBridge.Models</c> types are serialized with these
/// options on both sides so a payload produced by the RPC host deserializes
/// identically in RensaioBackend (and vice versa). Keeping this in one place is
/// what lets us avoid duplicating DTOs in the protobuf contract.
/// </summary>
public static class RpcJson
{
    /// <summary>Shared serializer options for all RPC model payloads.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Serializes a model payload to JSON.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes a model payload, returning default when the input is empty.</summary>
    public static T? Deserialize<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}
