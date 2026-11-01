using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Crypto;

public sealed class KeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("keystore-tests-").FullName;

    private string KeyPath => Path.Combine(_dir, "noise.key");

    [Fact]
    public void Load_missing_returns_null()
    {
        Assert.Null(new KeyStore(KeyPath, applyAcl: false).Load());
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var store = new KeyStore(KeyPath, applyAcl: false);

        store.Save(key);

        Assert.Equal(key, store.Load());
        Assert.Equal(key, new KeyStore(KeyPath, applyAcl: false).Load());
    }

    [Fact]
    public void Save_writes_base64_text_and_leaves_no_temp_file()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        new KeyStore(KeyPath, applyAcl: false).Save(key);

        Assert.Equal(Convert.ToBase64String(key), File.ReadAllText(KeyPath).Trim());
        Assert.Equal(["noise.key"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_replaces_an_existing_key_and_ignores_a_stale_temp_file()
    {
        var store = new KeyStore(KeyPath, applyAcl: false);
        store.Save(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(KeyPath + ".tmp", "left over from a crash");
        var second = RandomNumberGenerator.GetBytes(32);

        store.Save(second);

        Assert.Equal(second, store.Load());
        Assert.False(File.Exists(KeyPath + ".tmp"));
    }

    [Fact]
    public void Load_garbage_throws_KeyStoreCorruptException()
    {
        File.WriteAllText(KeyPath, "this is not base64 at all!");

        Assert.Throws<KeyStoreCorruptException>(() => new KeyStore(KeyPath, applyAcl: false).Load());
    }

    [Fact]
    public void Load_wrong_length_throws_KeyStoreCorruptException()
    {
        File.WriteAllText(KeyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        Assert.Throws<KeyStoreCorruptException>(() => new KeyStore(KeyPath, applyAcl: false).Load());
    }

    [Fact]
    public void Load_empty_or_all_zero_key_throws_KeyStoreCorruptException()
    {
        var store = new KeyStore(KeyPath, applyAcl: false);

        File.WriteAllText(KeyPath, "");
        Assert.Throws<KeyStoreCorruptException>(() => store.Load());

        // An all-zero key would make the "keyed" device accept zero-PSK clients: fail closed instead.
        File.WriteAllText(KeyPath, Convert.ToBase64String(new byte[32]));
        Assert.Throws<KeyStoreCorruptException>(() => store.Load());
    }

    [Fact]
    public void Load_unreadable_file_throws_KeyStoreCorruptException()
    {
        File.WriteAllText(KeyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        using var locked = new FileStream(KeyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<KeyStoreCorruptException>(() => new KeyStore(KeyPath, applyAcl: false).Load());
    }

    [Fact]
    public void Save_rejects_wrong_length_and_all_zero_keys()
    {
        var store = new KeyStore(KeyPath, applyAcl: false);

        Assert.Throws<ArgumentException>(() => store.Save(new byte[31]));
        Assert.Throws<ArgumentException>(() => store.Save(new byte[32]));
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void Key_file_security_is_SYSTEM_and_Administrators_only_without_inheritance()
    {
        File.WriteAllText(KeyPath, "x");

        new FileInfo(KeyPath).SetAccessControl(KeyStore.CreateFileSecurity());

        // The owner (this test user) keeps READ_CONTROL, so the ACL is readable even though the data no longer is.
        AssertSystemAndAdministratorsOnly(new FileInfo(KeyPath).GetAccessControl());
    }

    [Fact]
    public void Save_with_acl_protects_the_file_or_refuses_a_non_admin()
    {
        var store = new KeyStore(KeyPath);
        var key = RandomNumberGenerator.GetBytes(32);

        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // Elevated (CI runners, the service as SYSTEM): the key lands with the restricted ACL.
            store.Save(key);
            AssertSystemAndAdministratorsOnly(new FileInfo(KeyPath).GetAccessControl());
            Assert.Equal(key, store.Load());
        }
        else
        {
            // A normal user is not in the ACL it writes, so it cannot move the temp file into place: no key file appears.
            Assert.Throws<UnauthorizedAccessException>(() => store.Save(key));
            Assert.False(File.Exists(KeyPath));
        }
    }

    private static void AssertSystemAndAdministratorsOnly(FileSecurity security)
    {
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => (Sid: ((SecurityIdentifier)r.IdentityReference).Value, r.AccessControlType, r.FileSystemRights, r.IsInherited))
            .OrderBy(r => r.Sid, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [
                ("S-1-5-18", AccessControlType.Allow, FileSystemRights.FullControl, false), // NT AUTHORITY\SYSTEM
                ("S-1-5-32-544", AccessControlType.Allow, FileSystemRights.FullControl, false), // BUILTIN\Administrators
            ],
            rules);
    }

    public void Dispose()
    {
        // Give the files back to this user first: the ACL tests leave files only SYSTEM and Administrators may open.
        foreach (var file in Directory.GetFiles(_dir))
        {
            var security = new FileSecurity();
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        Directory.Delete(_dir, recursive: true);
    }
}
