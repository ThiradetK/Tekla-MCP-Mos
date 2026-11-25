# Build pipeline: restore -> build -> unit tests -> optional smoke test -> optional installer.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Configuration Release] [-Smoke] [-Installer]
#   -Smoke      also runs tests\integration\smoke.ps1 (needs Tekla Structures running; read-only).
#   -Installer  also builds dist\TeklaMcp-Setup-<version>.exe with Inno Setup 6 (Release only).
param(
    [string]$Configuration = 'Release',
    [switch]$Smoke,
    [switch]$Installer
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

if ($Installer) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
    $version = ([xml](Get-Content (Join-Path $PSScriptRoot 'src\TeklaMcp\TeklaMcp.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    & $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer\TeklaMcp.iss')
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    "INSTALLER OK -> $(Join-Path $PSScriptRoot "dist\TeklaMcp-Setup-$version.exe")"
}
"BUILD OK -> $out\TeklaMcp.exe"
