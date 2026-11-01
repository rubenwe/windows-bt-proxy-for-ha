using System.IO.Pipes;
using System.Security.Principal;

namespace WindowsBtProxy.Ipc;

/// <summary>Decides whether the client on the other end of a pipe is an elevated administrator, from its own token.</summary>
internal static class ClientToken
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// True when the connected client's token has BUILTIN\Administrators enabled, which for a UAC user means elevated (a filtered
    /// token carries the group as deny-only). Throws when the client's token cannot be read (for example an anonymous-level client);
    /// <see cref="StatusPipeServer"/> treats that as not an administrator.
    /// </summary>
    /// <remarks>
    /// Clients connect at identification level, so the token can be inspected but never acted with. The callback is synchronous:
    /// the thread must not leave the client's identity through an <c>await</c>.
    /// </remarks>
    public static bool IsAdministrator(NamedPipeServerStream pipe)
    {
        var administrator = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            // WindowsIdentity.GetCurrent falls back to the process token (here SYSTEM) when the thread has none; that must
            // never count as the client's. A client's token is an impersonation token, so it has a level.
            administrator = identity.ImpersonationLevel != TokenImpersonationLevel.None
                && new WindowsPrincipal(identity).IsInRole(Administrators);
        });
        return administrator;
    }
}
