using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TSM = Tekla.Structures.Model;
using Op = Tekla.Structures.Model.Operations.Operation;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Phase 6: model exports that only write files (the model is not changed). The server resolves a NEW folder inside
    /// exportDir ("folder" arg) so nothing is overwritten; Tekla also writes side files (IFC: .htm, .partReport, ifc4export.js).
    /// </summary>
    internal sealed class TeklaExport
    {
        private readonly TeklaSession _session;
        public TeklaExport(TeklaSession session) { _session = session; }

        private static readonly Dictionary<string, Op.IFCExportVersionEnum> Versions = new Dictionary<string, Op.IFCExportVersionEnum>
        { ["ifc2x3"] = Op.IFCExportVersionEnum.IFC2x3, ["ifc4"] = Op.IFCExportVersionEnum.IFC4, ["ifc43"] = Op.IFCExportVersionEnum.IFC43 };

        private static readonly Dictionary<string, Op.IFCExportViewTypeEnum> Views = new Dictionary<string, Op.IFCExportViewTypeEnum>
        {
            ["reference"] = Op.IFCExportViewTypeEnum.REFERENCE_VIEW, ["design_transfer"] = Op.IFCExportViewTypeEnum.DESIGN_TRANSFER_VIEW,
            ["precast"] = Op.IFCExportViewTypeEnum.PRECAST_VIEW, ["bridge"] = Op.IFCExportViewTypeEnum.BRIDGE_VIEW
        };

        /// <summary>args: folder, file_name, selection, version, view, base_point, bolts, welds, rebars, grids, assemblies</summary>
        public JObject Ifc(JObject a)
        {
            _session.RequireModel();
            string folder = NewFolder(a);
            string version = (string)a["version"] ?? "ifc4", view = (string)a["view"] ?? "reference", basePoint = (string)a["base_point"] ?? "global";
            if (!Versions.ContainsKey(version)) throw Invalid("version must be one of " + string.Join(", ", Versions.Keys) + ".");
            if (!Views.ContainsKey(view) && !(version == "ifc2x3" && view == "steel_fabrication")) throw Invalid("view must be one of " + string.Join(", ", Views.Keys) + " (ifc2x3 also: steel_fabrication).");
            if (basePoint != "global" && basePoint != "work_plane") throw Invalid("base_point must be global or work_plane.");
            // Same rule as the server's folder name: strip only ".ifc" (GetFileNameWithoutExtension turned "tower.v2" into "tower").
            string name = TableExport.TrimExtension((string)a["file_name"], ".ifc");
            string file = Path.Combine(folder, name + ".ifc");
            var flags = new Op.IFCExportFlags
            {
                IsBoltsEnabled = (bool?)a["bolts"] ?? true, IsWeldsEnabled = (bool?)a["welds"] ?? false, IsRebarsEnabled = (bool?)a["rebars"] ?? true,
                IsGridsEnabled = (bool?)a["grids"] ?? true, IsAssembliesEnabled = (bool?)a["assemblies"] ?? true
            };
            bool selection = (bool?)a["selection"] ?? false;
            var bp = basePoint == "global" ? Op.ExportBasePoint.GLOBAL : Op.ExportBasePoint.WORK_PLANE;
            var none = new List<string>();
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            bool ok;
            if (version == "ifc2x3")
            {
                // Tested 2026-11-26: CreateIFC4Export* with IFCExportVersionEnum.IFC2x3 still writes FILE_SCHEMA(('IFC4')); use the IFC2x3 API.
                if (view != "reference" && view != "steel_fabrication") throw Invalid("For ifc2x3, view must be reference (Coordination View 2.0) or steel_fabrication.");
                var f2 = new Op.IFC2x3ExportFlags
                {
                    ExportAllObjects = !selection, ExportSelectedObjectsAndTheirAssemblies = false,
                    IsBoltsEnabled = flags.IsBoltsEnabled, IsWeldsEnabled = flags.IsWeldsEnabled, IsRebarsEnabled = flags.IsRebarsEnabled, IsGridsEnabled = flags.IsGridsEnabled
                };
                var v2 = view == "steel_fabrication" ? Op.IFC2x3ExportViewTypeEnum.STEEL_FABRICATION_VIEW : Op.IFC2x3ExportViewTypeEnum.COORDINATION_VIEW_20;
                ok = Op.CreateIFC2x3Export(file, v2, none, string.Empty, bp, false, false, f2, string.Empty);
            }
            else ok = selection
                ? Op.CreateIFC4ExportFromSelected(file, Views[view], Versions[version], none, none, bp, "__Name__", "ByObjectClass", flags, string.Empty)
                : Op.CreateIFC4ExportFromAll(file, Views[view], Versions[version], none, none, bp, "__Name__", "ByObjectClass", flags, string.Empty);
            var r = Result(folder, sw);
            r["ok"] = ok && File.Exists(file);
            r["ifc_file"] = file;
            r["ifc_size_bytes"] = File.Exists(file) ? new FileInfo(file).Length : 0;
            r["options"] = new JObject { ["version"] = version, ["view"] = view, ["base_point"] = basePoint, ["selection"] = selection,
                ["bolts"] = flags.IsBoltsEnabled, ["welds"] = flags.IsWeldsEnabled, ["rebars"] = flags.IsRebarsEnabled, ["grids"] = flags.IsGridsEnabled, ["assemblies"] = flags.IsAssembliesEnabled };
            if (!(bool)r["ok"]) throw new TeklaException("export_failed", "Tekla did not create the IFC file (CreateIFC4Export returned " + ok + "). See the Tekla session log.");
            return r;
        }

        /// <summary>args: folder, settings, selection. Without a valid settings name returns the available NC settings.</summary>
        public JObject Nc(JObject a)
        {
            var m = _session.RequireModel();
            var available = NcSettings();
            string settings = (string)a["settings"];
            if (string.IsNullOrWhiteSpace(settings) || !available.Contains(settings, StringComparer.OrdinalIgnoreCase))
                throw Invalid("NC settings '" + settings + "' not found. Available in this environment: " + string.Join(", ", available) + ".");
            settings = available.First(s => string.Equals(s, settings, StringComparison.OrdinalIgnoreCase));
            if (!Op.IsNumberingUpToDateAll()) throw Invalid("Numbering is not up to date; NC files need it. Run tekla_run_numbering first.");
            string folder = NewFolder(a);
            bool selection = (bool?)a["selection"] ?? false;
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            // Tested 2026-11-26: Tekla appends the file name to DestinationFolder without a separator ("...\nc_xA4.nc1"), so pass a trailing '\'.
            string dest = folder.TrimEnd('\\') + "\\";
            bool ok = selection ? Op.CreateNCFilesFromSelected(settings, dest) : Op.CreateNCFilesFromAll(settings, dest);
            var r = Result(folder, sw);
            r["ok"] = ok; r["settings"] = settings; r["selection"] = selection;
            if (!ok) throw new TeklaException("export_failed", "Tekla reported that NC creation failed (numbering or settings).");
            if ((int)r["file_count"] == 0) r["note"] = "No NC files: the exported objects contain no parts these settings apply to (NC is for steel profiles/plates).";
            return r;
        }

        /// <summary>NC settings (*.ncfs) found in the XS_SYSTEM folders of the current environment.</summary>
        internal static List<string> NcSettings()
        {
            string sys = "";
            global::Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_SYSTEM", ref sys);
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in (sys ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.ncfs")) names.Add(Path.GetFileNameWithoutExtension(f)); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return names.ToList();
        }

        private static string NewFolder(JObject a)
        {
            string folder = (string)a["folder"];
            if (string.IsNullOrWhiteSpace(folder)) throw Invalid("folder is required.");
            if (Directory.Exists(folder) || File.Exists(folder)) throw Invalid("Output folder already exists (not overwritten): " + folder);
            return folder;
        }

        private static JObject Result(string folder, Stopwatch sw)
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            return new JObject
            {
                ["folder"] = folder, ["elapsed_ms"] = sw.ElapsedMilliseconds, ["file_count"] = files.Length,
                ["files"] = new JArray(files.Take(50).Select(f => new JObject { ["name"] = f.Substring(folder.Length).TrimStart('\\'), ["size_bytes"] = new FileInfo(f).Length }))
            };
        }

        private static TeklaException Invalid(string msg) => new TeklaException("invalid_arguments", msg);
    }
}
