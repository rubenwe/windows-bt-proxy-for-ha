using System.Security.AccessControl;
using System.Security.Principal;

namespace WindowsBtProxy.Setup;

/// <summary>What the data-dir check needs to know about a path; a seam because tests cannot create files owned by someone else.</summary>
public interface IDataDirInspector
{
    bool IsReparsePoint(string path);

    /// <summary>The owner of the file or folder, or null if it cannot be determined.</summary>
    SecurityIdentifier? GetOwner(string path);
}

/// <summary>The real inspector, reading file attributes and the security descriptor's owner.</summary>
public sealed class FileSystemInspector : IDataDirInspector
{
    public static FileSystemInspector Instance { get; } = new();

    public bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public SecurityIdentifier? GetOwner(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
    }
}

/// <summary>
/// Refuses to build on an existing data folder that a normal user could have prepared. Users may create folders in
/// <c>%ProgramData%</c> and then own them (an owner can always rewrite the permissions), pre-place files such as a known
/// <c>noise.key</c>, or make the folder a junction to somewhere else. Nothing is repaired: the user deletes it and installs again.
/// </summary>
public static class DataDirGuard
{
    /// <summary>Throws <see cref="SetupException"/> if <paramref name="root"/> or anything under it is a link or is not owned by SYSTEM or Administrators. A folder that does not exist yet is fine.</summary>
    public static void Verify(string root, IDataDirInspector inspector)
    {
        if (Directory.Exists(root))
        {
            Check(root, inspector);
        }
    }

    private static void Check(string path, IDataDirInspector inspector)
    {
        if (inspector.IsReparsePoint(path))
        {
            throw new SetupException($"{path} is a link (junction or symbolic link), not a plain folder or file; delete it and run install again.");
        }
        var owner = inspector.GetOwner(path);
        if (owner is null || !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
        {
            throw new SetupException($"{path} is not owned by SYSTEM or Administrators; delete it and run install again.");
        }
        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                Check(child, inspector);
            }
        }
    }
}
