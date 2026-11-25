# Test checklist (round 1: Phase 0–3 + 7a)

Machine: Windows 11, Tekla Structures 2026 SP6.1 (SE_Asia), test model `C:\TeklaStructuresModels\SSMC_Tekla`.
Run the automated items with Tekla open on the test model.

## Automated

| # | Test | Command | Result (2026-11-26) |
|---|---|---|---|
| A1 | Build, 60 unit tests, no Tekla/Trimble DLLs in output | `powershell -ExecutionPolicy Bypass -File build.ps1` | ✅ |
| A2 | stdio smoke: handshake, tools/list, ping, `tekla_status`, invalid args, stdout carries JSON-RPC only | `build.ps1 -Smoke` | ✅ |
| A3 | Installer builds | `build.ps1 -Installer` | ✅ `dist\TeklaMcp-Setup-0.1.0.exe` |
| A4 | Phase 2 read tools: find/get/selection/report properties/assembly/grids/workplane/catalog/quantity/export/report | `tests\integration\phase2.ps1` | ✅ |
| A5 | Phase 3 preview only: token refusals, catalog checks, previews of every write tool (model not changed) | `tests\integration\phase3.ps1` | ✅ |
| A6 | Phase 3 apply: create/modify/move/copy/assembly/work plane/delete with read-back, cleanup leaves no `MCP_TEST*` | `tests\integration\phase3.ps1 -Apply` (**only with the model owner's permission**) | ✅ 2026-11-26 (run once, with permission) |
| A7 | Doctor: x64, .NET 4.8, Tekla install, API connection, Claude config | `TeklaMcp.exe --doctor` | ✅ |

## Manual

| # | Test | How | Result |
|---|---|---|---|
| M1 | Claude Desktop chat calls `tekla_status` | Desktop chat: "ใช้ tekla_status" | ✅ 2026-11-25 |
| M2 | Server started while Tekla closed → connects after Tekla starts | close Tekla, call a tool (`not_connected`), start Tekla, call again | ✅ 2026-11-25 |
| M3 | Tekla restarted while server runs → recovers | restart Tekla, call a tool | ✅ 2026-11-25 |
| M4 | Tekla busy longer than timeout → `tekla_timeout`, next call works | open a modal dialog in Tekla, call a tool | ✅ 2026-11-25 |
| M5 | Silent install + uninstall: files, Desktop + Code entries added/removed, other servers kept | `TeklaMcp-Setup-0.1.0.exe /VERYSILENT /TASKS="desktop,code"`, then `unins000.exe /VERYSILENT` | ✅ 2026-11-26 |
| M6 | Wizard install, Desktop loads the installed exe | double-click installer, restart Desktop, `tekla_status` | ✅ 2026-11-26 (user) |
| M7 | `--readonly on` blocks writes, `off` restores | `TeklaMcp.exe --readonly on`, restart Claude, try a create preview → `read_only` | ⚠️ setting file write tested; refusal covered by unit tests, not tested in Claude |

## Not tested in round 1

- `tekla_save_model` apply (skipped on purpose: it saves the test model)
- Bolt groups of kind `xy_list` and `circle` (apply)
- Upgrading over an installed version while Claude Desktop is running the server
- Other Tekla environments than SE_Asia; Claude Desktop classic (non-Store) build
