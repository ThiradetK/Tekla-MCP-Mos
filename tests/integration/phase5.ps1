# Phase 5 integration test: drawings + numbering against the TEST model.
# Default: read-only (list/get + previews of every write tool; nothing in the model or drawings changes).
# -Apply:  creates a test cast-unit drawing and a test GA drawing, modifies titles, issues/unissues, opens, adds text,
#          closes with save, updates, then deletes both drawings. Run only with the user's explicit permission.
# -Numbering: (with -Apply) also runs tekla_run_numbering scope=modified, which numbers the parts that are not numbered (marks change).
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [string]$ExpectModelPath = 'C:\TeklaStructuresModels\SSMC_Tekla',
    [switch]$Apply,
    [switch]$Numbering
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
    return $r.result
}
function Read($name, $a) { $r = Tool $name $a; if ($r.isError) { $fail.Add("$name failed: $($r.content[0].text)") }; return $r.structuredContent }
function Preview($name, [hashtable]$a, [switch]$ExpectError) {
    $a.mode = 'preview'; $r = Tool $name $a
    if ($ExpectError) { if (-not $r.isError) { $fail.Add("$name preview should fail") } else { "  -> refused: $($r.structuredContent.error.message)" } }
    elseif ($r.isError) { $fail.Add("$name preview failed: $($r.content[0].text)") }
    return $r.structuredContent
}
function Apply($name, [hashtable]$a) {
    $pr = Preview $name $a; if (-not $pr.confirm_token) { return $null }
    $a.mode = 'apply'; $a.confirm_token = $pr.confirm_token
    $r = Tool $name $a; $a.Remove('confirm_token')
    if ($r.isError) { $fail.Add("$name apply failed: $($r.content[0].text)"); return $null }
    if ($r.structuredContent.verification -and -not $r.structuredContent.verification.ok) { $fail.Add("$name read-back mismatch: $($r.structuredContent.verification.mismatches -join '; ')") }
    return $r.structuredContent
}

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase5-test'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Read 'tekla_status' @{}
if ($st.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.model.path)"; $in.Close(); exit 3 }
"MODE: " + $(if ($Apply) { 'APPLY' + $(if ($Numbering) { ' + NUMBERING' } else { '' }) } else { 'READ-ONLY (previews only)' })

"--- list / get"
$all = Read 'tekla_list_drawings' @{ limit = 100 }
$before = @($all.items | ForEach-Object { $_.drawing_id })
"  drawings: $($all.total) -> $($before -join ' ; ')"
if ($all.any_drawing_open) { "ABORT: a drawing is open in Tekla; close it first"; $in.Close(); exit 3 }
$cu = Read 'tekla_list_drawings' @{ types = @('cast_unit') }
if (@($all.items | Where-Object { $_.type -eq 'cast_unit' }).Count -ne $cu.total) { $fail.Add('types filter mismatch') }
$none = Read 'tekla_list_drawings' @{ mark = 'NO_SUCH_MARK*' }
if ($none.total -ne 0) { $fail.Add('mark filter not applied') }
$ga = $all.items | Where-Object { $_.type -eq 'ga' } | Select-Object -First 1
if ($ga) {
    $g = Read 'tekla_get_drawing' @{ drawing_id = $ga.drawing_id; limit = 3 }
    "  GA $($g.mark): $($g.model_object_count) model objects, views: $(@($g.views).Count) $(if ($g.views_error) { "(views_error: $($g.views_error))" }) first: $($g.views[0].name) scale $($g.views[0].scale)"
    if ($g.model_object_count -lt 1 -or @($g.model_objects).Count -gt 3) { $fail.Add('get_drawing objects/paging wrong') }
}
$bad = Tool 'tekla_get_drawing' @{ drawing_id = 'ga|NOPE|20000101000000' }
if (-not $bad.isError) { $fail.Add('unknown drawing_id accepted') } else { '  unknown drawing_id -> refused' }

"--- previews"
$num = Preview 'tekla_run_numbering' @{}
"  numbering: $($num.parts_not_up_to_date) of $($num.parts) parts not up to date; macro folder $($num.macro_folder)"
$parts = (Read 'tekla_find_objects' @{ limit = 500 }).items
$unnumbered = $parts | Where-Object { $_.assembly_pos -match '\?' } | Select-Object -First 1
$numbered = $parts | Where-Object { $_.assembly_pos -notmatch '\?' -and $_.material -like 'C*' } | Select-Object -First 1
if ($unnumbered) { $null = Preview 'tekla_create_drawing' @{ kind = 'cast_unit'; part_guid = $unnumbered.guid } -ExpectError }
$null = Preview 'tekla_create_drawing' @{ kind = 'cast_unit'; part_guid = $numbered.guid }
$null = Preview 'tekla_create_drawing' @{ kind = 'ga' } -ExpectError
$null = Preview 'tekla_create_drawing' @{ kind = 'ga'; name = 'MCP_TEST_GA' }
$null = Preview 'tekla_update_drawings' @{ drawing_ids = @($before[0]) }
$null = Preview 'tekla_issue_drawings' @{ drawing_ids = @($before[0]) }
$mp = Preview 'tekla_modify_drawings' @{ drawing_ids = @($before[0]); set = @{ title1 = 'MCP test' } }
"  modify preview: title1 '$($mp.changes[0].title1.from)' -> '$($mp.changes[0].title1.to)'"
$null = Preview 'tekla_modify_drawings' @{ drawing_ids = @($before[0]); set = @{ mark = 'X' } } -ExpectError
$dp = Preview 'tekla_delete_drawings' @{ drawing_ids = @($before[0]) }
if (@($dp.objects).Count -ne 1) { $fail.Add('delete preview does not list the drawing') }
$null = Preview 'tekla_close_drawing' @{} -ExpectError
$null = Preview 'tekla_add_drawing_text' @{ drawing_id = $before[0]; text = 'x'; point_mm = @(10, 10) } -ExpectError

if ($Apply) {
    if ($Numbering) {
        "--- numbering"
        $n = Apply 'tekla_run_numbering' @{ scope = 'modified' }
        "  after: up to date=$($n.numbering_up_to_date_all), parts not up to date=$($n.parts_not_up_to_date)"
    }
    $created = New-Object Collections.Generic.List[string]
    try {
        "--- create / modify / issue / open / text / close / update"
        $c = Apply 'tekla_create_drawing' @{ kind = 'cast_unit'; part_guid = $numbered.guid }
        if ($c) { $created.AddRange([string[]]@($c.affected)) }
        $gaNew = Apply 'tekla_create_drawing' @{ kind = 'ga'; name = 'MCP_TEST_GA' }
        if ($gaNew) { $created.AddRange([string[]]@($gaNew.affected)) }
        if ($c -and $c.affected.Count -eq 1) {
            $d = $c.affected[0]
            $m = Apply 'tekla_modify_drawings' @{ drawing_ids = @($d); set = @{ title1 = 'MCP_TEST'; title2 = 'phase5' } }
            $null = Apply 'tekla_issue_drawings' @{ drawing_ids = @($d); issue = $true }
            $null = Apply 'tekla_issue_drawings' @{ drawing_ids = @($d); issue = $false }
            $o = Read 'tekla_open_drawing' @{ drawing_id = $d }
            if (-not $o.is_open) { $fail.Add('drawing not open after open') }
            $t = Apply 'tekla_add_drawing_text' @{ drawing_id = $d; text = 'MCP_TEST_TEXT'; point_mm = @(50, 50) }
            $null = Apply 'tekla_close_drawing' @{ save = $true }
            $null = Apply 'tekla_update_drawings' @{ drawing_ids = @($d) }
        }
    } finally {
        "--- cleanup"
        $open = Read 'tekla_list_drawings' @{ limit = 1 }
        if ($open.any_drawing_open) { $null = Apply 'tekla_close_drawing' @{ save = $false } }
        if ($created.Count) { $null = Apply 'tekla_delete_drawings' @{ drawing_ids = @($created) } }
        $after = @((Read 'tekla_list_drawings' @{ limit = 100 }).items | ForEach-Object { $_.drawing_id })
        if ((($after | Sort-Object) -join ';') -ne (($before | Sort-Object) -join ';')) { $fail.Add("drawing list differs after cleanup: $($after -join ' ; ')") } else { "  drawing list back to the original $($before.Count) drawing(s)" }
    }
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 5 " + $(if ($Apply) { 'APPLY' } else { 'READ-ONLY' }) + " TEST PASSED"
