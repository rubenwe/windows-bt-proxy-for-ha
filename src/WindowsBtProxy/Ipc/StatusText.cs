using System.Globalization;
using System.Text;
using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Ipc;

/// <summary>
/// The status as people read it: what the <c>status</c> command prints and the tray's status window shows, so both say the
/// same thing in the same words.
/// </summary>
internal static class StatusText
{
    /// <summary>Every line after the first starts with a label padded to this width, so the values line up.</summary>
    private const int LabelWidth = 16;

    /// <summary>
    /// The status as lines separated by <see cref="Environment.NewLine"/> (what both a console and a Win32 edit control expect),
    /// with no line break at the end.
    /// </summary>
    public static string Format(ProxyStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{s.FriendlyName} ({s.Name}), version {s.Version}");

        var bluetooth = $"{Describe(s.Scanner)}, {s.ScanMode.ToString().ToLowerInvariant()} scan";
        Line(text, "Bluetooth:", s.AdapterMac is null ? bluetooth : $"{bluetooth}, adapter {s.AdapterMac}");
        Line(text, "Advertisements:", s.AdvertisementsLastMinute is { } count
            ? string.Create(CultureInfo.InvariantCulture, $"{count} in the last minute")
            : "not counted yet");
        Line(text, "API:", s.ApiRunning ? string.Create(CultureInfo.InvariantCulture, $"port {s.Port}") : "waiting for a Bluetooth adapter");
        Line(text, "Encryption:", s.Encryption == EncryptionMode.Required
            ? "key set; only encrypted connections"
            : "no key yet; Home Assistant provisions one when it adds the proxy");

        if (s.Clients.Count == 0)
        {
            Line(text, "Clients:", "none");
        }
        for (var i = 0; i < s.Clients.Count; i++)
        {
            Line(text, i == 0 ? "Clients:" : string.Empty, Describe(s.Clients[i]));
        }
        return text.ToString();
    }

    /// <summary>
    /// What to say when nothing answers on the pipe. The pipe is named only when it is not the service's own, which only happens
    /// in tests, where it tells which proxy was asked.
    /// </summary>
    public static string NotRunning(string pipeName)
    {
        const string message = "The Windows BT Proxy service is not running (or not installed).";
        return pipeName == PipeProtocol.DefaultPipeName ? message : $"{message} (pipe {pipeName})";
    }

    private static void Line(StringBuilder text, string label, string value)
        => text.Append(Environment.NewLine).Append(label.PadRight(LabelWidth)).Append(value);

    private static string Describe(ScannerStatus scanner) => scanner switch
    {
        ScannerStatus.Starting => "Starting",
        ScannerStatus.Running => "Running",
        ScannerStatus.NoAdapter => "No adapter",
        ScannerStatus.RadioOff => "Radio off",
        ScannerStatus.Failed => "Failed",
        _ => scanner.ToString(),
    };

    private static string Describe(ClientStatus client)
    {
        var flags = $"{(client.Subscribed ? "subscribed" : "not subscribed")}, {(client.Encrypted ? "encrypted" : "not encrypted")}";
        return string.IsNullOrEmpty(client.Info) ? $"{client.Address}  {flags}" : $"{client.Address}  {Printable(client.Info)}  {flags}";
    }

    /// <summary>
    /// Control characters become spaces. The info is whatever a device on the LAN sent in its hello; a line break in it must not
    /// add a line of its own making to the status.
    /// </summary>
    private static string Printable(string text) => string.Create(text.Length, text, static (chars, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            chars[i] = char.IsControl(source[i]) ? ' ' : source[i];
        }
    });
}
