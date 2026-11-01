using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Config;

/// <summary>
/// Reads <c>settings.json</c>, creating it with defaults on first start. A file with missing fields is not rewritten (the user's
/// file is theirs; the defaults are applied in memory); an unreadable one is moved aside to <c>settings.json.bad</c> and replaced.
/// </summary>
public static class SettingsStore
{
    public static ProxySettings LoadOrCreate(DataPaths paths, string machineName, ILogger log)
    {
        var defaults = Defaults(machineName);
        string text;
        try
        {
            text = File.ReadAllText(paths.SettingsFile);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            TryWrite(paths, defaults, log);
            return defaults;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked (antivirus, an editor) or access denied: the file may be fine, so leave it exactly as it is.
            log.LogWarning(e, "Could not read {File}; using defaults without touching the file.", paths.SettingsFile);
            return defaults;
        }

        ProxySettings? parsed = null;
        string? problem = null;
        try
        {
            parsed = JsonSerializer.Deserialize(text, JsonContext.Default.ProxySettings);
            problem = parsed is null ? "it holds no settings object" : null;
        }
        catch (JsonException e)
        {
            problem = e.Message;
        }

        if (parsed is null)
        {
            log.LogWarning("{File} is not valid ({Problem}); keeping it as {Bad} and using defaults.", paths.SettingsFile, problem, paths.SettingsFile + ".bad");
            try
            {
                File.Move(paths.SettingsFile, paths.SettingsFile + ".bad", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Writing defaults now would destroy the only copy of the user's file, so run on defaults in memory instead.
                log.LogWarning(e, "Could not move the invalid settings file aside; leaving it in place and using defaults.");
                return defaults;
            }
            TryWrite(paths, defaults, log);
            return defaults;
        }

        return Normalize(parsed, defaults, log);
    }

    internal static ProxySettings Defaults(string machineName) => new(NameSanitizer.DefaultName(machineName), NameSanitizer.DefaultFriendlyName(machineName));

    /// <summary>Fills in what the file left out and repairs values the service cannot use, warning about each repair.</summary>
    private static ProxySettings Normalize(ProxySettings loaded, ProxySettings defaults, ILogger log)
    {
        var name = loaded.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = defaults.Name;
        }
        else
        {
            var clean = NameSanitizer.Sanitize(name);
            if (clean != name)
            {
                if (clean.Length == 0)
                {
                    clean = defaults.Name;
                }
                log.LogWarning("Settings name \"{Name}\" must be lower case a-z, 0-9 and dashes, at most {Max} characters; using \"{Clean}\".", name, NameSanitizer.MaxNameLength, clean);
            }
            name = clean;
        }

        var port = loaded.Port;
        if (port is < 1 or > 65535)
        {
            log.LogWarning("Settings port {Port} is not a valid TCP port; using {Default}.", port, ProxySettings.DefaultPort);
            port = ProxySettings.DefaultPort;
        }

        return loaded with
        {
            Name = name,
            FriendlyName = string.IsNullOrWhiteSpace(loaded.FriendlyName) ? defaults.FriendlyName : loaded.FriendlyName,
            Port = port,
            LogLevel = string.IsNullOrWhiteSpace(loaded.LogLevel) ? defaults.LogLevel : loaded.LogLevel,
        };
    }

    /// <summary>Writes through a temp file so a crash cannot leave half a settings file. Failing to write is logged, not fatal: the service can run on defaults.</summary>
    private static void TryWrite(DataPaths paths, ProxySettings settings, ILogger log)
    {
        var temp = paths.SettingsFile + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonContext.Default.ProxySettings) + Environment.NewLine);
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not write default settings to {File}; running on defaults.", paths.SettingsFile);
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
