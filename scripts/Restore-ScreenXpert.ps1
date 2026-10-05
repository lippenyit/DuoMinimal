#requires -Version 5.1
#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
if ((Get-Service DuoMinimal -ErrorAction SilentlyContinue).Status -eq 'Running') {
    throw 'Uninstall/stop DuoMinimal before restoring ScreenXpert.'
}
$legacy = Get-Service ScreenPadBrightnessSync -ErrorAction SilentlyContinue
if ($legacy) {
    Stop-Service ScreenPadBrightnessSync
    $legacy.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    Set-Service ScreenPadBrightnessSync -StartupType Disabled
}
# Run under the same Windows account that will use ScreenXpert.
$app = @(Get-StartApps | Where-Object Name -Like '*ScreenXpert*')
if ($app.Count -eq 0) {
    if (!(Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        Start-Process 'ms-windows-store://pdp/?ProductId=9N5RFFGFHHP6'
        throw 'Install ScreenXpert in the opened Microsoft Store, then rerun this script.'
    }
    & winget.exe install --id 9N5RFFGFHHP6 --source msstore --exact
    if ($LASTEXITCODE -ne 0) { throw 'ScreenXpert installation did not complete; finish it in Microsoft Store and rerun this script.' }
    $app = @(Get-StartApps | Where-Object Name -Like '*ScreenXpert*')
    if ($app.Count -eq 0) { throw 'ScreenXpert app registration is not yet visible. Sign out/in and rerun this script.' }
}
$backup = Join-Path $env:ProgramData 'DuoMinimal\screenxpert-services.xml'
$settings = if (Test-Path $backup) { @(Import-Clixml $backup) } else { @() }
foreach ($name in @('AsusScreenXpert','AsusScreenXpertHostService')) {
    $service = Get-Service $name -ErrorAction SilentlyContinue
    if (!$service) { throw "ASUS component missing: $name. Reinstall the ScreenXpert interface package from ASUS support for your model, then rerun this script." }
    $saved = @($settings | Where-Object Name -EQ $name)
    $startup = if ($saved.Count -eq 1 -and $saved[0].StartType -in @('Automatic','Manual')) { $saved[0].StartType } else { 'Automatic' }
    Set-Service $name -StartupType $startup
    Start-Service $name
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
}
if ($app.Count -eq 1) { Start-Process ('shell:AppsFolder\' + $app[0].AppID) }
Write-Output 'ScreenXpert installed and its services running. If its UI is absent, sign out and back in.'
