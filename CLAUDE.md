# CLAUDE.md — Tekla Structures MCP Server

Read `PLAN.md` before any work. It is the approved architecture and phase plan.

## Project

MCP server (`TeklaMcp.exe`) that connects Claude Desktop and Claude Code to Tekla Structures 2026 through the Tekla Open API. Delivered with a Windows `.exe` installer so others can install it.

- Language/runtime: C#, .NET Framework 4.8, x64 console app
- Tekla Open API: NuGet `Tekla.Structures*` 2026.0.3 (compile-time only, `Private=false`)
- Runtime Tekla DLLs: resolved from the local Tekla Structures 2026 installation (AssemblyResolve). Do not ship Trimble DLLs.
- Transport: MCP over stdio (JSON-RPC 2.0)
- Installer: Inno Setup 6, per-user install, no admin
- Tekla environment: SE_Asia (South-East Asia; no separate Thailand environment exists)

## Current status (handoff)

- Phase 0 done (2026-11-25). Results in `PLAN.md` §7.
- Phase 1 approved (2026-11-25): MCP stdio server, worker process, safety gate, audit log, `tekla_status`.
- Phase 2 built (2026-11-25): 12 read-only query/data tools; `tests\integration\phase2.ps1` passes; quantities match Tekla Material List. Approved.
- Phase 3 built (2026-11-26): 15 two-step write tools (28 tools total); `tests\integration\phase3.ps1 -Apply` passes on SSMC_Tekla (creates MCP_TEST* at x ≥ 100 m, deletes them). Approved 2026-11-26.
- Phase 7a built (2026-11-26): `--register|--unregister desktop|code|all`, `--readonly on|off`, `--doctor`; `installer\TeklaMcp.iss` → `build.ps1 -Installer` → `dist\TeklaMcp-Setup-<ver>.exe`. Silent install/uninstall tested (Desktop + Code entries added/removed, other servers kept). User tested the wizard install: passed (2026-11-26).
- Phase 8 built (2026-11-26): `docs\README.th.md`, `docs\TOOLS.md` (generated from tools/list), `docs\SAFETY.md`, `tests\CHECKLIST.md`; automated checklist A1–A5, A7 pass. Approved 2026-11-26; pushed to GitHub. Round 1 complete.
- Round 2 approved 2026-11-26: Phase 4 → 5 → 6 → 7b, each ends with report + approval. Decisions D8–D12 in `PLAN.md` §8.
- Phase 4 built (2026-11-26): `tekla_validate_model`, `tekla_clash_check` (30 tools); `tests\integration\phase4.ps1` read-only and `-Apply` (known clash on 2 test beams, run with permission 2026-11-26) pass. Approved 2026-11-26.
- Phase 5 built (2026-11-26): 11 drawing/numbering tools (41 total); `tests\integration\phase5.ps1` read-only and `-Apply` (without numbering, permission 2026-11-26) pass; `tests\integration\phase5-extra.ps1` (user asked 2026-11-26) passes: real numbering run (87 parts numbered, macro removed), assembly + single-part drawings of a test steel beam, text survives save + reopen (checked with spike probe6). SSMC_Tekla is now fully numbered (not saved). Full numbering tested (`numbering-full.ps1`). Approved 2026-11-26.
- Phase 6 in progress (2026-11-26): `tekla_export_ifc` (2x3/4/4.3) and `tekla_export_nc` built (43 tools); `tests\integration\phase6.ps1` files-only and `-Apply` (NC with steel test parts, permission 2026-11-26) pass. `tekla_export_drawings` (PDF/DWG/DXF via WPF macro, 44 tools) passes in `phase6.ps1` (2 PDFs, DWG, DXF, correct drawings, no dialogs left). Extra tests (user asked): GA [1] PDF (1.6 MB, 48 s), 5 drawings in one call (70 s), filtered Document manager category → wrong file deleted + error, then fixed by resetting the category in the macro. With screen access: sorted list and multidrawing tested → row order is not predictable, so the tool now guesses a row, identifies each file by `Drawing.GetPlotFileName(false)`, keeps files of requested drawings, deletes others and retries (max 10 rows per drawing); multidrawing + GA + 2 CU exported correctly; 5 drawings with shuffled order correct in 65 s. Approved 2026-11-26.
- Phase 7b built (2026-11-26): `tekla_batch_execute` (45 tools; D15: model write tools, `{{stepN.guid}}` refs, stop on first failure, ≤100 steps, DESTRUCTIVE level, one audit entry). 90 unit tests; `tests\integration\phase7b.ps1` preview and `-Apply` (permission 2026-11-26: 9-step batch with refs applied in 0.9 s and deleted its own objects; failing batch stopped at step 2, step 1 kept) pass. Approved 2026-11-26. Round 2 complete; pushed to GitHub.
- Scope of first round: Phase 0–3 + installer (7a) + docs (8). Round 2: Phase 4–7b.
- Build: `powershell -ExecutionPolicy Bypass -File build.ps1 [-Smoke] [-Installer]` (restore → build → unit tests → optional stdio smoke test → optional installer).

## Decisions

| # | Topic | Status |
|---|---|---|
| D2 | MCP layer | Approved: hand-written JSON-RPC on Newtonsoft.Json 13.0.4 (same version as Tekla bin) |
| D5 | Test model | `C:\TeklaStructuresModels\SSMC_Tekla` (user-given test model) |
| D6 | Environment | Approved: SE_Asia |
| D7 | Tool text language | Approved: English |

## Working rules

1. Follow only what the user asked. Propose extras as options; do not implement them unasked.
2. Do not guess. If an API, path, or requirement is unclear, verify (reflection on the DLL, Tekla docs at developer.tekla.com) or ask the user.
3. Never invent Tekla API members. Confirm each one against the 2026.0.3 assemblies before use.
4. If a planned API does not exist or fails, report it with alternatives. Do not silently switch approach.
5. End each phase with: build passes → test against the model copy → report results → wait for approval.
6. Keep answers short and factual. Separate "what the user asked" from "what Claude proposes".
7. Git: commit before starting each new phase. After the last phase of a round is approved (round 1: Phase 8; round 2: Phase 7b), push to `https://github.com/ThiradetK/Tekla-MCP-Mos.git`. No other pushes. Stage explicit paths; the user's own `skills/` folder is not part of the commits.

## Tekla model safety (mandatory)

- Test only on a **copy** of the model. Never open or modify the original.
- Every create/modify/delete tool uses two steps: `mode: "preview"` returns changes + `confirm_token`; `mode: "apply"` requires that token.
- After apply: `CommitChanges()`, read the objects back from the model, compare with the request, report the result.
- Never call apply on the user's behalf without explicit permission in the conversation.
- Delete requires the full object list in the preview.
- Write an audit log entry for every write.
- The Open API has no undo. Recommend saving the model before large changes.

## Code rules

- stdout carries MCP messages only. Logs go to file (`%LOCALAPPDATA%\TeklaMcp\logs\`) and stderr.
- All Tekla API calls run in the `TeklaMcp.exe --worker` child process, one at a time on its single thread. The server process never loads Tekla. The server replaces the worker when it reports `reset` (Tekla not reachable or connection stale) and retries once; write tools must pass `retryOnReset: false`.
- Units: mm, kg, degrees. Label units in every field.
- Reference objects by GUID; include ID as secondary info.
- Query tools must support limit/paging.
- Tool names start with `tekla_`.

## Machine notes

- Project folder: `D:\0_Project\MCP\Tekla` (git repo, branch `main`)
- Tekla Structures 2026 SP6.1 (Educational version): `C:\Program Files\Tekla Structures\2026.0\bin`. `Tekla.Structures.dll` is in `bin\Net48Runtime\`. Found via registry `HKLM\SOFTWARE\Trimble\Tekla Structures\2026.0\setup` (MainDir + TSVersionDir).
- .NET SDK 10.0.404 and Inno Setup 6.7.3 (`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`) installed. User NuGet config has no sources → repo `nuget.config` points to nuget.org.
- Claude Desktop is the MS Store build. The config it actually loads: `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc\claude_desktop_config.json`. The copies under `%APPDATA%\Claude\` and `Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\` are not read.
- Claude Code: `tekla` registered with `claude mcp add -s local` (this project only).
- Ask before installing anything.

## Tekla API pitfalls (found in Phase 0–1)

- The API writes "Connection failed …" to stdout when Tekla is not running → `Program.cs` keeps the raw stdout for MCP and sends `Console.Out` to stderr.
- `TeklaStructuresInfo` / `TeklaStructuresSettings` static constructors fail permanently if Tekla is not running → call `Model.GetConnectionStatus()` first.
- The API's remoting connection is process-wide static state that never resets: a process that first ran while Tekla was closed never connects, and after a Tekla restart every call fails with "Cannot find remote object". Hence the worker process (tested 2026-11-25: both cases recover).
- `TeklaStructuresSettings.GetAdvancedOption` returns `true` even for unknown names.
- No API for the environment name → derived from `XS_SYSTEM` paths.
- `ProfileItem` has no name; use `CatalogHandler.GetLibraryProfileItems()` → `LibraryProfileItem.ProfileName`.
- `ModelHandler.IsModelOpen` and `Operation.SaveModel` do not exist; save is `ModelHandler.Save(comment, user)`.
- Report property units: WEIGHT kg, LENGTH mm, AREA mm², VOLUME mm³.
- Rebar groups: `WEIGHT`/`LENGTH` are for ONE bar; use `WEIGHT_TOTAL` and LENGTH × `GetNumberOfRebars()`. `NUMBER` is not the bar count.
- Rebar sets: query `typeof(Reinforcement)` to get the generated bars (enum `REBARGROUP` misses them).
- Tekla reports group by the `PROFILE` report property (e.g. "250x500"), not `Profile.ProfileString` ("500*250") → field `report_profile`.
- `Model.GetReportPropertyDouble(ids, prop)` silently drops objects without the property → check the count, fall back per object.
- `RebarGroup.Insert` throws "Required information missing - RadiusValues" if `RadiusValues` is empty → set it from the catalog `RebarItem.BendRadius`. `Insert` also returns false (no message) when `Class` is not set.
- `Assembly.Add` + `Modify` return true but do nothing for a part that is a secondary of another assembly (e.g. shop-welded, the weld tool's default) → the assembly tool refuses such parts in preview.
- `Delete()` returns false for bolts/welds/rebar already removed with their parent part → judge deletes by read-back.
- Clash check works from the external worker: `Events.ClashDetected` / `ClashCheckDone` + `Register()`. It checks the objects selected in the model view (`UI.ModelObjectSelector.Select`), so save and restore the user's selection. SSMC_Tekla has 0 clashes (no intersecting part pairs).
- Clash events carry objects with only the identifier (Name reads "BEAM") → `Model.SelectModelObject(id)` before reading fields. `ClashCheckData.Overlap` = 0 and `GetIntersectionBoundingBoxes` returns an empty list for two crossing beams → no clash size is reported.
- `ModelObject.Delete()` clears the object's Identifier (GUID becomes all zeros) → take GUIDs before deleting.
- Numbering: only `Operation.IsNumberingUpToDate(All)`; no API runs numbering. Unnumbered marks show "(?)" in ASSEMBLY_POS / PART_POS. `tekla_run_numbering` writes a temporary macro using commands from Tekla's shipped macros: `akit.Callback("acmd_partnumbers_all", "" | "full", "main_frame")` (+ `PushButton("warning_ok", "full_numbering_ok")` for full).
- Proof that numbering ran: Tekla rewrites `<model>\logs\numberingresults.log` (full run shows "Diagnose & Repair Numbering: All"; written even when nothing needs numbering). `tekla_run_numbering` waits for it and returns the counts. Full numbering on an already numbered SSMC_Tekla changed 0 marks; drawings stayed up to date (`tests\integration\numbering-full.ps1 [-Scope modified]`).
- `XS_MACRO_DIRECTORY` = `C:\ProgramData\...\environments\common\macros` (writable) ; `...\bin\Env\Common\Macros\` (not writable). `Operation.RunMacro("file.cs")` runs from `<dir>\modeling`; `IsMacroRunning()` is false right after start, so poll the result.
- Drawings have no public GUID/Identifier → `drawing_id` = type|mark|CreationDate. `UpdateDrawing` and `Delete` refuse the open drawing; `SetActiveDrawing` throws if not up to date.
- IFC: `CreateIFC4ExportFromAll/Selected` with `IFCExportVersionEnum.IFC2x3` still writes `FILE_SCHEMA(('IFC4'))` → use `CreateIFC2x3Export` for 2x3. IFC43 writes `IFC4X3_ADD2`. Tekla also writes `<name>.htm`, `<name>.partReport` and a shared `ifc4export.js` → each export gets its own new folder. SSMC full IFC4 ≈ 1.6 MB in 4–11 s.
- NC: settings names are per environment (SE_Asia: "DSTV for Profile/Plate/Angle" `.ncfs` in XS_SYSTEM; default env uses "DSTV for profiles/plates"). `CreateNCFilesFromAll` returns false (no exception) for an unknown name; true with 0 files when there are no steel parts.
- PDF: `DrawingHandler.PrintDrawing(d, DPMPrinterAttributes{OutputType=PDF}, file)` returns true and DPMPrinterCommand.exe logs a clean run (`%LOCALAPPDATA%\Trimble\Tekla Structures\2026.0\Logs\DPMPrinter-*.log`, `printActive:true`), but no file is written — with or without the drawing open, both overloads, short path (tested 2026-11-26, Educational license).
  The API path launches `DPMPrinterCommand.exe`, which exits in ~4 s without loading settings. Printing from the UI (Document manager → right-click → Print → "Use one printer" → PDF file → Print) works in-process: writes `<model>\Plotfiles\Combined.pdf` (Educational watermark) and creates `attributes\<user>_PdfPrintOptions.xml`. The API call still fails after that file exists.
- NC: `CreateNCFiles*(settings, folder)` appends the file name to the folder string without a separator → pass a trailing `\` (files are named by part mark, e.g. `A6.nc1`).
- DWG/DXF: no API (print output types are Printer/PDF/Plot/Image) and no shipped macro exports drawings to DWG.
- Drawing PDF/DWG/DXF works with a WPF macro (`#pragma reference "Tekla.Macros.Wpf.Runtime"`, entry `Run(IMacroRuntime)`, `runtime.Get<IWpfMacroHost>()`) run by `Operation.RunMacro` — no window focus needed. Automation IDs come from `bin\Features\DocumentManagerFeature.dll`, `DPMPrinterFeature.dll`, `DrawingExport.UIFeature.dll` (UTF-16 strings `AID_*`). Views: `DocumentManager.MainWindow`, `DPMPrinterFeature.DPMPrinterViewWindow`, `DrawingExport.UIFeature.MainContentView` (not `DrawingDWGExport.MainWindow`). Command `wpf.InvokeCommand("CommandRepository", "Drawing.DrawingList")` opens the Document manager.
- Macro WPF pitfalls: `Find` is lazy; a wrong view/ID fails only at the action, after a 60 s timeout. Document manager search `SetText` does not filter until Enter; `DataGrid.NewSelection.With(string)` times out → select by row index (= `GetDrawings()` order) and verify the output file name starts with the mark. File type combo: `As.Selector.Select("DXF")` (`ComboBox.SetText` times out). Category list: `AID_DOCMAN_CategoryList` `As.Selector.Select(0)` = All documents, `Select(2)` = All drawings (select by name times out). Row order is NOT predictable (differs from `GetDrawings`, changes when drawings are added; multidrawings last; a user sort persists in `<model>\DocumentManagerDataGridSettings_<user>.xml`, written when the Document manager closes) → identify output by `Drawing.GetPlotFileName(false)` (exact file name, e.g. "PF13 - 1 - standard", GA "1 - Standard - Rev ", multidrawing "101 - Rev "). `AID_DOCMAN_ButtonSelectDrawings` is a toggle filter (state unreadable) — do not use. Search applies only on a real Enter (`KeyGesture("Enter")` has no binding; `InputTarget.Keyboard` does nothing). A failed step must stop the macro: each missing control costs 60 s on Tekla's main thread and blocks the worker. Tekla caches compiled macros by file name in `%LOCALAPPDATA%\Trimble\Tekla Structures\2026.0\Cache\MacroCacheFolder` → use fixed macro file names. Large GA PDF needs > 30 s → wait for the file per drawing (150 s). `MultiDrawing` has no public constructor (UI only). `AppActivate`/focus is refused by Windows while the user is active → prefer macros + `WM_CLOSE` over computer-use clicks. Print dialog closes with `View(...).As.Window.Close()`; the export window does not → `WM_CLOSE` to the "Export drawings as DWG/DXF/DGN" window. Macros run in `Tekla.Technology.Scripting.Sandbox`.
- Building while Claude Desktop/Code run the dev server fails (exe locked): stop `TeklaMcp` processes first.
