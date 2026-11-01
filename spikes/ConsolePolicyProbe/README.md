Frozen probe from the tray console-window spike (does `consoleAllocationPolicy=detached` give the tray no window while a terminal launch still works?). Kept for the record, not maintained code. Results and the decision are in `docs/decisions.md`, section "Console window for the tray".

Build: `dotnet publish spikes/ConsolePolicyProbe -c Release -r win-x64` (`-p:ProbeManifest=none` builds `probe-nopolicy.exe` without the policy). Run `probe tray` or `probe show` with `--hold <s>`, `--ctrlc`, `--exit <n>`, `--no-alloc`, `--release-if-alone`; it logs to `%TEMP%\console-probe.log`.
