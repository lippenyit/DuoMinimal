#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $compiler)) { throw 'The 64-bit .NET Framework compiler is required.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Management.dll /reference:System.ServiceProcess.dll ('/out:' + (Join-Path $out 'DuoMinimal.exe')) (Join-Path $root 'src\DuoMinimal.cs') (Join-Path $root 'src\TouchControl.cs') (Join-Path $root 'src\AssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Get-FileHash (Join-Path $out 'DuoMinimal.exe') -Algorithm SHA256
