# Tekla Structures MCP Server — โครงสร้างและแผนงาน (v0.3)

สถานะ: **รอบแรกเสร็จ (Phase 0–3, 7a, 8)**; รอบสอง Phase 4–7b อนุมัติแล้ว กำลังทำ Phase 4 (2026-11-26)
เป้าหมาย: Tekla Structures 2026 (Open API NuGet 2026.0.3, .NET Framework 4.8)
Environment: SE_Asia
Client: Claude Desktop + Claude Code (server ตัวเดียวกัน)
ตัวติดตั้ง: `.exe` (ไม่เซ็น code)
ที่ build: บนคอม Windows ของผู้ใช้ (ทำงานผ่าน Claude Code ในโฟลเดอร์ `D:\0_Project\MCP\Tekla`)
ขอบเขตรอบแรก: **Phase 0–3 + installer**

---

## 1. สถาปัตยกรรม

```
Claude Desktop / Claude Code
        │  MCP (JSON-RPC 2.0 ผ่าน stdio)
        ▼
TeklaMcp.exe  (.NET Framework 4.8, x64, console)
 ├─ Mcp/        โปรโตคอล MCP: initialize, tools/list, tools/call, logging
 ├─ Core/       Tool registry, schema, validation, safety gate, audit log
 ├─ Tekla/      Adapter ห่อ Tekla Open API: รันใน process ลูก `TeklaMcp.exe --worker` (thread เดียว, serialize ทุก call);
 │              server เปลี่ยน worker ใหม่เมื่อ Tekla restart หรือยังไม่เปิด (connection ของ API รีเซ็ตเองไม่ได้)
 └─ Tools/      tool แยกตามหน้าที่ (Model, Inspect, Drawing, Export, Data)
        │  Tekla Open API (.NET remoting)
        ▼
TeklaStructures.exe 2026 (ต้องเปิดอยู่ + เปิดโมเดลแล้ว)
```

ทำไมเป็น exe ตัวเดียว:
- Open API ต้องใช้ .NET Framework 4.8 และต้องรันเครื่องเดียวกับ Tekla
- ไม่ต้องมี Node/Python เพิ่ม ติดตั้งง่าย
- stdio ใช้ได้ทั้ง Claude Desktop และ Claude Code

หลักการออกแบบ:
- **stdout ใช้กับ MCP เท่านั้น** log ทั้งหมดลงไฟล์ + stderr
- **Tekla call ทุกตัววิ่งใน worker process แยก (thread เดียว)** กัน race และกู้การเชื่อมต่อได้เมื่อ Tekla restart
- **หน่วย**: Open API ใช้ mm; tool รับ/คืนค่าเป็น mm, kg, degree ระบุหน่วยในทุก field
- **อ้างอิง object ด้วย GUID** (คงที่) และแสดง ID ประกอบ
- tool ทุกตัวคืน JSON มีโครงสร้าง + สรุปข้อความสั้น
- **Tekla DLL โหลดจาก Tekla ที่ติดตั้งในเครื่อง** (AssemblyResolve ชี้ไปโฟลเดอร์ bin ของ Tekla 2026) ไม่แจก DLL ของ Trimble ไปกับตัวติดตั้ง; NuGet ใช้ตอน compile เท่านั้น (CopyLocal=false)

## 2. โครงสร้าง Solution

```
TeklaMcp/
├─ TeklaMcp.sln
├─ src/
│  └─ TeklaMcp/                    # exe หลัก
│     ├─ Program.cs                # args: (ไม่ใส่)=stdio, --register, --unregister, --doctor
│     ├─ Mcp/ JsonRpc.cs, McpServer.cs, Protocol.cs
│     ├─ Core/ ToolRegistry.cs, ITool.cs, SchemaBuilder.cs,
│     │        SafetyGate.cs, ConfirmTokenStore.cs, AuditLog.cs, Settings.cs
│     ├─ Tekla/ TeklaSession.cs, TeklaThread.cs, AssemblyResolver.cs,
│     │         Serializers.cs (ModelObject → JSON), Geometry.cs
│     ├─ Registrar/ ClaudeDesktopConfig.cs, ClaudeCodeConfig.cs
│     └─ Tools/
│        ├─ Session/   connection, model info, save
│        ├─ Query/     find, get, selection
│        ├─ Create/    beam, column, plate, bolt, weld, rebar, grid, assembly
│        ├─ Modify/    modify, move/copy, delete, UDA
│        ├─ Data/      quantity, BOQ, part/assembly list, CSV/XLSX/JSON
│        ├─ Inspect/   (Phase 4)
│        ├─ Drawing/   (Phase 5)
│        ├─ Export/    (Phase 6)
│        └─ Batch/     (Phase 7)
├─ tests/
│  ├─ TeklaMcp.Tests/              # unit test: protocol, schema, safety (ไม่ต้องมี Tekla)
│  └─ integration/                  # สคริปต์ทดสอบกับ Tekla จริง (สำเนาโมเดล)
├─ installer/
│  └─ TeklaMcp.iss                 # Inno Setup 6
├─ docs/ README.th.md, TOOLS.md, SAFETY.md
└─ build.ps1                        # restore → build → test → pack installer
```

Dependencies (ยืนยันตอน Phase 0):
- `Tekla.Structures`, `.Model`, `.Drawing`, `.Catalogs`, `.Datatype` 2026.0.3 (compile only)
- `Newtonsoft.Json`
- `ClosedXML` (สำหรับ .xlsx)
- MCP layer: ตาม D2

## 3. ระบบความปลอดภัย (ตามข้อกำหนด "ไม่แก้/ลบโดยไม่ได้รับอนุญาต")

ระดับ tool:
| ระดับ | ตัวอย่าง | เงื่อนไข |
|---|---|---|
| READ | find, get, report | รันได้ทันที |
| WRITE | create, modify, move | ต้องผ่าน 2 ขั้น |
| DESTRUCTIVE | delete, overwrite file | 2 ขั้น + แสดงรายการ object ครบ |

Flow 2 ขั้น:
1. เรียก tool ด้วย `mode: "preview"` → server ตรวจ input, ตรวจ profile/material ใน catalog, คำนวณผล → คืน **รายการสิ่งที่จะเปลี่ยน + `confirm_token`** (หมดอายุ 10 นาที, ผูกกับ input ชุดนั้น)
2. Claude แสดงให้ผู้ใช้ดู → ผู้ใช้อนุญาต → เรียกซ้ำด้วย `mode: "apply"` + token
3. หลัง apply: `CommitChanges()` → **อ่านค่ากลับจากโมเดลจริง** → เทียบกับที่ขอ → คืนผลตรวจ

เพิ่มเติม:
- Settings `readOnly: true` ปิด WRITE/DESTRUCTIVE ทั้งหมด
- Audit log ทุก WRITE: เวลา, tool, input, GUID ที่กระทบ, ผล → `%LOCALAPPDATA%\TeklaMcp\logs\`
- Open API ไม่มี undo → แนะนำ `save_model` ก่อน batch ใหญ่ (tool เตือนเมื่อจำนวน object > ค่ากำหนด)
- Export: ห้ามเขียนทับไฟล์เดิมถ้าไม่ได้ขอ; จำกัด output path ไว้ใต้โฟลเดอร์ที่อนุญาต
- ทดสอบกับ **สำเนา** โมเดลจริงเท่านั้น ไม่แตะต้นฉบับ

## 4. รายการ MCP Tools

ชื่อขึ้นต้น `tekla_` ทุกตัว

### รอบแรก (Phase 1–3)

**Session**
- `tekla_status` — เชื่อมต่อได้ไหม, เวอร์ชัน, โมเดลที่เปิด, environment, หน่วย
- `tekla_model_info` — ชื่อ/path/project properties/phase
- `tekla_save_model` (WRITE)

**Query**
- `tekla_find_objects` — filter ตามชนิด, profile, material, name, class, phase, UDA, bounding box; มี paging
- `tekla_get_objects` — รายละเอียดเต็มตาม GUID (geometry, report properties, UDA)
- `tekla_get_selection` / `tekla_select_objects`
- `tekla_get_assembly` — main part, secondary, bolts, welds
- `tekla_list_grids`, `tekla_get_workplane`
- `tekla_get_report_properties` — อ่าน property ใดๆ (WEIGHT, LENGTH, AREA …)
- `tekla_list_catalog` — profile / material / bolt ที่มีใน environment Asia/Thailand

**Data**
- `tekla_quantity_summary` — รวมตาม profile/material/assembly: จำนวน, ความยาว, น้ำหนัก, พื้นที่
- `tekla_export_data` — part list / assembly list / BOQ / material list → CSV / XLSX / JSON
- `tekla_run_report` — ใช้ report template ของ Tekla

**Create (WRITE)**
- `tekla_create_beam`, `tekla_create_column`, `tekla_create_plate` (contour)
- `tekla_create_bolts` (array/xy list/circle), `tekla_create_weld`
- `tekla_create_rebar` (single / group), `tekla_create_grid`
- `tekla_create_assembly` / `tekla_add_to_assembly`

**Modify (WRITE / DESTRUCTIVE)**
- `tekla_modify_objects` — profile, material, class, name, position, offset, UDA
- `tekla_move_objects`, `tekla_copy_objects`
- `tekla_delete_objects` (DESTRUCTIVE)
- `tekla_set_workplane`

### รอบถัดไป (Phase 4–7, ยังไม่ทำในรอบแรก)
- Validation: `tekla_validate_model`, `tekla_clash_check`, `tekla_check_geometry`
- Drawing: list / create GA-A-W / open / close / update / issue / content / annotation / properties
- Export: IFC, PDF, DWG/DXF, NC/DSTV
- Batch: `tekla_batch_execute`

## 5. ตัวติดตั้ง (.exe)

เครื่องมือ: Inno Setup 6
- ติดตั้งแบบ per-user ที่ `%LOCALAPPDATA%\Programs\TeklaMcp\` (ไม่ต้องใช้ admin)
- ตรวจก่อนติดตั้ง: Windows x64, .NET Framework 4.8, Tekla Structures 2026 (ถ้าไม่พบ เตือนแต่ให้ติดตั้งต่อได้)
- หน้าเลือก:
  - ☑ ลงทะเบียนกับ Claude Desktop → `TeklaMcp.exe --register desktop`: สำรอง `claude_desktop_config.json` แล้ว merge entry `tekla` (ไม่ลบ server อื่น)
    - path ที่ Desktop (MS Store) อ่านจริง: `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc\claude_desktop_config.json` (ยืนยันแล้ว 2026-11-25); ไฟล์ใน `%APPDATA%\Claude\` และ `Packages\...\LocalCache\Roaming\Claude\` Desktop ไม่อ่าน
    - ชื่อ package (`claude_pzs8sxrjxfjjc`) อาจต่างกันในแต่ละเครื่อง → registrar ต้องค้นหาโฟลเดอร์ `Claude-Data\claude_*` ไม่ hard-code
  - ☐ ลงทะเบียนกับ Claude Code → ถ้าพบคำสั่ง `claude`: `claude mcp add --scope user tekla -- "<path>\TeklaMcp.exe"`
  - Uninstall เอา entry Desktop ออกเฉพาะเมื่อชี้ไปที่ exe ตัวนี้ (หรือไฟล์ที่ไม่มีแล้ว) เพื่อไม่ลบ entry ของ copy อื่น เช่น dev build
  - ☐ โหมดอ่านอย่างเดียว
- Uninstall: เอา entry ออกจาก config (เก็บ backup ไว้)
- `TeklaMcp.exe --doctor` ตรวจปัญหา: หา Tekla, โหลด API, เชื่อมต่อ, path config
- ไม่มี code-signing cert → SmartScreen จะเตือนตอนติดตั้ง; README จะบอกวิธีกด "More info → Run anyway"

## 6. ลำดับงาน (Phases)

| Phase | งาน | ผลลัพธ์ / เกณฑ์ผ่าน | รอบแรก |
|---|---|---|---|
| 0 | เตรียมเครื่อง + API spike | .NET SDK, Inno Setup พร้อม; restore NuGet 2026.0.3 ได้; ยืนยัน API ในข้อ 7 | ✅ เสร็จ |
| 1 | แกนหลัก | MCP handshake ผ่านใน Claude Desktop และ Claude Code; `tekla_status` เชื่อมต่อ Tekla จริงได้; log + safety gate + audit | ✅ เสร็จ |
| 2 | Query + Data | find/get/selection/quantity/export CSV-XLSX-JSON; ตัวเลขตรงกับ report ของ Tekla | ✅ เสร็จ |
| 3 | Create/Modify | สร้าง/แก้/ลบ ทุกชนิดในข้อ 4 ผ่าน preview→apply; อ่านค่ากลับตรง | ✅ เสร็จ (2026-11-26) |
| 7a | Installer | `.exe` ติดตั้งได้; register/unregister Claude Desktop + Code ทำงาน | ✅ เสร็จ (2026-11-26) |
| 4 | Validation | validate + clash + geometry check | — |
| 5 | Drawing | GA/A/W, update, issue, annotation | — |
| 6 | Export | IFC, PDF, DWG/DXF, NC | — |
| 7b | Batch | batch_execute | — |
| 8 | ทดสอบรวม + เอกสาร | test checklist ผ่าน; README ภาษาไทย | ✅ เสร็จ (2026-11-26) — `tests\CHECKLIST.md` |

แต่ละ phase จบด้วย: build ผ่าน → ทดสอบกับสำเนาโมเดล → รายงานผลให้ผู้ใช้ก่อนไป phase ถัดไป

## 7. API ที่ต้องยืนยันใน Phase 0 (ยังไม่ยืนยัน ห้ามสมมติ)

ยืนยันแล้วจากเอกสาร Tekla (ใช้ใน phase หลัง):
- IFC4 export ผ่าน `Operation.CreateIFC4ExportFromAll` / `FromSelected`; 2026 ใช้ Open API แบบเดียวกันได้ทั้ง IFC2x3 และ IFC4
- PDF ผ่าน `DrawingHandler.PrintDrawing(s)` + `DPMPrinterAttributes`

ผล Phase 0 (ตรวจกับ DLL 2026.0.3 และ Tekla ที่ติดตั้ง, 2026-11-25):
- bin: `C:\Program Files\Tekla Structures\2026.0\bin` จาก registry `HKLM\SOFTWARE\Trimble\Tekla Structures\2026.0\setup` (MainDir + TSVersionDir); `Tekla.Structures.dll` อยู่ใน `bin\Net48Runtime\` → resolver ค้นทั้งสองโฟลเดอร์
- NuGet: Model/Drawing/Catalogs/Datatype เป็น netstandard2.0, Tekla.Structures เป็น net48; `ExcludeAssets=runtime` ไม่คัดลอก DLL ของ Tekla/Trimble; binding redirect ที่สร้างตรงกับเวอร์ชันใน Tekla bin
- save: `ModelHandler.Save(Comment, User[, Reason])`; ไม่มี `ModelHandler.IsModelOpen` และ `Operation.SaveModel`
- catalog (SE_Asia): profile 3,952 (เช่น `WF400X200X8X13`, `H600X300X170`), material 167 (มี SS400, SM490, SN400), bolt 193 (8.8XOX, A325…), rebar grade มี SD40, SR24; ชื่อ profile ต้องใช้ `GetLibraryProfileItems()` → `LibraryProfileItem.ProfileName`
- environment: ไม่มี API ตรง → อ่านจาก path ใน `XS_SYSTEM`
- Claude Desktop config: ดูข้อ 5
- `claude mcp add -s user|local <name> -- <exe>` ใช้ได้บน Windows (Claude Code 2.1.332)
- ข้อควรระวัง: API เขียน "Connection failed" ลง stdout เมื่อ Tekla ปิด; static ctor ของ `TeklaStructuresInfo` พังถาวรถ้าเรียกตอน Tekla ปิด

ต้องตรวจ (phase หลัง):
- DWG/DXF และ NC/DSTV export — API ตรงหรือ macro (`Operation.RunMacro`)
- ClashCheckHandler + events จาก process ภายนอก
- การสร้าง drawing แต่ละชนิด + attribute file

ถ้า API ตัวไหนใช้ไม่ได้ → แจ้งผู้ใช้พร้อมทางเลือก ไม่ทำแทนเอง

## 8. การตัดสินใจ

| # | เรื่อง | ผล |
|---|---|---|
| D1 | ขอบเขตรอบแรก | Phase 0–3 + installer |
| D2 | MCP layer | อนุมัติ: เขียนเองบน Newtonsoft.Json 13.0.4 |
| D3 | Tekla DLL | โหลดจาก Tekla ในเครื่อง |
| D4 | Code signing | ไม่มี cert |
| D5 | โมเดลทดสอบ | `C:\TeklaStructuresModels\SSMC_Tekla` |
| D6 | Environment | อนุมัติ: SE_Asia |
| D7 | ภาษาข้อความใน tool | อนุมัติ: อังกฤษ |
| D8 | รอบสอง (2026-11-26) | อนุมัติ Phase 4–7b; แต่ละ phase ยังต้องรายงาน + รออนุมัติ |
| D9 | DWG/DXF export | ไม่มี API → ใช้ macro ผ่าน `Operation.RunMacro` (ต้อง spike ยืนยัน) |
| D10 | Numbering | ไม่มี API สั่ง numbering → เพิ่ม `tekla_run_numbering` (WRITE, ผ่าน macro) |
| D11 | Validation | `tekla_validate_model` ตรวจเฉพาะ numbering / assembly; ตัด `tekla_check_geometry` ออก |
| D12 | โมเดลทดสอบ Phase 4–6 | ใช้ SSMC_Tekla ตามเดิม (drawing/numbering ที่ทดสอบสร้างจะอยู่ในโมเดล ถ้าไม่ลบ) |
| D13 | PDF ของ drawing | `PrintDrawing` ผ่าน API ไม่เขียนไฟล์ (Educational) → ผู้ใช้เลือกใช้ macro ผ่าน UI: Document manager → Print (WPF macro) |
| D14 | IFC2x3 | ใช้ `CreateIFC2x3Export` (API IFC4 ที่ระบุเวอร์ชัน 2x3 ยังได้ IFC4) |

## 9. ความเสี่ยง

- Open API ต้องให้ Tekla เปิดอยู่ ถ้า Tekla ปิด/ค้าง tool จะคืน error ชัดเจน ไม่ค้าง
- โมเดลใหญ่: query ต้องมี paging/limit กันข้อมูลล้น context
- ไม่มี undo ผ่าน API → พึ่ง preview + save ก่อนแก้
- ไม่มี cert → บางองค์กรอาจบล็อกการติดตั้ง exe ที่ไม่เซ็น
