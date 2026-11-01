using System.Text.Json.Serialization;

namespace WindowsBtProxy.Config;

public enum ScanMode
{
    [JsonStringEnumMemberName("passive")]
    Passive,

    [JsonStringEnumMemberName("active")]
    Active,
}

public sealed record UpdateSettings(bool Check = true, bool Auto = false);

/// <summary>
/// The contents of <c>settings.json</c>. <see cref="Name"/> and <see cref="FriendlyName"/> have no constant default (they depend on
/// the machine name), so <see cref="SettingsStore"/> fills them in when the file leaves them out.
/// </summary>
public sealed record ProxySettings(
    string Name,
    string FriendlyName,
    int Port = ProxySettings.DefaultPort,
    ScanMode ScanMode = ScanMode.Passive,
    string[]? Interfaces = null,
    string LogLevel = "info",
    UpdateSettings? Updates = null)
{
    public const int DefaultPort = 6053;

    /// <summary>Never null: a file without an <c>updates</c> object (or with <c>null</c>) gets the update defaults.</summary>
    public UpdateSettings Updates { get; init; } = Updates ?? new UpdateSettings();
}
