using System.Text.Json.Serialization;

namespace WindowsBtProxy.Ipc;

/// <summary>Source-generated JSON for the local status pipe (no reflection, so it is trim and AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProxyStatus))]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
