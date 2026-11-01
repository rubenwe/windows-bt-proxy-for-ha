using System.Buffers;
using System.Buffers.Text;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WindowsBtProxy.Crypto;

/// <summary>
/// The Noise pre-shared key on disk (<c>noise.key</c>): one line of base64 text for 32 bytes.
/// <see cref="Save"/> writes a temp file and moves it over the old one, so a crash never leaves a half-written key, and
/// (unless <c>applyAcl</c> is false, for tests running as a normal user) creates it with an ACL of SYSTEM and
/// Administrators full control and no inherited entries. Not thread-safe; <see cref="EncryptionState"/> serializes saves.
/// </summary>
public sealed class KeyStore(string path, bool applyAcl = true)
{
    public const int KeySize = NoiseHandshake.PskSize;

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>The stored key, or null if there is no key file. Throws <see cref="KeyStoreCorruptException"/> if it cannot be read or is not a valid key.</summary>
    public byte[]? Load()
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(Path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new KeyStoreCorruptException($"The key file {Path} could not be read: {e.Message}", e);
        }

        // Bytes rather than strings throughout, so every copy of the secret can be wiped.
        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(raw.Length)];
        try
        {
            // Never echo the file content: it is (or is close to) the secret.
            var text = raw.AsSpan().Trim(" \t\r\n"u8);
            if (Base64.DecodeFromUtf8(text, buffer, out _, out var written) != OperationStatus.Done || written != KeySize)
            {
                throw new KeyStoreCorruptException($"The key file {Path} does not hold base64 for a {KeySize}-byte key.");
            }
            var key = buffer[..KeySize];
            if (IsAllZero(key))
            {
                throw new KeyStoreCorruptException($"The key file {Path} holds an all-zero key, which is the unprovisioned key.");
            }
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>Atomically replaces the key file with <paramref name="key32"/>. IO failures propagate and leave any previous file in place.</summary>
    public void Save(ReadOnlySpan<byte> key32)
    {
        if (key32.Length != KeySize)
        {
            throw new ArgumentException($"A Noise key is {KeySize} bytes.", nameof(key32));
        }
        if (IsAllZero(key32))
        {
            throw new ArgumentException("The all-zero key is reserved for unprovisioned devices.", nameof(key32));
        }

        var text = new byte[Base64.GetMaxEncodedToUtf8Length(KeySize) + 2];
        Base64.EncodeToUtf8(key32, text, out _, out var length);
        "\r\n"u8.CopyTo(text.AsSpan(length));
        var temp = Path + ".tmp";
        try
        {
            // A temp file left by a crash may carry an old ACL; creating with a security descriptor only applies it to a new file.
            File.Delete(temp);
            using (var stream = applyAcl
                ? new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, CreateFileSecurity())
                : new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(text.AsSpan(0, length + 2));
                stream.Flush(flushToDisk: true);
            }
            // Same-volume rename: the key file keeps the temp file's explicit, protected ACL.
            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }
    }

    /// <summary>SYSTEM and BUILTIN\Administrators full control; inheritance from the parent folder removed.</summary>
    internal static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static bool IsAllZero(ReadOnlySpan<byte> key) => key.IndexOfAnyExcept((byte)0) < 0;

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The key file exists but cannot be used. The service must fail closed: never serve the API without the key.</summary>
public sealed class KeyStoreCorruptException : Exception
{
    public KeyStoreCorruptException()
        : this("The key file is corrupt.")
    {
    }

    public KeyStoreCorruptException(string message)
        : base(message)
    {
    }

    public KeyStoreCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
