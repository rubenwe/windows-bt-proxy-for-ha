using System.Text;

namespace WindowsBtProxy.Config;

/// <summary>Turns machine names into ESPHome-style device names: lower case a-z, 0-9 and single dashes, at most 31 characters.</summary>
public static class NameSanitizer
{
    public const int MaxNameLength = 31;

    private const string DefaultPrefix = "bt-proxy-";

    /// <summary><c>bt-proxy-</c> plus the sanitised machine name.</summary>
    public static string DefaultName(string machineName) => Sanitize(DefaultPrefix + machineName);

    public static string DefaultFriendlyName(string machineName) => "BT Proxy " + machineName;

    /// <summary>Lower-cases (invariant), maps everything outside a-z, 0-9 to a dash, collapses dash runs, trims dashes and caps the length.</summary>
    public static string Sanitize(string raw)
    {
        var result = new StringBuilder(Math.Min(raw.Length, MaxNameLength + 1));
        foreach (var c in raw.ToLowerInvariant())
        {
            var keep = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (keep)
            {
                result.Append(c);
            }
            else if (result.Length > 0 && result[^1] != '-')
            {
                result.Append('-');
            }

            // Everything past the cap is cut anyway, so stop early.
            if (result.Length > MaxNameLength)
            {
                break;
            }
        }

        // Cut first, trim after: the cut may land right behind a dash.
        if (result.Length > MaxNameLength)
        {
            result.Length = MaxNameLength;
        }
        while (result.Length > 0 && result[^1] == '-')
        {
            result.Length--;
        }
        return result.ToString();
    }
}
