using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsBtProxy.Config;

/// <summary>Source-generated JSON for <c>settings.json</c> (no reflection, so it is trim and AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProxySettings))]
internal sealed partial class JsonContext : JsonSerializerContext;
