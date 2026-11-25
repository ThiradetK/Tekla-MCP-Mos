# Phase 2 integration test (read-only on the model; creates export/report files only).
# Calls every Phase 2 tool through MCP stdio against the open test model and prints key results + timings.
# Usage: powershell -File tests\integration\phase2.ps1 [-ExpectModelPath C:\TeklaStructuresModels\SSMC_Tekla]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\TeklaMcp\bin\Release\net48\TeklaMcp.exe'),
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
function Call($name, $arguments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Rpc 'tools/call' @{ name = $name; arguments = $arguments }
    $line = ($r.result.content[0].text -split "`n")[0]
    Write-Host ("{0,-28} {1,7} ms  {2}" -f $name, $sw.ElapsedMilliseconds, $line)
    if ($r.error) { $fail.Add("$name protocol error $($r.error.message)") } elseif ($r.result.isError) { $fail.Add("$name -> $line") }
    return $r.result.structuredContent
}
function Check($cond, $msg) { if (-not $cond) { $fail.Add($msg); "   CHECK FAILED: $msg" } }

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase2-test'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')

$st = Call 'tekla_status' @{}
if ($st.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.model.path), expected $ExpectModelPath"; $in.Close(); exit 3 }

$beams = Call 'tekla_find_objects' @{ types = @('beam'); limit = 3; include_quantities = $true }
Check ($beams.total -eq 764) "beam total $($beams.total) != 764"
$b = $beams.items[0]; "   first beam: $($b.guid) $($b.profile) $($b.material) w=$($b.weight_kg) kg L=$($b.length_mm) mm start=$($b.start_mm -join ',')"
$parts = Call 'tekla_find_objects' @{ limit = 1 }
"   parts total: $($parts.total)"
$rebar = Call 'tekla_find_objects' @{ types = @('rebar'); limit = 2 }
Check ($rebar.total -eq 11049) "rebar total $($rebar.total) != 11049"
$c40 = Call 'tekla_find_objects' @{ types = @('beam'); profile = '500*250'; material = 'C4*'; limit = 1 }
"   beams 500*250 C4*: $($c40.total)"

$get = Call 'tekla_get_objects' @{ guids = @($b.guid, '00000000-0000-0000-0000-000000000000'); report_properties = @('PART_POS', 'WEIGHT_GROSS') }
Check ($get.items.Count -eq 1 -and $get.not_found.Count -eq 1) "get_objects found=$($get.items.Count) not_found=$($get.not_found.Count) types=$(($get.items | ForEach-Object { $_.type + ':' + $_.guid }) -join ',')"
"   bbox: $($get.items[0].bounding_box_mm.min -join ',') .. $($get.items[0].bounding_box_mm.max -join ',')  report: $($get.items[0].report_properties | ConvertTo-Json -Compress)"

$rp = Call 'tekla_get_report_properties' @{ guids = @($b.guid); names = @('WEIGHT', 'LENGTH', 'AREA', 'VOLUME', 'PROFILE', 'NOT_A_PROPERTY') }
"   values: $($rp.items[0].values | ConvertTo-Json -Compress)"
Check ($null -eq $rp.items[0].values.NOT_A_PROPERTY) "unknown property should be null"

$asm = Call 'tekla_get_assembly' @{ guid = $b.guid }
"   assembly main part: $($asm.main_part.guid) secondaries=$($asm.secondaries.Count) rebar=$($asm.reinforcement.object_count) ($($asm.reinforcement.total_weight_kg) kg)"

$grids = Call 'tekla_list_grids' @{}
$planes = @($grids.grids | ForEach-Object { $_.planes }); Check ($planes.Count -eq 31) "grid planes $($planes.Count) != 31"
"   planes: " + (($planes | Select-Object -First 8 | ForEach-Object { "$($_.label)@$($_.axis)=$($_.coordinate_mm)" }) -join ' ')
$wp = Call 'tekla_get_workplane' @{}

$cp = Call 'tekla_list_catalog' @{ kind = 'profile'; filter = 'WF400*'; limit = 5 }
"   " + (($cp.items | ForEach-Object { $_.name }) -join ', ')
$cm = Call 'tekla_list_catalog' @{ kind = 'material'; filter = 'SS*' }
"   " + (($cm.items | ForEach-Object { $_.name }) -join ', ')
$cb = Call 'tekla_list_catalog' @{ kind = 'bolt'; filter = '8.8XOX*'; limit = 3 }
$cr = Call 'tekla_list_catalog' @{ kind = 'rebar'; filter = 'SD40*'; limit = 3 }
"   " + (($cr.items | ForEach-Object { "$($_.name) $($_.weight_kg_m) kg/m" }) -join ', ')

$q1 = Call 'tekla_quantity_summary' @{}
"   parts: groups=$($q1.group_count) total=$($q1.total | ConvertTo-Json -Compress)"
$q1.groups | ForEach-Object { "     {0,-12} {1,-5} n={2,4} L={3,10} mm  A={4,8:N1} m2  W={5,12:N1} kg" -f $_.report_profile, $_.material, $_.object_count, $_.total_length_mm, ($_.total_area_mm2 / 1e6), $_.total_weight_kg }
$q2 = Call 'tekla_quantity_summary' @{ types = @('rebar'); group_by = @('profile', 'material') }
"   rebar: " + (($q2.groups | ForEach-Object { "$($_.material) DB$($_.profile): $($_.total_weight_kg) kg" }) -join '; ')
"   rebar total: $($q2.total | ConvertTo-Json -Compress)"
$q3 = Call 'tekla_quantity_summary' @{ group_by = @('material') }
"   parts by material: " + (($q3.groups | ForEach-Object { "$($_.material): $($_.object_count) obj, $($_.total_weight_kg) kg, $($_.total_volume_mm3) mm3" }) -join '; ')

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$e1 = Call 'tekla_export_data' @{ kind = 'boq'; format = 'csv'; file_name = "boq_$stamp" }
$e2 = Call 'tekla_export_data' @{ kind = 'part_list'; format = 'xlsx'; file_name = "parts_$stamp"; types = @('beam') }
$e3 = Call 'tekla_export_data' @{ kind = 'assembly_list'; format = 'json'; file_name = "assemblies_$stamp" }
$e4 = Call 'tekla_export_data' @{ kind = 'material_list'; format = 'csv'; file_name = "materials_$stamp"; types = @('rebar') }
$dup = Rpc 'tools/call' @{ name = 'tekla_export_data'; arguments = @{ kind = 'boq'; format = 'csv'; file_name = "boq_$stamp" } }
Check ($dup.result.isError) "export overwrite was not refused"
"   overwrite refused: $($dup.result.isError)"

$rpBad = Rpc 'tools/call' @{ name = 'tekla_run_report'; arguments = @{ template = 'No Such Template' } }
"   unknown template -> isError=$($rpBad.result.isError)"
$rr = Call 'tekla_run_report' @{ template = 'Material List'; file_name = "material_list_$stamp.xsr" }
"   report: exists=$($rr.exists) size=$($rr.size_bytes)"
if ($rr.exists) { "   --- report subtotal lines (count, length mm, area m2, weight kg) ---"; Get-Content $rr.path | Where-Object { $_ -match '^\s+\d+\s+\d+\s+[\d.]+\s+[\d.]+\s*$' -or $_ -match 'Total|TOTAL' } | ForEach-Object { "   | $_" } }

$sel = Call 'tekla_select_objects' @{ guids = @($b.guid) }
$gs = Call 'tekla_get_selection' @{}
Check ($gs.total -ge 1) "selection count $($gs.total)"

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 2 INTEGRATION PASSED"
