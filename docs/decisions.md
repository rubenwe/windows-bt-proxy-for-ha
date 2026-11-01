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

### Service run (session 0): pending (author)

Not yet run. Needs an elevated shell (`sc.exe create/start`, see the Task 1 report for commands) and a
sign-out window of about 2 minutes.

To record:

| Check | Result |
| --- | --- |
| `crypto ok` in service log | pending |
| Lines with `session=0` and `ads>0` | pending |
| `ads>0` across the signed-out window | pending |

Gate: if the service sees zero advertisements while the console run sees some, stop and move the scanner
to the user session (spec section 8).
