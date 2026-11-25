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
- Phase 7a (installer) in progress.
- Scope of first round: Phase 0–3 + installer (7a). Do not start Phase 4–7b without user approval.
- Build: `powershell -ExecutionPolicy Bypass -File build.ps1 [-Smoke]` (restore → build → unit tests → optional stdio smoke test).

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
7. Git: commit before starting each new phase. After the last phase of the round (Phase 8) is approved, push to `https://github.com/ThiradetK/Tekla-MCP-Mos.git`. No other pushes.

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
- Building while Claude Desktop/Code run the dev server fails (exe locked): stop `TeklaMcp` processes first.
