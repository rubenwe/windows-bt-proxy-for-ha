using System.Globalization;
using System.Text;

namespace WindowsBtProxy.Discovery;

/// <summary>
/// Skips a DNS-SD publish when nothing it would announce has changed since the last successful one: the selected interfaces (by
/// index, with their IPv4 addresses), whether a key is set, and the network type. Windows raises <c>NetworkAddressChanged</c> for
/// many things that do not touch those (a DHCP lease renewal, an unrelated adapter), and every publish tears the registrations
/// down and up again. Used from one thread at a time (the debounced publish action).
/// </summary>
internal sealed class RepublishFilter
{
    private string? _published;

    /// <summary>Runs <paramref name="publish"/> unless it would announce what was announced last time. A publish that throws is not remembered, so the next trigger retries it.</summary>
    /// <returns>True if <paramref name="publish"/> ran.</returns>
    public bool TryPublish(IReadOnlyList<NetInterface> interfaces, bool hasKey, Action publish)
    {
        var state = Describe(interfaces, hasKey);
        if (state == _published)
        {
            return false;
        }
        publish();
        _published = state;
        return true;
    }

    internal static string Describe(IReadOnlyList<NetInterface> interfaces, bool hasKey)
    {
        var text = new StringBuilder(FormattableString.Invariant($"key={hasKey};network={TxtRecords.NetworkFor(interfaces)}"));
        foreach (var nic in interfaces.OrderBy(i => i.Index))
        {
            text.Append(CultureInfo.InvariantCulture, $";{nic.Index}={nic.IPv4}");
        }
        return text.ToString();
    }
}
