# Builds StretchRes.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No Visual Studio or SDK needed.  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4 C# compiler (csc.exe) not found.' }

$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$out\StretchRes.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $root 'src\StretchRes.cs')

if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)." }
Write-Host "OK -> $out\StretchRes.exe"
