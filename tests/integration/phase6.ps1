# Phase 6 integration test: model exports (IFC, NC) against the TEST model.
# Default: the model is not changed; export folders are created in exportDir and removed at the end.
# -Apply:  also creates a test steel beam + plate at x >= 100000 mm, numbers them (scope=modified), checks that NC creates
#          DSTV files for them, then deletes the parts. Run -Apply only with the user's explicit permission.
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

$null = Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'phase6-test'; version = '1' } }
$in.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$st = Read 'tekla_status' @{}
if ($st.model.path -ne $ExpectModelPath) { "ABORT: open model is $($st.model.path)"; $in.Close(); exit 3 }
$tag = 'mcptest_' + (Get-Date -Format 'yyyyMMdd_HHmmss')
"MODE: " + $(if ($Apply) { 'APPLY (test steel parts created, numbered, deleted)' } else { 'MODEL NOT CHANGED (files only)' })

try {
    "--- IFC"
    $i4 = Read 'tekla_export_ifc' @{ file_name = $tag }
    "  ifc4: $($i4.ifc_size_bytes) bytes; files: $(($i4.files | ForEach-Object { $_.name }) -join ', ')"
    if ($i4.ifc_size_bytes -lt 1000) { $fail.Add('IFC4 file too small') }
    $head = Get-Content $i4.ifc_file -TotalCount 20 | Select-String 'FILE_SCHEMA' | ForEach-Object Line
    "  header: $head"
    if ($head -notmatch 'IFC4') { $fail.Add('IFC4 header does not say IFC4') }
    $dup = Tool 'tekla_export_ifc' @{ file_name = $tag }
    if (-not $dup.isError) { $fail.Add('existing export folder was not refused') } else { '  same name again -> refused (no overwrite)' }
    $i2 = Read 'tekla_export_ifc' @{ file_name = "$($tag)_2x3"; version = 'ifc2x3'; rebars = $false }
    $h2 = Get-Content $i2.ifc_file -TotalCount 20 | Select-String 'FILE_SCHEMA' | ForEach-Object Line
    "  ifc2x3 without rebars: $($i2.ifc_size_bytes) bytes; header: $h2"
    if ($h2 -notmatch 'IFC2X3') { $fail.Add('IFC2x3 header does not say IFC2X3') }
    if ($i2.ifc_size_bytes -ge $i4.ifc_size_bytes) { $fail.Add('IFC without rebars is not smaller') }
    $i43 = Read 'tekla_export_ifc' @{ file_name = "$($tag)_43"; version = 'ifc43' }
    $h43 = Get-Content $i43.ifc_file -TotalCount 20 | Select-String 'FILE_SCHEMA' | ForEach-Object Line
    "  ifc43: $($i43.ifc_size_bytes) bytes; header: $h43"
    if ($h43 -notmatch 'IFC4X3') { $fail.Add('IFC4.3 header does not say IFC4X3') }
    $bad = Tool 'tekla_export_ifc' @{ file_name = '..\escape' }
    if (-not $bad.isError) { $fail.Add('path traversal accepted') } else { '  path traversal -> refused' }

    "--- drawings (PDF / DWG / DXF through Tekla's Document manager)"
    $tp = (Get-Process TeklaStructures).Id
    Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p); public delegate bool EnumProc(IntPtr h, IntPtr p); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);' -ErrorAction SilentlyContinue
    function TeklaWindows { $t = New-Object Collections.Generic.List[string]; [W.U]::EnumWindows({ param($h, $x) $wp = 0; [void][W.U]::GetWindowThreadProcessId($h, [ref]$wp); if ($wp -eq $tp -and [W.U]::IsWindowVisible($h)) { $sb = New-Object Text.StringBuilder 256; [void][W.U]::GetWindowText($h, $sb, 256); $t.Add($sb.ToString()) }; $true }, [IntPtr]::Zero) | Out-Null; return $t }
    $docmanBefore = (TeklaWindows) -contains 'Document manager'
    $dl = (Read 'tekla_list_drawings' @{ types = @('cast_unit'); limit = 2 }).items
    $ids = @($dl | ForEach-Object { $_.drawing_id })
    $pdf = Read 'tekla_export_drawings' @{ folder_name = "$($tag)_pdf"; drawing_ids = $ids; format = 'pdf' }
    "  pdf: $(($pdf.files | ForEach-Object { "$($_.name) ($($_.size_bytes) B)" }) -join ', ')"
    if (-not $pdf.ok -or $pdf.file_count -ne $ids.Count) { $fail.Add("PDF export: ok=$($pdf.ok), files=$($pdf.file_count), failed steps: $(($pdf.drawings.macro_steps_failed) -join ' | ')") }
    $dwg = Read 'tekla_export_drawings' @{ folder_name = "$($tag)_dwg"; drawing_ids = @($ids[0]); format = 'dwg' }
    $dxf = Read 'tekla_export_drawings' @{ folder_name = "$($tag)_dxf"; drawing_ids = @($ids[1]); format = 'dxf' }
    "  dwg: $(($dwg.files | ForEach-Object { "$($_.name) ($($_.size_bytes) B)" }) -join ', '); dxf: $(($dxf.files | ForEach-Object { "$($_.name) ($($_.size_bytes) B)" }) -join ', ')"
    if (-not $dwg.ok) { $fail.Add("DWG export failed: $(($dwg.drawings.macro_steps_failed) -join ' | ')") }
    if (-not $dxf.ok) { $fail.Add("DXF export failed: $(($dxf.drawings.macro_steps_failed) -join ' | ')") }
    if ($dxf.files[0].name -notlike '*.dxf') { $fail.Add('DXF export did not write a .dxf file') }
    $check = @('Export drawings as DWG/DXF/DGN', 'Print drawings') + $(if ($docmanBefore) { @() } else { @('Document manager') })
    $left = @(TeklaWindows | Where-Object { $_ -in $check })
    if ($left.Count) { $fail.Add("Tekla dialogs left open: $($left -join ', ')") } else { "  no export/print dialog left open$(if ($docmanBefore) { ' (Document manager was already open and stays open)' } else { ', Document manager closed again' })" }
    $bad2 = Tool 'tekla_export_drawings' @{ folder_name = "$($tag)_x"; drawing_ids = @('ga|NOPE|20000101000000') }
    if (-not $bad2.isError) { $fail.Add('unknown drawing accepted') } else { '  unknown drawing -> refused' }

    "--- NC"
    $nb = Tool 'tekla_export_nc' @{ folder_name = $tag; settings = 'NO_SUCH' }
    if (-not $nb.isError) { $fail.Add('unknown NC settings accepted') } else { "  unknown settings -> $($nb.structuredContent.error.message)" }
    $nc = Read 'tekla_export_nc' @{ folder_name = $tag; settings = 'DSTV for Profile' }
    "  all parts with 'DSTV for Profile': $($nc.file_count) file(s) $($nc.note)"

    if ($Apply) {
        "--- NC with steel test parts"
        $made = New-Object Collections.Generic.List[string]
        try {
            $b = Apply 'tekla_create_beam' @{ start_mm = @(100000, 0, 0); end_mm = @(106000, 0, 0); profile = 'WF400X200X8X13'; material = 'SS400'; name = 'MCP_TEST_NC' }
            if ($b) { $made.AddRange([string[]]@($b.affected)) }
            $pl = Apply 'tekla_create_plate' @{ contour_mm = @(@(100000, -300, 300), @(100600, -300, 300), @(100600, 300, 300), @(100000, 300, 300)); profile = 'PL20'; material = 'SS400'; name = 'MCP_TEST_NC' }
            if ($pl) { $made.AddRange([string[]]@($pl.affected)) }
            $null = Apply 'tekla_run_numbering' @{ scope = 'modified' }
            $null = Read 'tekla_select_objects' @{ guids = @($made) }
            $np = Read 'tekla_export_nc' @{ folder_name = "$($tag)_profile"; settings = 'DSTV for Profile'; selection = $true }
            $nq = Read 'tekla_export_nc' @{ folder_name = "$($tag)_plate"; settings = 'DSTV for Plate'; selection = $true }
            "  profile: $($np.file_count) file(s) $(($np.files | ForEach-Object { $_.name }) -join ', '); plate: $($nq.file_count) file(s) $(($nq.files | ForEach-Object { $_.name }) -join ', ')"
            if ($np.file_count -lt 1) { $fail.Add('no NC file for the test beam') }
            if ($nq.file_count -lt 1) { $fail.Add('no NC file for the test plate') }
            $first = @(Get-ChildItem $np.folder -File | Select-Object -First 1)
            if ($first) { "  first lines of $($first[0].Name): $((Get-Content $first[0].FullName -TotalCount 4) -join ' | ')" }
        } finally {
            if ($made.Count) { $null = Apply 'tekla_delete_objects' @{ guids = @($made) } }
            $left = Read 'tekla_find_objects' @{ name = 'MCP_TEST*'; limit = 5 }
            if ($left.total -ne 0) { $fail.Add("$($left.total) MCP_TEST part(s) left") } else { '  no MCP_TEST parts left' }
        }
    }
} finally {
    "--- cleanup export folders"
    foreach ($f in ($folders | Select-Object -Unique)) { if ((Test-Path $f) -and ($f -like "*mcptest_*")) { Remove-Item $f -Recurse -Force; "  removed $f" } }
}

$in.Close(); $null = $p.WaitForExit(10000)
""
if ($fail.Count) { "FAILED ($($fail.Count)):"; $fail | ForEach-Object { "  - $_" }; exit 1 }
"PHASE 6 " + $(if ($Apply) { 'APPLY' } else { 'FILES-ONLY' }) + " TEST PASSED"

