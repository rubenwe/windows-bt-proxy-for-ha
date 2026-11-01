using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Api;

namespace WindowsBtProxy.Discovery;

/// <summary>
/// Advertises the proxy as an <c>_esphomelib._tcp</c> service through the Windows DNS-SD API (<c>dnsapi.dll</c>), one
/// registration per interface. <see cref="Publish"/> replaces whatever was published before.
/// </summary>
public sealed unsafe partial class DnsSdAdvertiser(ILogger log) : IDisposable
{
    private const string ServiceType = "_esphomelib._tcp.local";
    private const uint DnsRequestPending = 9506;
    private const uint DnsQueryRequestVersion1 = 1;
    private static readonly TimeSpan DeregisterTimeout = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private List<Registration> _active = [];
    private bool _disposed;

    /// <summary>Registers <paramref name="id"/> on each interface that has an IPv4 address, dropping earlier registrations first.</summary>
    public void Publish(DeviceIdentity id, ushort port, bool hasKey, IReadOnlyList<NetInterface> interfaces)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            DeregisterAll();

            var txt = TxtRecords.Build(id, hasKey, TxtRecords.NetworkFor(interfaces));
            foreach (var nic in interfaces)
            {
                if (nic.IPv4 is null || nic.IPv4.AddressFamily != AddressFamily.InterNetwork)
                {
                    log.LogWarning("DNS-SD: skipping interface {Name}, it has no IPv4 address", nic.Name);
                    continue;
                }

                var registration = Register(id, port, txt, nic);
                if (registration is not null)
                {
                    _active.Add(registration);
                }
            }

            if (_active.Count == 0)
            {
                log.LogWarning("DNS-SD: {Service} is not advertised on any interface", id.Name);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DeregisterAll();
        }
    }

    private Registration? Register(DeviceIdentity id, ushort port, IReadOnlyDictionary<string, string> txt, NetInterface nic)
    {
        var instance = ConstructInstance(id.Name, port, txt, nic);
        if (instance == 0)
        {
            log.LogWarning("DNS-SD: could not construct the service instance for interface {Name}", nic.Name);
            return null;
        }

        var registration = new Registration(log, nic.Name, instance);
        registration.Handle = GCHandle.Alloc(registration);

        var request = (DnsServiceRegisterRequest*)NativeMemory.AllocZeroed((nuint)sizeof(DnsServiceRegisterRequest));
        request->Version = DnsQueryRequestVersion1;
        request->InterfaceIndex = (uint)nic.Index;
        request->ServiceInstance = instance;
        request->CompletionCallback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnComplete;
        request->QueryContext = GCHandle.ToIntPtr(registration.Handle);
        registration.Request = request;

        registration.Tracker.Expect(); // before the call: the callback may fire inside it
        var status = DnsServiceRegister(request, 0);
        if (status != DnsRequestPending)
        {
            registration.Tracker.NoCallback(); // no completion callback will follow
            if (status != 0)
            {
                log.LogWarning("DNS-SD: DnsServiceRegister failed on {Name}, status {Status}", nic.Name, status);
                registration.Release();
                return null;
            }
        }

        log.LogInformation("DNS-SD: registering {Service} on {Name} (index {Index}, {Ip}:{Port})",
            $"{id.Name}.{ServiceType}", nic.Name, nic.Index, nic.IPv4, port);
        return registration;
    }

    private static nint ConstructInstance(string name, ushort port, IReadOnlyDictionary<string, string> txt, NetInterface nic)
    {
        var count = txt.Count;
        var keys = new nint[count];
        var values = new nint[count];
        try
        {
            var i = 0;
            foreach (var (key, value) in txt)
            {
                keys[i] = Marshal.StringToCoTaskMemUni(key);
                values[i] = Marshal.StringToCoTaskMemUni(value);
                i++;
            }

            // IP4_ADDRESS holds the address bytes in network order, i.e. the same memory layout as the 4 bytes read here.
            Span<byte> octets = stackalloc byte[4];
            nic.IPv4!.TryWriteBytes(octets, out _);
            var ip4 = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(octets));

            fixed (nint* k = keys)
            fixed (nint* v = values)
            {
                // The API copies everything into its own allocation, so the strings and the address can go afterwards.
                return DnsServiceConstructInstance(
                    $"{name}.{ServiceType}", $"{name}.local", &ip4, 0, port, 0, 0, (uint)count, k, v);
            }
        }
        finally
        {
            foreach (var p in keys)
            {
                Marshal.FreeCoTaskMem(p);
            }

            foreach (var p in values)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private void DeregisterAll()
    {
        var active = _active;
        _active = [];

        // Announce the deregister callback first, then close: see CompletionTracker for why that order matters. Whatever the
        // deregister call returns, a registration is only released once every callback it owes us has arrived.
        foreach (var registration in active)
        {
            registration.Tracker.Expect();
            registration.Tracker.BeginClose();
            var status = DnsServiceDeRegister(registration.Request, 0);
            if (status != DnsRequestPending)
            {
                registration.Tracker.NoCallback();
                if (status != 0)
                {
                    log.LogWarning("DNS-SD: DnsServiceDeRegister failed on {Name}, status {Status}", registration.InterfaceName, status);
                }
            }
        }

        var deadline = DateTime.UtcNow + DeregisterTimeout;
        foreach (var registration in active)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (registration.Tracker.WaitSettled(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
            {
                registration.Release();
            }
            else
            {
                // A completion callback may still fire, so its context and the instance must stay valid: leak them.
                log.LogWarning("DNS-SD: deregistration on {Name} did not complete in time", registration.InterfaceName);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnComplete(uint status, nint context, nint instance)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not Registration registration)
            {
                return;
            }

            // Observed: pInstance is a fresh copy on every callback (never the registered pointer), and the docs make a
            // non-null one the caller's to free.
            registration.Log.LogDebug("DNS-SD: callback instance 0x{Callback:X}, registered instance 0x{Registered:X}", instance, registration.Instance);
            if (instance != 0 && instance != registration.Instance)
            {
                DnsServiceFreeInstance(instance);
            }

            var phase = registration.Tracker.IsLastCallback ? "deregistered" : "registered";
            if (status == 0)
            {
                registration.Log.LogInformation("DNS-SD: {Phase} on {Name}, status {Status}", phase, registration.InterfaceName, status);
            }
            else
            {
                registration.Log.LogWarning("DNS-SD: {Phase} on {Name} failed, status {Status}", phase, registration.InterfaceName, status);
            }

            // Last use of the registration: once this settles, Release() may free the context, request and instance.
            registration.Tracker.Arrived();
        }
        catch
        {
            // An exception must never cross into native code.
        }
    }

    /// <summary>One service instance on one interface, with everything the native API still points at.</summary>
    private sealed class Registration(ILogger log, string interfaceName, nint instance)
    {
        public nint Instance { get; } = instance;
        public ILogger Log { get; } = log;
        public string InterfaceName { get; } = interfaceName;
        public GCHandle Handle { get; set; }
        public DnsServiceRegisterRequest* Request { get; set; }
        public CompletionTracker Tracker { get; } = new();

        /// <summary>Frees the native instance and request and the callback context. Only call once the API is done with them.</summary>
        public void Release()
        {
            DnsServiceFreeInstance(Instance);
            NativeMemory.Free(Request);
            Request = null;
            Handle.Free();
        }
    }

    // DNS_SERVICE_REGISTER_REQUEST from windns.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint ServiceInstance;
        public nint CompletionCallback;
        public nint QueryContext;
        public nint Credentials;
        public int UnicastEnabled;
    }

    [LibraryImport("dnsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint DnsServiceConstructInstance(
        string serviceName, string hostName, uint* ip4, nint ip6, ushort port, ushort priority, ushort weight,
        uint propertiesCount, nint* keys, nint* values);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsServiceFreeInstance(nint instance);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceRegister(DnsServiceRegisterRequest* request, nint cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceDeRegister(DnsServiceRegisterRequest* request, nint cancel);
}
