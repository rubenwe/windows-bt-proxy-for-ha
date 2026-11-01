using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WindowsBtProxy.Ipc;

/// <summary>Who may open the status pipe: local interactive users to talk to it, administrators and the service to run it, never the network.</summary>
internal static class PipeAcl
{
    /// <summary>
    /// What a logged-on user gets: read and write the pipe (and read its attributes and permissions, which the client needs to
    /// connect). Deliberately without <see cref="PipeAccessRights.CreateNewInstance"/>: with it, any user could add a server
    /// instance of this pipe and answer other users' clients, including an administrator's <c>key</c> request.
    /// </summary>
    public const PipeAccessRights InteractiveRights = PipeAccessRights.ReadWrite;

    /// <summary>
    /// Deny NETWORK (<c>S-1-5-2</c>) everything; allow SYSTEM, BUILTIN\Administrators and this process's own user full control,
    /// and INTERACTIVE (<c>S-1-5-4</c>) <see cref="InteractiveRights"/>. Nothing else: no Everyone, Users or Authenticated Users,
    /// so service accounts and remote logons get nothing. Well-known SIDs, so it works on any display language.
    /// </summary>
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        // A network logon of an interactive user's account still carries NETWORK; the deny wins over every allow below.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The server's own user must be able to create the further instances; as the service that is SYSTEM again (merged with the rule above).
        using (var self = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), InteractiveRights, AccessControlType.Allow));
        return security;
    }
}
