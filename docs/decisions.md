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
