# ความปลอดภัยของโมเดล

Tekla Open API **ไม่มี undo** ทุกการเปลี่ยนแปลงที่ apply แล้วย้อนกลับเองไม่ได้ เอกสารนี้อธิบายว่าโปรแกรมป้องกันอะไรให้บ้าง และผู้ใช้ควรทำอะไร

## สิ่งที่ผู้ใช้ควรทำ

1. **ทดลองกับสำเนาโมเดลก่อน** อย่าทดลองกับโมเดลงานจริง
2. **Save โมเดลก่อนเปลี่ยนแปลงจำนวนมาก** (บอก Claude ว่า "save โมเดล" หรือ save ใน Tekla) โปรแกรมเตือนเมื่อการเปลี่ยนแปลงครั้งเดียวเกิน 50 ชิ้น
3. **อ่าน preview ก่อนอนุมัติทุกครั้ง** โดยเฉพาะรายการชิ้นที่จะถูกลบ
4. ถ้าต้องการให้ Claude อ่านอย่างเดียว ให้เปิด read-only: `TeklaMcp.exe --readonly on`

## ระดับของ tool

| ระดับ | tool | เงื่อนไข |
|---|---|---|
| READ | 18 ตัว (รวม `tekla_open_drawing` ที่เปิด drawing editor อย่างเดียว ไม่ save) เช่น `tekla_find_objects`, `tekla_quantity_summary`, `tekla_clash_check` | รันได้ทันที ไม่เปลี่ยนโมเดล (`tekla_select_objects` เปลี่ยนแค่การเลือกในหน้าจอ, `tekla_clash_check` เปลี่ยน selection ชั่วคราวแล้วคืนให้, `tekla_export_data` / `tekla_run_report` สร้างไฟล์ใหม่ ไม่เขียนทับ) |
| WRITE | 21 ตัว: สร้าง / แก้ / ย้าย / คัดลอก / assembly / work plane / save / numbering / drawing (สร้าง แก้ title issue update ใส่ text ปิด) | ต้อง preview → อนุมัติ → apply |
| DESTRUCTIVE | `tekla_delete_objects`, `tekla_delete_drawings` | เหมือน WRITE และ preview ต้องแสดงรายการทุกชิ้นที่จะถูกลบ |

## ขั้นตอน preview → apply

1. Claude เรียก tool ด้วย `mode: "preview"`
   - โมเดล **ไม่เปลี่ยน**
   - ได้สิ่งที่จะเปลี่ยน (ค่าเดิม → ค่าใหม่, จำนวนชิ้น, รายการชิ้นที่จะลบ) + `confirm_token`
   - ตรวจชื่อ profile / material / bolt / rebar กับ catalog ของ environment ก่อน ถ้าไม่มีจริงจะปฏิเสธตั้งแต่ขั้นนี้
2. Claude ต้องแสดง preview ให้ผู้ใช้ และ **apply ได้เมื่อผู้ใช้อนุมัติในแชตเท่านั้น**
3. Claude เรียก tool อีกครั้งด้วย `mode: "apply"` + `confirm_token`
   - token ใช้ได้ **ครั้งเดียว** อายุ 10 นาที (ตั้งได้ที่ `confirmTokenMinutes`)
   - token ผูกกับชื่อ tool และ argument ทุกตัว ถ้า argument ต่างจากตอน preview แม้แต่ตัวเดียว → ปฏิเสธ (`confirm_token_arguments_changed`)
   - ไม่มี token → ปฏิเสธ (`confirm_token_missing`)
4. หลัง apply: `CommitChanges()` → อ่านชิ้นงานกลับจากโมเดล → เทียบกับที่ขอ → รายงานใน `verification { ok, mismatches }`

การ apply จะไม่ถูกลองซ้ำอัตโนมัติ แม้การเชื่อมต่อกับ Tekla หลุดกลางทาง เพื่อไม่ให้เกิดการเปลี่ยนแปลงซ้ำ 2 ครั้ง

## ขีดจำกัด

- เปลี่ยน / ย้าย / คัดลอก / ลบ ได้สูงสุด 500 ชิ้นต่อครั้ง
- ลบชิ้น part แล้ว bolt / weld / rebar ที่ติดอยู่อาจถูก Tekla ลบตามไปด้วย preview แสดงรายการเหล่านี้แยกไว้
- tool ทุกตัวใช้พิกัด global เสมอ (mm) ไม่ขึ้นกับ work plane ใน Tekla

## Read-only mode

- `settings.json` → `"readOnly": true` หรือ `TeklaMcp.exe --readonly on` แล้ว restart Claude
- ทุก tool ระดับ WRITE / DESTRUCTIVE ถูกปฏิเสธ (`read_only`) ทั้ง preview และ apply
- ถ้า `settings.json` อ่านไม่ได้ (JSON เสีย) โปรแกรมจะเข้า read-only อัตโนมัติ

## Audit log

ทุกครั้งที่มีการเรียก apply (ทั้งสำเร็จ ล้มเหลว และถูกปฏิเสธ) จะถูกบันทึกที่
`%LOCALAPPDATA%\TeklaMcp\logs\audit-yyyyMM.jsonl` 1 บรรทัดต่อ 1 ครั้ง:

| field | ความหมาย |
|---|---|
| `time` | เวลา |
| `tool`, `level` | ชื่อ tool และระดับ |
| `outcome` | `applied` / `failed` / `refused` |
| `reason` | เหตุผลที่ล้มเหลว / ถูกปฏิเสธ |
| `user`, `machine` | ผู้ใช้ Windows และชื่อเครื่อง |
| `input` | argument ที่ส่งมา (`confirm_token` ถูกแทนด้วย `***`) |
| `affected`, `result` | GUID ที่ถูกเปลี่ยน และผลเต็มรวม verification |

## สิ่งที่โปรแกรมไม่ทำ

- ไม่ส่งข้อมูลโมเดลออกนอกเครื่อง นอกจากสิ่งที่ Claude อ่านผ่าน tool ในการสนทนา
- ไม่ save โมเดลเอง (ต้องเรียก `tekla_save_model` และผ่านการอนุมัติ)
- ไม่เขียนทับไฟล์ export / report ที่มีอยู่แล้ว
