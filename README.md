# Windows BT Proxy for Home Assistant

Turns a Windows PC's built-in Bluetooth into a passive, **ESPHome-compatible** Bluetooth proxy for [Home Assistant](https://www.home-assistant.io/). Home Assistant's built-in ESPHome integration adopts it like an ESP32 running ESPHome's `bluetooth_proxy`, so every Bluetooth LE integration in Home Assistant gets the advertisements this PC hears.

> This project is not affiliated with or endorsed by the Open Home Foundation, which owns the Home Assistant and ESPHome trademarks.

## Requirements

- Windows 11, x64 or ARM64
- A Bluetooth LE adapter (built in or USB)

It ships as a single Native AOT executable, `windows-bt-proxy.exe`. No .NET runtime is needed.

## Install

1. Download the release zip for your architecture (x64 or ARM64) and unpack it.
2. Open an **administrator** terminal and run:

   ```
   windows-bt-proxy.exe install
   ```

   The build is unsigned, so SmartScreen may warn on first run: click **More info**, then **Run anyway**.

`install` copies the exe to `%ProgramFiles%\WindowsBtProxyForHA\`, registers the Windows service `WindowsBtProxyForHA` (starts automatically, restarts on failure), adds a firewall rule for TCP 6053 on Private and Domain networks, and creates default settings.

## Add it to Home Assistant

The proxy should appear under **Settings → Devices & services → Discovered**. Click **Add**.

If it does not show up: **Add integration → ESPHome**, enter the PC's IP address as host and `6053` as port.

You don't need a key. Home Assistant provisions an encryption key automatically on first connect, and from then on the proxy accepts only encrypted connections.

## Settings

`%ProgramData%\WindowsBtProxyForHA\settings.json`. Restart the service to apply changes.

| Setting | Default | Meaning |
| --- | --- | --- |
| `name` | `bt-proxy-<hostname>` | ESPHome node name |
| `friendlyName` | `BT Proxy <HOSTNAME>` | Name shown in Home Assistant |
| `port` | `6053` | TCP port to listen on |
| `scanMode` | `passive` | `passive` or `active` |
| `interfaces` | `null` | `null` = auto: real LAN adapters only (VPN, Tailscale, Hyper-V and WSL adapters are skipped) |
| `logLevel` | `info` | Log verbosity |
| `updates.check` | `true` | Check for updates (used by a future release) |
| `updates.auto` | `false` | Install updates automatically |

## Encryption key

Run these in an administrator terminal:

```
windows-bt-proxy.exe key show     # print the current key
windows-bt-proxy.exe key rotate   # new key; Home Assistant will ask for it
windows-bt-proxy.exe key clear    # forget the key; Home Assistant provisions a new one
```

## Uninstall

```
windows-bt-proxy.exe uninstall
```

Add `--keep-settings` to keep the settings and key.

## Logs

`%ProgramData%\WindowsBtProxyForHA\logs\proxy.log` (rolling). Errors are also written to the Windows Event Log.

## Limits

- Passive only: the proxy never connects to Bluetooth devices, it only forwards advertisements.
- It works only while the PC is on and awake.
- Coverage is whatever this PC's Bluetooth radio can hear.

## Build from source

Requirements: the .NET 11 SDK (version pinned in `global.json`) and Visual Studio Build Tools with the C++ workload for x64 and ARM64, which Native AOT needs.

```
dotnet test
dotnet publish src/WindowsBtProxy -c Release -r win-x64
```

Use `-r win-arm64` for ARM64.

## License

[MIT](LICENSE)
