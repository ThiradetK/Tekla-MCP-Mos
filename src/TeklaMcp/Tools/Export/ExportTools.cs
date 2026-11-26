using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Export
{
    /// <summary>Export that only writes files into a new folder under exportDir (READ level, like tekla_export_data).</summary>
    internal sealed class FolderExportTool : IReadTool
    {
        private readonly TeklaWorker _worker;
        private readonly Settings _settings;
        private readonly string _op, _nameArg, _prefix;
        private readonly Func<JObject, string> _summary;
        private readonly Func<JObject, TimeSpan?> _timeout;

        public FolderExportTool(TeklaWorker worker, Settings settings, string name, string title, string description, JObject schema,
                                string op, string nameArg, string prefix, Func<JObject, string> summary, Func<JObject, TimeSpan?> timeout = null)
        {
            _worker = worker; _settings = settings; Name = name; Title = title; Description = description; InputSchema = schema;
            _op = op; _nameArg = nameArg; _prefix = prefix; _summary = summary; _timeout = timeout ?? (a => null);
        }

        public string Name { get; }
        public string Title { get; }
        public string Description { get; }
        public ToolLevel Level => ToolLevel.Read;
        public JObject InputSchema { get; }

        public ToolResult Execute(JObject args)
        {
            string name = ((string)args[_nameArg] ?? "").Trim();
            if (name.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            string folder;
            // ResolveFolder validates the user's name as given (no folders, no ".."); the prefix is added afterwards.
            try { TableExport.ValidateName(name); folder = TableExport.ResolveFolder(_settings.ExportDir, _prefix + name); }
            catch (ArgumentException ex) { return ToolResult.Fail("invalid_arguments", ex.Message); }
            var w = (JObject)args.DeepClone();
            w["folder"] = folder;
            JObject r = _worker.Call(_op, w, timeout: _timeout(args));
            return ToolResult.Ok(_summary(r), r);
        }
    }

    /// <summary>Phase 6 tools (PLAN §4 "Export"). PDF and DWG/DXF are not included yet: blocked, see CLAUDE.md "Tekla API pitfalls".</summary>
    internal static class ExportTools
    {
        private const string Folder = " Output goes to a new folder in the export folder (settings.json exportDir, default Documents\\TeklaMcp\\exports); an existing folder is never overwritten. The model is not changed.";

        public static IEnumerable<ITool> Create(TeklaWorker w, Settings settings)
        {
            yield return new FolderExportTool(w, settings, "tekla_export_ifc", "Export IFC",
                "Export the model (or the objects selected in Tekla) to IFC 2x3, IFC4 or IFC4.3 with Tekla's IFC export: global or work-plane coordinates, " +
                "layers by name, colors by class, optional bolts/welds/rebars/grids/assemblies. Tekla also writes a .htm summary and a .partReport next to the .ifc." + Folder,
                S.Object(new JObject
                {
                    ["file_name"] = S.String("IFC file name without folders, e.g. \"tower_a\" (.ifc added). Also used as the folder name \"ifc_<name>\"."),
                    ["version"] = S.String("IFC version. Default ifc4.", new[] { "ifc2x3", "ifc4", "ifc43" }),
                    ["view"] = S.String("Export view type. Default reference. ifc2x3 supports reference (Coordination View 2.0) and steel_fabrication.", new[] { "reference", "design_transfer", "precast", "bridge", "steel_fabrication" }),
                    ["selection"] = S.Boolean("true = only objects selected in Tekla. Default false (all)."),
                    ["base_point"] = S.String("Coordinates. Default global.", new[] { "global", "work_plane" }),
                    ["bolts"] = S.Boolean("Include bolts. Default true."),
                    ["welds"] = S.Boolean("Include welds. Default false."),
                    ["rebars"] = S.Boolean("Include reinforcement. Default true."),
                    ["grids"] = S.Boolean("Include grids. Default true."),
                    ["assemblies"] = S.Boolean("Include assemblies (IFC4/4.3). Default true.")
                }, "file_name"),
                "export_ifc", "file_name", "ifc_",
                r => "IFC exported: " + r["ifc_file"] + " (" + r["ifc_size_bytes"] + " bytes, " + r["elapsed_ms"] + " ms).");

            yield return new FolderExportTool(w, settings, "tekla_export_nc", "Export NC (DSTV)",
                "Create NC files (DSTV) for steel parts of the model or of the objects selected in Tekla, with an NC settings file of the environment " +
                "(SE_Asia: \"DSTV for Profile\", \"DSTV for Plate\", \"DSTV for Angle\"; an unknown name returns the list). Numbering must be up to date." + Folder,
                S.Object(new JObject
                {
                    ["folder_name"] = S.String("Output folder name without path, e.g. \"tower_a\" (created as \"nc_<name>\")."),
                    ["settings"] = S.String("NC settings name, e.g. \"DSTV for Profile\"."),
                    ["selection"] = S.Boolean("true = only parts selected in Tekla. Default false (all).")
                }, "folder_name", "settings"),
                "export_nc", "folder_name", "nc_",
                r => "NC files created with '" + r["settings"] + "': " + r["file_count"] + " file(s) in " + r["folder"] + "." + (r["note"] != null ? " " + r["note"] : ""));

            yield return new FolderExportTool(w, settings, "tekla_export_drawings", "Export drawings (PDF/DWG/DXF)",
                "Export drawings to PDF, DWG or DXF (1-" + TeklaDrawingExport.MaxDrawings + " per call, by drawing_id from tekla_list_drawings). " +
                "The Open API cannot do this in Tekla 2026, so the server drives Tekla's own Document manager > Print / Export with a temporary macro " +
                "(5-10 s per drawing, over 30 s for a large GA sheet; Tekla's Document manager and dialogs open briefly, and they remember the folder and options used). " +
                "Each file is identified by the drawing's plot file name (Tekla's naming); the Document manager row order is not predictable, so wrong rows are retried and their files deleted; the Educational license adds a watermark to PDFs. No drawing may be open in the editor." + Folder,
                S.Object(new JObject
                {
                    ["folder_name"] = S.String("Output folder name without path, e.g. \"issue_01\" (created as \"drawings_<name>\")."),
                    ["drawing_ids"] = S.Array(S.String("drawing_id"), "Drawings to export (1-" + TeklaDrawingExport.MaxDrawings + ")."),
                    ["format"] = S.String("pdf (default), dwg or dxf.", new[] { "pdf", "dwg", "dxf" })
                }, "folder_name", "drawing_ids"),
                "export_drawings", "folder_name", "drawings_",
                r => "Exported " + ((JArray)r["drawings"]).Count + " drawing(s) to " + ((string)r["format"]).ToUpperInvariant() + ": " + r["file_count"] + " file(s) in " + r["folder"] +
                     ((bool)r["ok"] ? "." : ". WARNING: " + r["warning"]),
                a => TimeSpan.FromSeconds(Math.Min(1800, 300 * Math.Max(1, (a["drawing_ids"] as JArray)?.Count ?? 1))));
        }
    }
}




