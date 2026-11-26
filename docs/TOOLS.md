# Tools

Generated from the server's `tools/list` (TeklaMcp 0.1.0, 30 tools). Units: mm, kg, degrees; coordinates are global.

Every WRITE / DESTRUCTIVE tool also takes `mode` (`"preview"` | `"apply"`, required) and `confirm_token` (required for apply). See [SAFETY.md](SAFETY.md).

## Session

| Tool | Level | What it does | Parameters (* = required) |
|---|---|---|---|
| `tekla_status` | READ | Check the connection to Tekla Structures 2026: whether Tekla is running, program version, environment, the open model (name, path, current phase), units (mm, kg, degree), and whether this server is read-only. Call this first if other tools report connection errors. Read-only; does not change the model. | - |
| `tekla_save_model` | WRITE | Save the open Tekla model (recommended before large changes; the Open API has no undo). | `comment` |
| `tekla_set_workplane` | WRITE | Set Tekla's current work plane (affects the Tekla UI only; tekla_* tools always use global coordinates). global=true restores the global plane. | `global`, `origin_mm`, `axis_x`, `axis_y` |
| `tekla_get_workplane` | READ | Current Tekla work plane (origin in mm and unit axes, expressed in global coordinates) and whether it is the global plane. | - |

## Query

| Tool | Level | What it does | Parameters (* = required) |
|---|---|---|---|
| `tekla_find_objects` | READ | Find objects in the open Tekla model by type, profile, material, name, class, phase, user attribute, bounding box or current selection. Returns a page of brief rows (guid, id, type, name, profile, material, class, phase, positions) plus the total match count. Set include_quantities for weight_kg, length_mm, area_mm2, volume_mm3 per object (slower). Coordinates are global, in mm. | `types`, `profile`, `material`, `name`, `class`, `phase`, `uda`, `selection`, `bbox`, `offset`, `limit`, `include_quantities` |
| `tekla_get_objects` | READ | Full details for objects by GUID: brief fields, quantities (weight_kg, length_mm, area_mm2, volume_mm3), geometry (beam start/end, plate contour, bolt positions, bounding box), all user-defined attributes, and optional report properties. Coordinates are global, in mm. | `guids`*, `report_properties` |
| `tekla_get_report_properties` | READ | Read any Tekla report properties for objects by GUID (e.g. WEIGHT kg, WEIGHT_GROSS kg, LENGTH mm, AREA mm2, VOLUME mm3, PROFILE, MATERIAL, PART_POS, ASSEMBLY_POS). Each value is returned as number or text as Tekla provides it; null when the property is not available for that object. | `guids`*, `names`* |
| `tekla_get_selection` | READ | List the objects currently selected in the Tekla model view (brief rows, paged). Coordinates are global, in mm. | `offset`, `limit`, `include_quantities` |
| `tekla_select_objects` | READ | Select objects by GUID in the Tekla model view (replaces the current selection). Changes only the UI selection, not the model. | `guids`* |
| `tekla_get_assembly` | READ | Assembly of a part, rebar or assembly GUID: the assembly, its main part, secondary parts (with quantities), sub-assemblies, bolts, welds, and the count/weight of reinforcement in its parts. Coordinates are global, in mm. | `guid`* |
| `tekla_list_grids` | READ | List grids with their raw coordinate/label strings and every grid plane (label, axis x/y/z, coordinate_mm along that axis). Coordinates are global, in mm. | - |
| `tekla_list_catalog` | READ | List profiles, materials, bolts or rebar sizes available in the current Tekla environment (SE_Asia), with wildcard name filter and paging. Use it to check a profile/material name before creating objects. | `kind`*, `filter`, `offset`, `limit` |

## Data / reports

| Tool | Level | What it does | Parameters (* = required) |
|---|---|---|---|
| `tekla_quantity_summary` | READ | Sum quantities of filtered objects grouped by chosen fields: object_count, bar_count (rebar), total_length_mm, total_weight_kg, total_area_mm2, total_volume_mm3, plus a grand total. Values come from Tekla report properties WEIGHT, LENGTH, AREA, VOLUME. Default: all parts grouped by report_profile and material, which matches Tekla's Material List report (report_profile = Tekla PROFILE report property, e.g. "250x500"; profile = the entered profile, e.g. "500*250"). For reinforcement use types ["rebar"] (profile = bar size, material = grade). | `types`, `profile`, `material`, `name`, `class`, `phase`, `uda`, `selection`, `bbox`, `group_by` |
| `tekla_export_data` | READ | Export a part list, assembly list, material list (grouped by material) or BOQ (grouped by report_profile + material, as in Tekla's Material List) of filtered objects to CSV, XLSX or JSON. Files go to the export folder (settings.json exportDir, default Documents\TeklaMcp\exports); an existing file is never overwritten. Units: mm, kg, mm2, mm3. Creates a file only; the model is not changed. | `types`, `profile`, `material`, `name`, `class`, `phase`, `uda`, `selection`, `bbox`, `kind`*, `format`*, `file_name`* |
| `tekla_run_report` | READ | Run a Tekla report template (e.g. "Part List", "Material List", "Part List.xls") for the whole model or the current selection. The file is written to the model's Reports folder (never overwrites); text output is previewed. An unknown template name returns the list of available templates. | `template`*, `file_name`, `selected`, `title1`, `title2`, `title3` |

## Create

| Tool | Level | What it does | Parameters (* = required) |
|---|---|---|---|
| `tekla_create_beam` | WRITE | Create a beam between two points. | `profile`*, `material`*, `name`, `class`, `finish`, `position`, `uda`, `start_mm`*, `end_mm`* |
| `tekla_create_column` | WRITE | Create a column from a base point up to a top point (or base + height_mm along global Z). | `profile`*, `material`*, `name`, `class`, `finish`, `position`, `uda`, `base_mm`*, `top_mm`, `height_mm` |
| `tekla_create_plate` | WRITE | Create a contour plate from 3+ corner points; profile gives the thickness (e.g. "PL20"). | `profile`*, `material`*, `name`, `class`, `finish`, `position`, `uda`, `contour_mm`* |
| `tekla_create_bolts` | WRITE | Create a bolt group connecting parts. kind=array: dist_x_mm/dist_y_mm are spacings (Tekla bolt array); xy_list: one x/y pair per bolt; circle: number_of_bolts + diameter_mm. Bolt standard and size are checked against the bolt catalog. Defaults: bolt and nut on, washers off, holes in all parts. | `kind`*, `part_to_bolt_to_guid`*, `part_to_be_bolted_guid`, `first_point_mm`*, `second_point_mm`*, `bolt_standard`*, `bolt_size_mm`*, `dist_x_mm`, `dist_y_mm`, `number_of_bolts`, `diameter_mm`, `tolerance_mm`, `bolt`, `nut`, `washers` |
| `tekla_create_weld` | WRITE | Create a weld between a main part and a secondary part. Weld types: fillet, bevel_groove_single_v_butt, square_groove_square_butt, plug, ... (Tekla WeldTypeEnum names without WELD_TYPE_). | `main_part_guid`*, `secondary_part_guid`*, `size_above_mm`*, `type_above`, `size_below_mm`, `type_below`, `shop_weld` |
| `tekla_create_rebar` | WRITE | Create a single rebar or a rebar group in a concrete part. points_mm is the bar shape polygon. For a group the shape is distributed from start_mm to end_mm at target spacing_mm. Grade + size are checked against the rebar catalog (e.g. SD40 16, SR24 9). | `kind`*, `father_part_guid`*, `points_mm`*, `grade`*, `size`*, `name`, `class`, `start_mm`, `end_mm`, `spacing_mm` |
| `tekla_create_grid` | WRITE | Create a grid. Coordinates use Tekla grid syntax (first value absolute, then spacings, n*spacing allowed), e.g. coordinate_x "0 4*6000", label_x "A B C D E". | `coordinate_x`*, `coordinate_y`*, `coordinate_z`*, `label_x`*, `label_y`*, `label_z`*, `origin_mm`, `name` |
| `tekla_create_assembly` | WRITE | Build an assembly: the main part's assembly receives the given parts as secondaries (optionally renamed). | `main_part_guid`*, `secondary_part_guids`*, `name` |
| `tekla_add_to_assembly` | WRITE | Add parts as secondaries to an existing assembly (given by the assembly GUID or any part in it). | `target_guid`*, `part_guids`* |

## Validation

| Tool | Level | What it does | Parameters (* = required) |
|---|---|---|---|
| `tekla_validate_model` | READ | Check numbering and assemblies of filtered parts (default: all parts): parts whose numbering is not up to date, parts without an assembly mark or part mark (Tekla shows "(?)"), and model-wide assemblies without a main part. Returns counts and a page of the parts with issues. Read-only; does not run numbering. | `types`, `profile`, `material`, `name`, `class`, `phase`, `uda`, `selection`, `bbox`, `offset`, `limit` |
| `tekla_clash_check` | READ | Run Tekla's clash check between native parts of the given objects (guids, or a filter; default: all parts). Returns each clash with type (clash, cutthrough, isinside, exactmatch, mindistance, ...) and both objects (no clash size: the API does not provide it). min_distance_mm > 0 also reports objects closer than that distance. Tekla checks the objects selected in the model view, so the selection is replaced during the run and restored afterwards. The model is not changed. The run is stopped after 55 s (settings.json teklaTimeoutSeconds - 5); then results are partial. | `types`, `profile`, `material`, `name`, `class`, `phase`, `uda`, `selection`, `bbox`, `offset`, `limit`, `guids`, `min_distance_mm` |

## Modify

| Tool | Level | What it does | Parameters (* = required) |
|---|---|---|---|
| `tekla_modify_objects` | WRITE | Change attributes of objects by GUID (max 500). Parts: profile, material, name, class, finish, position. Rebar: size, grade, name, class. Any object: uda. The preview shows from -> to for every object. | `guids`*, `set`* |
| `tekla_move_objects` | WRITE | Move objects by a vector (global mm). Max 500 objects. | `guids`*, `vector_mm`* |
| `tekla_copy_objects` | WRITE | Copy objects by a vector (global mm); count > 1 makes repeated copies at 1x, 2x, ... the vector. Returns the new GUIDs. | `guids`*, `vector_mm`*, `count` |
| `tekla_delete_objects` | DESTRUCTIVE | Delete objects by GUID (max 500). Bolts, welds and rebar attached to deleted parts may be deleted too; the preview lists them. | `guids`* |

