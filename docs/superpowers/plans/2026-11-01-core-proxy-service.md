# Core Proxy Service Implementation Plan (plan 1 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An installable, Native-AOT Windows service that HA adopts as an encrypted, passive ESPHome Bluetooth proxy.

**Architecture:** One exe (`windows-bt-proxy.exe`) with subcommands. The service hosts a WinRT BLE scanner, a per-client batching fan-out, and an ESPHome Native API server on TCP that speaks plaintext only until HA provisions a Noise key over a zero-PSK session, then Noise only. Discovery goes through Windows' own DNS-SD (`DnsServiceRegister`).

**Tech Stack:** .NET 11 (C#), Native AOT, `Google.Protobuf` + `Grpc.Tools` (code from ESPHome's `api.proto`), BCL crypto only (`X25519DiffieHellman`, `ChaCha20Poly1305`), `Microsoft.Extensions.Hosting.WindowsServices`, xUnit v3, Python `pytest` + `aioesphomeapi` for interop tests.

**Spec:** `docs/superpowers/specs/2026-11-01-windows-bt-proxy-for-ha-design.md`

**Later plans (not in this one):** plan 2 = tray app + named-pipe status IPC (spec §5.8); plan 3 = update check/apply + ECDSA-signed GitHub releases (spec §5.9, release half of §6). This plan leaves the `updates` settings in the file but nothing reads them yet.

**Reference implementation:** the Python spike, `integrations/windows-bt-proxy/bt_proxy.py (in the author's private home-automation repo)`, with a venv in the same folder (`.venv`, `aioesphomeapi` 46.25.0). The Noise reference is `.venv\Lib\site-packages\aioesphomeapi\_frame_helper\noise.py`.

## Global Constraints

- Target framework `net11.0-windows10.0.26100.0`, `SupportedOSPlatformVersion` `10.0.22000.0` (Windows 11 only — no Windows 10). At start, `ChaCha20Poly1305.IsSupported` and `X25519DiffieHellman.IsSupported` must both be true, else exit code 4 with a clear message. SDK pinned in `global.json` to `11.0.100-rc.3.26522.148`, `rollForward: latestFeature`, `allowPrerelease: true`; move to 11.0 GA when it ships.
- `PublishAot=true`, `SelfContained=true`, runtimes `win-x64` and `win-arm64`. `TreatWarningsAsErrors=true` everywhere, AOT/trim analyzers on (`IsAotCompatible=true`). Zero AOT/trim warnings is a requirement, not a goal.
- No reflection-based serialization: `System.Text.Json` only via source-generated contexts; protobuf binary only.
- Names: exe `windows-bt-proxy.exe`; root namespace `WindowsBtProxy`; service name `WindowsBtProxyForHA`, display name `Windows BT Proxy for Home Assistant`; data dir `%ProgramData%\WindowsBtProxyForHA\`; install dir `%ProgramFiles%\WindowsBtProxyForHA\`; firewall rule `Windows BT Proxy for Home Assistant (TCP <port>)`.
- Reported to HA: API version **1.13**, `esphome_version` `"2025.6.0"`, `manufacturer` `"Windows"`, `model` = machine name, `bluetooth_proxy_feature_flags` = `33` (PASSIVE_SCAN 1 | RAW_ADVERTISEMENTS 32), `api_encryption_supported = true`, `api_encryption_provisionable = (no key stored)`.
- MAC formats: `DeviceInfoResponse.mac_address` and `bluetooth_mac_address` = `"02:11:22:33:44:55"` style (upper case, colons); Noise server-hello MAC and mDNS TXT `mac` = `"021122334455"` style (lower case hex, no colons).
- Batching: ≤ 16 advertisements per `BluetoothLERawAdvertisementsResponse`, flushed every 100 ms or when full; per-client queue cap 1024, drop oldest on overflow.
- Noise: `Noise_NNpsk0_25519_ChaChaPoly_SHA256`, prologue `"NoiseAPIInit\0\0"` (14 bytes), frames `0x01, len_hi, len_lo, payload`; zero PSK = 32 zero bytes.
- Encryption policy (author's decision): plaintext only while no key is stored; once a key exists, plaintext and zero-PSK sessions are refused. A corrupt key file fails closed (service refuses to start the API and logs an error); it never falls back to plaintext.
- Licence MIT. README says "ESPHome-compatible" and "not affiliated with or endorsed by the Open Home Foundation"; no OHF logos.
- Production HA (the author's): only add/reconfigure/remove this proxy's own ESPHome entry.

## Review Focus

- **Radio off or adapter unplugged, at start or mid-run** → service keeps running, status `NoAdapter`/`RadioOff`, scanning resumes by itself when the radio returns. Pinned in Task 8 (`ScannerSupervisorTests`).
- **Client vanishes without `DisconnectRequest`** (PC sleep, HA restart, half-open TCP) → its subscription and queue are released; other clients unaffected. Pinned in Task 4 (`ApiServerTests.Abrupt_disconnect_releases_subscription`).
- **Garbage on port 6053** (port scanners, HTTP probes, oversized length fields) → that connection closes, server keeps serving others, no large allocation. Pinned in Task 2 (`PlaintextFrameCodecTests.Oversized_frame_is_rejected_without_allocating`) and Task 4 (`ApiServerTests.Garbage_client_does_not_affect_others`).
- **Missing or corrupt `settings.json`** → defaults are used and the file rewritten, with a warning; corrupt `noise.key` → fail closed (see constraints). Pinned in Task 7 (`SettingsStoreTests`) and Task 6 (`KeyStoreTests`).
- **IP address or network change after resume** → DNS-SD registration follows the new address. Pinned in Task 9 (`InterfaceSelectorTests` + re-registration on `NetworkChange`).

---

## File Structure

```
global.json, Directory.Build.props, WindowsBtProxy.slnx, LICENSE, README.md, docs/decisions.md, .gitignore
proto/api.proto, proto/api_options.proto, proto/ESPHOME_VERSION          vendored, pinned
spikes/ServiceScanProbe/                      Task 1 risk probe (kept, documented in decisions.md)
src/WindowsBtProxy/
  WindowsBtProxy.csproj
  Program.cs                                  command dispatch only
  Ble/RawAdvertisement.cs                     record + ScannerStatus
  Ble/IAdvertisementSource.cs
  Ble/AdStructures.cs                         DataSections -> raw AD bytes
  Ble/WinRtAdvertisementSource.cs             real scanner
  Ble/FakeAdvertisementSource.cs              canned ads for tests / --fake-scanner
  Ble/ScannerSupervisor.cs                    restart-on-radio-return logic
  Api/Varint.cs, Api/MessageIds.cs, Api/MessageRegistry.cs
  Api/Framing/IFrameCodec.cs, PlaintextFrameCodec.cs, NoiseFrameCodec.cs
  Api/AdvertisementBatcher.cs
  Api/DeviceIdentity.cs
  Api/ApiConnection.cs, Api/ApiServer.cs
  Crypto/NoiseHandshake.cs                    NNpsk0 responder + initiator (initiator for tests)
  Crypto/CipherState.cs
  Crypto/KeyStore.cs, Crypto/EncryptionState.cs
  Config/DataPaths.cs, Config/ProxySettings.cs, Config/SettingsStore.cs, Config/NameSanitizer.cs, Config/JsonContext.cs
  Discovery/InterfaceSelector.cs, Discovery/DnsSdAdvertiser.cs, Discovery/TxtRecords.cs
  Hosting/ProxyHost.cs, Hosting/RollingFileLogger.cs
  Setup/Installer.cs, Setup/IProcessRunner.cs
tests/WindowsBtProxy.Tests/                   xUnit v3, mirrors src folders
tests/vectors/make_noise_vectors.py, tests/vectors/noise_nnpsk0.json
tests/interop/requirements.txt, tests/interop/conftest.py, tests/interop/test_proxy.py
.github/workflows/ci.yml
```

---

### Task 0: Build prerequisites (author)

Native AOT on Windows links with MSVC; this PC has no Visual Studio C++ tools.

- [ ] **Step 1: Author installs the C++ build tools (elevated)**

```powershell
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.ARM64 --includeRecommended"
```

- [ ] **Step 2: Verify**

Run: `& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property displayName`
Expected: prints a Build Tools product name.

---

### Task 1: Risk probe — WinRT scanning as a service, under Native AOT

Settles spec §8 risks 1 and 2 before anything else. **Gate:** if the service sees zero advertisements while the console run sees some, stop and report to the author (scanner moves to the user session; spec §8).

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.gitignore`, `spikes/ServiceScanProbe/ServiceScanProbe.csproj`, `spikes/ServiceScanProbe/Program.cs`, `docs/decisions.md`

- [ ] **Step 1: Write the probe**

`Program.cs`: a `Host.CreateApplicationBuilder` app with `AddWindowsService()` and one `BackgroundService` that (a) at start does an X25519 key agreement between two `X25519DiffieHellman.GenerateKey()` keys and a BCL `ChaCha20Poly1305` encrypt/decrypt round trip (proves both work under Native AOT and in session 0), logging `crypto ok`/`crypto FAILED`; (b) starts a `BluetoothLEAdvertisementWatcher` (passive) and every 10 s appends `"{UtcNow:o} ads={count} session={Process.GetCurrentProcess().SessionId}"` to `%ProgramData%\BtProbe\probe.log`, resetting the count.

- [ ] **Step 2: Publish with Native AOT, zero warnings**

Run: `dotnet publish spikes/ServiceScanProbe -c Release -r win-x64`
Expected: succeeds, no warnings (warnings are errors via `Directory.Build.props`). Repeat with `-r win-arm64`: succeeds.

- [ ] **Step 3: Console run**

Run the published exe directly for 30 s.
Expected: `crypto ok`, then lines with `ads>0` and `session=1` (or the user's session).

- [ ] **Step 4: Service run (author, elevated)**

```powershell
sc.exe create BtProbe binPath= "W:\windows-bt-proxy-for-ha\spikes\ServiceScanProbe\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish\ServiceScanProbe.exe" start= demand
sc.exe start BtProbe
```

Wait 60 s, read `C:\ProgramData\BtProbe\probe.log`.
Expected: `crypto ok`, lines with `session=0` and `ads>0`. Then the author signs out for ~2 min, signs back in, and the log shows `ads>0` across that window.

- [ ] **Step 5: Clean up and record**

`sc.exe stop BtProbe; sc.exe delete BtProbe` (author). Write the outcome (counts console vs service, signed-out window) into `docs/decisions.md` under "Scanning in session 0".

- [ ] **Step 6: Commit**

```bash
git add global.json Directory.Build.props .gitignore spikes docs/decisions.md
git commit -m "spike: verify WinRT BLE scanning as AOT service"
```

---

### Task 2: Solution skeleton, vendored proto, framing primitives

**Files:**
- Create: `WindowsBtProxy.slnx`, `src/WindowsBtProxy/WindowsBtProxy.csproj`, `proto/*`, `src/WindowsBtProxy/Api/Varint.cs`, `Api/MessageIds.cs`, `Api/MessageRegistry.cs`, `Api/Framing/IFrameCodec.cs`, `Api/Framing/PlaintextFrameCodec.cs`
- Test: `tests/WindowsBtProxy.Tests/WindowsBtProxy.Tests.csproj`, `Api/VarintTests.cs`, `Api/MessageIdsTests.cs`, `Api/PlaintextFrameCodecTests.cs`

**Interfaces:**
- Produces:
  - `static class Varint { int Write(Span<byte> dest, uint value); bool TryRead(ReadOnlySpan<byte> src, out uint value, out int consumed); }` (`TryRead` returns false on incomplete; throws `InvalidDataException` on > 5 bytes)
  - `static class MessageIds` — `const int` per message used: HelloRequest 1, HelloResponse 2, AuthenticationRequest 3, DisconnectRequest 5, DisconnectResponse 6, PingRequest 7, PingResponse 8, DeviceInfoRequest 9, DeviceInfoResponse 10, ListEntitiesRequest 11, ListEntitiesDoneResponse 19, SubscribeBluetoothLEAdvertisementsRequest 66, UnsubscribeBluetoothLEAdvertisementsRequest 87, BluetoothLERawAdvertisementsResponse 93, NoiseEncryptionSetKeyRequest 124, NoiseEncryptionSetKeyResponse 125
  - `static class MessageRegistry { int IdOf(IMessage m); IMessage? Parse(int id, ReadOnlySpan<byte> payload); }` — `Parse` returns `null` for ids not in `MessageIds`
  - `interface IFrameCodec { ValueTask<Frame?> ReadAsync(CancellationToken ct); ValueTask WriteAsync(int messageId, IMessage message, CancellationToken ct); }` with `readonly record struct Frame(int MessageId, ReadOnlyMemory<byte> Payload)`; `ReadAsync` returns `null` on clean EOF
  - `sealed class PlaintextFrameCodec(Stream stream, int maxPayload = 65536) : IFrameCodec`

- [ ] **Step 1: Vendor the proto** — copy `api.proto` and `api_options.proto` from the ESPHome release matching `aioesphomeapi`'s current proto (record the ESPHome version/tag in `proto/ESPHOME_VERSION`). `csproj`: `<Protobuf Include="..\..\proto\*.proto" GrpcServices="None" ProtoRoot="..\..\proto" />`.

- [ ] **Step 2: Write failing tests**

```csharp
// VarintTests
[Theory] [InlineData(0u, new byte[]{0x00})] [InlineData(127u, new byte[]{0x7F})]
[InlineData(128u, new byte[]{0x80,0x01})] [InlineData(300u, new byte[]{0xAC,0x02})]
public void Write_and_read_round_trip(uint v, byte[] bytes) { /* Write == bytes; TryRead(bytes) == (v, bytes.Length) */ }
[Fact] public void TryRead_incomplete_returns_false()            // {0x80}
[Fact] public void TryRead_six_byte_varint_throws()              // {0x80,0x80,0x80,0x80,0x80,0x01}

// MessageIdsTests — reads the (id) custom option from the generated descriptors (JIT test, reflection is fine here)
[Fact] public void Every_constant_matches_the_proto_id_option()

// PlaintextFrameCodecTests
[Fact] public void Writes_preamble_length_type_payload()          // PingResponse -> 00 00 08
[Fact] public async Task Reads_frame_split_across_reads()          // stream yielding 1 byte at a time
[Fact] public async Task Returns_null_on_clean_eof()
[Fact] public async Task Wrong_preamble_throws_InvalidDataException()   // first byte 0x01
[Fact] public async Task Oversized_frame_is_rejected_without_allocating() // length varint = 10_000_000 -> InvalidDataException before reading payload
```

- [ ] **Step 3: Run** `dotnet test` — Expected: FAIL (types missing).
- [ ] **Step 4: Implement** the types above. `PlaintextFrameCodec` reads preamble, length varint, type varint, then exactly `length` bytes; writes in one buffered `WriteAsync`.
- [ ] **Step 5: Run** `dotnet test` — Expected: PASS. `dotnet publish src/WindowsBtProxy -c Release -r win-x64` — Expected: no warnings.
- [ ] **Step 6: Commit** `feat: proto codegen, varint and plaintext framing`

---

### Task 3: Advertisement model, AD rebuild, batcher, fake source

**Files:**
- Create: `Ble/RawAdvertisement.cs`, `Ble/IAdvertisementSource.cs`, `Ble/AdStructures.cs`, `Ble/FakeAdvertisementSource.cs`, `Api/AdvertisementBatcher.cs`
- Test: `Ble/AdStructuresTests.cs`, `Api/AdvertisementBatcherTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct RawAdvertisement(ulong Address, int Rssi, uint AddressType, byte[] Data);` (`AddressType`: 0 public, 1 random)
  - `enum ScannerStatus { Starting, Running, NoAdapter, RadioOff, Failed }`
  - `interface IAdvertisementSource : IAsyncDisposable { event Action<RawAdvertisement>? Received; event Action<ScannerStatus>? StatusChanged; ScannerStatus Status { get; } ulong AdapterAddress { get; } Task StartAsync(CancellationToken ct); }`
  - `static class AdStructures { byte[] Build(IEnumerable<(byte Type, ReadOnlyMemory<byte> Data)> sections); }`
  - `sealed class FakeAdvertisementSource(TimeSpan interval) : IAdvertisementSource` — `AdapterAddress = 0x0000DEADBEEF01`; each tick emits three fixed ads: `0xA4C138000001` public, `0xC0FFEE000002` random, `0xA4C138000003` public, each with `Data = AdStructures.Build([(0x01, [0x06]), (0x16, [0xD2,0xFC,0x40,0x02,0xC4,0x09])])` (BTHome service data)
  - `sealed class AdvertisementBatcher(Func<IReadOnlyList<RawAdvertisement>, ValueTask> flush, TimeProvider time, int maxBatch = 16, int maxQueued = 1024, TimeSpan? interval = null /* 100 ms */) : IAsyncDisposable { void Enqueue(RawAdvertisement ad); long Dropped { get; } }`

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Build_prefixes_each_section_with_length_and_type()   // [(0x01,[0x06]),(0x09,"ab")] -> 02 01 06 03 09 61 62
[Fact] public void Build_of_no_sections_is_empty()
[Fact] public async Task Flushes_full_batch_immediately()                // 16 enqueued -> one flush of 16 before time advances
[Fact] public async Task Flushes_partial_batch_after_interval()          // 3 enqueued, FakeTimeProvider +100ms -> one flush of 3
[Fact] public async Task Splits_large_backlog_into_batches_of_16()       // 40 -> 16,16,8
[Fact] public async Task Overflow_drops_oldest_and_counts()              // flush blocked; 1030 enqueued -> Dropped == 6, oldest gone
```
(`FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.)

- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** Batcher: a bounded `Channel<RawAdvertisement>` with `BoundedChannelFullMode.DropOldest` + a single reader loop; count drops via the channel's `itemDropped` callback.
- [ ] **Step 4: Run** — PASS. **Step 5: Commit** `feat: advertisement model, AD builder, batcher, fake source`

---

### Task 4: API connection and server over plaintext

**Files:**
- Create: `Api/DeviceIdentity.cs`, `Api/ApiConnection.cs`, `Api/ApiServer.cs`
- Test: `Api/ApiServerTests.cs` (real `TcpListener` on port 0, a small C# test client built on `PlaintextFrameCodec`)

**Interfaces:**
- Consumes: Task 2 framing/registry, Task 3 source + batcher.
- Produces:
  - `sealed record DeviceIdentity(string Name, string FriendlyName, ulong BluetoothAddress, string Model, string CompilationTime) { string MacColon { get; } string MacHex { get; } }`
  - `sealed class ApiServer(IPEndPoint endpoint, DeviceIdentity identity, IAdvertisementSource source, EncryptionState encryption, ILogger<ApiServer> log, TimeProvider time)` with `Task RunAsync(CancellationToken ct)`, `IPEndPoint LocalEndpoint { get; }` (after start), `IReadOnlyList<ClientInfo> Clients { get; }`, `event Action? ClientsChanged`; `sealed record ClientInfo(IPEndPoint Remote, string ClientInfoText, bool Subscribed, bool Encrypted)`.
  - `EncryptionState` comes from Task 6; in this task use it only through `bool HasKey` and construct it with an in-memory store (`EncryptionState.InMemory()`), so define that minimal shape here and Task 6 fills it in.

Behaviour per spec §5.3 table: Hello → HelloResponse (1.13, `server_info = "windows-bt-proxy {version}"`, `name`); DeviceInfo → all Global-Constraints values; ListEntities → Done; Subscribe → attach a batcher; Unsubscribe → detach; Ping → PingResponse; Disconnect → DisconnectResponse then close; everything else ignored (debug log). Advertisements reach a client only after Subscribe.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Hello_returns_api_1_13_and_name()
[Fact] public async Task DeviceInfo_reports_flags_33_mac_formats_and_encryption_fields() // mac "02:11:22:33:44:55", bt mac same, flags 33, supported true, provisionable true
[Fact] public async Task ListEntities_returns_only_done()
[Fact] public async Task No_advertisements_before_subscribe()
[Fact] public async Task Subscribed_client_receives_fake_advertisements_in_batches()   // addresses 0xA4C138000001 etc., batch size <= 16
[Fact] public async Task Two_clients_each_receive_advertisements()
[Fact] public async Task Ping_is_answered()
[Fact] public async Task Disconnect_request_gets_response_and_close()
[Fact] public async Task Abrupt_disconnect_releases_subscription()      // client socket closed without Disconnect -> Clients empty within 1 s, source still feeding others
[Fact] public async Task Garbage_client_does_not_affect_others()        // client A sends "GET / HTTP/1.1\r\n"; A closed; client B still served
```

- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** One `ApiConnection` per accepted socket; a single writer lock per connection so batcher flushes and replies don't interleave.
- [ ] **Step 4: Run** — PASS. **Step 5: Commit** `feat: ESPHome API server over plaintext`

---

### Task 5: Noise handshake and transport

**Files:**
- Create: `Crypto/CipherState.cs`, `Crypto/NoiseHandshake.cs`, `Api/Framing/NoiseFrameCodec.cs`, `tests/vectors/make_noise_vectors.py`, `tests/vectors/noise_nnpsk0.json`
- Test: `Crypto/NoiseHandshakeTests.cs`, `Api/NoiseFrameCodecTests.cs`

**Interfaces:**
- Produces:
  - `sealed class CipherState(byte[] key) { byte[] Encrypt(ReadOnlySpan<byte> plaintext); byte[] Decrypt(ReadOnlySpan<byte> ciphertext); }` — BCL `ChaCha20Poly1305`, nonce = 4 zero bytes ‖ 8-byte little-endian counter, empty associated data, counter += 1 per call; `Decrypt` throws `CryptographicException` on tag failure
  - `sealed class NoiseResponder(ReadOnlySpan<byte> psk32, X25519DiffieHellman? ephemeralForTests = null) { void ReadMessageA(ReadOnlySpan<byte> m); byte[] WriteMessageB(); (CipherState Send, CipherState Receive) Split(); }`
  - `sealed class NoiseInitiator(ReadOnlySpan<byte> psk32, ...)` — same shape mirrored, used by tests only
  - `sealed class NoiseFrameCodec(Stream stream, CipherState send, CipherState receive) : IFrameCodec` — inner plaintext = `type_hi, type_lo, len_hi, len_lo, payload`; outer `0x01, len_hi, len_lo, ciphertext`
  - `static class NoiseServerHandshake { Task<NoiseFrameCodec> RunAsync(Stream s, ReadOnlyMemory<byte> psk, string nameForHello, string macHex, CancellationToken ct); }` — protocol per `aioesphomeapi/_frame_helper/noise.py`: read client-hello frame (empty payload), read handshake frame (`0x00` + message A); send server-hello frame (`0x01` + name + `\0` + macHex + `\0`); on decrypt failure send frame `0x01` + `"Handshake MAC failure"` and throw `NoiseAuthenticationException`; else send frame `0x00` + message B
- Symmetric state per Noise spec rev 34: `h = SHA256(protocol name)` (name is 37 bytes > 32 → hash), `ck = h`, MixHash(prologue); NNpsk0 pattern `-> psk, e` / `<- e, ee`; HKDF = HMAC-SHA256 (BCL).

- [ ] **Step 1: Generate vectors** — `make_noise_vectors.py` (run with the spike venv, `python -I`) uses `noiseprotocol` with fixed ephemeral private keys (`set_keypair_from_private_bytes(Keypair.EPHEMERAL, …)`), PSK = bytes 0..31, prologue as above, and writes JSON: both ephemeral keys, message A, message B, then two transport messages each way (plaintext + ciphertext hex). Commit the JSON.

- [ ] **Step 2: Failing tests**

```csharp
[Fact] public void Responder_reads_vector_message_A_and_writes_vector_message_B()
[Fact] public void Split_ciphers_match_vector_transport_messages()
[Fact] public void Wrong_psk_fails_reading_message_A()                 // throws CryptographicException
[Fact] public void Initiator_and_responder_round_trip_with_random_keys()
[Fact] public async Task NoiseFrameCodec_round_trips_DeviceInfoResponse()
[Fact] public async Task NoiseFrameCodec_rejects_plaintext_preamble()   // first byte 0x00 -> InvalidDataException
[Fact] public async Task Server_handshake_sends_name_and_mac_in_hello() // hello payload == 01 "bt-proxy-x" 00 "021122334455" 00
[Fact] public async Task Server_handshake_with_wrong_psk_sends_MAC_failure_frame()
```

- [ ] **Step 3: Run** — FAIL. **Step 4: Implement.** **Step 5: Run** — PASS; AOT publish still warning-free.
- [ ] **Step 6: Commit** `feat: Noise NNpsk0 handshake and transport`

---

### Task 6: Key store, provisioning, encryption policy in the server

**Files:**
- Create/complete: `Crypto/KeyStore.cs`, `Crypto/EncryptionState.cs`; modify `Api/ApiConnection.cs`, `Api/ApiServer.cs`
- Test: `Crypto/KeyStoreTests.cs`, `Crypto/EncryptionStateTests.cs`, `Api/ApiServerEncryptionTests.cs`

**Interfaces:**
- Produces:
  - `sealed class KeyStore(string path) { byte[]? Load(); void Save(ReadOnlySpan<byte> key32); }` — file holds base64 text; `Load` returns null if absent, throws `KeyStoreCorruptException` if unreadable or not 32 bytes after decode; `Save` writes atomically (temp + replace) and sets an ACL of SYSTEM + Administrators full control with inheritance removed
  - `sealed class EncryptionState { static EncryptionState Load(KeyStore store); static EncryptionState InMemory(byte[]? key = null); bool HasKey { get; } ReadOnlyMemory<byte> Key { get; } bool TryProvision(ReadOnlySpan<byte> keyFieldBytes, bool sessionIsZeroPsk); event Action? KeyChanged; }`
- Rules (Global Constraints + spec §5.4):
  - First byte `0x00` from a client: allowed only when `!HasKey`; otherwise reply with frame `01 00 00` and close (aioesphomeapi raises `RequiresEncryptionAPIError`).
  - First byte `0x01`: Noise handshake with `Key` if `HasKey`, else with the zero PSK.
  - `NoiseEncryptionSetKeyRequest`: success only if `!HasKey` **and** the session is zero-PSK Noise **and** `key` base64-decodes (lenient, like `decode_noise_psk`) to exactly 32 bytes that are not all zero. Empty key → `success=false` (clearing is local-only, Task 11). After success: persist, respond `success=true`, then close all zero-PSK and plaintext sessions.
  - `api_encryption_provisionable` in DeviceInfo = `!HasKey`.

- [ ] **Step 1: Failing tests**

```csharp
// KeyStoreTests
[Fact] public void Load_missing_returns_null()
[Fact] public void Save_then_load_round_trips()
[Fact] public void Load_garbage_throws_KeyStoreCorruptException()
[Fact] public void Load_wrong_length_throws_KeyStoreCorruptException()       // base64 of 16 bytes
// EncryptionStateTests
[Fact] public void Provision_accepts_base64_32_bytes_on_zero_psk_session()
[Fact] public void Provision_rejects_on_plaintext_session()
[Fact] public void Provision_rejects_when_key_exists()
[Fact] public void Provision_rejects_all_zero_and_empty_and_wrong_length()
// ApiServerEncryptionTests (C# NoiseInitiator-based client)
[Fact] public async Task Unprovisioned_accepts_plaintext_and_zero_psk()
[Fact] public async Task Set_key_over_zero_psk_then_plaintext_is_refused()
[Fact] public async Task After_provisioning_new_key_session_receives_advertisements()
[Fact] public async Task After_provisioning_zero_psk_gets_MAC_failure()
[Fact] public async Task Device_info_provisionable_flips_to_false()
```

- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat: key store and HA-style key provisioning`

---

### Task 7: Settings, data paths, name sanitising, logging

**Files:**
- Create: `Config/DataPaths.cs`, `Config/ProxySettings.cs`, `Config/SettingsStore.cs`, `Config/NameSanitizer.cs`, `Config/JsonContext.cs`, `Hosting/RollingFileLogger.cs`
- Test: `Config/SettingsStoreTests.cs`, `Config/NameSanitizerTests.cs`, `Hosting/RollingFileLoggerTests.cs`

**Interfaces:**
- Produces:
  - `sealed record DataPaths(string Root) { string SettingsFile; string KeyFile /* noise.key */; string LogDir; static DataPaths Default /* %ProgramData%\WindowsBtProxyForHA */; }`
  - `sealed record ProxySettings(string Name, string FriendlyName, int Port = 6053, ScanMode ScanMode = ScanMode.Passive, string[]? Interfaces = null, string LogLevel = "info", UpdateSettings Updates = default)`; `enum ScanMode { Passive, Active }`; `sealed record UpdateSettings(bool Check = true, bool Auto = false)`
  - `static class SettingsStore { ProxySettings LoadOrCreate(DataPaths paths, string machineName, ILogger log); }` — missing file → defaults written; corrupt JSON → warning, defaults written (old file kept as `settings.json.bad`); unknown properties ignored
  - `static class NameSanitizer { string DefaultName(string machineName) /* "bt-proxy-" + sanitized */; string DefaultFriendlyName(string machineName) /* "BT Proxy " + machineName */; string Sanitize(string raw); }` — lower case, `[a-z0-9-]` only (others → `-`), collapse repeated `-`, trim `-`, max 31 chars total
  - `RollingFileLogger` — `ILoggerProvider` writing `proxy.log` in `LogDir`, rolls at 1 MB, keeps 5 files

- [ ] **Step 1: Failing tests**

```csharp
[Theory]
[InlineData("DESKTOP-ABC123", "bt-proxy-desktop-abc123")]
[InlineData("Media_PC", "bt-proxy-media-pc")]
[InlineData("Küche", "bt-proxy-k-che")]
[InlineData("---", "bt-proxy")]
public void DefaultName_sanitizes(string machine, string expected)
[Fact] public void DefaultName_is_at_most_31_chars()
[Fact] public void LoadOrCreate_writes_defaults_when_missing()
[Fact] public void LoadOrCreate_recovers_from_corrupt_json_and_keeps_bad_copy()
[Fact] public void LoadOrCreate_keeps_user_values()                      // port 7000, scanMode active survive
[Fact] public void Rolls_after_1MB_and_keeps_5_files()
```

- [ ] **Step 2–4:** Run (FAIL) → implement → run (PASS). `System.Text.Json` via `JsonContext` (source generated, camelCase, enums as strings).
- [ ] **Step 5: Commit** `feat: settings, data paths, file logging`

---

### Task 8: Real WinRT scanner with supervision

**Files:**
- Create: `Ble/WinRtAdvertisementSource.cs`, `Ble/ScannerSupervisor.cs`
- Test: `Ble/ScannerSupervisorTests.cs`

**Interfaces:**
- Produces:
  - `sealed class WinRtAdvertisementSource(ScanMode mode, ILogger log) : IAdvertisementSource` — mapping per spec §5.2 (DataSections → `AdStructures.Build`, `RawSignalStrengthInDBm`, `BluetoothAddressType.Random` → 1 else 0, scan responses forwarded as separate ads); reads adapter via `BluetoothAdapter.GetDefaultAsync()`; watches `Radio.StateChanged` and the watcher's `Stopped` event and raises `StatusChanged`
  - `sealed class ScannerSupervisor(Func<IAdvertisementSource> factory, TimeProvider time, ILogger log) : IAdvertisementSource` — wraps a source; on `NoAdapter`/`RadioOff`/`Failed` retries `StartAsync` every 10 s (fresh source from `factory` after `Failed`); forwards `Received`; `Status` reflects the inner source

- [ ] **Step 1: Failing tests** (inner = a controllable test double)

```csharp
[Fact] public async Task Starts_inner_and_forwards_advertisements()
[Fact] public async Task Retries_every_10s_while_no_adapter()            // FakeTimeProvider; 3 StartAsync calls after 20 s
[Fact] public async Task Recreates_source_after_failed()
[Fact] public async Task Status_follows_inner_through_radio_off_and_back()
```

- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Manual check** — `dotnet run --project src/WindowsBtProxy -- run --data-dir %TEMP%\btp --no-mdns` (wired in Task 10; if Task 10 isn't done yet, a temporary test harness is fine), turn Bluetooth off in Windows settings for 20 s and back on. Expected log: `RadioOff`, then `Running`, and per-minute counts > 0 again.
- [ ] **Step 6: Commit** `feat: WinRT scanner with supervision`

---

### Task 9: DNS-SD advertising

**Files:**
- Create: `Discovery/InterfaceSelector.cs`, `Discovery/TxtRecords.cs`, `Discovery/DnsSdAdvertiser.cs`
- Test: `Discovery/InterfaceSelectorTests.cs`, `Discovery/TxtRecordsTests.cs`

**Interfaces:**
- Produces:
  - `sealed record NetInterface(int Index, string Name, string Description, NetworkInterfaceType Type, bool IsUp, IPAddress? IPv4)`
  - `static class InterfaceSelector { IReadOnlyList<NetInterface> Select(IEnumerable<NetInterface> all, string[]? pinned); }` — pinned names (case-insensitive match on `Name`) win; otherwise: up, has IPv4, not loopback/tunnel, and `Name`/`Description` not matching `tailscale|wireguard|wintun|hyper-v|vethernet|wsl|vpn|tap-|zerotier|virtualbox|vmware` (case-insensitive)
  - `static class TxtRecords { IReadOnlyDictionary<string,string> Build(DeviceIdentity id, bool hasKey); }` — `friendly_name`, `version` = `2025.6.0`, `mac` = MacHex, `platform` = `Windows`, `board` = `windows`, `network` = `ethernet`|`wifi` (from the interface type), and either `api_encryption` = `Noise_NNpsk0_25519_ChaChaPoly_SHA256` (key stored) or `api_encryption_supported` = same string + `api_provisioning` = `zero-psk` (no key)
  - `sealed class DnsSdAdvertiser(ILogger log) : IDisposable { void Publish(DeviceIdentity id, ushort port, bool hasKey, IReadOnlyList<NetInterface> interfaces); }` — P/Invoke `DnsServiceConstructInstance` / `DnsServiceRegister` / `DnsServiceDeRegister` (`dnsapi.dll`, `[LibraryImport]`, AOT-safe); service `"<name>._esphomelib._tcp.local"`, host `"<name>.local"`, one registration per selected interface (`InterfaceIndex`); `Publish` again replaces previous registrations

The proxy re-publishes on `NetworkChange.NetworkAddressChanged` and on `EncryptionState.KeyChanged` (wired in Task 10).

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public void Excludes_tailscale_hyperv_loopback_and_down_interfaces()
[Fact] public void Pinned_interface_wins_even_if_virtual()
[Fact] public void Txt_without_key_has_supported_and_zero_psk()
[Fact] public void Txt_with_key_has_api_encryption_only()
```

- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Manual check** — with the proxy running (Task 10), from the spike venv: `python -I -c "from zeroconf import Zeroconf, ServiceBrowser; ..."` browsing `_esphomelib._tcp.local.` lists `<name>` with the TXT keys above. Record the exact command in `docs/decisions.md`.
- [ ] **Step 6: Commit** `feat: DNS-SD advertising via Windows dnsapi`

---

### Task 10: Host, `run` and `service` commands

**Files:**
- Create: `Hosting/ProxyHost.cs`; modify `Program.cs`
- Test: `tests/interop/requirements.txt` (`aioesphomeapi`, `pytest`, `pytest-asyncio` — unpinned, latest), `tests/interop/conftest.py`, `tests/interop/test_proxy.py`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `static class ProxyHost { Task<int> RunAsync(ProxyHostOptions o, CancellationToken ct); }`; `sealed record ProxyHostOptions(DataPaths Paths, bool AsService, bool FakeScanner, bool NoMdns, int? PortOverride)`
  - Commands: `run [--data-dir <dir>] [--fake-scanner] [--no-mdns] [--port <n>]` (console, Ctrl+C stops), `service` (what the SCM starts; same as run with `AsService`), `version`
  - Exit codes: 0 normal, 2 corrupt key (`KeyStoreCorruptException` → error logged, API never started), 3 port in use, 4 BCL crypto unsupported (see Global Constraints)
  - Per-minute info log: `"{n} advertisements in the last minute, {clients} client(s), status {status}"`

Interop fixture (`conftest.py`): builds once (`dotnet build -c Debug`), starts `windows-bt-proxy.exe run --fake-scanner --no-mdns --port <free> --data-dir <tmp>`, waits for the port, yields `(host, port, data_dir)`, kills the process after the test.

- [ ] **Step 1: Failing interop tests** (`pytest tests/interop -q`)

```python
async def test_plaintext_device_info(proxy):          # flags 33 via bluetooth_proxy_feature_flags_compat, api_encryption_supported, api_encryption_provisionable
async def test_raw_advertisements_flow(proxy):        # subscribe_bluetooth_le_raw_advertisements, see 0xA4C138000001 within 2 s
async def test_ha_style_provisioning(proxy):          # ZERO_NOISE_PSK client -> noise_encryption_set_key(base64 key) is True;
                                                      # plaintext connect -> RequiresEncryptionAPIError; key client -> ads flow;
                                                      # wrong key -> InvalidEncryptionKeyAPIError
async def test_expected_name_and_mac_accepted(proxy): # key client with expected_name=<name>, expected_mac="<mac hex>" connects
async def test_key_survives_restart(proxy_restartable)  # restart process; plaintext still refused
async def test_corrupt_key_file_exits_2(tmp_path)     # write garbage noise.key, run -> exit code 2, port never opens
```

- [ ] **Step 2: Run** — FAIL. **Step 3: Implement** `ProxyHost` (Generic Host, `AddWindowsService` when `AsService`, `RollingFileLogger`, EventLog provider for errors when `AsService`) and `Program.cs` dispatch.
- [ ] **Step 4: Run** `dotnet test` and `pytest tests/interop -q` — PASS.
- [ ] **Step 5: Verify HA's call site for `expected_mac`** (HA `homeassistant/components/esphome`, search `expected_mac`) and confirm its format matches `MacHex`; if HA uses another format, change the Noise hello and the test, and note it in `docs/decisions.md`.
- [ ] **Step 6: Commit** `feat: proxy host with run/service commands and interop tests`

---

### Task 11: `install`, `uninstall`, `key` commands

**Files:**
- Create: `Setup/IProcessRunner.cs`, `Setup/Installer.cs`; modify `Program.cs`
- Test: `Setup/InstallerTests.cs` (fake `IProcessRunner` records command lines; temp dirs stand in for Program Files / ProgramData)

**Interfaces:**
- Produces:
  - `interface IProcessRunner { Task<int> RunAsync(string exe, string args, CancellationToken ct); }`
  - `sealed class Installer(IProcessRunner runner, InstallPaths paths, ILogger log) { Task InstallAsync(CancellationToken ct); Task UninstallAsync(bool keepSettings, CancellationToken ct); }`; `sealed record InstallPaths(string ProgramDir, DataPaths Data)`
  - Commands: `install`, `uninstall [--keep-settings]`, `key show` (prints base64 key or "no key — HA will provision one when you add the proxy"), `key rotate` (new random key, saved; prints it; note that HA will ask for it), `key clear` (removes key file → provisionable again). All except `key show` need elevation: if not elevated, relaunch self with `runas` verb and wait.
- Install steps (spec §5.7): copy exe to `ProgramDir` (stop the service first if it's running = upgrade); `SettingsStore.LoadOrCreate`; `sc.exe create WindowsBtProxyForHA binPath= "\"<exe>\" service" start= auto DisplayName= "Windows BT Proxy for Home Assistant"`; `sc.exe description …`; `sc.exe failure WindowsBtProxyForHA reset= 86400 actions= restart/5000/restart/5000/restart/30000`; `netsh advfirewall firewall add rule name="Windows BT Proxy for Home Assistant (TCP <port>)" dir=in action=allow protocol=TCP localport=<port> profile=private,domain program="<exe>"` (delete an existing rule of that name first); `sc.exe start`; warn if any connected network profile is Public (`Get-NetConnectionProfile` equivalent via `INetworkListManager` is fine, or `netsh`/PowerShell output).
- Uninstall reverses: stop + delete service, delete firewall rule, delete `ProgramDir`, delete data dir unless `--keep-settings`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Install_runs_sc_create_with_auto_start_and_service_arg()
[Fact] public async Task Install_sets_restart_on_failure()
[Fact] public async Task Install_replaces_firewall_rule_for_private_and_domain_only()
[Fact] public async Task Install_over_existing_stops_service_before_copy()
[Fact] public async Task Uninstall_keep_settings_leaves_data_dir()
[Fact] public async Task Uninstall_removes_service_rule_and_files()
```

- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5: Commit** `feat: install, uninstall and key commands`

---

### Task 12: CI, README, licence

**Files:**
- Create: `.github/workflows/ci.yml`, `README.md`, `LICENSE` (MIT, "Ruben W."), finish `docs/decisions.md`

- [ ] **Step 1: `ci.yml`** — triggers: push, pull_request, `schedule: cron '0 4 * * 1'` (weekly, catches aioesphomeapi/HA protocol drift). Job on `windows-latest`: `actions/setup-dotnet` (from `global.json`), `dotnet test`, `dotnet publish src/WindowsBtProxy -c Release -r win-x64` and `-r win-arm64` (AOT; warnings fail), `actions/setup-python` 3.12 + `pip install -r tests/interop/requirements.txt`, `pytest tests/interop -q`, upload both publish folders as artifacts.
- [ ] **Step 2: README** — what it is (ESPHome-compatible passive Bluetooth proxy for Home Assistant), the OHF non-affiliation line, requirements (Windows 11, x64 or ARM64, Bluetooth LE adapter), install (`windows-bt-proxy.exe install` as admin; SmartScreen "More info → Run anyway"), adding in HA (Discovered, or Add integration → ESPHome → host/port; key is provisioned automatically), settings table (spec §5.6), `key` commands, uninstall, logs location, limits (passive only; only while the PC is awake).
- [ ] **Step 3: Push and watch CI** — `git push -u origin main` (author has approved the remote); CI green.
- [ ] **Step 4: Commit** `ci: build, test, AOT publish; docs: README and licence`

---

### Task 13: End-to-end on the real HA

- [ ] **Step 1:** Stop the Python spike (kill the `python.exe` running `bt_proxy.py`).
- [ ] **Step 2:** Author runs `windows-bt-proxy.exe install` (elevated) from the CI artifact or a local AOT publish.
- [ ] **Step 3:** In HA the existing ESPHome entry *Windows Bt Proxy* (unique id `02:11:22:33:44:55`) reconnects. Check: name updated to the new default, no second entry; HA provisions a key (entry data gains `noise_psk`; proxy log shows provisioning; `key show` prints it); the entry is loaded afterwards. If HA instead shows a re-authentication prompt, record exactly what it asked and what was needed.
- [ ] **Step 4:** `bluetooth/subscribe_advertisements` (e.g. via a small HA websocket script) shows devices with source `02:11:22:33:44:55`; a SwitchBot meter near the PC keeps updating.
- [ ] **Step 5:** Reboot the PC; before logging in, wait 2 min; after login, the proxy log shows advertisements and a connected client during the logged-out window.
- [ ] **Step 6:** Discovery check (spec §5.5) when the author installs on a second PC (e.g. the media PC): HA shows it under *Discovered* without manual add. If not, record what HA saw (HA logs at debug for `homeassistant.components.zeroconf` / `esphome`) and fix TXT/registration.
- [ ] **Step 7:** Record results in `docs/decisions.md` ("End-to-end, 2026-11-xx"); in the author's private home-automation repo, update `integrations/windows-bt-proxy/README.md` to point at this project and mark the spike retired.
- [ ] **Step 8: Commit** `docs: record end-to-end verification`
