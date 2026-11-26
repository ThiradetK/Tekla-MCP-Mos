# Phase 6 extra test (user asked 2026-11-26): GA drawing to PDF, and 5 drawings in one tekla_export_drawings call.
# Creates 2 temporary cast-unit drawings (to have 5) and deletes them at the end. Export folders are removed afterwards.
# Optional -MultiDrawingId: also export a multidrawing created beforehand in the UI.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [string]$ExpectModelPath = 'C:\TeklaStructuresModels\SSMC_Tekla',
    [string]$MultiDrawingId = '',
    [switch]$KeepFiles
)
$ErrorActionPreference = 'Stop'
$psi = New-Object Diagnostics.ProcessStartInfo (Resolve-Path $Exe).Path
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$p = [Diagnostics.Process]::Start($psi); $null = $p.StandardError.ReadToEndAsync()
$in = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding($false))); $in.NewLine = "`n"; $in.AutoFlush = $true
$script:id = 0; $fail = New-Object Collections.Generic.List[string]; $folders = New-Object Collections.Generic.List[string]
function Rpc($method, $params) {
    $script:id++; $in.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 30 -Compress))
    while ($true) { $m = $p.StandardOutput.ReadLine() | ConvertFrom-Json; if ($m.id -eq $script:id) { return $m } }
}
function Tool($name, $arguments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    Write-Host ("{0,-24} {1,-8} {2,6} ms  {3}" -f $name, $arguments.mode, $sw.ElapsedMilliseconds, ($r.result.content[0].text -split "`n")[0])
    if ($r.result.structuredContent.folder) { $folders.Add($r.result.structuredContent.folder) }
    return $r.result
}
function Read($name, $a) { $r = Tool $name $a; if ($r.isError) { $fail.Add("$name failed: $($r.content[0].text)") }; return $r.structuredContent }
function Apply($name, [hashtable]$a) {
    $a.mode = 'preview'; $pr = Tool $name $a
    if ($pr.isError) { $fail.Add("$name preview failed: $($pr.content[0].text)"); return $null }
    $a.mode = 'apply'; $a.confirm_token = $pr.structuredContent.confirm_token
    $r = Tool $name $a; $a.Remove('confirm_token')
    if ($r.isError) { $fail.Add("$name apply failed: $($r.content[0].text)"); return $null }
    if ($r.structuredContent.verification -and -not $r.structuredContent.verification.ok) { $fail.Add("$name read-back mismatch: $($r.structuredContent.verification.mismatches -join '; ')") }
    return $r.structuredContent
}
function Check($res, [int]$expected, [string]$label) {
    "  $label -> $(($res.files | ForEach-Object { "$($_.name) ($($_.size_bytes) B)" }) -join ', ') in $([int]($res.elapsed_ms/1000)) s"
    if (-not $res.ok -or $res.file_count -ne $expected) { $fail.Add("$label : ok=$($res.ok) files=$($res.file_count)/$expected; $(($res.drawings | ForEach-Object { "$($_.mark): $($_.error) $($_.macro_steps_failed -join ' | ')" }) -join ' ; ')") }
}

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase6-extra'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Read 'tekla_status' @{}
if ($st.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.model.path)"; $in.Close(); exit 3 }
$tag = 'mcptest_' + (Get-Date -Format 'yyyyMMdd_HHmmss')
$all = (Read 'tekla_list_drawings' @{ limit = 100 }).items
$before = @($all | ForEach-Object { $_.drawing_id })
$created = New-Object Collections.Generic.List[string]

try {
    "--- GA drawing to PDF"
    $ga = $all | Where-Object { $_.type -eq 'ga' } | Select-Object -First 1
    Check (Read 'tekla_export_drawings' @{ folder_name = "$($tag)_ga"; drawing_ids = @($ga.drawing_id); format = 'pdf' }) 1 "GA $($ga.mark) pdf"

    if ($MultiDrawingId) {
        "--- multidrawing to PDF"
        Check (Read 'tekla_export_drawings' @{ folder_name = "$($tag)_multi"; drawing_ids = @($MultiDrawingId); format = 'pdf' }) 1 "multidrawing pdf"
    }

    "--- 5 drawings in one call (2 temporary cast-unit drawings)"
    $used = @($all | ForEach-Object { $_.mark })
    $parts = (Read 'tekla_find_objects' @{ limit = 500; material = 'C*' }).items
    $cand = $parts | Where-Object { $_.assembly_pos -and $_.assembly_pos -notmatch '\?' } | Group-Object assembly_pos | Where-Object { $m = $_.Name; -not ($used | Where-Object { $_ -like "*$m*" }) } | Select-Object -First 2
    foreach ($g in $cand) {
        $c = Apply 'tekla_create_drawing' @{ kind = 'cast_unit'; part_guid = $g.Group[0].guid }
        if ($c) { $created.AddRange([string[]]@($c.affected)) }
    }
    $five = @($before) + @($created) | Select-Object -First 5
    "  drawings: $($five -join ' ; ')"
    if ($five.Count -ne 5) { $fail.Add("only $($five.Count) drawings available") }
    Check (Read 'tekla_export_drawings' @{ folder_name = "$($tag)_five"; drawing_ids = @($five); format = 'pdf' }) 5 "5 drawings pdf"
    $over = Tool 'tekla_export_drawings' @{ folder_name = "$($tag)_six"; drawing_ids = @($five + 'x|y|z') }
    if (-not $over.isError) { $fail.Add('6 drawings accepted') } else { '  6 drawings -> refused' }
} finally {
    "--- cleanup"
    if ($created.Count) { $null = Apply 'tekla_delete_drawings' @{ drawing_ids = @($created) } }
    $after = @((Read 'tekla_list_drawings' @{ limit = 100 }).items | ForEach-Object { $_.drawing_id })
    $expect = @($before)
    if ((($after | Sort-Object) -join ';') -ne (($expect | Sort-Object) -join ';')) { $fail.Add("drawing list differs after cleanup: $($after -join ' ; ')") } else { "  drawing list back to the original $($expect.Count) drawing(s)" }
    if (-not $KeepFiles) { foreach ($f in ($folders | Select-Object -Unique)) { if ((Test-Path $f) -and ($f -like "*mcptest_*")) { Remove-Item $f -Recurse -Force; "  removed $f" } } }
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 6 EXTRA TEST PASSED"
