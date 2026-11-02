# Decisions

## Scanning in session 0

Question (spec section 8, risks 1 and 2): does `BluetoothLEAdvertisementWatcher` deliver advertisements
to a Native AOT Windows service (LocalSystem, session 0), and do WinRT plus the BCL crypto work under
Native AOT?

Probe: `spikes/ServiceScanProbe` (.NET 11 rc.3, `PublishAot`, passive scan, log every 10 s to
`%ProgramData%\BtProbe\probe.log`).

### Build

`dotnet publish spikes/ServiceScanProbe -c Release -r win-x64` and `-r win-arm64` both succeed with zero
warnings (`TreatWarningsAsErrors=true`, `IsAotCompatible=true`). No trimming/AOT workarounds were needed
for the WinRT projection (`Windows.Devices.Bluetooth.Advertisement`).

### Console run (user session, 30 s, win-x64 AOT exe)

```
crypto ok
watcher started: Created
2026-11-01T11:23:55.1088440Z ads=24 session=1
2026-11-01T11:24:05.1189011Z ads=25 session=1
2026-11-01T11:24:15.1141515Z ads=26 session=1
```

- `crypto ok`: `X25519DiffieHellman` key agreement plus `ChaCha20Poly1305` round trip work under Native AOT.
- About 2.5 ads/s in the user session (passive scan, Intel AX210).

### Service run (LocalSystem, session 0, author)

- Started manually (`start= demand`) while logged in: 11:33:51Z to 11:39:01Z, every 10 s `ads=22..42 session=0`,
  then `watcher stopped: Success` (service stop).
- Reconfigured to `start= auto` and rebooted (boot 11:54:13Z). Log: `crypto ok`, `watcher started: Created`, then
  11:54:40Z `ads=10 session=0` and 11:54:50Z `ads=6 session=0`, both before the interactive logon at 11:54:52Z.
  After logon: `ads=15, 29, 24 ...`.

| Check | Result |
| --- | --- |
| `crypto ok` in service log | pass |
| Lines with `session=0` and `ads>0` | pass |
| `ads>0` before any user logon (about 40 s window, two intervals) | pass |

### Decision

The WinRT advertisement watcher works in a Native AOT LocalSystem service in session 0, including before any
user logs in. Gate passed: the service does the scanning, as designed. No user-session helper is needed.

## Host (`run` / `service`)

- **Google.Protobuf 3.32.1 to 3.36.2.** Once the API server was reachable from `Main`, the Native AOT publish reported
  IL3053 for Google.Protobuf (`RepeatedField<T>.TryGetArrayAsSpanPinnedUnsafe` calls `Marshal.SizeOf(Type)`, IL3050).
  3.36.2 no longer does, so the build stays warning-free without suppressing anything.
- **`expected_mac` format checked, no change.** Home Assistant's ESPHome integration does not pass `expected_mac`
  to `APIClient` at all; it compares the MAC from the Noise server hello (`InvalidEncryptionKeyAPIError.received_mac`)
  with the config entry's unique id after `format_mac()`, which accepts 12 hex digits without separators. aioesphomeapi's own
  `expected_mac` is documented as lower case without separators, which is what `DeviceIdentity.MacHex` sends.
- **Identity needs the adapter address.** The device MAC is the Bluetooth adapter's address, known only once the scanner has
  looked the adapter up. With no adapter at all the host keeps the scanner retrying and does not open the API port until an
  address exists (a made-up MAC would make Home Assistant adopt a bogus device).
- **`--no-key-acl`** (undocumented, tests only): creates `noise.key` without the SYSTEM/Administrators-only ACL so the interop
  tests can provision a key as a normal user. The service is always started by the SCM as plain `service`.

## End-to-end, 2026-11-01

On the author's desktop (Windows 11, Intel AX210 over Wi-Fi) against the author's production Home Assistant 2026.10.4
(HA Green). Build `0.1.0`, Native AOT win-x64, installed with `windows-bt-proxy.exe install` from an elevated terminal.
The spike's old ESPHome entry was removed first, so this is a fresh adoption.

| Check | Result |
| --- | --- |
| `install`: copy, service (auto start, restart on failure, `failureflag` set), firewall rule (Private/Domain), settings | pass |
| Re-run of `install` over a running install (upgrade in place) | pass, twice |
| Service starts, scans (about 100–117 advertisements a minute), API listens on 6053 | pass |
| HA adds it (ESPHome, host + port), no key typed: HA provisions one over zero-PSK Noise | pass |
| After provisioning: plaintext refused, HA reconnects encrypted and subscribes to raw advertisements | pass |
| TXT flips from `api_encryption_supported` + `api_provisioning` to `api_encryption` (debounced re-publish) | pass |
| `noise.key` not readable by a normal user | pass |
| HA receives `BluetoothLERawAdvertisementsResponse` from the proxy; Bluetooth remote scanner entry auto-created | pass |
| No HA repairs for the device | pass, after the version fix below |
| Zeroconf discovery (HA debug log: "Discovered new device bt-proxy-…" with IP and TXT) | pass, after the SRV fix below |

Two defects showed up only against the real HA, both fixed:

- **`ble_firmware_outdated` repair.** HA raises it for a Bluetooth proxy that reports an ESPHome version below its
  `STABLE_BLE_VERSION` (2026.5.1 in HA 2026.10). The proxy reported 2025.6.0; it now reports 2026.10.0. Keep
  `DeviceIdentity.EsphomeVersion` at or above HA's value when HA raises it.
- **Discovery never completed.** HA received PTR/SRV/TXT, then asked for the A/AAAA records of the SRV target
  `<name>.local` and got no answer: Windows' mDNS responder answers address queries only for the computer's own name,
  not for the host name given to `DnsServiceConstructInstance`. The SRV target is now `<computer name>.local`. HA's ESPHome
  flow only uses the host name as a provisional device name and takes the real one from the device. (The Python spike
  had the same problem for a different reason.)

### Manual checks, same day

| Check | Result |
| --- | --- |
| Bluetooth radio off for 20 s and on again (via `Windows.Devices.Radios`, since mouse and keyboard are Bluetooth) | pass: `RadioOff`, then `Running` as soon as the radio was back; HA stayed connected; normal advertisement rate the next minute |
| Sleep for about 1.5 min, then wake | pass: the watcher kept running (no silent stall); HA's connection died during sleep and HA reconnected by itself 20 s after wake |
| Reboot, logon delayed | pass: scanning 15 s after boot; no network yet, so DNS-SD waited and re-published on the address change 6 s later; HA connected 22 s after boot and was fed for about 16 min before anyone logged on |
| `uninstall --keep-settings`, then `install` | pass: key and settings kept; HA reconnected with the existing key within a second |
| `uninstall` (removes the key), then `install` | pass: HA asked to confirm the encryption change, then provisioned a new key; plaintext refused again afterwards |

The radio-off check showed one wart: switching the radio off stops the watcher with `Success` about half a second before
`Radio.StateChanged`, so the scanner briefly reported `Failed`. An unexpected stop is now reported as `RadioOff` when the
radio is not on (not yet re-checked on hardware).

**Second PC, from the 0.1.0 release.** A Surface laptop, installed from the published release zip, appeared under
*Discovered* in Home Assistant on its own and was added from there (config entry source `zeroconf`); HA provisioned its key
and created its Bluetooth scanner entry, with no repairs. Discovery of a device HA has not seen before works.

## Console window for the tray

Question (plan 2): the exe is a console-subsystem program. Started from the Run key, Explorer or a UAC relaunch it gets a
console window, which is unacceptable for a tray icon. Can the same exe show a window for `run`, `install` and the other verbs
and none for `tray`?

Mechanism (Windows 11 24H2+, build 26100): the manifest setting `consoleAllocationPolicy` = `detached` (namespace
`http://schemas.microsoft.com/SMI/2024/WindowsSettings`) stops Windows from creating a console for a launch that has none to
inherit, and `AllocConsoleWithOptions` (kernel32, exported on 24H2+ only) gets one back. Struct layout and constants checked
against `consoleapi.h` of Windows SDK 10.0.26100: `ALLOC_CONSOLE_OPTIONS { int mode; BOOL useShowWindow; WORD showWindow; }`,
`ALLOC_CONSOLE_MODE_DEFAULT = 0` (attach to the parent's console, else open a window), `NEW_WINDOW = 1`, `NO_WINDOW = 2`.

**Decision: variant A.** `app.manifest` sets the policy. `Program.cs` calls `ConsoleAttach.Ensure()` first, before anything
touches `Console`, for every verb except `service` and `tray`. `Ensure` calls `AllocConsoleWithOptions(DEFAULT)` and ignores
`EntryPointNotFoundException` (pre-24H2: the policy is ignored, the console already exists). The export is bound lazily (not in
the import table of the AOT exe), so the exe still loads on Windows before 24H2. `Installer.TrayCommandLine` is plain
`"<exe>" tray`.

### Matrix, this PC (Windows 11 build 26300), probe `spikes/ConsolePolicyProbe`, Native AOT

"Window" is read from the probe's own log (`GetConsoleWindow() != 0`, processes on the console, kind of stdout) and from
whether a `conhost.exe` child appeared. Terminal rows ran in a `pwsh` and a `cmd.exe` window opened by the harness; their
screen buffer was read back with `ReadConsoleOutputCharacter`.

| Launch | Result under A |
| --- | --- |
| `probe show` typed in pwsh | pass: output on that terminal's screen, shell waited, `$LASTEXITCODE` 7. The console is inherited (window present, 2 processes) before `AllocConsoleWithOptions`; the call returns `EXISTING_CONSOLE` |
| same in cmd.exe | pass: `%ERRORLEVEL%` 7, same log |
| `$x = & probe show`, `probe show \| Out-String` | pass: output captured; stdout is a pipe and stays one (the call does not replace redirected handles) |
| `Start-Process probe tray` (ShellExecute, like Run key and Explorer), from the tool shell and from a pwsh window | pass: no window at all: `GetConsoleWindow` 0, 0 processes on a console, no `conhost.exe` child, before and after |
| `Start-Process probe show`, same two origins | pass: `AllocConsoleWithOptions` returns `NEW_CONSOLE`, a `conhost.exe` child and a Windows Terminal window appear, stdout is the console |
| Ctrl+C on a `show --ctrlc` wait loop (probe started with `-NoNewWindow` in a pwsh window, `GenerateConsoleCtrlEvent`) | pass: `CancelKeyPress` fired, exit 130 |
| `Start-Process probe tray` / `show` with `-Verb RunAs` (UAC) | pass with the real exe (author, 2026-11-02): `install` relaunched elevated shows its output in a new window and waits for Enter; `tray --show-key` relaunched elevated shows no console |
| HKCU `...\Run` value `"<probe>" tray`, sign out and in | not checked (author skipped the sign-out) |

Also with the real exe (variant A applied), same harness: `version`, an unknown verb (usage, exit 1) and `key show`
(not elevated: "Run this from an elevated terminal", exit 1) show their output in a pwsh window and exit with the right code;
`run --no-mdns --fake-scanner --port 16053 --data-dir <temp>` logs to the terminal, to a redirected file, and, via
`Start-Process`, in a new console window; `Start-Process ... tray` (not implemented yet, so it exits 1) opens no console.
`install` and `uninstall` are not exercised here because both raise UAC; they go through the pending `-Verb RunAs` rows.

### Pre-24H2

The manifest setting is ignored, so every launch gets a console. Fallback in code: `tray` calls `ConsoleAttach.ReleaseIfAlone()`
(`FreeConsole` when `GetConsoleProcessList` returns 1), so a launch from Run/Explorer only flashes a window. Not testable on
this PC; the shape was simulated with the probe built without the policy (`probe-nopolicy tray --release-if-alone`, started
through `Start-Process`): console present with 1 process, freed, `GetConsoleWindow` 0 afterwards. Variant B (no manifest policy,
`conhost.exe --headless "<exe>" tray` in the Run key) is not needed while the terminal rows pass.

## Tray, 2026-11-02

On the author's desktop against production Home Assistant 2026.10.4. Build `0.1.0+c05c97a` (the tray branch before the version
bump), Native AOT win-x64, installed with `windows-bt-proxy.exe install` from a **non-elevated** terminal over the running 0.1.0
service.

| Check | Result |
| --- | --- |
| `install` from a normal terminal: UAC, elevated window with the output, upgrade in place, HKLM Run value `"<exe>" tray` | pass |
| Tray starts unelevated in the user's session after the elevated install exits | pass |
| Icon after install | pass: green |
| `windows-bt-proxy status` in a normal terminal | pass: scanner, advertisements, port, encryption, HA's IP subscribed and encrypted |
| Status window (click), menu (right-click) | pass |
| Show encryption key: UAC declined | pass: nothing happens |
| Show encryption key: UAC accepted | pass: key shown, copied to the clipboard |
| This proxy's own ESPHome entry disabled in HA, then re-enabled | pass: `status` showed no clients and the icon went amber; on re-enable HA reconnected encrypted and the icon went green |
| `net stop` / `net start` of the service (elevated) | pass: red, then green |
| Bluetooth off for 20 s | not checked: mouse and keyboard are Bluetooth, so the tray cannot be inspected while the radio is off. The scanner side (`RadioOff`) passed on 2026-11-01; the tray mapping is covered by unit tests |
| Restart Explorer, sign out and in | not checked |
| `install` again with the tray running (restart into the new build), scripted `install` with captured output | not checked on hardware; covered by tests and the final review |
| `uninstall --keep-settings` with the tray running | not checked on hardware |
| Surface laptop | not checked |
