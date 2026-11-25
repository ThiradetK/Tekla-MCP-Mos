# Manual integration test (read-only). Waits until Tekla Structures is closed, then starts TeklaMcp.exe and
# calls tekla_status every few seconds, printing each result. Use it to check:
#   1) server started while Tekla is closed, then Tekla opened  -> connected must become true
#   2) Tekla busy (e.g. a modal dialog) longer than the timeout  -> error tekla_timeout, then recovery
# Usage: powershell -File tests\integration\watch-status.ps1 [-Minutes 15] [-IntervalSeconds 5]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [int]$Minutes = 15,
    [int]$IntervalSeconds = 5,
    # Start at once (Tekla open) to test "connected, Tekla restarted, connected again".
    [switch]$NoWait
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
function Stamp { (Get-Date).ToString('HH:mm:ss') }

if (-not $NoWait -and (Get-Process TeklaStructures -ErrorAction SilentlyContinue)) {
    "$(Stamp) Waiting for Tekla Structures to close..."
    while (Get-Process TeklaStructures -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
}
"$(Stamp) Starting server (Tekla running: $([bool](Get-Process TeklaStructures -ErrorAction SilentlyContinue)))."

$psi = New-Object Diagnostics.ProcessStartInfo $Exe
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$p = [Diagnostics.Process]::Start($psi)
$null = $p.StandardError.ReadToEndAsync()
$stdin = $p.StandardInput; $stdin.AutoFlush = $true
function Send($o) { $stdin.WriteLine(($o | ConvertTo-Json -Depth 20 -Compress)) }
function Receive([int]$id) {
    while ($true) {
        $line = $p.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "server stdout closed" }
        $m = $line | ConvertFrom-Json
        if ($m.PSObject.Properties.Name -contains 'id' -and $m.id -eq $id) { return $m }
    }
}

Send @{ jsonrpc = '2.0'; id = 0; method = 'initialize'; params = @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'watch-status'; version = '1' } } }
$null = Receive 0
Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

$end = (Get-Date).AddMinutes($Minutes); $id = 1; $last = ''
$sawDisconnected = $false; $sawConnected = $false; $sawTimeout = $false; $recovered = $false
while ((Get-Date) -lt $end) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Send @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = 'tekla_status'; arguments = @{} } }
    $r = Receive $id; $id++
    $sc = $r.result.structuredContent
    $state = if ($r.result.isError) { "ERROR $($sc.error.code)" } elseif (-not $sc.connected) { 'not connected' } elseif (-not $sc.model_open) { 'connected, no model' } else { "connected, model $($sc.model.name)" }
    if ($state -ne $last) { "$(Stamp) [$([int]$sw.ElapsedMilliseconds) ms] $state"; $last = $state }
    if ($state -eq 'not connected') { $sawDisconnected = $true }
    if ($state -like 'connected, model*') { $sawConnected = $true; if ($sawTimeout) { $recovered = $true } }
    if ($state -eq 'ERROR tekla_timeout') { $sawTimeout = $true }
    if ($sawDisconnected -and $sawConnected -and $sawTimeout -and $recovered) { break }
    Start-Sleep -Seconds $IntervalSeconds
}
$stdin.Close(); $null = $p.WaitForExit(10000)
""
"RESULT reconnect (not connected -> connected): $(if ($sawDisconnected -and $sawConnected) { 'PASS' } else { 'NOT SEEN' })"
"RESULT timeout (tekla_timeout then recovered):  $(if ($sawTimeout -and $recovered) { 'PASS' } elseif ($sawTimeout) { 'TIMEOUT SEEN, NO RECOVERY' } else { 'NOT SEEN' })"
