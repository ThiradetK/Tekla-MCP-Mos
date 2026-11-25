using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Modify
{
    /// <summary>WRITE/DESTRUCTIVE tool backed by the worker "write" op. The SafetyGate calls Preview or Apply.</summary>
    internal sealed class WorkerWriteTool : IWriteTool
    {
        private readonly TeklaWorker _worker;
        private readonly string _action;
        private readonly Func<JObject, JObject> _map;

        public WorkerWriteTool(TeklaWorker worker, string name, string title, string description, ToolLevel level,
                               JObject properties, string[] required, string action, Func<JObject, JObject> map = null)
        {
            _worker = worker; Name = name; Title = title; Level = level; _action = action; _map = map ?? (a => a);
            Description = description + WriteTools.TwoStep + (level == ToolLevel.Destructive ? " The preview lists every object that will be deleted." : "");
            InputSchema = S.ForWrite(properties, required);
        }

        public string Name { get; }
        public string Title { get; }
        public string Description { get; }
        public ToolLevel Level { get; }
        public JObject InputSchema { get; }

        public ToolResult Preview(JObject args) => Run(args, "preview");
        public ToolResult Apply(JObject args) => Run(args, "apply");

        private ToolResult Run(JObject args, string mode)
        {
            var p = (JObject)args.DeepClone();
            p.Remove(S.ModeProperty); p.Remove(S.TokenProperty);
            var req = new JObject { ["action"] = _action, ["mode"] = mode, ["params"] = _map(p) };
            // A model-changing call must never be re-sent automatically after a worker restart.
            JObject r = _worker.Call("write", req, retryOnReset: mode != "apply");
            return ToolResult.Ok((string)r["summary"] ?? _action, r);
        }
    }

    /// <summary>Phase 3 tools (PLAN §4 "Create" / "Modify" + tekla_save_model).</summary>
    internal static class WriteTools
    {
        public const string TwoStep = " Two steps: call with mode=\"preview\" (nothing changes; returns the planned change and a confirm_token), show it to the user, " +
                                      "then call again with identical arguments, mode=\"apply\" and the confirm_token ONLY after the user explicitly approves. " +
                                      "After apply the objects are committed and read back; 'verification' reports any mismatch. Global coordinates in mm; there is no undo.";

        private static JObject Pt(string d) => S.Array(S.Number("mm"), d + " [x, y, z] in global mm.");
        private static JObject Guids(string d) => S.Array(S.String("GUID"), d);

        private static JObject Position() => S.Object(new JObject
        {
            ["plane"] = S.String("On-plane position.", new[] { "middle", "left", "right" }),
            ["plane_offset_mm"] = S.Number("On-plane offset, mm."),
            ["rotation"] = S.String("Rotation.", new[] { "front", "top", "back", "below" }),
            ["rotation_offset_deg"] = S.Number("Rotation offset, degrees."),
            ["depth"] = S.String("Depth position.", new[] { "middle", "front", "behind" }),
            ["depth_offset_mm"] = S.Number("Depth offset, mm.")
        });

        private static JObject Uda() => S.Array(S.Object(new JObject
        {
            ["name"] = S.String("User-defined attribute name."),
            ["value"] = S.String("Value as text."),
            ["type"] = S.String("Value type. Default string.", new[] { "string", "double", "integer" })
        }, "name", "value"), "User-defined attributes to set.");

        private static JObject PartProps(JObject extra)
        {
            var p = new JObject
            {
                ["profile"] = S.String("Profile from the catalog (e.g. \"WF400X200X8X13\") or parametric (e.g. \"500*250\", \"PL20\"). Checked against the SE_Asia catalog."),
                ["material"] = S.String("Material from the catalog (e.g. \"SS400\", \"C40\")."),
                ["name"] = S.String("Part name."),
                ["class"] = S.String("Class number as text (e.g. \"3\")."),
                ["finish"] = S.String("Finish."),
                ["position"] = Position(),
                ["uda"] = Uda()
            };
            foreach (var x in extra.Properties()) p[x.Name] = x.Value;
            return p;
        }

        public static IEnumerable<ITool> Create(TeklaWorker w)
        {
            yield return new WorkerWriteTool(w, "tekla_create_beam", "Create beam",
                "Create a beam between two points.", ToolLevel.Write,
                PartProps(new JObject { ["start_mm"] = Pt("Start point"), ["end_mm"] = Pt("End point") }),
                new[] { "start_mm", "end_mm", "profile", "material" }, "create_beam");

            yield return new WorkerWriteTool(w, "tekla_create_column", "Create column",
                "Create a column from a base point up to a top point (or base + height_mm along global Z).", ToolLevel.Write,
                PartProps(new JObject { ["base_mm"] = Pt("Base point"), ["top_mm"] = Pt("Top point"), ["height_mm"] = S.Number("Height along +Z, mm (used when top_mm is not given).", 1) }),
                new[] { "base_mm", "profile", "material" }, "create_column");

            yield return new WorkerWriteTool(w, "tekla_create_plate", "Create contour plate",
                "Create a contour plate from 3+ corner points; profile gives the thickness (e.g. \"PL20\").", ToolLevel.Write,
                PartProps(new JObject { ["contour_mm"] = S.Array(S.Array(S.Number("mm"), "[x, y, z]"), "Contour points (3-99), global mm.") }),
                new[] { "contour_mm", "profile", "material" }, "create_plate");

            yield return new WorkerWriteTool(w, "tekla_create_bolts", "Create bolt group",
                "Create a bolt group connecting parts. kind=array: dist_x_mm/dist_y_mm are spacings (Tekla bolt array); xy_list: one x/y pair per bolt; " +
                "circle: number_of_bolts + diameter_mm. Bolt standard and size are checked against the bolt catalog. Defaults: bolt and nut on, washers off, holes in all parts.",
                ToolLevel.Write, new JObject
                {
                    ["kind"] = S.String("Bolt group type.", new[] { "array", "xy_list", "circle" }),
                    ["part_to_bolt_to_guid"] = S.String("Main part GUID."),
                    ["part_to_be_bolted_guid"] = S.String("Secondary part GUID (default: same as part_to_bolt_to)."),
                    ["first_point_mm"] = Pt("Bolt group first point"), ["second_point_mm"] = Pt("Bolt group second point (direction)"),
                    ["bolt_standard"] = S.String("Bolt standard, e.g. \"8.8XOX\", \"A325N\"."),
                    ["bolt_size_mm"] = S.Number("Bolt diameter, mm (e.g. 20).", 1),
                    ["dist_x_mm"] = S.Array(S.Number("mm"), "array: spacings along the group axis; xy_list: x of each bolt."),
                    ["dist_y_mm"] = S.Array(S.Number("mm"), "array: spacings across; xy_list: y of each bolt."),
                    ["number_of_bolts"] = S.Integer("circle: number of bolts.", 1),
                    ["diameter_mm"] = S.Number("circle: bolt circle diameter, mm.", 1),
                    ["tolerance_mm"] = S.Number("Hole tolerance, mm (Tekla default if omitted).", 0),
                    ["bolt"] = S.Boolean("Create bolt. Default true."), ["nut"] = S.Boolean("Nut. Default true."), ["washers"] = S.Boolean("Washers. Default false.")
                },
                new[] { "kind", "part_to_bolt_to_guid", "first_point_mm", "second_point_mm", "bolt_standard", "bolt_size_mm" }, "create_bolts");

            yield return new WorkerWriteTool(w, "tekla_create_weld", "Create weld",
                "Create a weld between a main part and a secondary part. Weld types: fillet, bevel_groove_single_v_butt, square_groove_square_butt, plug, ... (Tekla WeldTypeEnum names without WELD_TYPE_).",
                ToolLevel.Write, new JObject
                {
                    ["main_part_guid"] = S.String("Main part GUID."), ["secondary_part_guid"] = S.String("Secondary part GUID."),
                    ["size_above_mm"] = S.Number("Weld size above the line, mm.", 0.1), ["type_above"] = S.String("Weld type above. Default fillet."),
                    ["size_below_mm"] = S.Number("Weld size below the line, mm.", 0.1), ["type_below"] = S.String("Weld type below. Default fillet."),
                    ["shop_weld"] = S.Boolean("Shop weld (true) or site weld (false). Default true.")
                },
                new[] { "main_part_guid", "secondary_part_guid", "size_above_mm" }, "create_weld");

            yield return new WorkerWriteTool(w, "tekla_create_rebar", "Create reinforcement",
                "Create a single rebar or a rebar group in a concrete part. points_mm is the bar shape polygon. For a group the shape is distributed from start_mm to end_mm at target spacing_mm. " +
                "Grade + size are checked against the rebar catalog (e.g. SD40 16, SR24 9).",
                ToolLevel.Write, new JObject
                {
                    ["kind"] = S.String("single or group.", new[] { "single", "group" }),
                    ["father_part_guid"] = S.String("Concrete part GUID that holds the rebar."),
                    ["points_mm"] = S.Array(S.Array(S.Number("mm"), "[x, y, z]"), "Bar shape points (2-99), global mm."),
                    ["grade"] = S.String("Rebar grade, e.g. \"SD40\"."), ["size"] = S.String("Rebar size, e.g. \"16\"."),
                    ["name"] = S.String("Rebar name."), ["class"] = S.Integer("Class number.", 0),
                    ["start_mm"] = Pt("group: distribution start point"), ["end_mm"] = Pt("group: distribution end point"),
                    ["spacing_mm"] = S.Number("group: target spacing, mm.", 1)
                },
                new[] { "kind", "father_part_guid", "points_mm", "grade", "size" }, "create_rebar");

            yield return new WorkerWriteTool(w, "tekla_create_grid", "Create grid",
                "Create a grid. Coordinates use Tekla grid syntax (first value absolute, then spacings, n*spacing allowed), e.g. coordinate_x \"0 4*6000\", label_x \"A B C D E\".",
                ToolLevel.Write, new JObject
                {
                    ["coordinate_x"] = S.String("X coordinates, mm, Tekla syntax."), ["coordinate_y"] = S.String("Y coordinates, mm."), ["coordinate_z"] = S.String("Z levels, mm."),
                    ["label_x"] = S.String("X labels, space separated."), ["label_y"] = S.String("Y labels."), ["label_z"] = S.String("Z labels."),
                    ["origin_mm"] = Pt("Grid origin (default 0,0,0)"), ["name"] = S.String("Grid name.")
                },
                new[] { "coordinate_x", "coordinate_y", "coordinate_z", "label_x", "label_y", "label_z" }, "create_grid");

            yield return new WorkerWriteTool(w, "tekla_create_assembly", "Create assembly",
                "Build an assembly: the main part's assembly receives the given parts as secondaries (optionally renamed).", ToolLevel.Write,
                new JObject { ["main_part_guid"] = S.String("Main part GUID."), ["secondary_part_guids"] = Guids("Parts to add as secondaries."), ["name"] = S.String("Assembly name.") },
                new[] { "main_part_guid", "secondary_part_guids" }, "assemble",
                p =>
                {
                    var r = new JObject { ["target_guid"] = p["main_part_guid"], ["part_guids"] = p["secondary_part_guids"] };
                    if (p["name"] != null && p["name"].Type != JTokenType.Null) r["name"] = p["name"];
                    return r;
                });

            yield return new WorkerWriteTool(w, "tekla_add_to_assembly", "Add parts to assembly",
                "Add parts as secondaries to an existing assembly (given by the assembly GUID or any part in it).", ToolLevel.Write,
                new JObject { ["target_guid"] = S.String("Assembly GUID or a part of that assembly."), ["part_guids"] = Guids("Parts to add.") },
                new[] { "target_guid", "part_guids" }, "assemble");

            yield return new WorkerWriteTool(w, "tekla_modify_objects", "Modify objects",
                "Change attributes of objects by GUID (max " + TeklaWrite.MaxObjects + "). Parts: profile, material, name, class, finish, position. Rebar: size, grade, name, class. Any object: uda. " +
                "The preview shows from -> to for every object.",
                ToolLevel.Write, new JObject
                {
                    ["guids"] = Guids("Objects to modify."),
                    ["set"] = S.Object(new JObject
                    {
                        ["profile"] = S.String("New profile (parts)."), ["material"] = S.String("New material (parts)."),
                        ["name"] = S.String("New name."), ["class"] = S.String("New class as text."), ["finish"] = S.String("New finish (parts)."),
                        ["position"] = Position(), ["size"] = S.String("New rebar size."), ["grade"] = S.String("New rebar grade."), ["uda"] = Uda()
                    })
                },
                new[] { "guids", "set" }, "modify");

            yield return new WorkerWriteTool(w, "tekla_move_objects", "Move objects",
                "Move objects by a vector (global mm). Max " + TeklaWrite.MaxObjects + " objects.", ToolLevel.Write,
                new JObject { ["guids"] = Guids("Objects to move."), ["vector_mm"] = Pt("Move vector") },
                new[] { "guids", "vector_mm" }, "move");

            yield return new WorkerWriteTool(w, "tekla_copy_objects", "Copy objects",
                "Copy objects by a vector (global mm); count > 1 makes repeated copies at 1x, 2x, ... the vector. Returns the new GUIDs.", ToolLevel.Write,
                new JObject { ["guids"] = Guids("Objects to copy."), ["vector_mm"] = Pt("Copy vector"), ["count"] = S.Integer("Number of copies (1-20). Default 1.", 1, 20) },
                new[] { "guids", "vector_mm" }, "copy");

            yield return new WorkerWriteTool(w, "tekla_delete_objects", "Delete objects",
                "Delete objects by GUID (max " + TeklaWrite.MaxObjects + "). Bolts, welds and rebar attached to deleted parts may be deleted too; the preview lists them.", ToolLevel.Destructive,
                new JObject { ["guids"] = Guids("Objects to delete.") },
                new[] { "guids" }, "delete");

            yield return new WorkerWriteTool(w, "tekla_set_workplane", "Set work plane",
                "Set Tekla's current work plane (affects the Tekla UI only; tekla_* tools always use global coordinates). global=true restores the global plane.", ToolLevel.Write,
                new JObject { ["global"] = S.Boolean("true = global work plane."), ["origin_mm"] = Pt("Work plane origin"), ["axis_x"] = Pt("X axis direction"), ["axis_y"] = Pt("Y axis direction (perpendicular to X)") },
                new string[0], "set_workplane");

            yield return new WorkerWriteTool(w, "tekla_save_model", "Save model",
                "Save the open Tekla model (recommended before large changes; the Open API has no undo).", ToolLevel.Write,
                new JObject { ["comment"] = S.String("Save comment.") },
                new string[0], "save");
        }
    }
}
