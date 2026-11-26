# Phase 5 extra test (APPLY; run only with the user's explicit permission). Changes the model:
#   - runs tekla_run_numbering scope=modified (parts without marks get numbered; this stays until the model is closed without saving)
#   - creates a test steel beam MCP_TEST_W at x >= 100000 mm, its assembly and single-part drawings
#   - opens the assembly drawing, adds text, closes with save, reopens and checks the text is still there (spike probe6)
#   - deletes both drawings and the beam; checks the drawing list is back to the original
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
    [string]$Spike = (Join-Path $PSScriptRoot '..\..\spike\ApiSpike\bin\Release\net48\ApiSpike.exe'),
    [string]$ExpectModelPath = 'C:\TeklaStructuresModels\SSMC_Tekla'
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
function Apply($name, [hashtable]$a) {
    $a.mode = 'preview'; $pr = Tool $name $a
    if ($pr.isError) { $fail.Add("$name preview failed: $($pr.content[0].text)"); return $null }
    $a.mode = 'apply'; $a.confirm_token = $pr.structuredContent.confirm_token
    $r = Tool $name $a; $a.Remove('confirm_token')
    if ($r.isError) { $fail.Add("$name apply failed: $($r.content[0].text)"); return $null }
    if ($r.structuredContent.verification -and -not $r.structuredContent.verification.ok) { $fail.Add("$name read-back mismatch: $($r.structuredContent.verification.mismatches -join '; ')") }
    return $r.structuredContent
}
# The spike writes a status line to stderr; with ErrorActionPreference=Stop PowerShell 5 would turn it into an error.
function Texts { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; try { @(& (Resolve-Path $Spike).Path probe6 2>$null) } finally { $ErrorActionPreference = $old } }

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase5-extra'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Read 'tekla_status' @{}
if ($st.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.model.path)"; $in.Close(); exit 3 }
$list0 = Read 'tekla_list_drawings' @{ limit = 100 }
if ($list0.any_drawing_open) { "ABORT: a drawing is open in Tekla; close it first"; $in.Close(); exit 3 }
$before = @($list0.items | ForEach-Object { $_.drawing_id })
"drawings before: $($before.Count)"

$beam = $null; $drawings = New-Object Collections.Generic.List[string]
try {
    "--- test steel beam"
    $b = Apply 'tekla_create_beam' @{ start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_W' }
    $beam = $b.affected[0]

    "--- numbering (scope=modified)"
    $v0 = Read 'tekla_validate_model' @{ limit = 1 }
    $n = Apply 'tekla_run_numbering' @{ scope = 'modified' }
    $v1 = Read 'tekla_validate_model' @{ limit = 5 }
    "  before: $($v0.counts.numbering_not_up_to_date) not up to date; after: $($v1.counts.numbering_not_up_to_date) (no assembly mark $($v1.counts.no_assembly_mark)); up to date all: $($v1.numbering_up_to_date_all)"
    if (-not $v1.numbering_up_to_date_all -or $v1.total_issues -ne 0) { $fail.Add('numbering did not bring the model up to date') }
    $bw = (Read 'tekla_get_objects' @{ guids = @($beam) }).items[0]
    "  test beam marks: assembly $($bw.assembly_pos), part $($bw.part_pos)"
    if ($bw.assembly_pos -match '\?') { $fail.Add('test beam still unnumbered') }
    $leftover = @(Get-ChildItem "$env:ProgramData\Trimble\Tekla Structures\2026.0\environments\common\macros\modeling" -Filter 'TeklaMcp_*.cs' -ErrorAction SilentlyContinue)
    if ($leftover.Count) { $fail.Add("macro file left: $($leftover.Name -join ', ')") } else { '  temporary macro file removed' }

    "--- assembly and single-part drawings of the test beam"
    $a = Apply 'tekla_create_drawing' @{ kind = 'assembly'; part_guid = $beam }
    if ($a) { $drawings.AddRange([string[]]@($a.affected)); "  assembly: $($a.affected[0]) views on create" }
    $w = Apply 'tekla_create_drawing' @{ kind = 'single_part'; part_guid = $beam }
    if ($w) { $drawings.AddRange([string[]]@($w.affected)); "  single part: $($w.affected[0])" }
    foreach ($d in $drawings) { $g = Read 'tekla_get_drawing' @{ drawing_id = $d; limit = 5 }; "  $d -> $($g.model_object_count) object(s), $(@($g.views).Count) view(s): $(($g.views | ForEach-Object { "$($_.view_type) 1:$($_.scale)" }) -join ', ')" ; if ($g.model_object_count -lt 1) { $fail.Add("$d shows no model objects") } }

    "--- text survives save + reopen"
    if ($a) {
        $ad = $a.affected[0]
        $null = Read 'tekla_open_drawing' @{ drawing_id = $ad }
        $null = Apply 'tekla_add_drawing_text' @{ drawing_id = $ad; text = 'MCP_TEST_TEXT'; point_mm = @(50, 50) }
        $null = Apply 'tekla_close_drawing' @{ save = $true }
        $null = Read 'tekla_open_drawing' @{ drawing_id = $ad }
        $t = Texts
        "  texts after reopen: $(($t | Where-Object { $_ -like 'TEXT *' }) -join ' | ')"
        if (-not ($t | Where-Object { $_ -like 'TEXT MCP_TEST_TEXT*' })) { $fail.Add('text not found after save + reopen') } else { '  text found after save + reopen' }
        $null = Apply 'tekla_close_drawing' @{ save = $false }
    }
} finally {
    "--- cleanup"
    if ((Read 'tekla_list_drawings' @{ limit = 1 }).any_drawing_open) { $null = Apply 'tekla_close_drawing' @{ save = $false } }
    if ($drawings.Count) { $null = Apply 'tekla_delete_drawings' @{ drawing_ids = @($drawings) } }
    if ($beam) { $null = Apply 'tekla_delete_objects' @{ guids = @($beam) } }
    $after = @((Read 'tekla_list_drawings' @{ limit = 100 }).items | ForEach-Object { $_.drawing_id })
    if ((($after | Sort-Object) -join ';') -ne (($before | Sort-Object) -join ';')) { $fail.Add("drawing list differs after cleanup: $($after -join ' ; ')") } else { "  drawing list back to the original $($before.Count) drawing(s)" }
    $left = Read 'tekla_find_objects' @{ name = 'MCP_TEST*'; limit = 5 }
    if ($left.total -ne 0) { $fail.Add("$($left.total) MCP_TEST part(s) left") } else { '  no MCP_TEST parts left' }
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 5 EXTRA TEST PASSED"
