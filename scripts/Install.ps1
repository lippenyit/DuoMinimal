#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$DisableScreenXpert)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'build\DuoMinimal.exe'
$target = Join-Path $env:ProgramFiles 'DuoMinimal'
$data = Join-Path $env:ProgramData 'DuoMinimal'
$legacy = Get-Service ScreenPadBrightnessSync -ErrorAction SilentlyContinue
$current = Get-Service DuoMinimal -ErrorAction SilentlyContinue
if (!(Test-Path $source)) { throw 'Run scripts\Build.ps1 first.' }
if ((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'UX581GV') { throw 'Only ASUS UX581GV is supported.' }
$main = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorBrightness | Where-Object { $_.Active -and $_.InstanceName.StartsWith('DISPLAY\SDCA029\', [StringComparison]::OrdinalIgnoreCase) })
if ($main.Count -ne 1) { throw 'Expected one active SDCA029 main panel with WMI brightness support.' }
Get-CimClass -Namespace root\wmi -ClassName AsusAtkWmi_WMNB | Out-Null
$hid = @(Get-CimInstance Win32_PnPEntity -Filter "PNPClass = 'HIDClass'")
foreach ($prefix in @('HID\ELAN9008&COL01\','HID\ELAN9009&COL01\')) {
    $matches = @($hid | Where-Object { $_.PNPDeviceID -and $_.PNPDeviceID.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -ne 1) { throw "Expected one touchscreen collection: $prefix" }
}
if ($DisableScreenXpert) { & (Join-Path $PSScriptRoot 'Disable-ScreenXpert.ps1') }
foreach ($name in 'AsusScreenXpert','AsusScreenXpertHostService') {
    $sx = Get-Service $name -ErrorAction SilentlyContinue
    if ($sx -and ($sx.Status -ne 'Stopped' -or $sx.StartType -ne 'Disabled')) {
        throw "Disable ScreenXpert before installation: $name is not stopped and disabled. See README."
    }
}
$oldRunning = $legacy -and $legacy.Status -eq 'Running'
$oldStartup = if ($legacy) { [string]$legacy.StartType } else { $null }
$backup = Join-Path $target 'DuoMinimal.previous.exe'
$exe = Join-Path $target 'DuoMinimal.exe'
try {
    foreach ($service in @($legacy, $current)) {
        if ($service) {
            Stop-Service $service.Name
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
        }
    }
    foreach ($path in @($target, $data)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        & icacls.exe $path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not secure $path" }
    }
    if (!$current -and $legacy) {
        $oldMode = Join-Path $env:ProgramData 'ScreenPadBrightnessSync\panel-mode.txt'
        if (Test-Path $oldMode) { Copy-Item $oldMode (Join-Path $data 'panel-mode.txt') -Force }
    }
    if (Test-Path $exe) { Copy-Item $exe $backup -Force }
    Copy-Item $source $exe -Force
    if (!$current) {
        New-Service -Name DuoMinimal -BinaryPathName ('"' + $exe + '"') -DisplayName 'DuoMinimal' -Description 'Minimal ScreenPad Plus brightness, display and touch controls for ASUS UX581GV.' -StartupType Automatic -DependsOn Winmgmt | Out-Null
    }
    & sc.exe failure DuoMinimal reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
    Start-Service DuoMinimal
    (Get-Service DuoMinimal).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    Start-Sleep -Seconds 2
    if ((Get-Service DuoMinimal).Status -ne 'Running') { throw 'Service stopped after startup.' }
    if ($legacy) { Set-Service ScreenPadBrightnessSync -StartupType Disabled }
    Write-Output 'DuoMinimal installed and running. Legacy service, if present, is disabled and retained for rollback.'
} catch {
    $failure = $_
    Stop-Service DuoMinimal -ErrorAction SilentlyContinue
    if ($current -and (Test-Path $backup)) {
        Copy-Item $backup $exe -Force
        Start-Service DuoMinimal -ErrorAction SilentlyContinue
    } elseif (!$current -and (Get-Service DuoMinimal -ErrorAction SilentlyContinue)) {
        & sc.exe delete DuoMinimal | Out-Null
    }
    if ($legacy) {
        Set-Service ScreenPadBrightnessSync -StartupType $oldStartup
        if ($oldRunning) { Start-Service ScreenPadBrightnessSync }
    }
    throw $failure
}

