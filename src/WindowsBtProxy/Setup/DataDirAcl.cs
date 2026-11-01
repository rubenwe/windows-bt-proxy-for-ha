using System.Security.AccessControl;
using System.Security.Principal;

namespace WindowsBtProxy.Setup;

/// <summary>The access rules of the data directory: settings and logs readable by everyone, writable only by administrators and the service.</summary>
public static class DataDirAcl
{
    /// <summary>
    /// Owner BUILTIN\Administrators; SYSTEM and BUILTIN\Administrators full control, BUILTIN\Users read and execute, inheritance from
    /// the parent removed, and every rule inherited by sub-folders and files. Well-known SIDs, so it works on any display language.
    /// </summary>
    public static DirectorySecurity Build()
    {
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        // The owner can always rewrite the permissions, so it must not be whoever happened to create the folder.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Creates <paramref name="path"/> with the ACL, or replaces the owner and ACL of an existing folder. Refuses a link.</summary>
    public static void Apply(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SetupException($"{path} is a link (junction or symbolic link), not a plain folder; delete it and run install again.");
            }
        }
        else
        {
            info.Create(Build());
        }
        // Narrow the window between the checks above and the write: a junction swapped in meanwhile must not be re-ACLed.
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupException($"{path} is a link (junction or symbolic link), not a plain folder; delete it and run install again.");
        }
        // The descriptor has both the owner and the access rules marked as changed, so both are written.
        info.SetAccessControl(Build());
    }
}
