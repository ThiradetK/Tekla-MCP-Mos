# Build pipeline: restore -> build -> unit tests. Installer packing is added in Phase 7a.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Configuration Release] [-Smoke]
#   -Smoke  also runs tests\integration\smoke.ps1 (needs Tekla Structures running; read-only).
param(
    [string]$Configuration = 'Release',
    [switch]$Smoke
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

& $dotnet restore TeklaMcp.sln
if ($LASTEXITCODE -ne 0) { throw "restore failed" }
& $dotnet build TeklaMcp.sln -c $Configuration --no-restore -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }
& $dotnet test TeklaMcp.sln -c $Configuration --no-build -nologo
if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }

$out = Join-Path $PSScriptRoot "src\TeklaMcp\bin\$Configuration\net48"
$forbidden = Get-ChildItem $out -Filter '*.dll' | Where-Object { $_.Name -like 'Tekla.*' -or $_.Name -like 'Trimble.*' }
if ($forbidden) { throw "Trimble/Tekla DLLs must not be in the output: $($forbidden.Name -join ', ')" }

if ($Smoke) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tests\integration\smoke.ps1') -Exe (Join-Path $out 'TeklaMcp.exe')
    if ($LASTEXITCODE -ne 0) { throw "smoke test failed" }
}
"BUILD OK -> $out\TeklaMcp.exe"
