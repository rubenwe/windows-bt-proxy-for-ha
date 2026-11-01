using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBtProxy.Crypto;
using WindowsBtProxy.Hosting;

namespace WindowsBtProxy.Tests.Hosting;

public sealed class ProxyHostTests
{
    [Fact]
    public void Exit_codes_have_the_documented_values()
    {
        Assert.Equal(0, ExitCodes.Ok);
        Assert.Equal(2, ExitCodes.CorruptKey);
        Assert.Equal(3, ExitCodes.PortInUse);
        Assert.Equal(4, ExitCodes.CryptoUnsupported);
    }

    [Fact]
    public void Exceptions_map_to_exit_codes()
    {
        Assert.Equal(2, ProxyHost.ExitCodeFor(new KeyStoreCorruptException("bad")));
        Assert.Equal(3, ProxyHost.ExitCodeFor(new HostFatalException(ExitCodes.PortInUse, "busy")));
        Assert.Equal(4, ProxyHost.ExitCodeFor(new HostFatalException(ExitCodes.CryptoUnsupported, "no crypto")));
        Assert.Equal(1, ProxyHost.ExitCodeFor(new SocketException()));
        Assert.Equal(1, ProxyHost.ExitCodeFor(new InvalidOperationException()));
    }

    [Fact]
    public void Crypto_check_passes_only_when_both_primitives_are_supported()
    {
        Assert.Null(ProxyHost.CryptoProblem(true, true));
        Assert.Contains("ChaCha20-Poly1305", ProxyHost.CryptoProblem(false, true));
        Assert.DoesNotContain("X25519", ProxyHost.CryptoProblem(false, true));
        Assert.Contains("X25519", ProxyHost.CryptoProblem(true, false));
        var both = ProxyHost.CryptoProblem(false, false);
        Assert.Contains("ChaCha20-Poly1305", both);
        Assert.Contains("X25519", both);
    }

    [Fact]
    public async Task A_key_that_loads_is_loaded_once()
    {
        var calls = 0;
        var state = await ProxyHost.LoadEncryptionAsync(() => { calls++; return EncryptionState.InMemory(); }, TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.False(state.HasKey);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_sharing_violation_is_retried_once()
    {
        var calls = 0;
        var state = await ProxyHost.LoadEncryptionAsync(() =>
        {
            if (++calls == 1)
            {
                throw new KeyStoreCorruptException("locked", new IOException("The process cannot access the file"));
            }
            return EncryptionState.InMemory();
        }, TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.NotNull(state);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_second_failure_is_fatal()
    {
        var calls = 0;
        await Assert.ThrowsAsync<KeyStoreCorruptException>(() => ProxyHost.LoadEncryptionAsync(
            () => { calls++; throw new KeyStoreCorruptException("locked", new IOException()); },
            TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_corrupt_key_is_not_retried()
    {
        var calls = 0;
        await Assert.ThrowsAsync<KeyStoreCorruptException>(() => ProxyHost.LoadEncryptionAsync(
            () => { calls++; throw new KeyStoreCorruptException("not base64"); },
            TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Compilation_time_looks_like_ESPHomes()
    {
        Assert.Matches(@"^[A-Z][a-z]{2} \d{2} \d{4}, \d{2}:\d{2}:\d{2}$", ProxyHost.CompilationTime());
    }
}
