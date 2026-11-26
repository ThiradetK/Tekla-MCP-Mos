# Phase 7b integration test: tekla_batch_execute against the TEST model.
# Default: preview only (the model is not changed) + refusal checks.
# -Apply:  runs a batch at x >= 100000 mm that creates beam/column/plate, bolts/welds them by reference, modifies, copies and
#          finally deletes everything it created; then a batch that fails at step 2 (stop check) and cleans up step 1's beam.
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
    $script:id++; $in.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 40 -Compress))
    while ($true) { $m = $p.StandardOutput.ReadLine() | ConvertFrom-Json; if ($m.id -eq $script:id) { return $m } }
}
function Tool($name, $arguments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    Write-Host ("{0,-22} {1,-8} {2,6} ms  {3}" -f $name, $arguments.mode, $sw.ElapsedMilliseconds, ($r.result.content[0].text -split "`n")[0])
    return $r.result
}
function S($tool, $stepArgs) { @{ tool = $tool; args = $stepArgs } }   # not $args: that is PowerShell's automatic variable

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase7b-test'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Tool 'tekla_status' @{}
if ($st.structuredContent.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.structuredContent.model.path)"; $in.Close(); exit 3 }
"MODE: " + $(if ($Apply) { 'APPLY (test objects created and deleted by the batch)' } else { 'PREVIEW ONLY' })

$steps = @(
    (S 'tekla_create_column' @{ base_mm = @(100000, 0, 0); height_mm = 3000; profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_B_COL' }),
    (S 'tekla_create_beam' @{ start_mm = @(100000, 0, 3000); end_mm = @(106000, 0, 3000); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_B_BEAM' }),
    (S 'tekla_create_plate' @{ contour_mm = @(@(100000, -300, 3500), @(100600, -300, 3500), @(100600, 300, 3500), @(100000, 300, 3500)); profile = 'PL20'; material = 'SS400'; name = 'MCP_TEST_B_PLATE' }),
    (S 'tekla_create_bolts' @{ kind = 'array'; part_to_bolt_to_guid = '{{step1.guid}}'; part_to_be_bolted_guid = '{{step3.guid}}'; first_point_mm = @(100100, 0, 3500); second_point_mm = @(100500, 0, 3500); bolt_standard = '8.8XOX'; bolt_size_mm = 20; dist_x_mm = @(100, 100); dist_y_mm = @(150) }),
    (S 'tekla_create_weld' @{ main_part_guid = '{{step1.guid}}'; secondary_part_guid = '{{step2.guid}}'; size_above_mm = 6; shop_weld = $false }),
    (S 'tekla_modify_objects' @{ guids = @('{{step2.guid}}'); set = @{ name = 'MCP_TEST_B_BEAM_MOD'; class = '7' } }),
    (S 'tekla_move_objects' @{ guids = @('{{step3.guid}}'); vector_mm = @(0, 0, 50) }),
    (S 'tekla_copy_objects' @{ guids = @('{{step1.guid}}'); vector_mm = @(0, 3000, 0); count = 1 }),
    (S 'tekla_delete_objects' @{ guids = @('{{step1.guid}}', '{{step2.guid}}', '{{step3.guid}}', '{{step8.guid}}') })
)

"--- preview"
$pv = Tool 'tekla_batch_execute' @{ mode = 'preview'; steps = $steps }
if ($pv.isError) { $fail.Add("batch preview failed: $($pv.content[0].text)") }
else {
    "  steps $($pv.structuredContent.step_count), deferred $($pv.structuredContent.deferred_steps), objects listed $(@($pv.structuredContent.objects).Count)"
    $pv.structuredContent.steps | ForEach-Object { "   $($_.step) $($_.tool): $($_.summary)" }
    if ($pv.structuredContent.deferred_steps -ne 6) { $fail.Add("expected 6 deferred steps, got $($pv.structuredContent.deferred_steps)") }
}
$noTok = Tool 'tekla_batch_execute' @{ mode = 'apply'; steps = $steps }
if ($noTok.structuredContent.error.code -ne 'confirm_token_missing') { $fail.Add('apply without token not refused') } else { '  apply without token -> refused' }
$fwd = Tool 'tekla_batch_execute' @{ mode = 'preview'; steps = @((S 'tekla_modify_objects' @{ guids = @('{{step2.guid}}'); set = @{ name = 'x' } }), (S 'tekla_create_beam' @{ start_mm = @(0,0,0); end_mm = @(1000,0,0); profile = 'WF400X200X8X13'; material = 'SS400' })) }
if (-not $fwd.isError) { $fail.Add('forward reference accepted') } else { '  forward reference -> refused' }
$save = Tool 'tekla_batch_execute' @{ mode = 'preview'; steps = @((S 'tekla_save_model' @{})) }
if (-not $save.isError) { $fail.Add('tekla_save_model accepted in a batch') } else { '  save_model in batch -> refused' }
$many = Tool 'tekla_batch_execute' @{ mode = 'preview'; steps = @(1..101 | ForEach-Object { S 'tekla_create_beam' @{ start_mm = @(0,0,0); end_mm = @(1000,0,0); profile = 'WF400X200X8X13'; material = 'SS400' } }) }
if (-not $many.isError) { $fail.Add('101 steps accepted') } else { '  101 steps -> refused' }
$badProf = Tool 'tekla_batch_execute' @{ mode = 'preview'; steps = @((S 'tekla_create_beam' @{ start_mm = @(0,0,0); end_mm = @(1000,0,0); profile = 'HEA300'; material = 'SS400' })) }
if (-not $badProf.isError) { $fail.Add('bad profile accepted') } else { "  bad profile in step 1 -> refused: $($badProf.structuredContent.error.message)" }

if ($Apply) {
    "--- apply (full batch)"
    $ap = Tool 'tekla_batch_execute' @{ mode = 'apply'; steps = $steps; confirm_token = $pv.structuredContent.confirm_token }
    if ($ap.isError) { $fail.Add("batch apply failed: $($ap.content[0].text)") }
    $ap.structuredContent.steps | ForEach-Object { "   $($_.step) $($_.tool): $($_.summary)" }
    $left = (Tool 'tekla_find_objects' @{ name = 'MCP_TEST*'; types = @('part', 'bolt', 'weld'); limit = 20 }).structuredContent
    if ($left.total -ne 0) { $fail.Add("$($left.total) MCP_TEST object(s) left after the batch") } else { '  no MCP_TEST objects left (batch deleted what it created)' }

    "--- apply (batch that fails at step 2)"
    $bad = @((S 'tekla_create_beam' @{ start_mm = @(110000, 0, 3000); end_mm = @(116000, 0, 3000); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_B_STOP' }),
             (S 'tekla_modify_objects' @{ guids = @('{{step1.guid}}'); set = @{ profile = 'HEA300' } }),
             (S 'tekla_create_beam' @{ start_mm = @(120000, 0, 3000); end_mm = @(126000, 0, 3000); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_B_NEVER' }))
    $bp = Tool 'tekla_batch_execute' @{ mode = 'preview'; steps = $bad }
    $ba = Tool 'tekla_batch_execute' @{ mode = 'apply'; steps = $bad; confirm_token = $bp.structuredContent.confirm_token }
    "  completed_steps=$($ba.structuredContent.completed_steps); $($ba.structuredContent.error.message)"
    if (-not $ba.isError -or $ba.structuredContent.completed_steps -ne 1) { $fail.Add('failing batch did not stop after step 1') }
    $kept = (Tool 'tekla_find_objects' @{ name = 'MCP_TEST_B_*'; limit = 10 }).structuredContent
    if ($kept.total -ne 1 -or $kept.items[0].name -ne 'MCP_TEST_B_STOP') { $fail.Add("expected only MCP_TEST_B_STOP to remain, found $($kept.total)") } else { '  step 1 beam kept, step 3 never ran' }
    foreach ($g in $kept.items) {
        $dp = Tool 'tekla_delete_objects' @{ mode = 'preview'; guids = @($g.guid) }
        $null = Tool 'tekla_delete_objects' @{ mode = 'apply'; guids = @($g.guid); confirm_token = $dp.structuredContent.confirm_token }
    }
    $left2 = (Tool 'tekla_find_objects' @{ name = 'MCP_TEST*'; limit = 10 }).structuredContent
    if ($left2.total -ne 0) { $fail.Add("$($left2.total) MCP_TEST part(s) left") } else { '  cleanup check: no MCP_TEST parts left' }
    $audit = Get-Content (Join-Path $env:LOCALAPPDATA "TeklaMcp\logs\audit-$(Get-Date -Format yyyyMM).jsonl") -Tail 20 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.tool -eq 'tekla_batch_execute' }
    "  audit entries for batch (last 20 lines): $(($audit | ForEach-Object { $_.outcome }) -join ', ')"
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 7B " + $(if ($Apply) { 'APPLY' } else { 'PREVIEW' }) + " TEST PASSED"

