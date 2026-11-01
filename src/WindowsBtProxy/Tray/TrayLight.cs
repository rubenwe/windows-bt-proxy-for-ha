using WindowsBtProxy.Ble;
using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Tray;

/// <summary>The tray icon's colour; the hex values live with the icon drawing, not here.</summary>
public enum Light
{
    /// <summary>At least one Home Assistant client is subscribed to advertisements.</summary>
    Green,

    /// <summary>The service answers and its scanner is not in a red state, but nothing is subscribed.</summary>
    Amber,

    /// <summary>The service is unreachable or broken, or its scanner cannot scan.</summary>
    Red,
}

/// <summary>What the tray shows for one poll: the icon colour and the hover text.</summary>
/// <param name="Light">The icon colour.</param>
/// <param name="Tooltip">At most <see cref="TrayLight.MaxTooltipLength"/> UTF-16 chars, so the shell never cuts it itself.</param>
public readonly record struct TrayView(Light Light, string Tooltip);

/// <summary>
/// The rules that turn a status-pipe answer into the tray icon's colour and tooltip. Pure, so the Win32 tray only has to draw the result.
/// </summary>
public static class TrayLight
{
    /// <summary>Capacity of <c>NOTIFYICONDATA.szTip</c> (128 UTF-16 chars) minus its terminating NUL.</summary>
    public const int MaxTooltipLength = 127;

    private const string DefaultName = "Windows BT Proxy";
    private const string Ellipsis = "…";

    /// <summary>Decides the colour and tooltip for one poll.</summary>
    /// <param name="response">The service's answer, or null when the pipe could not be reached.</param>
    /// <remarks>
    /// An answer with neither a status nor an error is malformed and is treated like an error answer: the service is there but cannot be trusted
    /// to be doing its job, so red. A scanner in a red state outranks a subscribed client, because a client that is subscribed to nothing
    /// useful is not a working proxy.
    /// </remarks>
    public static TrayView Evaluate(PipeResponse? response)
    {
        if (response is null)
        {
            return Red(DefaultName, "service not running");
        }
        if (response.Status is not { } status)
        {
            return Red(DefaultName, $"service answered \"{response.Error ?? "<no status>"}\"");
        }

        var name = string.IsNullOrWhiteSpace(status.FriendlyName) ? DefaultName : status.FriendlyName;
        switch (status.Scanner)
        {
            case ScannerStatus.RadioOff:
                return Red(name, "Bluetooth is off");
            case ScannerStatus.NoAdapter:
                return Red(name, "no Bluetooth adapter");
            case ScannerStatus.Failed:
                return Red(name, "Bluetooth scanner failed");
        }

        return status.Clients.Any(c => c.Subscribed)
            ? new TrayView(Light.Green, Tooltip(name, "connected to Home Assistant"))
            : new TrayView(Light.Amber, Tooltip(name, "running, Home Assistant not connected"));
    }

    private static TrayView Red(string name, string text) => new(Light.Red, Tooltip(name, text));

    private static string Tooltip(string name, string text)
    {
        var full = $"{name}: {text}";
        if (full.Length <= MaxTooltipLength)
        {
            return full;
        }

        // Keep room for the ellipsis, and do not leave half a surrogate pair at the cut.
        var keep = MaxTooltipLength - Ellipsis.Length;
        if (char.IsHighSurrogate(full[keep - 1]))
        {
            keep--;
        }
        return string.Concat(full.AsSpan(0, keep), Ellipsis);
    }
}
