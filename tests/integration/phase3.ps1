# Phase 3 integration test for write tools against the TEST model copy.
# Default: preview-only (nothing in the model changes) + negative safety checks.
# -Apply:  creates test objects far from the model (x >= 100000 mm), modifies/moves/copies them, toggles the
#          work plane, then deletes everything it created. Run -Apply only with the user's explicit permission.
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
$script:id = 0; $fail = New-Object Collections.Generic.List[string]; $created = New-Object Collections.Generic.List[string]
function Rpc($method, $params) {
    $script:id++; $in.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 30 -Compress))
    while ($true) { $m = $p.StandardOutput.ReadLine() | ConvertFrom-Json; if ($m.id -eq $script:id) { return $m } }
}
function Tool($name, $arguments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    $line = ($r.result.content[0].text -split "`n")[0]
    Write-Host ("{0,-24} {1,-8} {2,6} ms  {3}" -f $name, $arguments.mode, $sw.ElapsedMilliseconds, $line)
    return $r.result
}
# preview -> (optional) apply with the token. Returns the apply (or preview) structured result.
function Step($name, [hashtable]$a, [switch]$ExpectError) {
    $a.mode = 'preview'
    $pr = Tool $name $a
    if ($ExpectError) { if (-not $pr.isError) { $fail.Add("$name preview should fail") }; return $pr.structuredContent }
    if ($pr.isError) { $fail.Add("$name preview failed: $($pr.content[0].text)"); return $null }
    if (-not $Apply) { return $pr.structuredContent }
    $a.mode = 'apply'; $a.confirm_token = $pr.structuredContent.confirm_token
    $ap = Tool $name $a
    $a.Remove('confirm_token')
    if ($ap.isError) { $fail.Add("$name apply failed: $($ap.content[0].text)"); return $null }
    $sc = $ap.structuredContent
    if ($sc.verification -and -not $sc.verification.ok) { $fail.Add("$name read-back mismatch: $($sc.verification.mismatches -join '; ')") }
    return $sc
}
function Track($sc) { if ($sc -and $sc.affected) { foreach ($g in $sc.affected) { if (-not $created.Contains($g)) { $created.Add($g) } } } }

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase3-test'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Tool 'tekla_status' @{}
if ($st.structuredContent.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.structuredContent.model.path)"; $in.Close(); exit 3 }
"MODE: " + $(if ($Apply) { 'APPLY (model will change, test objects are deleted at the end)' } else { 'PREVIEW ONLY' })

"--- safety checks"
$neg = Rpc 'tools/call' @{ name = 'tekla_create_beam'; arguments = @{ mode = 'apply'; start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'WF400X200X8X13'; material = 'SS400' } }
if ($neg.result.structuredContent.error.code -ne 'confirm_token_missing') { $fail.Add('apply without token was not refused') } else { '  apply without token -> refused' }
$pv = Tool 'tekla_create_beam' @{ mode = 'preview'; start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'WF400X200X8X13'; material = 'SS400' }
$chg = Rpc 'tools/call' @{ name = 'tekla_create_beam'; arguments = @{ mode = 'apply'; confirm_token = $pv.structuredContent.confirm_token; start_mm = @(100000, 0, 0); end_mm = @(107000, 0, 0); profile = 'WF400X200X8X13'; material = 'SS400' } }
if ($chg.result.structuredContent.error.code -ne 'confirm_token_arguments_changed') { $fail.Add('apply with changed args was not refused') } else { '  apply with changed arguments -> refused' }
$null = Step 'tekla_create_beam' @{ start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'HEA300'; material = 'SS400' } -ExpectError
$null = Step 'tekla_create_beam' @{ start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'WF400X200X8X13'; material = 'NOPE' } -ExpectError

"--- create"
$beam = Step 'tekla_create_beam' @{ start_mm = @(100000, 0, 3000); end_mm = @(106000, 0, 3000); profile = 'wf400x200x8x13'; material = 'ss400'; name = 'MCP_TEST_BEAM'; class = '3'; position = @{ depth = 'middle'; rotation = 'top' } }
Track $beam; $beamGuid = if ($Apply) { $beam.affected[0] } else { $null }
$col = Step 'tekla_create_column' @{ base_mm = @(100000, 0, 0); height_mm = 3000; profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_COLUMN'; class = '2' }
Track $col; $colGuid = if ($Apply) { $col.affected[0] } else { $null }
$plate = Step 'tekla_create_plate' @{ contour_mm = @(@(100000, -300, 3500), @(100600, -300, 3500), @(100600, 300, 3500), @(100000, 300, 3500)); profile = 'PL20'; material = 'SS400'; name = 'MCP_TEST_PLATE' }
Track $plate; $plateGuid = if ($Apply) { $plate.affected[0] } else { $null }
$conc = Step 'tekla_create_beam' @{ start_mm = @(110000, 0, 0); end_mm = @(116000, 0, 0); profile = '500*250'; material = 'C40'; name = 'MCP_TEST_RCBEAM' }
Track $conc; $concGuid = if ($Apply) { $conc.affected[0] } else { $null }

if ($Apply) {
    Track (Step 'tekla_create_bolts' @{ kind = 'array'; part_to_bolt_to_guid = $colGuid; part_to_be_bolted_guid = $plateGuid; first_point_mm = @(100100, 0, 3500); second_point_mm = @(100500, 0, 3500); bolt_standard = '8.8XOX'; bolt_size_mm = 20; dist_x_mm = @(100, 100); dist_y_mm = @(150) })
    Track (Step 'tekla_create_weld' @{ main_part_guid = $colGuid; secondary_part_guid = $plateGuid; size_above_mm = 6; type_above = 'fillet' })
    Track (Step 'tekla_create_rebar' @{ kind = 'single'; father_part_guid = $concGuid; points_mm = @(@(110050, 0, -450), @(115950, 0, -450)); grade = 'SD40'; size = '16'; name = 'MCP_TEST_BAR' })
    Track (Step 'tekla_create_rebar' @{ kind = 'group'; father_part_guid = $concGuid; points_mm = @(@(110050, -80, -450), @(110050, -80, -50)); start_mm = @(110050, -80, -450); end_mm = @(115950, -80, -450); spacing_mm = 200; grade = 'SR24'; size = '9'; name = 'MCP_TEST_STIRRUP' })
    Track (Step 'tekla_create_grid' @{ coordinate_x = '0 2*6000'; coordinate_y = '0 6000'; coordinate_z = '0 3000'; label_x = 'T1 T2 T3'; label_y = 'TA TB'; label_z = '+0 +3000'; origin_mm = @(100000, 20000, 0); name = 'MCP_TEST_GRID' })
    # The first plate is shop-welded to the column, so it belongs to the column's assembly: must be refused.
    $null = Step 'tekla_create_assembly' @{ main_part_guid = $beamGuid; secondary_part_guids = @($plateGuid) } -ExpectError
    $plate2 = Step 'tekla_create_plate' @{ contour_mm = @(@(105000, -150, 3200), @(105300, -150, 3200), @(105300, 150, 3200), @(105000, 150, 3200)); profile = 'PL12'; material = 'SS400'; name = 'MCP_TEST_PLATE2' }
    Track $plate2; $plate2Guid = $plate2.affected[0]
    $null = Step 'tekla_create_assembly' @{ main_part_guid = $beamGuid; secondary_part_guids = @($plate2Guid); name = 'MCP_TEST_ASM' }
    $asm = (Tool 'tekla_get_assembly' @{ guid = $beamGuid }).structuredContent
    if (@($asm.secondaries | Where-Object { $_.guid -eq $plate2Guid }).Count -ne 1) { $fail.Add('plate2 is not a secondary of the beam assembly') } else { '  assembly check: plate2 is secondary of beam assembly' }

    "--- modify / move / copy"
    $mod = Step 'tekla_modify_objects' @{ guids = @($beamGuid); set = @{ name = 'MCP_TEST_BEAM_MOD'; class = '5'; profile = 'WF400X200X8X13'; uda = @(@{ name = 'USER_FIELD_1'; value = 'mcp-test' }) } }
    $mv = Step 'tekla_move_objects' @{ guids = @($colGuid); vector_mm = @(0, 0, 100) }
    $cp = Step 'tekla_copy_objects' @{ guids = @($concGuid); vector_mm = @(0, 2000, 0); count = 2 }
    Track $cp
    "--- work plane"
    $null = Step 'tekla_set_workplane' @{ origin_mm = @(100000, 0, 0); axis_x = @(1, 0, 0); axis_y = @(0, 1, 0) }
    $wp = (Tool 'tekla_get_workplane' @{}).structuredContent
    if ($wp.is_global) { $fail.Add('work plane should be local after set') }
    $null = Step 'tekla_set_workplane' @{ global = $true }

    "--- cleanup (delete everything created: $($created.Count) object(s))"
    $del = Step 'tekla_delete_objects' @{ guids = @($created) }
    $left = (Tool 'tekla_find_objects' @{ types = @('part'); name = 'MCP_TEST*'; limit = 50 }).structuredContent
    if ($left.total -ne 0) { $fail.Add("$($left.total) MCP_TEST part(s) left after cleanup") } else { '  cleanup check: no MCP_TEST parts left' }
} else {
    "--- previews needing existing objects use the first two beams of the model"
    $two = (Tool 'tekla_find_objects' @{ types = @('beam'); limit = 2 }).structuredContent.items
    $g = @($two | ForEach-Object { $_.guid })
    $null = Step 'tekla_modify_objects' @{ guids = $g; set = @{ name = 'X'; class = '9' } }
    $null = Step 'tekla_move_objects' @{ guids = $g; vector_mm = @(0, 0, 10) }
    $null = Step 'tekla_copy_objects' @{ guids = $g; vector_mm = @(0, 1000, 0); count = 2 }
    $d = Step 'tekla_delete_objects' @{ guids = @($g[0]) }
    "  delete preview lists $($d.objects.Count) object(s) + $($d.attached_objects_may_be_deleted.Count) attached object(s)"
    $null = Step 'tekla_create_bolts' @{ kind = 'array'; part_to_bolt_to_guid = $g[0]; part_to_be_bolted_guid = $g[1]; first_point_mm = @(0, 0, 0); second_point_mm = @(100, 0, 0); bolt_standard = '8.8XOX'; bolt_size_mm = 20; dist_x_mm = @(100); dist_y_mm = @(0) }
    $null = Step 'tekla_create_bolts' @{ kind = 'array'; part_to_bolt_to_guid = $g[0]; first_point_mm = @(0, 0, 0); second_point_mm = @(100, 0, 0); bolt_standard = '8.8XOX'; bolt_size_mm = 21; dist_x_mm = @(100); dist_y_mm = @(0) } -ExpectError
    $null = Step 'tekla_create_weld' @{ main_part_guid = $g[0]; secondary_part_guid = $g[1]; size_above_mm = 6 }
    $null = Step 'tekla_create_rebar' @{ kind = 'group'; father_part_guid = $g[0]; points_mm = @(@(0, 0, 0), @(0, 0, 400)); start_mm = @(0, 0, 0); end_mm = @(5000, 0, 0); spacing_mm = 200; grade = 'SD40'; size = '16' }
    $null = Step 'tekla_create_rebar' @{ kind = 'single'; father_part_guid = $g[0]; points_mm = @(@(0, 0, 0), @(1000, 0, 0)); grade = 'SD40'; size = '17' } -ExpectError
    $null = Step 'tekla_create_grid' @{ coordinate_x = '0 2*6000'; coordinate_y = '0 6000'; coordinate_z = '0 3000'; label_x = 'T1 T2 T3'; label_y = 'TA TB'; label_z = '+0 +3000' }
    $null = Step 'tekla_create_assembly' @{ main_part_guid = $g[0]; secondary_part_guids = @($g[1]) }
    $null = Step 'tekla_set_workplane' @{ global = $true }
    $null = Step 'tekla_save_model' @{ comment = 'test' }
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 3 " + $(if ($Apply) { 'APPLY' } else { 'PREVIEW' }) + " TEST PASSED"
