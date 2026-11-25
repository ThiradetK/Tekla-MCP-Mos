# Stdio smoke test: starts TeklaMcp.exe, runs the MCP handshake and calls tekla_status (read-only).
# Checks that every stdout line is a JSON-RPC message (no Tekla noise on stdout).
# Usage: powershell -File tests\integration\smoke.ps1 [-Exe path] [-ExpectModelPath C:\TeklaStructuresModels\X]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [string]$ExpectModelPath = ''
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path

$psi = New-Object Diagnostics.ProcessStartInfo $Exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$p = [Diagnostics.Process]::Start($psi)
$stderrTask = $p.StandardError.ReadToEndAsync()
$stdin = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding($false)))
$stdin.NewLine = "`n"; $stdin.AutoFlush = $true

$failures = New-Object Collections.Generic.List[string]
$allLines = New-Object Collections.Generic.List[string]
function Send($obj) { $stdin.WriteLine(($obj | ConvertTo-Json -Depth 20 -Compress)) }
function Receive([int]$id) {
    while ($true) {
        $line = $p.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "stdout closed while waiting for id $id" }
        $allLines.Add($line)
        try { $m = $line | ConvertFrom-Json } catch { $failures.Add("non-JSON stdout line: $line"); continue }
        if ($m.jsonrpc -ne '2.0') { $failures.Add("not JSON-RPC: $line"); continue }
        if ($m.PSObject.Properties.Name -contains 'id' -and $m.id -eq $id) { return $m }
    }
}

Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke'; version = '1' } } }
$init = Receive 1
"initialize: protocol=$($init.result.protocolVersion) server=$($init.result.serverInfo.name) $($init.result.serverInfo.version)"
if ($init.result.protocolVersion -ne '2025-06-18') { $failures.Add('protocol version not echoed') }
Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
$list = Receive 2
$names = @($list.result.tools | ForEach-Object { $_.name })
"tools/list: $($names -join ', ')"
if ($names -notcontains 'tekla_status') { $failures.Add('tekla_status missing') }

Send @{ jsonrpc = '2.0'; id = 3; method = 'ping' }
$null = Receive 3; 'ping: ok'

$sw = [Diagnostics.Stopwatch]::StartNew()
Send @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'tekla_status'; arguments = @{} } }
$st = Receive 4
"tekla_status ($([int]$sw.ElapsedMilliseconds) ms): isError=$($st.result.isError)"
$sc = $st.result.structuredContent
"  " + ($st.result.content[0].text -split "`n")[0]
if ($st.result.isError) { $failures.Add('tekla_status returned isError') }
if ($ExpectModelPath) {
    if (-not $sc.connected) { $failures.Add('not connected') }
    elseif ($sc.model.path.TrimEnd('\') -ne $ExpectModelPath.TrimEnd('\')) { $failures.Add("model path $($sc.model.path) != $ExpectModelPath") }
}

Send @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'tekla_status'; arguments = @{ unexpected = 1 } } }
$bad = Receive 5
"invalid args -> isError=$($bad.result.isError) code=$($bad.result.structuredContent.error.code)"
if (-not $bad.result.isError) { $failures.Add('invalid args accepted') }

$stdin.Close()
if (-not $p.WaitForExit(15000)) { $failures.Add('server did not exit after stdin closed'); $p.Kill() }
"exit code: $($p.ExitCode)"
# Drain any trailing stdout and check it too.
while ($null -ne ($l = $p.StandardOutput.ReadLine())) { $allLines.Add($l); try { $null = $l | ConvertFrom-Json } catch { $failures.Add("non-JSON stdout line: $l") } }
"stdout lines: $($allLines.Count)"
$err = $stderrTask.Result
"stderr lines: $(($err -split "`n").Count) (Tekla noise lines: $((($err -split "`n") | Where-Object { $_ -match 'Connection failed' }).Count))"

if ($failures.Count -gt 0) { "FAILED:"; $failures | ForEach-Object { "  - $_" }; exit 1 }
"SMOKE TEST PASSED"
