using System.Security.AccessControl;
using System.Security.Principal;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

public sealed class DataDirAclTests
{
    private static readonly InheritanceFlags ChildrenInherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    // Never applied to a real folder here: a protected, admin-only ACL would lock this (non-elevated) test user out of its own clean-up.
    private static List<FileSystemAccessRule> Rules()
        => DataDirAcl.Build().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    [Fact]
    public void Build_is_protected_so_nothing_is_inherited_from_ProgramData()
    {
        Assert.True(DataDirAcl.Build().AreAccessRulesProtected);
        Assert.All(Rules(), r => Assert.False(r.IsInherited));
    }

    [Fact]
    public void Build_gives_exactly_system_administrators_and_users_a_rule()
    {
        var sids = Rules().Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Order().ToArray();

        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-32-545" }.Order().ToArray(), sids);
    }

    [Theory]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    public void Build_gives_system_and_administrators_full_control(WellKnownSidType who)
    {
        var rule = RuleFor(who);

        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(ChildrenInherit, rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
    }

    [Fact]
    public void Build_gives_users_read_and_execute_only()
    {
        var rule = RuleFor(WellKnownSidType.BuiltinUsersSid);

        // Windows adds SYNCHRONIZE to "Read & execute"; it grants nothing beyond reading.
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rule.FileSystemRights);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(ChildrenInherit, rule.InheritanceFlags);
        Assert.Equal(0, (int)(rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.ChangePermissions)));
    }

    private static FileSystemAccessRule RuleFor(WellKnownSidType who)
        => Rules().Single(r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(who));
}
