using Microsoft.Win32;

namespace WindowsBtProxy.Setup;

/// <summary>
/// The autostart value that starts the tray at every user's logon. Behind an interface so install and uninstall can be tested without
/// touching the registry; the tray reads it too, because a missing value is its signal to quit (see <c>TrayLifecycle</c>).
/// </summary>
public interface IRunKey
{
    /// <summary>The value's command line, or null when there is none.</summary>
    string? Read();

    /// <summary>Creates or replaces the value. Needs administrator rights.</summary>
    void Write(string commandLine);

    /// <summary>Removes the value; nothing happens when it is already gone. Needs administrator rights.</summary>
    void Delete();
}

/// <summary>
/// The real <see cref="IRunKey"/>: value <see cref="ValueName"/> under <c>HKLM\</c><see cref="KeyPath"/> in the default registry view.
/// HKLM rather than HKCU so one install starts the tray for every user. <see cref="Read"/> opens the key read-only, so a tray running as a
/// normal user can call it; only <see cref="Write"/> and <see cref="Delete"/> open it for writing.
/// </summary>
public sealed class HklmRunKey : IRunKey
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "WindowsBtProxyForHA";

    public string? Read()
    {
        using var hklm = OpenLocalMachine();
        using var key = hklm.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string commandLine)
    {
        using var hklm = OpenLocalMachine();
        using var key = hklm.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, commandLine, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var hklm = OpenLocalMachine();
        using var key = hklm.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static RegistryKey OpenLocalMachine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
}
