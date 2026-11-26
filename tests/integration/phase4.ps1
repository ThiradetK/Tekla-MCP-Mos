# Phase 4 integration test: tekla_validate_model and tekla_clash_check against the TEST model.
# Default: read-only (the model is not changed; the Tekla selection is replaced during clash checks and restored).
# -Apply:  also creates two overlapping test beams at x >= 100000 mm, checks that the clash is found, then deletes them.
#          Run -Apply only with the user's explicit permission.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [string]$ExpectModelPath = 'C:\TeklaStructuresModels\SSMC_Tekla',
    [switch]$Apply
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
    if ($r.result.isError -and -not $arguments.mode) { $fail.Add("$name failed: $($r.result.content[0].text)") }
    return $r.result
}
function Apply($name, [hashtable]$a) {
    $a.mode = 'preview'; $pr = Tool $name $a
    if ($pr.isError) { $fail.Add("$name preview failed"); return $null }
    $a.mode = 'apply'; $a.confirm_token = $pr.structuredContent.confirm_token
    $ap = Tool $name $a
    if ($ap.isError) { $fail.Add("$name apply failed: $($ap.content[0].text)"); return $null }
    return $ap.structuredContent
}
function SelectionGuids { @((Tool 'tekla_get_selection' @{ limit = 500 }).structuredContent.items | ForEach-Object { $_.guid } | Sort-Object) -join ',' }

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase4-test'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Tool 'tekla_status' @{}
if ($st.structuredContent.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.structuredContent.model.path)"; $in.Close(); exit 3 }
"MODE: " + $(if ($Apply) { 'APPLY (two test beams are created and deleted)' } else { 'READ-ONLY' })

"--- validate"
$v = (Tool 'tekla_validate_model' @{ limit = 5 }).structuredContent
$parts = (Tool 'tekla_find_objects' @{ limit = 1 }).structuredContent.total
if ($v.checked_parts -ne $parts) { $fail.Add("validate checked $($v.checked_parts) parts, find_objects reports $parts") }
$q = 0; for ($o = 0; $o -lt $parts; $o += 500) { $q += @((Tool 'tekla_find_objects' @{ limit = 500; offset = $o }).structuredContent.items | Where-Object { $_.assembly_pos -match '\?' -or -not $_.assembly_pos }).Count }
if ($v.counts.no_assembly_mark -ne $q) { $fail.Add("no_assembly_mark $($v.counts.no_assembly_mark) != $q parts with '(?)' assembly_pos") } else { "  no_assembly_mark matches find_objects ($q)" }
if ($v.total_issues -gt 0 -and $v.numbering_up_to_date_all) { $fail.Add('issues found but numbering_up_to_date_all is true') }
if ($v.items.Count -gt 5) { $fail.Add('limit not applied') }
$vf = (Tool 'tekla_validate_model' @{ name = 'NO_SUCH_NAME_*' }).structuredContent
if ($vf.checked_parts -ne 0) { $fail.Add('filter not applied') }

"--- clash check (read-only)"
$sel0 = SelectionGuids
$c = (Tool 'tekla_clash_check' @{ limit = 5 }).structuredContent
if (-not $c.finished) { $fail.Add('clash check over all parts did not finish') }
if ($c.checked_objects -ne $parts) { $fail.Add("clash checked $($c.checked_objects) objects, expected $parts") }
"  all parts: $($c.clash_count) clash(es), reported by Tekla $($c.reported_by_tekla), by type: $($c.by_type | ConvertTo-Json -Compress)"
if ((SelectionGuids) -ne $sel0) { $fail.Add('selection was not restored after clash check') } else { '  selection restored' }
$two = @((Tool 'tekla_find_objects' @{ limit = 4 }).structuredContent.items | ForEach-Object { $_.guid })
$null = Tool 'tekla_select_objects' @{ guids = @($two[0], $two[1]) }
$selA = SelectionGuids
$null = Tool 'tekla_clash_check' @{ guids = @($two[2], $two[3]) }
if ((SelectionGuids) -ne $selA -or -not $selA) { $fail.Add('non-empty selection was not restored') } else { '  non-empty selection (2 objects) restored' }
# Put the user's selection back (tekla_select_objects cannot select nothing, so an empty selection stays as the 2 test objects).
if ($sel0) { $null = Tool 'tekla_select_objects' @{ guids = @($sel0 -split ',') } }
$bad = Tool 'tekla_clash_check' @{ guids = @('00000000-0000-0000-0000-000000000000') }
if (-not $bad.isError) { $fail.Add('unknown guid accepted') } else { $fail.RemoveAt($fail.Count - 1); '  unknown guid -> refused' }

if ($Apply) {
    "--- clash check with a known clash (two overlapping test beams)"
    $a1 = Apply 'tekla_create_beam' @{ start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_CLASH1' }
    $a2 = Apply 'tekla_create_beam' @{ start_mm = @(103000, -3000, 0); end_mm = @(103000, 3000, 0); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_CLASH2' }
    $g = @($a1.affected[0], $a2.affected[0]) | Where-Object { $_ }
    try {
    if ($g.Count -eq 2) {
        $k = (Tool 'tekla_clash_check' @{ guids = $g }).structuredContent
        "  clashes: $($k.clash_count); first: $($k.items[0].type) tekla_overlap=$($k.items[0].tekla_overlap) $($k.items[0].object1.name) x $($k.items[0].object2.name)"
        if (@($k.items[0].object1.name, $k.items[0].object2.name | Where-Object { $_ -like 'MCP_TEST_CLASH*' }).Count -ne 2) { $fail.Add('clash object names not read back') }
        if ($k.clash_count -lt 1) { $fail.Add('known clash not found') }
        elseif (@($k.items[0].object1.guid, $k.items[0].object2.guid | Where-Object { $g -contains $_ }).Count -ne 2) { $fail.Add('clash objects are not the two test beams') }
        $mt = (Tool 'tekla_validate_model' @{ name = 'MCP_TEST_CLASH*' }).structuredContent
        "  validate on new beams: $($mt.total_issues) issue(s) (new parts are not numbered yet)"
    }
    } finally {
    if ($g.Count) {
        $null = Apply 'tekla_delete_objects' @{ guids = @($g) }
        $left = (Tool 'tekla_find_objects' @{ name = 'MCP_TEST*'; limit = 10 }).structuredContent
        if ($left.total -ne 0) { $fail.Add("$($left.total) MCP_TEST part(s) left") } else { '  cleanup check: no MCP_TEST parts left' }
        $del = (Tool 'tekla_get_objects' @{ guids = @($g) }).structuredContent
        if (@($del.not_found).Count -ne $g.Count) { $fail.Add('deleted beams still found by GUID') }
    }
    }
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 4 " + $(if ($Apply) { 'APPLY' } else { 'READ-ONLY' }) + " TEST PASSED"



