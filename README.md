# Windows BT Proxy for Home Assistant

Turns a Windows PC's built-in Bluetooth into a passive, **ESPHome-compatible** Bluetooth proxy for [Home Assistant](https://www.home-assistant.io/). Home Assistant's built-in ESPHome integration adopts it like an ESP32 running ESPHome's `bluetooth_proxy`, so every Bluetooth LE integration in Home Assistant gets the advertisements this PC hears.

> This project is not affiliated with or endorsed by the Open Home Foundation, which owns the Home Assistant and ESPHome trademarks.

## Requirements

- Windows 11, x64 or ARM64
- A Bluetooth LE adapter (built in or USB)

It ships as a single Native AOT executable, `windows-bt-proxy.exe`. No .NET runtime is needed.

## Install

Download the zip for your PC from [Releases](https://github.com/rubenwe/windows-bt-proxy-for-ha/releases/latest) (`win-x64` for Intel/AMD, `win-arm64` for ARM) and unzip it, or [build from source](#build-from-source). `SHA256SUMS.txt` in the release lets you check the download with `Get-FileHash`.

Open an **administrator** terminal in the folder with the exe and run:

```
windows-bt-proxy.exe install
```

The build is unsigned, so SmartScreen may warn on first run: click **More info**, then **Run anyway**.

`install` copies the exe to `%ProgramFiles%\WindowsBtProxyForHA\`, registers the Windows service `WindowsBtProxyForHA` (starts automatically, restarts on failure), adds a firewall rule for TCP 6053 on Private and Domain networks, and creates default settings.

## Add it to Home Assistant

The proxy may appear under **Settings → Devices & services → Discovered** (mDNS discovery depends on your network). Click **Add**.

The reliable way: **Add integration → ESPHome**, enter the PC's IP address as host and `6053` as port.

You don't need a key. Home Assistant 2026.8 or newer provisions an encryption key automatically on first connect ([it sends the key over an encrypted zero-PSK session](https://github.com/home-assistant/core/pull/176144), merged 2026-07-15), and from then on the proxy accepts only encrypted connections. With an older Home Assistant the proxy stays unencrypted (`key show` then prints "no key"); run `key rotate` to force encryption, and Home Assistant then asks you for the key.

Until Home Assistant has adopted the proxy it has no key, and any device on your LAN could provision one first. That is inherent to ESPHome's zero-PSK provisioning. If it happens, run `key clear` and add the proxy again.

## Settings

`%ProgramData%\WindowsBtProxyForHA\settings.json`. Only administrators can write it: edit it from an elevated editor. Apply changes by restarting the service in an elevated terminal:

```
net stop WindowsBtProxyForHA
net start WindowsBtProxyForHA
```

After changing `port`, run `install` again so the firewall rule follows.

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

All key commands, including `key show`, need an administrator terminal:

```
windows-bt-proxy.exe key show     # print the current key ("no key" if Home Assistant has not provisioned one)
windows-bt-proxy.exe key rotate   # new key, encryption forced; Home Assistant will ask for it
windows-bt-proxy.exe key clear    # forget the key; Home Assistant asks you to confirm, then provisions a new one
```

## Uninstall

```
windows-bt-proxy.exe uninstall
```

Add `--keep-settings` to keep the settings and key.

## Logs

`%ProgramData%\WindowsBtProxyForHA\logs\proxy.log` (rolling). Errors are also written to the Windows Event Log.

## Limits

- Advertisements only: the proxy never connects to Bluetooth devices, it only forwards what it hears.
- It works only while the PC is on and awake.
- Coverage is whatever this PC's Bluetooth radio can hear.

## Build from source

Requirements: the .NET 11 SDK (version pinned in `global.json`) and Visual Studio Build Tools with the C++ workload for x64 and ARM64, which Native AOT needs.

```
dotnet test
dotnet publish src/WindowsBtProxy -c Release -r win-x64
```

Use `-r win-arm64` for ARM64.

## Releasing

Set `<Version>` in `src/WindowsBtProxy/WindowsBtProxy.csproj`, merge to `main`, then push a matching tag (`git tag -a v0.2.0 -m 0.2.0 && git push origin v0.2.0`). The [Release workflow](.github/workflows/release.yml) checks the tag against the version, runs all tests, builds both architectures and publishes the GitHub release with the zips and `SHA256SUMS.txt`. A tag with a suffix (`v0.2.0-rc.1`) becomes a pre-release.

## License

[MIT](LICENSE)
