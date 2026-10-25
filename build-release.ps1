$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$publishDir = Join-Path $PSScriptRoot 'artifacts\publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish .\TrackTelemetryViewer.csproj -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc .\PitWall.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }

Write-Host "Installer: $(Join-Path $PSScriptRoot 'artifacts\PitWall-Setup.exe')"
