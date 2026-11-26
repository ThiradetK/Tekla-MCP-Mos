# Tekla MCP Server

เชื่อม **Claude Desktop** และ **Claude Code** เข้ากับ **Tekla Structures 2026** ผ่าน Tekla Open API
ให้ Claude ค้นหา อ่านข้อมูล สรุปปริมาณ ส่งออกตาราง และสร้าง/แก้ไขชิ้นงานในโมเดลได้ โดยทุกคำสั่งที่เปลี่ยนโมเดลต้องให้ผู้ใช้อนุมัติก่อนเสมอ

- 44 tools: อ่านอย่างเดียว 21 ตัว (รวม validate, clash check, รายการ drawing, export IFC/NC/PDF/DWG/DXF), เปลี่ยนโมเดล/drawing 23 ตัว (รายละเอียดใน [TOOLS.md](TOOLS.md))
- กฎความปลอดภัย: [SAFETY.md](SAFETY.md)
- หน่วย: ความยาว mm, น้ำหนัก kg, มุม องศา; พิกัดเป็น global เสมอ

## ความต้องการของระบบ

| รายการ | ต้องมี |
|---|---|
| Windows | 10/11 แบบ 64-bit |
| .NET Framework | 4.8 ขึ้นไป (Windows 10/11 มีอยู่แล้ว) |
| Tekla Structures | 2026 (ทดสอบกับ 2026 SP6.1, environment SE_Asia) |
| Claude | Claude Desktop และ/หรือ Claude Code |

ตัวติดตั้งไม่มี DLL ของ Tekla/Trimble โปรแกรมโหลดจาก Tekla ที่ติดตั้งในเครื่องตอนทำงาน

## ติดตั้ง

1. ดาวน์โหลด `TeklaMcp-Setup-<version>.exe` แล้วดับเบิลคลิก
2. Windows อาจขึ้นหน้าจอ SmartScreen ("Windows protected your PC") เพราะไฟล์ยังไม่มี code-signing certificate
   ให้กด **More info → Run anyway**
3. เลือกตัวเลือก:
   - ☑ **Register with Claude Desktop** (ค่าเริ่มต้น)
   - ☐ **Register with Claude Code** ต้องมีคำสั่ง `claude` ในเครื่อง
   - ☐ **Read-only mode** ปิดทุก tool ที่เปลี่ยนโมเดล
4. ติดตั้งแบบรายผู้ใช้ ไม่ต้องใช้สิทธิ์ admin ไฟล์อยู่ที่ `%LOCALAPPDATA%\Programs\TeklaMcp\`
5. **Quit Claude Desktop จากไอคอนใน tray** แล้วเปิดใหม่ (แค่ปิดหน้าต่างไม่พอ)

ตอนลงทะเบียน โปรแกรมจะสำรองไฟล์ `claude_desktop_config.json` เดิมไว้เป็น `claude_desktop_config.json.bak-<วันเวลา>` และเพิ่มเฉพาะ entry `tekla` ส่วน MCP server อื่นยังอยู่ครบ

## ตรวจว่าใช้งานได้

1. เปิด Tekla Structures 2026 และเปิดโมเดล
2. ในแชต Claude พิมพ์: `ใช้ tekla_status`
3. ถ้ามีปัญหา เปิด PowerShell แล้วรัน

```powershell
& "$env:LOCALAPPDATA\Programs\TeklaMcp\TeklaMcp.exe" --doctor
```

`--doctor` ตรวจ: Windows 64-bit, .NET 4.8, การติดตั้ง Tekla 2026, การเชื่อมต่อ Open API, ไฟล์ settings, และ entry ใน config ของ Claude Desktop / Claude Code

## ตัวอย่างคำสั่งในแชต

อ่านข้อมูล (ไม่เปลี่ยนโมเดล):
- "ตรวจ numbering และ assembly ของโมเดล"
- "ทำ clash check ชิ้นที่เลือก"
- "สรุปปริมาณคอนกรีตแยกตาม profile และ material"
- "สรุปเหล็กเสริมแยกตามขนาด น้ำหนักรวมเท่าไร"
- "ส่งออก BOQ เป็น Excel ชื่อ boq_tower_a.xlsx"
- "ชิ้นที่เลือกอยู่ใน Tekla ตอนนี้มีอะไรบ้าง"
- "เลือกคานทุกตัวที่เป็น WF400X200X8X13 ใน Tekla"
- "รัน report Material List"

เปลี่ยนโมเดล (Claude จะแสดง preview ก่อน แล้วรอให้คุณอนุมัติ):
- "สร้างคาน WF400X200X8X13 SS400 จาก (0,0,3000) ถึง (6000,0,3000)"
- "เปลี่ยน class ของชิ้นที่เลือกเป็น 5"
- "ลบชิ้นที่เลือก" → preview จะแสดงรายการทุกชิ้นที่จะถูกลบ รวม bolt/weld/rebar ที่ติดอยู่
- "save โมเดล"

## การอนุมัติก่อนเปลี่ยนโมเดล

ทุก tool ที่เปลี่ยนโมเดลทำงาน 2 ขั้น:
1. **preview**: Claude ส่งคำขอแบบ preview → ได้รายการสิ่งที่จะเปลี่ยน + `confirm_token` โมเดลยังไม่เปลี่ยน
2. **apply**: ทำเมื่อคุณพิมพ์อนุมัติในแชตเท่านั้น ต้องใช้ token เดิม และ argument ต้องเหมือนตอน preview ทุกตัว

หลัง apply โปรแกรม commit แล้วอ่านค่ากลับจากโมเดลมาเทียบกับที่ขอ ถ้าไม่ตรงจะแจ้งใน `verification`
**Open API ไม่มี undo** ให้ save โมเดลก่อนเปลี่ยนแปลงจำนวนมาก (มีคำเตือนอัตโนมัติเมื่อเกิน 50 ชิ้น)

## ตั้งค่า (`%LOCALAPPDATA%\TeklaMcp\settings.json`)

ไม่มีไฟล์ = ใช้ค่าเริ่มต้น แก้แล้วต้อง restart Claude

```json
{
  "readOnly": false,
  "logLevel": "info",
  "teklaTimeoutSeconds": 60,
  "confirmTokenMinutes": 10,
  "exportDir": "C:\\Users\\<ชื่อ>\\Documents\\TeklaMcp\\exports"
}
```

| key | ความหมาย |
|---|---|
| `readOnly` | `true` = ปฏิเสธทุก tool ที่เปลี่ยนโมเดล (ตั้งได้ด้วย `--readonly on` / `--readonly off`) |
| `logLevel` | `debug` / `info` / `warning` / `error` |
| `teklaTimeoutSeconds` | เวลาสูงสุดต่อ 1 คำสั่ง Tekla (วินาที) โมเดลใหญ่อาจต้องเพิ่ม |
| `confirmTokenMinutes` | อายุของ `confirm_token` (นาที) |
| `exportDir` | โฟลเดอร์ไฟล์ที่ `tekla_export_data` สร้าง (ไม่เขียนทับไฟล์เดิม) |

ถ้าไฟล์ settings อ่านไม่ได้ (JSON เสีย) โปรแกรมจะเปิดแบบ read-only เพื่อความปลอดภัย

## คำสั่ง command line

| คำสั่ง | ทำอะไร |
|---|---|
| `TeklaMcp.exe` | เริ่ม MCP server (Claude เรียกเอง ไม่ต้องรันมือ) |
| `TeklaMcp.exe --register desktop\|code\|all` | ลงทะเบียน server `tekla` กับ Claude Desktop / Claude Code |
| `TeklaMcp.exe --unregister desktop\|code\|all` | เอา entry ออก |
| `TeklaMcp.exe --readonly on\|off` | เปิด/ปิดโหมดอ่านอย่างเดียว |
| `TeklaMcp.exe --doctor` | ตรวจปัญหา |

Claude Code ลงทะเบียนแบบ user scope (`claude mcp add --scope user tekla -- "<path>\TeklaMcp.exe"`)

## Log

- Log: `%LOCALAPPDATA%\TeklaMcp\logs\`
- Audit log ของทุกการเปลี่ยนโมเดล: `%LOCALAPPDATA%\TeklaMcp\logs\audit-yyyyMM.jsonl` (1 บรรทัดต่อ 1 ครั้ง: เวลา, tool, argument, ผล)

## ถอนการติดตั้ง

Settings → Apps → Installed apps → **Tekla MCP Server** → Uninstall
จะเอา entry `tekla` ออกจาก Claude Desktop (เฉพาะ entry ที่ชี้มาที่ตัวที่ติดตั้งนี้) และ Claude Code (user scope)
ไฟล์ settings และ log ใน `%LOCALAPPDATA%\TeklaMcp\` ยังเก็บไว้ ลบเองได้

## แก้ปัญหา

| อาการ | วิธีแก้ |
|---|---|
| Claude ไม่เห็น tool `tekla_*` | Quit Claude Desktop จาก tray แล้วเปิดใหม่; รัน `--doctor` ดูว่ามี entry ใน config |
| `not_connected` | เปิด Tekla Structures 2026 และเปิดโมเดล; โปรแกรมเชื่อมต่อใหม่ให้เองในคำสั่งถัดไป |
| `tekla_timeout` | Tekla ไม่ว่าง (มีหน้าต่าง dialog ค้าง หรือโมเดลใหญ่) ปิด dialog แล้วลองใหม่ หรือเพิ่ม `teklaTimeoutSeconds` |
| `read_only` | ตั้ง `readOnly` ไว้ รัน `--readonly off` แล้ว restart Claude |
| `confirm_token_*` | token หมดอายุ ใช้ไปแล้ว หรือ argument เปลี่ยน ให้ Claude ทำ preview ใหม่ |
| Claude Desktop แบบ Microsoft Store | config จริงอยู่ที่ `%LOCALAPPDATA%\Claude-Data\claude_*\claude_desktop_config.json` (โปรแกรมหาให้เอง) |

## ข้อจำกัดที่รู้แล้ว

- แก้ bolt group ด้วย `tekla_modify_objects` ไม่ได้ (เปลี่ยนได้เฉพาะ uda) ให้ลบแล้วสร้างใหม่
- `tekla_create_assembly` ไม่ย้ายชิ้นที่เป็น secondary ของ assembly อื่นอยู่แล้ว (เช่น เชื่อมแบบ shop weld) Tekla ไม่ยอมย้าย โปรแกรมจะแจ้งตั้งแต่ preview
- โมเดลใหญ่: คำสั่งแรกที่อ่าน catalog profile ใช้ราว 13 วินาที (ครั้งต่อไปใช้ cache); สรุปเหล็กเสริมหมื่นเส้นใช้ราว 30 วินาที
- `tekla_clash_check` ใช้ selection ใน Tekla ระหว่างตรวจ (คืน selection เดิมให้หลังตรวจ) และหยุดเมื่อเกิน `teklaTimeoutSeconds` - 5 วินาที
- Drawing ไม่มี GUID ใน Open API จึงอ้างด้วย `drawing_id` = "ชนิด|mark|เวลาสร้าง" จาก `tekla_list_drawings`
- `tekla_run_numbering` ไม่มี API ตรง จึงเขียน macro ชั่วคราวลงโฟลเดอร์ `XS_MACRO_DIRECTORY\modeling` แล้วลบทิ้งหลังรัน
- Export IFC (2x3 / 4 / 4.3) และ NC (DSTV) เขียนลงโฟลเดอร์ใหม่ใน `exportDir` ทุกครั้ง (ไม่เขียนทับ)
- Export drawing เป็น PDF / DWG / DXF (`tekla_export_drawings`, ครั้งละ 1–5 แผ่น): Open API ทำไม่ได้ จึงสั่งผ่าน Document manager ของ Tekla ด้วย macro ชั่วคราว
  - ระหว่าง export หน้าต่าง Document manager / Print / Export ของ Tekla จะเปิดขึ้นชั่วครู่แล้วปิดเอง และจะจำโฟลเดอร์/ตัวเลือกล่าสุดไว้
  - ห้ามเปิด drawing ค้างใน editor; โปรแกรมสลับ Document manager ไปหมวด "All drawings" และตรวจชื่อไฟล์กับ plot file name ของ drawing ทุกครั้ง ถ้าแถวที่เลือกเป็นแผ่นอื่น (เช่น list ถูก sort ไว้) จะลบไฟล์นั้นแล้วลองแถวถัดไป (สูงสุด 10 แถวต่อแผ่น)
  - license Educational ใส่ลายน้ำใน PDF
- ยังไม่มี: batch (Phase 7b)

## Build จาก source

ต้องมี .NET SDK (ทดสอบกับ 10.0.404) และ Inno Setup 6

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Installer
```

ได้ `src\TeklaMcp\bin\Release\net48\TeklaMcp.exe` และ `dist\TeklaMcp-Setup-<version>.exe`
`-Smoke` รัน stdio smoke test เพิ่ม (ต้องเปิด Tekla) ถ้า build ล้มเพราะไฟล์ถูกล็อก ให้ปิด process `TeklaMcp` ก่อน
