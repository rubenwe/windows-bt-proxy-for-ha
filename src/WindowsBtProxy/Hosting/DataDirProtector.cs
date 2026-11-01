using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Hosting;

/// <summary>
/// What the service does to the data folder before it reads or writes anything in it: apply the access rules (creating the folder
/// with them if it is missing, so it is never created with %ProgramData%'s inherited ACL, which lets Users create files), then
/// verify that nothing in it is a link or is owned by anyone but SYSTEM or Administrators. A seam: tests must not apply real ACLs
/// to temp folders that a non-elevated user has to clean up.
/// </summary>
internal sealed class DataDirProtector(Action<string> apply, Action<string> verify)
{
    /// <summary>The real thing; the service runs as LocalSystem, which may do both.</summary>
    public static DataDirProtector System { get; } = new(DataDirAcl.Apply, root => DataDirGuard.Verify(root, FileSystemInspector.Instance));

    /// <summary>Applies, then verifies. Null if the folder can be trusted, otherwise the fatal error (exit code 2) to report.</summary>
    public HostFatalException? TryProtect(string root)
    {
        try
        {
            apply(root);
            verify(root);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = e is SetupException
                ? $"The data folder cannot be trusted: {e.Message} The API is not started."
                : $"The data folder {root} could not be protected: {e.Message} The API is not started.";
            return new HostFatalException(ExitCodes.CorruptKey, message, e);
        }
    }
}
