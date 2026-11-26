# Numbering test (APPLY; run only with the user's explicit permission). -Scope full (default) renumbers every part of the
# open TEST model; -Scope modified numbers only new/changed parts. Checks Tekla's numbering log, records marks and drawing
# status before/after. Nothing is created or deleted; the model is not saved.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [string]$ExpectModelPath = 'C:\TeklaStructuresModels\SSMC_Tekla',
    [ValidateSet('full', 'modified')][string]$Scope = 'full'
)
$ErrorActionPreference = 'Stop'
$psi = New-Object Diagnostics.ProcessStartInfo (Resolve-Path $Exe).Path
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$p = [Diagnostics.Process]::Start($psi); $null = $p.StandardError.ReadToEndAsync()
$in = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding($false))); $in.NewLine = "`n"; $in.AutoFlush = $true
$script:id = 0; $fail = New-Object Collections.Generic.List[string]
function Rpc($method, $params) {
    $script:id++; $in.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 30 -Compress))
    while ($true) { $m = $p.StandardOutput.ReadLine() | ConvertFrom-Json; if ($m.id -eq $script:id) { return $m } }
}
function Tool($name, $arguments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    Write-Host ("{0,-24} {1,-8} {2,6} ms  {3}" -f $name, $arguments.mode, $sw.ElapsedMilliseconds, ($r.result.content[0].text -split "`n")[0])
    if ($r.result.isError) { $fail.Add("$name failed: $($r.result.content[0].text)") }
    return $r.result.structuredContent
}
function Marks {
    $map = @{}; $total = (Tool 'tekla_find_objects' @{ limit = 1 }).total
    for ($o = 0; $o -lt $total; $o += 500) { foreach ($x in (Tool 'tekla_find_objects' @{ limit = 500; offset = $o }).items) { $map[$x.guid] = "$($x.assembly_pos)/$($x.part_pos)" } }
    return $map
}

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'numbering-full'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Tool 'tekla_status' @{}
if ($st.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.model.path)"; $in.Close(); exit 3 }

"--- before"
$m0 = Marks
$d0 = (Tool 'tekla_list_drawings' @{ limit = 100 }).items
$d0 | ForEach-Object { "  drawing $($_.drawing_id): $($_.up_to_date_status)" }

"--- numbering ($Scope)"
$pr = Tool 'tekla_run_numbering' @{ mode = 'preview'; scope = $Scope }
"  preview warnings: $($pr.warnings -join ' | ')"
$ap = Tool 'tekla_run_numbering' @{ mode = 'apply'; scope = $Scope; confirm_token = $pr.confirm_token }
if ($ap -and -not $ap.verification.ok) { $fail.Add("read-back mismatch: $($ap.verification.mismatches -join '; ')") }
"  Tekla numbering log: $($ap.tekla_numbering_log -join ' | ')"
if ($Scope -eq 'full' -and -not ($ap.tekla_numbering_log | Where-Object { $_ -match 'Diagnose & Repair Numbering: All' })) { $fail.Add('Tekla log does not show a full (Diagnose & Repair: All) numbering run') }
$leftover = @(Get-ChildItem "$env:ProgramData\Trimble\Tekla Structures\2026.0\environments\common\macros\modeling" -Filter 'TeklaMcp_*.cs' -ErrorAction SilentlyContinue)
if ($leftover.Count) { $fail.Add("macro file left: $($leftover.Name -join ', ')") } else { '  temporary macro file removed' }

"--- after"
$v = Tool 'tekla_validate_model' @{ limit = 5 }
if (-not $v.numbering_up_to_date_all -or $v.total_issues -ne 0) { $fail.Add('model not fully numbered after full numbering') }
$m1 = Marks
$changed = @($m0.Keys | Where-Object { $m1[$_] -ne $m0[$_] })
"  parts: $($m0.Count); marks changed: $($changed.Count)"
$changed | Select-Object -First 8 | ForEach-Object { "    $_ : $($m0[$_]) -> $($m1[$_])" }
$d1 = (Tool 'tekla_list_drawings' @{ limit = 100 }).items
$d1 | ForEach-Object { "  drawing $($_.drawing_id): $($_.up_to_date_status)" }
if ($d1.Count -ne $d0.Count) { $fail.Add("drawing count changed $($d0.Count) -> $($d1.Count)") }

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"NUMBERING ($Scope) TEST PASSED"

