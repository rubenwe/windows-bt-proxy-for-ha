using System.Security.Cryptography;
using System.Text;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Crypto;

public sealed class EncryptionStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("encryption-state-tests-").FullName;

    private KeyStore Store => new(Path.Combine(_dir, "noise.key"), applyAcl: false);

    /// <summary>The key field as Home Assistant sends it: the ASCII bytes of base64(32 random bytes).</summary>
    private static byte[] KeyField(byte[] key) => Encoding.ASCII.GetBytes(Convert.ToBase64String(key));

    [Fact]
    public void Provision_accepts_base64_32_bytes_on_zero_psk_session()
    {
        var state = EncryptionState.Load(Store);
        var changed = 0;
        state.KeyChanged += () => changed++;
        var key = RandomNumberGenerator.GetBytes(32);

        Assert.True(state.TryProvision(KeyField(key), sessionIsZeroPsk: true));

        Assert.True(state.HasKey);
        Assert.Equal(key, state.Key.ToArray());
        Assert.Equal(1, changed);
        Assert.Equal(key, Store.Load()); // persisted
        Assert.Equal(key, EncryptionState.Load(Store).Key.ToArray());
    }

    [Fact]
    public void Provision_rejects_on_plaintext_session()
    {
        var state = EncryptionState.Load(Store);

        Assert.False(state.TryProvision(KeyField(RandomNumberGenerator.GetBytes(32)), sessionIsZeroPsk: false));

        Assert.False(state.HasKey);
        Assert.True(state.Key.IsEmpty);
        Assert.Null(Store.Load());
    }

    [Fact]
    public void Provision_rejects_when_key_exists()
    {
        var existing = RandomNumberGenerator.GetBytes(32);
        Store.Save(existing);
        var state = EncryptionState.Load(Store);
        var changed = 0;
        state.KeyChanged += () => changed++;

        Assert.False(state.TryProvision(KeyField(RandomNumberGenerator.GetBytes(32)), sessionIsZeroPsk: true));

        Assert.Equal(existing, state.Key.ToArray());
        Assert.Equal(existing, Store.Load());
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Provision_rejects_all_zero_and_empty_and_wrong_length()
    {
        var state = EncryptionState.Load(Store);
        byte[][] rejected =
        [
            KeyField(new byte[32]),                              // all zero
            [],                                                  // empty: clearing the key is local-only
            KeyField(RandomNumberGenerator.GetBytes(16)),        // too short
            KeyField(RandomNumberGenerator.GetBytes(33)),        // too long
            RandomNumberGenerator.GetBytes(32),                  // raw key bytes instead of base64 text
            Encoding.ASCII.GetBytes("not base64 *** at all"),
            Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))[..42]), // 42 chars: 31 bytes
        ];

        foreach (var field in rejected)
        {
            Assert.False(state.TryProvision(field, sessionIsZeroPsk: true));
        }

        Assert.False(state.HasKey);
        Assert.Null(Store.Load());
    }

    [Fact]
    public void Provision_decodes_leniently_like_aioesphomeapi()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var text = Convert.ToBase64String(key);
        // binascii.a2b_base64 (non-strict) skips whitespace and other non-alphabet characters.
        var messy = $"  {text[..10]}\r\n{text[10..30]}\t!{text[30..]}  ";
        var state = EncryptionState.InMemory();

        Assert.True(state.TryProvision(Encoding.ASCII.GetBytes(messy), sessionIsZeroPsk: true));

        Assert.Equal(key, state.Key.ToArray());
    }

    [Fact]
    public void Provision_race_has_exactly_one_winner()
    {
        for (var round = 0; round < 50; round++)
        {
            var state = EncryptionState.InMemory();
            var keys = Enumerable.Range(0, 8).Select(_ => RandomNumberGenerator.GetBytes(32)).ToArray();
            var changed = 0;
            state.KeyChanged += () => Interlocked.Increment(ref changed);
            using var start = new Barrier(keys.Length);

            var wins = keys.AsParallel().WithDegreeOfParallelism(keys.Length).Select(key =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return state.TryProvision(KeyField(key), sessionIsZeroPsk: true) ? key : null;
            }).Where(k => k is not null).ToList();

            var winner = Assert.Single(wins);
            Assert.Equal(winner, state.Key.ToArray());
            Assert.Equal(1, changed);
        }
    }

    [Fact]
    public void Provision_store_failure_leaves_state_unprovisioned()
    {
        var state = EncryptionState.Load(new KeyStore(Path.Combine(_dir, "missing-dir", "noise.key"), applyAcl: false));

        Assert.ThrowsAny<IOException>(() => state.TryProvision(KeyField(RandomNumberGenerator.GetBytes(32)), sessionIsZeroPsk: true));

        Assert.False(state.HasKey);
        Assert.True(state.Key.IsEmpty);
    }

    [Fact]
    public void Load_reads_an_existing_key_and_corrupt_file_throws()
    {
        Assert.False(EncryptionState.Load(Store).HasKey);

        File.WriteAllText(Path.Combine(_dir, "noise.key"), "garbage!");

        Assert.Throws<KeyStoreCorruptException>(() => EncryptionState.Load(Store));
    }

    [Fact]
    public void InMemory_rejects_invalid_keys()
    {
        Assert.Throws<ArgumentException>(() => EncryptionState.InMemory(new byte[16]));
        Assert.Throws<ArgumentException>(() => EncryptionState.InMemory(new byte[32]));
        Assert.True(EncryptionState.InMemory(RandomNumberGenerator.GetBytes(32)).HasKey);
        Assert.False(EncryptionState.InMemory().HasKey);
    }

    [Fact]
    public void A_throwing_KeyChanged_handler_does_not_undo_provisioning()
    {
        var state = EncryptionState.InMemory();
        state.KeyChanged += () => throw new InvalidOperationException("boom");
        var after = 0;
        state.KeyChanged += () => after++;

        Assert.True(state.TryProvision(KeyField(RandomNumberGenerator.GetBytes(32)), sessionIsZeroPsk: true));

        Assert.True(state.HasKey);
        Assert.Equal(1, after);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
