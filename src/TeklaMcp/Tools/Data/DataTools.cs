using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Data
{
    /// <summary>Phase 2 data tools (PLAN §4 "Data"): quantity summary, export, Tekla report.</summary>
    internal static class DataTools
    {
        public static IEnumerable<ITool> Create(TeklaWorker w, Settings settings)
        {
            yield return new QuantityTool(w);
            yield return new ExportTool(w, settings);
            yield return new WorkerTool(w, "tekla_run_report", "Run Tekla report",
                "Run a Tekla report template (e.g. \"Part List\", \"Material List\", \"Part List.xls\") for the whole model or the current selection. " +
                "The file is written to the model's Reports folder (never overwrites); text output is previewed. An unknown template name returns the list of available templates.",
                S.Object(new JObject
                {
                    ["template"] = S.String("Report template name without .rpt."),
                    ["file_name"] = S.String("Output file name (no folders). Default: <template>_<timestamp>.xsr"),
                    ["selected"] = S.Boolean("true = only selected objects. Default false (all)."),
                    ["title1"] = S.String("Report title 1"), ["title2"] = S.String("Report title 2"), ["title3"] = S.String("Report title 3")
                }, "template"),
                "run_report", null,
                r => (bool)r["exists"] ? "Report written: " + r["path"] + (r["line_count"] != null ? " (" + r["line_count"] + " lines)" : "") : "Tekla did not create the report file (" + r["path"] + ").");
        }

        /// <summary>Rows with quantities for the filter (no paging).</summary>
        internal static List<JObject> Rows(TeklaWorker w, JObject args)
        {
            var q = ObjectFilter.Merge(ObjectFilter.ToWorker(args), new JObject { ["include_quantities"] = true });
            return ((JArray)w.Call("query", q)["items"]).OfType<JObject>().ToList();
        }

        internal static JObject QueryEcho(JObject args)
        {
            var e = new JObject();
            foreach (var k in new[] { "types", "profile", "material", "name", "class", "phase", "uda", "selection", "bbox" })
                if (args[k] != null) e[k] = args[k].DeepClone();
            return e;
        }
    }

    internal sealed class QuantityTool : IReadTool
    {
        private const int MaxGroups = 500;
        private readonly TeklaWorker _w;
        public QuantityTool(TeklaWorker w) { _w = w; }

        public string Name => "tekla_quantity_summary";
        public string Title => "Quantity summary";
        public string Description =>
            "Sum quantities of filtered objects grouped by chosen fields: object_count, bar_count (rebar), total_length_mm, total_weight_kg, total_area_mm2, total_volume_mm3, plus a grand total. " +
            "Values come from Tekla report properties WEIGHT, LENGTH, AREA, VOLUME. Default: all parts grouped by report_profile and material, " +
            "which matches Tekla's Material List report (report_profile = Tekla PROFILE report property, e.g. \"250x500\"; profile = the entered profile, e.g. \"500*250\"). " +
            "For reinforcement use types [\"rebar\"] (profile = bar size, material = grade).";
        public ToolLevel Level => ToolLevel.Read;
        public JObject InputSchema => S.Object(ObjectFilter.Merge(ObjectFilter.Properties(), new JObject
        {
            ["group_by"] = S.Array(S.String("Group field", Quantity.GroupFields), "Fields to group by. Default [\"report_profile\",\"material\"].")
        }));

        public ToolResult Execute(JObject args)
        {
            var groupBy = args["group_by"]?.Values<string>().ToList() ?? new List<string>();
            if (groupBy.Count == 0) groupBy = new List<string> { "report_profile", "material" };
            var rows = DataTools.Rows(_w, args);
            var s = Quantity.Summarize(rows, groupBy);
            var groups = (JArray)s["groups"];
            s["group_count"] = groups.Count;
            if (groups.Count > MaxGroups)
            {
                s["groups"] = new JArray(groups.Take(MaxGroups));
                s["note"] = "Only the first " + MaxGroups + " groups are shown; use tekla_export_data for the full list.";
            }
            s["query"] = DataTools.QueryEcho(args);
            var t = (JObject)s["total"];
            return ToolResult.Ok("Total " + t["object_count"] + " object(s) in " + groups.Count + " group(s): weight " + t["total_weight_kg"] + " kg, length " + t["total_length_mm"] + " mm.", s);
        }
    }

    internal sealed class ExportTool : IReadTool
    {
        private readonly TeklaWorker _w;
        private readonly Settings _settings;
        public ExportTool(TeklaWorker w, Settings settings) { _w = w; _settings = settings; }

        private static readonly string[] Kinds = { "part_list", "assembly_list", "material_list", "boq" };
        private static readonly string[] PartColumns = { "guid", "id", "type", "assembly_pos", "part_pos", "name", "profile", "report_profile", "material", "class", "phase", "length_mm", "weight_kg", "area_mm2", "volume_mm3", "number_of_bars" };
        private static readonly string[] AssemblyColumns = { "guid", "id", "assembly_pos", "name", "material", "phase", "main_part_guid", "length_mm", "weight_kg", "area_mm2", "volume_mm3" };
        private static readonly string[] SumColumns = { "object_count", "bar_count", "total_length_mm", "total_weight_kg", "total_area_mm2", "total_volume_mm3" };

        public string Name => "tekla_export_data";
        public string Title => "Export data to file";
        public string Description =>
            "Export a part list, assembly list, material list (grouped by material) or BOQ (grouped by report_profile + material, as in Tekla's Material List) of filtered objects to CSV, XLSX or JSON. " +
            "Files go to the export folder (settings.json exportDir, default Documents\\TeklaMcp\\exports); an existing file is never overwritten. " +
            "Units: mm, kg, mm2, mm3. Creates a file only; the model is not changed.";
        public ToolLevel Level => ToolLevel.Read;
        public JObject InputSchema => S.Object(ObjectFilter.Merge(ObjectFilter.Properties(), new JObject
        {
            ["kind"] = S.String("What to export.", Kinds),
            ["format"] = S.String("File format.", TableExport.Formats),
            ["file_name"] = S.String("File name without folders; extension is added if missing.")
        }), "kind", "format", "file_name");

        public ToolResult Execute(JObject args)
        {
            string kind = (string)args["kind"], format = (string)args["format"];
            string path;
            try { path = TableExport.ResolvePath(_settings.ExportDir, (string)args["file_name"], format); }
            catch (ArgumentException ex) { return ToolResult.Fail("invalid_file", ex.Message); }

            if (kind == "assembly_list") args = ObjectFilter.Merge(args, new JObject { ["types"] = new JArray("assembly") });
            var rows = DataTools.Rows(_w, args);
            IList<string> columns; IList<JObject> table;
            switch (kind)
            {
                case "part_list": columns = PartColumns; table = rows; break;
                case "assembly_list": columns = AssemblyColumns; table = rows; break;
                case "material_list": columns = new[] { "material" }.Concat(SumColumns).ToList(); table = Groups(rows, "material"); break;
                default: columns = new[] { "report_profile", "material" }.Concat(SumColumns).ToList(); table = Groups(rows, "report_profile", "material"); break;
            }
            var meta = new JObject
            {
                ["kind"] = kind, ["created"] = DateTimeOffset.Now.ToString("o"), ["query"] = DataTools.QueryEcho(args),
                ["units"] = new JObject { ["length"] = "mm", ["weight"] = "kg", ["area"] = "mm2", ["volume"] = "mm3" }
            };
            TableExport.Write(path, format, columns, table, meta);
            return ToolResult.Ok("Exported " + table.Count + " row(s) to " + path, new JObject
            {
                ["path"] = path, ["kind"] = kind, ["format"] = format, ["row_count"] = table.Count, ["source_object_count"] = rows.Count, ["columns"] = new JArray(columns)
            });
        }

        private static IList<JObject> Groups(List<JObject> rows, params string[] by) =>
            ((JArray)Quantity.Summarize(rows, by)["groups"]).OfType<JObject>().ToList();
    }
}
