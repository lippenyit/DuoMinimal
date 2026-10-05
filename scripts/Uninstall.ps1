#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$RestoreLegacy, [switch]$RestoreScreenXpert)
$ErrorActionPreference = 'Stop'
if ($RestoreLegacy -and $RestoreScreenXpert) { throw 'Choose only one controller to restore.' }
$service = Get-Service DuoMinimal -ErrorAction SilentlyContinue
$exe = Join-Path $env:ProgramFiles 'DuoMinimal\DuoMinimal.exe'
if ($service) {
    Stop-Service DuoMinimal
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    if (!(Test-Path $exe)) { throw 'Installed executable missing: cannot restore hardware state.' }
    $restore = Start-Process $exe -ArgumentList '--restore' -Wait -PassThru -WindowStyle Hidden
    if ($restore.ExitCode -ne 0) { throw 'Hardware recovery failed; service retained. See the local service.log.' }
    & sc.exe delete DuoMinimal
    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
}
if ($RestoreLegacy) {
    if (!(Get-Service ScreenPadBrightnessSync -ErrorAction SilentlyContinue)) { throw 'No legacy service is available.' }
    Set-Service ScreenPadBrightnessSync -StartupType Automatic
    Start-Service ScreenPadBrightnessSync
}
if ($RestoreScreenXpert) { & (Join-Path $PSScriptRoot 'Restore-ScreenXpert.ps1') }
Write-Output 'DuoMinimal unregistered; hardware restored. Program/data files retained for rollback.'
