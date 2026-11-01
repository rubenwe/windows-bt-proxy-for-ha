# Windows BT Proxy for Home Assistant — design

*2026-11-01 · status: draft for review*

## 1. What this is and why

A small native Windows program that turns a PC's built-in Bluetooth into a **passive Bluetooth
proxy for Home Assistant**. It speaks the **ESPHome Native API**, so HA's built-in ESPHome
integration adopts it exactly like an ESP32 running ESPHome's `bluetooth_proxy:`. Every BLE
integration in HA (SwitchBot, BTHome, Xiaomi, iBeacon, …) then gets advertisements heard by that PC.

**Why:** BLE range in a house is patchy. PCs that are on most of the time (desktop, media PC,
laptops) already have Bluetooth radios in useful places. An ESP32 per room is the usual answer;
this reuses hardware that is already there.

**Where it comes from:** a working Python spike (in the author's private HA repo,
`integrations/windows-bt-proxy/`: `bt_proxy.py` + `test_client.py`). It was adopted by HA 2026.10.4
and fed real advertisements into HA's Bluetooth stack. This project is the robust, distributable
version of that spike. The spike's behaviour is the reference; §7 lists what it proved.

**Who it's for:**
1. The author's house: a desktop, a media PC, several laptops.
2. Anyone, as a **public MIT-licensed GitHub repo** with automated builds and releases.

**Success:** "If it works, it works." Install it on a PC, HA picks it up, BLE devices near that PC
show up in HA, and nobody has to think about it again.

**Name:** *Windows BT Proxy for Home Assistant*, repo `windows-bt-proxy-for-ha`.
"Home Assistant" and "ESPHome" are Open Home Foundation trademarks: the brand goes at the end
("… for Home Assistant"), ESPHome is only mentioned descriptively ("ESPHome-compatible"),
no OHF logos, and the README says it is not affiliated with or endorsed by OHF.

## 2. Goals

- **Passive BLE proxy** over the ESPHome Native API that HA adopts with its stock ESPHome
  integration — no custom HA component.
- **Windows service** that does all the work: starts with Windows before anyone logs in, restarts
  on crash, writes a rolling log.
- **Optional tray app** (per-user, starts at logon) showing status. Display only.
- **Several proxies per house** without clashes; machines that sleep or leave the house come back
  without fuss.
- **ESPHome Noise encryption**, ideally provisioned by HA itself (§5.4).
- **Native AOT, self-contained, single .exe** per architecture, no runtime to install.
- **Self-install / uninstall** from the .exe (service, firewall rule, tray autostart).
- **GitHub Actions** CI + tagged releases with signed artifacts.
- **Update check** with **opt-in auto-update** that only installs releases signed by the project key.

## 3. Non-goals (v1)

- **Active connections (GATT).** Nothing in the author's house needs them (all BLE devices are
  broadcast-only; the one candidate, a Husqvarna Automower, is ignored in HA). Keep the design from
  blocking it later, but don't build it.
- MSI installer, winget, Microsoft Store, paid code signing — later, if ever.
- A configuration UI. Settings are a file; the tray only displays.
- Decoding sensor data. The proxy forwards raw advertisements; HA's integrations decode.
- Linux/macOS. Windows 10 1809+ and Windows 11 only.

## 4. Decisions already made

| Topic | Decision |
|---|---|
| HA-side protocol | ESPHome Native API. Considered and rejected: Ruuvi Gateway protocol (polling, passive-only forever, Ruuvi-specific flow), a custom HACS integration (fragile against HA internals, extra install step), MQTT gateways (not a proxy — HA's BLE integrations never see raw data), upstreaming to HA core (long path, needs this first) |
| Language/runtime | **.NET 11**, C#, **Native AOT**, self-contained. The SDK is `11.0.100-rc.3` on the dev machine; .NET 11 GA ships Nov 2026 — move to GA when it lands |
| Architectures | `win-x64` and `win-arm64` |
| Process model | Service does everything; tray app is optional and display-only, talks to the service over local IPC |
| Default scan mode | Passive (configurable to active) |
| Auto-update | Off by default; update *check* on by default |
| Licence | MIT |
| Repo | `W:\windows-bt-proxy-for-ha`, public GitHub remote added by the author later |

## 5. How it works

### 5.1 Components

```
 ┌─────────────────────────── Windows service (LocalSystem) ───────────────────────────┐
 │  BLE scanner (WinRT BluetoothLEAdvertisementWatcher)                                │
 │        │ raw advertisements                                                         │
 │        ▼                                                                            │
 │  Fan-out + batching ──► ESPHome API server (TCP 6053, plaintext or Noise) ◄── HA    │
 │  mDNS registration (Windows DnsServiceRegister)                                     │
 │  Settings, encryption key, update checker/installer, log                            │
 │  Local status endpoint (named pipe) ──────────────────────────────┐                 │
 └───────────────────────────────────────────────────────────────────│─────────────────┘
                                                                     ▼
                                          Tray app (per user, optional, display only)
```

One .exe, several modes chosen by command line: `service` (run by the SCM), `tray`,
`install`, `uninstall`, `run` (console, for debugging), `status`.

### 5.2 BLE scanning

- WinRT `BluetoothLEAdvertisementWatcher`, `ScanningMode = Passive` by default.
- Per advertisement: rebuild the raw AD payload from `Advertisement.DataSections` as
  `[len = data.Length + 1][type][data…]` concatenated; `address = BluetoothAddress` (uint64);
  `rssi = RawSignalStrengthInDBm`; `address_type`: WinRT `Random` → 1, `Public`/`Unspecified` → 0.
- Scan responses arrive as separate events (`AdvertisementType == ScanResponse`) — forward them as
  separate advertisements, like ESPHome does; HA merges them.
- Adapter missing / radio off / adapter re-plugged: the service keeps running, reports "no
  adapter" (tray + log), and restarts the watcher when the radio comes back (watch
  `BluetoothAdapter`/`Radio` state, plus the watcher's `Stopped` event with its error).
- The proxy's Bluetooth MAC is `BluetoothAdapter.GetDefaultAsync().BluetoothAddress`.

### 5.3 ESPHome Native API server

Source of truth for messages: ESPHome's `api.proto` + `api_options.proto`, vendored into the repo
at a pinned ESPHome release; C# generated at build time with `Grpc.Tools` (bundles `protoc`) and
serialised with `Google.Protobuf` (binary only — no JSON/reflection paths, AOT-safe).

**Plaintext framing:** `0x00`, varint payload length, varint message type, protobuf payload.
**Noise framing:** see §5.4.

**Messages the server must handle** (type IDs from `api.proto`):

| Request | Response / behaviour |
|---|---|
| `HelloRequest` (1) | `HelloResponse` (2): `api_version_major/minor` (see below), `server_info`, `name` |
| `AuthenticationRequest` (3) | Ignore (no password support), as current ESPHome does |
| `DeviceInfoRequest` (9) | `DeviceInfoResponse` (10): `name`, `friendly_name`, `mac_address` = BT MAC, `bluetooth_mac_address` = BT MAC, `esphome_version`, `compilation_time`, `manufacturer`, `model`, `bluetooth_proxy_feature_flags = PASSIVE_SCAN(1) \| RAW_ADVERTISEMENTS(32)` (+ `FEATURE_STATE_AND_MODE(64)` if implemented), encryption fields per §5.4 |
| `ListEntitiesRequest` (11) | `ListEntitiesDoneResponse` (19) — no entities |
| `SubscribeBluetoothLEAdvertisementsRequest` (66) | Start streaming `BluetoothLERawAdvertisementsResponse` (93) to that client |
| `UnsubscribeBluetoothLEAdvertisementsRequest` (87) | Stop streaming to that client |
| `PingRequest` (7) | `PingResponse` (8) |
| `DisconnectRequest` (5) | `DisconnectResponse` (6), close |
| everything else | Accept silently (log at debug) — a real device does the same |

- **Batching:** up to 16 advertisements per `BluetoothLERawAdvertisementsResponse`, flushed every
  ~100 ms or when full (matches ESPHome; the spike used the same numbers).
- **Several clients** at once (HA + a debug client) each get their own subscription.
- **API version:** the spike reports **1.13**: below 1.15 HA reads proxy features from
  `DeviceInfoResponse` instead of sending `DeviceCapabilitiesRequest` (149/150); below 1.16 it
  doesn't expect proxy-subscription acks. HA 2026.10.4 (client API 1.18) accepted 1.13. Keep 1.13
  unless encryption provisioning (§5.4) or another needed feature requires a higher version; if
  so, implement whatever that higher version obliges (capabilities RPC, acks) and record it here.
- **Keepalive:** HA pings; answer pings. Drop clients whose socket dies; never let one slow client
  block others (bounded per-client queue, drop oldest advertisements on overflow).

### 5.4 Encryption

ESPHome's Noise transport: `Noise_NNpsk0_25519_ChaChaPoly_SHA256`, 32-byte PSK (base64 in HA),
frames prefixed `0x01` + 2-byte big-endian length, prologue/handshake as implemented by
`aioesphomeapi`'s `_frame_helper/noise.py` (the reference — read it, and ESPHome's
`api_frame_helper` C++).

Wanted behaviour (**the author decided: always encrypted**):
- **Preferred path, if stock HA supports it:** fresh install has no key and advertises that it
  **supports encryption and accepts a key** (`DeviceInfoResponse.api_encryption_supported` /
  `api_encryption_provisionable`, mDNS TXT as ESPHome does). Plaintext is served **only until
  HA provisions a key** (`NoiseEncryptionSetKeyRequest` 124 → `NoiseEncryptionSetKeyResponse` 125)
  during adoption; the proxy stores it and is Noise-only from then on.
- **Otherwise:** `install` **always** generates a key, prints it, and the tray shows it (admin
  only, §5.8); the user pastes it into HA when adding the proxy. Plaintext is never served.
- In both cases, once a key exists plaintext connections are refused. A `key --rotate` command
  replaces the key (HA then asks for the new one).
- The spike's existing plaintext HA entry: after switching to the new proxy, HA will ask for the
  key (a re-authentication prompt on the ESPHome entry), or receive one via provisioning.
  Verify which happens and document it.

**Open technical question for the builder:** confirm from HA's ESPHome integration source (at
the HA version current at build time) when and how HA sends `NoiseEncryptionSetKeyRequest`, and
which API version / flags / TXT records trigger it. Pick the path that works with stock HA; if
auto-provisioning isn't possible, ship the manual path and note it in the README.

Key storage: in the settings directory (§5.6), file ACL'd to SYSTEM + Administrators. DPAPI
(machine scope) on top is the builder's call.

**Noise crypto in .NET:** ChaCha20-Poly1305 and SHA-256 are in the BCL; X25519 is not. Choose a
small, AOT-compatible, MIT/BSD-licensed implementation (e.g. NSec, BouncyCastle, or a vetted
standalone X25519) — builder's call, record the choice.

### 5.5 Discovery (mDNS)

Register `_esphomelib._tcp` via the Windows DNS-SD API (`DnsServiceRegister` in `dnsapi.dll`,
P/Invoke, Windows 10 1809+) so Windows' own mDNS responder answers. Instance/host name = proxy
name; TXT records as ESPHome sends them (at least `mac` lowercase hex without colons, `version`,
`platform`, `network`, `friendly_name`, plus the encryption-related keys from §5.4).

The spike's Python `zeroconf` announcement did **not** make HA show it under *Discovered* within
~10 minutes; it was added manually. Getting automatic discovery working is a goal; if it turns out
HA needs something more (specific TXT keys, a reply to a query from HA), find out from HA's
ESPHome zeroconf handling. Manual add (host + port) must always work.

Advertise only on real LAN interfaces: skip loopback, Tailscale/WireGuard/VPN and Hyper-V/WSL
virtual adapters by default; a setting can pin an interface.

### 5.6 Settings

`%ProgramData%\WindowsBtProxyForHA\settings.json` (exact folder name builder's call), created with
defaults on install, read at service start (restart the service to apply; hot reload optional):

| Setting | Default |
|---|---|
| `name` | `bt-proxy-<hostname>` lower-cased, sanitised to ESPHome's allowed characters (a-z, 0-9, `-`) |
| `friendlyName` | `BT Proxy <HOSTNAME>` |
| `port` | 6053 |
| `scanMode` | `passive` |
| `interfaces` | auto (see §5.5) |
| `logLevel` | `info` |
| `updates.check` | `true` |
| `updates.auto` | `false` |

The proxy's identity in HA is its Bluetooth MAC; renaming doesn't create a second device.

### 5.7 Install / uninstall

`windows-bt-proxy.exe install` (elevated; prompts for elevation if not):
1. Copy itself to `%ProgramFiles%\WindowsBtProxyForHA\`.
2. Create default settings if absent.
3. Register and start the service (auto-start, restart on failure).
4. Add an inbound firewall rule: TCP on the configured port, **Private and Domain profiles**, for
   that program path. Warn (don't change it) if the active network is classified Public.
5. Register the tray app to start at logon for all users (`HKLM\…\Run`) and start it for the
   current user.

`uninstall` reverses all of it; `uninstall --keep-settings` keeps settings and key.
Re-running `install` over an existing install = upgrade in place.

### 5.8 Tray app

Per-user process, starts at logon, no admin rights. Icon states: **green** (HA connected and
subscribed), **amber** (running, no HA client), **red** (service stopped, no adapter, or radio off).
Click → small status window: proxy name, adapter MAC, scan mode, advertisements in the last minute,
connected clients (IP), encryption state (and the key, for the manual path), version, update status
(and an "Install update" button that asks the service to do it — elevation stays in the service).

UI technology is the builder's call, constrained by Native AOT. WinForms works with AOT on recent
.NET only with caveats; a plain Win32 tray icon + a minimal window via P/Invoke is the safe
baseline. Keep it small.

IPC: a named pipe owned by the service, JSON messages (source-generated `System.Text.Json` for
AOT). Nothing reachable from the network. *(Designer's call, from review:)* the pipe's ACL lets
local interactive users **read status only**. "Show key" and "install update" require the caller
to be in Administrators (the service checks the client's token); the tray triggers them by
relaunching itself elevated (UAC prompt) for that one action. The `key` command in an elevated
console does the same without the tray.

### 5.9 Updates

- Service checks the GitHub Releases API (latest non-prerelease) once a day and at start.
- **Verification:** each release ships, per architecture, the .exe zip, a SHA-256 file, and an
  **Ed25519 signature** over the SHA-256 file. The public key is compiled into the program; the
  private key lives only in a GitHub Actions secret. The service refuses any update whose
  signature doesn't verify. (Ed25519 verify in .NET: same library decision as X25519.)
- `updates.auto = false`: tray shows "update available"; the user clicks Install.
- `updates.auto = true`: service installs by itself.
- Install mechanics (builder's call): download to a temp dir, verify, stage the new exe, stop the
  service, swap, start — e.g. via a short-lived helper process or a `--apply-update` mode. A failed
  start rolls back to the previous exe.
- No telemetry. The only outbound call is the GitHub Releases API.

### 5.10 Logging

Rolling text log in `%ProgramData%\WindowsBtProxyForHA\logs\` (size-capped, a few files).
Errors also go to the Windows Event Log. Per-minute summary at info level ("N advertisements,
M clients"), protocol detail at debug.

## 6. Build, test, release

- **Dev and test environment:**
  - Dev PC: the author's desktop (Windows 11, Intel AX210, admin rights), the same machine the spike
    ran on, with the .NET 10 and 11 RC SDKs installed.
  - HA: the author's **production** HA (HA Green, 2026.10.x). Adding, re-configuring and removing
    *this proxy's own* ESPHome entry there is fine; touch nothing else.
  - Known sensors near the dev PC: the SwitchBot meters (e.g. *Thermometer Office*) that
    HA already reads.
  - The spike is still adopted on that HA as ESPHome entry *Windows Bt Proxy* (unique id = the
    adapter MAC `02:11:22:33:44:55`). Stop the spike before running the new service. The new proxy
    reports the same MAC, so HA treats it as the same device; if its name differs, HA updates the
    entry rather than adding a second one. Verify that.
  - GitHub: public remote `git@github.com:rubenwe/windows-bt-proxy-for-ha.git` (set up by the
    author). CI runs there once pushed. The builder generates the
    Ed25519 release key pair, commits only the public key, and hands the private key to the author
    to store as an Actions secret. The first tagged release is the end-to-end test of signing and
    updating.
- **Repo layout** (builder's call), but keep the vendored `api.proto` pinned and its ESPHome
  version recorded.
- **CI (GitHub Actions, `windows-latest`):** build both architectures with Native AOT; **treat
  AOT/trim warnings as errors**; run tests.
- **Tests:**
  - Unit: framing (plaintext + Noise round-trips against known vectors), AD-structure rebuild,
    batching, settings defaults/name sanitising.
  - Protocol integration without Bluetooth: run the server with a **fake scanner** emitting canned
    advertisements, then connect with the **latest `aioesphomeapi`** from Python (pip in CI) — a
    port of the spike's `test_client.py` — and assert hello/device-info/flags/advertisements, both
    plaintext and encrypted. This is the early warning for ESPHome/HA protocol changes; run it on a
    weekly schedule too, not only on push.
  - Manual on real hardware before each release: install on a PC, adopt in HA, see a known sensor
    update through it.
- **Release:** pushing tag `vX.Y.Z` builds, signs (Ed25519, §5.9), and publishes a GitHub Release
  with zips + checksums + signatures. Versions follow SemVer.
- **SmartScreen:** releases are not code-signed (no certificate), so Windows warns on first run.
  Say so in the README with the "More info → Run anyway" step. Revisit signing later.

## 7. What the spike proved (facts to rely on)

- WinRT advertisement scanning works from a desktop process; the Intel AX210 heard ~7 devices /
  ~180 advertisements per minute in passive mode in the office; active mode roughly doubles
  advertisements (scan responses). Passive scans sometimes omit the Flags AD section — fine.
- HA 2026.10.4 adopted a plaintext server reporting API 1.13 with flags `PASSIVE_SCAN |
  RAW_ADVERTISEMENTS`, sent `SubscribeBluetoothLEAdvertisementsRequest` with `flags=1` (raw), and
  auto-created a Bluetooth scanner config entry `<name> (<MAC>)`. HA's
  `bluetooth/subscribe_advertisements` showed devices with the proxy as their best source.
- HA connects twice on add (a short probe by `aioesphomeapi`, then the real client).
- mDNS via Python `zeroconf` did not trigger HA discovery in ~10 min (§5.5).
- Windows Firewall blocked port 6053 by default; the LAN was a *Private* network and existing
  `python.exe` allow-rules only covered *Public*.

## 8. Risks — check these first, in this order

1. **BLE watcher in a service.** Does `BluetoothLEAdvertisementWatcher` deliver advertisements when
   running as a service (LocalSystem, session 0, no user logged in)? Build a tiny AOT test that
   only scans, run it as a service, log counts. **If it doesn't work:** the scanner moves into a
   per-user process (the tray app, started at logon) that feeds the service over the pipe, and
   "works before login" is dropped — tell the author before going further.
2. **WinRT under Native AOT.** The same tiny test must be built with Native AOT and zero
   trim/AOT warnings.
3. **Encryption provisioning by HA** (§5.4 open question).
4. **Automatic discovery** (§5.5).

## 9. Left to the builder

Project structure, UI toolkit for the tray (within AOT limits), crypto library choice, exact
install paths and names, update-apply mechanics, log library (or none), hot reload of settings.
Record each such choice in the README or a short `docs/decisions.md`.
