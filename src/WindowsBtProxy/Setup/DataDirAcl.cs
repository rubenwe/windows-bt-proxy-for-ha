using System.Security.AccessControl;
using System.Security.Principal;

namespace WindowsBtProxy.Setup;

/// <summary>The access rules of the data directory: settings and logs readable by everyone, writable only by administrators and the service.</summary>
public static class DataDirAcl
{
    /// <summary>
    /// SYSTEM and BUILTIN\Administrators full control, BUILTIN\Users read and execute, inheritance from the parent removed, and every
    /// rule inherited by sub-folders and files. Well-known SIDs, so it works on any display language.
    /// </summary>
    public static DirectorySecurity Build()
    {
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Creates <paramref name="path"/> with the ACL, or replaces the ACL of an existing folder.</summary>
    public static void Apply(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            info.SetAccessControl(Build());
        }
        else
        {
            info.Create(Build());
        }
    }
}
