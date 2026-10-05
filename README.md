# DuoMinimal

**Two screens. Just the essentials.**

A tiny Windows service for the **ASUS ZenBook Pro Duo UX581GV**: synchronized ScreenPad Plus brightness, practical display-button controls, and a touch toggle that leaves the pen available on the tested laptop.

No window, floating icon, tray app, keyboard hook, network requests or telemetry. One ~19 KB executable, using the .NET Framework supplied with Windows. Executable size is not RAM usage.

[MIT license](LICENSE)

## Controls

| Control | Action |
| --- | --- |
| Main screen brightness | Lower screen follows, with a nonzero floor at the minimum |
| ScreenPad button: single press | Backlight off / on; also restores a disconnected panel |
| ScreenPad button: double press within 500 ms | Disconnect / reconnect the lower display |
| Former window-swap button | Toggle touch input on both screens |

A single press waits half a second for a possible second press. Dark mode retains the extended desktop. Disconnected mode removes the lower display from Windows, which may rearrange windows. Brightness changes do not wake a dark/disconnected panel. Reconnecting restores brightness synchronization.

## Compatibility

Version 0.1 is tested on **one UX581GV running Windows 11 x64**. A laptop marketed simply as “ZenBook Duo” is not necessarily compatible. The installer/service reject other model names.

Required hardware/software:

- ASUS System Control Interface and its `root\wmi` ASUS ATK interface.
- Main panel `DISPLAY\SDCA029\`, exposing `WmiMonitorBrightness`.
- Touchscreen leaf collections `HID\ELAN9008&COL01\` and `HID\ELAN9009&COL01\`. Instance suffixes are discovered locally, never hardcoded. Missing/ambiguous collections cause touch control to refuse the operation.
- ScreenXpert stopped and disabled (or removed); do not run competing display controllers together.
- Windows PowerShell 5.1 and the 64-bit .NET Framework compiler that ships with supported Windows installations.

The pen stayed functional with touch disabled on the tested machine. Sleep/resume handling is implemented, but wider hardware and restart/resume testing is still needed. This is a focused community tool, not an ASUS-supported replacement for every ScreenXpert feature.

## Install from source

1. Download this repository with **Code → Download ZIP**, extract it, and open **Windows PowerShell as administrator** in the extracted folder.
2. Build, then install:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1 -DisableScreenXpert
```

The execution-policy override affects only these PowerShell processes. Scripts do not download or execute third-party code. Review them before running.

`-DisableScreenXpert` saves its two services' existing startup states, disables/stops them and closes known ScreenXpert processes. It **does not uninstall the app or remove drivers**. Settings are saved under `%ProgramData%\DuoMinimal\screenxpert-services.xml`; later runs preserve the first backup. If ScreenXpert is already disabled/removed, the flag is optional.

DuoMinimal installs to `%ProgramFiles%\DuoMinimal`, runs as an automatic LocalSystem service, and stores state plus a small rotating local log in `%ProgramData%\DuoMinimal`. It needs hardware-management privileges, but has no network functionality or user-session companion.

If the original `ScreenPadBrightnessSync` prototype exists, installation stops it, copies its display mode, and retains it disabled for rollback. Both services must never run simultaneously. Existing touch suppression is released when the old service stops.

### Remove ScreenXpert completely (optional)

After DuoMinimal is working, open **Settings → Apps → Installed apps**, find the **ScreenXpert application**, open its three-dot menu and choose **Uninstall**. Follow Windows' prompts. If requested, restart, then check the controls again.

**Keep ASUS System Control Interface and display/HID drivers installed.** Do not use Device Manager to remove touchscreen, pen or display devices. Removing the ScreenXpert application is separate from removing ASUS drivers. Service disabling is enough to avoid the conflict; application removal is optional. If a later ASUS update reinstalls/re-enables ScreenXpert, repeat the disable script.

Official instructions: [Microsoft: uninstall apps](https://support.microsoft.com/en-gb/windows/uninstall-or-remove-apps-and-programs-in-windows-4b55f974-2cc6-2d2b-d092-5905080eaf98), [ASUS ScreenXpert FAQ](https://www.asus.com/us/support/faq/1050477/).

## Update, uninstall and rollback

Build the new source and rerun `Install.ps1`. An existing DuoMinimal executable is backed up as `DuoMinimal.previous.exe`; installation attempts to restore it if startup fails.

```powershell
# Remove DuoMinimal's service registration; retains local files and logs.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall.ps1

# Or return to the previous prototype, if it was installed on this machine:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall.ps1 -RestoreLegacy
```

Stopping DuoMinimal normally restores touch if it disabled it. Uninstall also restores the lower display to visible mode. Following an abnormal exit, its marker enables recovery at next service startup. Recovery failures are logged; keep the physical keyboard available.

To return to ScreenXpert using the same Windows account, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall.ps1 -RestoreScreenXpert
```

This stops/unregisters DuoMinimal, restores its owned touch suppression and turns the lower panel on. If the ScreenXpert app is missing, the restore script uses `winget` to install Microsoft Store product **9N5RFFGFHHP6**. Store sign-in/agreements may require interaction. Without winget, it opens the Store page and asks you to finish installation and rerun the script. It then starts the two ASUS services and attempts to open the app. Previously disabled/missing saved startup settings default to Automatic when explicitly restoring ScreenXpert.

The ASUS ScreenXpert background/interface components must still exist. If you removed those too, reinstall the model-specific package from ASUS support; the script reports the missing service instead of claiming success. The app reinstall branch has not been exercised end-to-end on the development laptop, to avoid removing its installed ASUS app solely for testing. Program/data files are retained for rollback.

## Troubleshooting and internals

```powershell
Get-Service DuoMinimal
Get-Content "$env:ProgramData\DuoMinimal\service.log" -Tail 30
```

ASUS WMI event 106 handles the ScreenPad key; event 156 handles the touch key. ASUS device IDs `0x50031` and `0x50032` control panel power and raw brightness. The minimum visible brightness is raw 4/255. Brightness uses WMI events, a five-second fallback and a thirty-second refresh. Power commands are checked against firmware state.

Touch control targets only the two known HID collections, uses nonpersistent Configuration Manager disable calls, and attempts recovery if either operation fails. No ordinary key events are captured. The ASUS hardware layer can still display its own OSD; DuoMinimal adds none.

The ASUS device identifiers were cross-checked with [G-Helper's ASUS ACPI implementation](https://github.com/seerge/g-helper/blob/main/app/AsusACPI.cs); the project contains no bundled ASUS or G-Helper binaries.

Source contains no machine logs, dumps, credentials or machine-specific device instance suffixes. Please avoid posting full system diagnostics when reporting an issue.


