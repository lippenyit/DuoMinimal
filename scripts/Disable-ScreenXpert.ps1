#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ((Get-CimInstance Win32_ComputerSystem).Model -notmatch 'UX581GV') { throw 'Only ASUS UX581GV is supported.' }
$data = Join-Path $env:ProgramData 'DuoMinimal'
New-Item -ItemType Directory -Path $data -Force | Out-Null
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure settings backup folder.' }
$services = @(Get-Service -Name AsusScreenXpert,AsusScreenXpertHostService -ErrorAction SilentlyContinue)
$backup = Join-Path $data 'screenxpert-services.xml'
if (!(Test-Path $backup)) {
    @($services | Select-Object Name,@{n='StartType';e={[string]$_.StartType}},@{n='WasRunning';e={$_.Status -eq 'Running'}}) | Export-Clixml $backup
}
foreach ($service in $services) {
    Set-Service $service.Name -StartupType Disabled
    Stop-Service $service.Name -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}
$names = @('AsusLibra','AsusLibraService','AsusLinkToScreenXpert','AsusScreenXpertUI','AsusScreenXpertReunion','AsusScreenXpertUserUI')
Get-Process -Name $names -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'ScreenXpert disabled. App and ASUS hardware drivers retained. See README for optional app removal.'
