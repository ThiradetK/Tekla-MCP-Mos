using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using TSD = Tekla.Structures.Drawing;
using Op = Tekla.Structures.Model.Operations.Operation;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Phase 6: drawings to PDF / DWG / DXF (decision D9). The Open API cannot do this here: DrawingHandler.PrintDrawing
    /// returns true but DPMPrinterCommand writes nothing, and there is no DWG API (tested 2026-11-26). So each drawing is
    /// sent through Tekla's own Document manager with a temporary WPF macro (Operation.RunMacro), using automation IDs from
    /// Tekla's feature DLLs. Writes only files into a new folder; the model is not changed. Print/export dialogs remember the
    /// last folder and options in the user's settings.
    /// </summary>
    internal sealed class TeklaDrawingExport
    {
        public const int MaxDrawings = 5;
        private const int MaxTries = 10;   // Document manager rows tried per drawing before giving up
        private const string ExportWindowTitle = "Export drawings as DWG/DXF/DGN";
        private const string DocumentManagerTitle = "Document manager";
        private static readonly string[] Formats = { "pdf", "dwg", "dxf" };
        private readonly TeklaSession _session;
        public TeklaDrawingExport(TeklaSession session) { _session = session; }

        /// <summary>args: folder (new), drawing_ids[], format</summary>
        public JObject Export(JObject a)
        {
            _session.RequireModel();
            string format = ((string)a["format"] ?? "pdf").ToLowerInvariant();
            if (!Formats.Contains(format)) throw Invalid("format must be pdf, dwg or dxf.");
            var ids = (a["drawing_ids"] as JArray)?.Values<string>().Distinct().ToList() ?? new List<string>();
            if (ids.Count == 0 || ids.Count > MaxDrawings) throw Invalid("drawing_ids must contain 1-" + MaxDrawings + " items (each drawing takes several seconds in Tekla's dialogs).");
            string folder = (string)a["folder"];
            if (string.IsNullOrWhiteSpace(folder) || Directory.Exists(folder) || File.Exists(folder)) throw Invalid("Output folder must be new: " + folder);

            var dh = new TSD.DrawingHandler();
            if (!dh.GetConnectionStatus()) throw new TeklaException("not_connected", "The Tekla drawing API is not available.");
            if (dh.IsAnyDrawingOpen()) throw Invalid("A drawing is open in the drawing editor. Close it first (tekla_close_drawing).");
            var all = new List<TSD.Drawing>(); var en = dh.GetDrawings(); while (en.MoveNext()) all.Add(en.Current);
            // How a drawing is picked (tested 2026-11-26): the macro can only select a Document manager row by index. Text selection does
            // not work (the search applies only on a real Enter key; DataGrid.With(string) times out), and "Select drawings" is a toggle
            // filter whose state a macro cannot read. The row order of category "All drawings" is not predictable (it differed from
            // GetDrawings order after drawings were added; multidrawings come last). So: guess the row, export, and identify the file by
            // its plot file name (Drawing.GetPlotFileName). A file of another requested drawing is kept; others are deleted; up to
            // MaxTries rows are tried per drawing, and every row seen is remembered for the rest of the call.
            var rows = all.Where(x => !(x is TSD.MultiDrawing)).Concat(all.Where(x => x is TSD.MultiDrawing)).ToList();
            var targets = new List<(TSD.Drawing d, int guess, string plot)>();
            foreach (var id in ids)
            {
                int row = rows.FindIndex(x => TeklaDrawing.Row(x, null)["drawing_id"].ToString() == id);
                if (row < 0) throw Invalid("Drawing not found: " + id + ". Use tekla_list_drawings.");
                string plot = rows[row].GetPlotFileName(false);
                if (string.IsNullOrWhiteSpace(plot)) throw Invalid("Drawing " + id + " has no plot file name; its output file cannot be verified.");
                if (rows.Count(x => string.Equals(x.GetPlotFileName(false).TrimEnd(), plot.TrimEnd(), StringComparison.OrdinalIgnoreCase)) > 1)
                    throw Invalid("Drawing " + id + " shares its plot file name '" + plot.TrimEnd() + "' with another drawing; its output cannot be told apart.");
                targets.Add((rows[row], row, plot));
            }

            int tekla = Process.GetProcessesByName("TeklaStructures").FirstOrDefault()?.Id ?? throw new TeklaException("not_connected", "Tekla Structures process not found.");
            bool docmanWasOpen = Win.Find(tekla, DocumentManagerTitle).Count > 0;
            Directory.CreateDirectory(folder);
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // plot name -> kept file
            var seen = new Dictionary<int, string>();                                          // row -> plot name exported from it ("" = nothing)
            var failedSteps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int exports = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                foreach (var t in targets)
                {
                    var tried = new List<int>();
                    var candidates = new[] { t.guess }.Concat(Enumerable.Range(0, rows.Count)).Distinct().Where(r => !seen.ContainsKey(r));
                    foreach (int row in candidates)
                    {
                        if (found.ContainsKey(t.plot.TrimEnd()) || tried.Count >= MaxTries) break;
                        tried.Add(row); exports++;
                        var before = new HashSet<string>(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase);
                        var log = RunMacro(SelectByRow(row) + (format == "pdf" ? PdfSteps(folder) : DwgSteps(folder, format.ToUpperInvariant())));
                        bool macroFailed = log.Any(l => l.Contains(" FAIL "));
                        // A large GA sheet takes > 30 s to print: wait until the file stops growing.
                        var added = WaitForFiles(folder, before, format, TimeSpan.FromSeconds(macroFailed ? 3 : format == "pdf" ? 150 : 60));
                        if (format != "pdf") foreach (var h in Win.Find(tekla, ExportWindowTitle)) Win.Close(h);
                        if (macroFailed) failedSteps[t.plot.TrimEnd()] = log.Where(l => l.Contains(" FAIL ")).ToList();
                        seen[row] = "";
                        foreach (var f in added)
                        {
                            string plot = Path.GetFileNameWithoutExtension(f).TrimEnd();
                            seen[row] = plot;
                            if (targets.Any(x => x.plot.TrimEnd().Equals(plot, StringComparison.OrdinalIgnoreCase)) && !found.ContainsKey(plot)) found[plot] = f;
                            else { try { File.Delete(f); } catch { } }   // not requested (or a duplicate): never hand back a wrong file
                        }
                    }
                }
            }
            finally
            {
                foreach (var h in Win.Find(tekla, ExportWindowTitle)) Win.Close(h);
                if (!docmanWasOpen) foreach (var h in Win.Find(tekla, DocumentManagerTitle)) Win.Close(h);
            }
            var results = new JArray(targets.Select(t =>
            {
                string key = t.plot.TrimEnd();
                var res = new JObject { ["drawing_id"] = TeklaDrawing.Row(t.d, null)["drawing_id"], ["mark"] = t.d.Mark, ["plot_file_name"] = key };
                res["ok"] = found.ContainsKey(key);
                res["file"] = found.ContainsKey(key) ? Path.GetFileName(found[key]) : null;
                if (!found.ContainsKey(key))
                    res["error"] = "Not found in " + MaxTries + " Document manager row(s)" +
                                   (failedSteps.ContainsKey(key) ? " (last failed step: " + failedSteps[key].Last().Trim() + ")" : "") +
                                   ". Show 'All drawings' in the Document manager without sorting, then retry.";
                return res;
            }));
            var files = Directory.GetFiles(folder);
            var r = new JObject
            {
                ["folder"] = folder, ["format"] = format, ["elapsed_ms"] = sw.ElapsedMilliseconds, ["exports_run"] = exports, ["file_count"] = files.Length,
                ["files"] = new JArray(files.Select(f => new JObject { ["name"] = Path.GetFileName(f), ["size_bytes"] = new FileInfo(f).Length })),
                ["drawings"] = results, ["ok"] = results.All(x => (bool)x["ok"])
            };
            if (!(bool)r["ok"]) r["warning"] = "Some drawings were not exported; see drawings[].error.";
            return r;
        }

        // "[PF.13 - 1]" -> "PF13-1": Tekla's file names drop brackets and dots (seen: "PF13 - 1 - standard.dwg").
        internal static string MarkToken(string mark) => new string((mark ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

        /// <summary>
        /// The output file must carry the drawing's plot file name (Drawing.GetPlotFileName: "PF13 - 1 - standard", "1 - Standard - Rev ",
        /// multidrawing "101 - Rev "; matched the written PDF/DWG/DXF names exactly, 2026-11-26). This follows the environment's naming template.
        /// </summary>
        internal static bool MatchesPlotName(string fileName, string plotName) =>
            !string.IsNullOrWhiteSpace(plotName) &&
            string.Equals(Path.GetFileNameWithoutExtension(fileName).TrimEnd(), plotName.TrimEnd(), StringComparison.OrdinalIgnoreCase);

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private const string OpenDocumentManager =
            "T(\"open document manager\", () => wpf.InvokeCommand(\"CommandRepository\", \"Drawing.DrawingList\"));\n" +
            "var dm = wpf.View(\"DocumentManager.MainWindow\");\n";

        private static string SelectByRow(int row) => OpenDocumentManager +
            "T(\"category: All drawings\", () => dm.Find(\"AID_DOCMAN_CategoryList\").As.Selector.Select(2));\n" +
            "T(\"clear search\", () => dm.Find(\"AID_DOCMAN_SearchBox\").As.TextBox.SetText(\"\"));\n" +
            "System.Threading.Thread.Sleep(1500);\n" +
            "T(\"select row\", () => dm.Find(\"AID_DOCMAN_DataGridControl\").As.DataGrid.NewSelection.With(" + row + ").Invoke());\n";

        private static string PdfSteps(string folder) =>
            "T(\"print\", () => dm.Find(\"AID_DOCMAN_ButtonPrintDocument\").As.Button.Invoke());\n" +
            "var pv = wpf.View(\"DPMPrinterFeature.DPMPrinterViewWindow\");\n" +
            "T(\"pdf\", () => pv.Find(\"AID_PDFPD_PDFRadio\").As.ToggleButton.SetIsChecked(true));\n" +
            "T(\"folder\", () => pv.Find(\"AID_PDFPD_FileLocation\").As.TextBox.SetText(\"" + Esc(folder) + "\"));\n" +
            "T(\"no open\", () => pv.Find(\"AID_PDFPD_OpenFileWhenFinished\").As.ToggleButton.SetIsChecked(false));\n" +
            "T(\"one file per drawing\", () => pv.Find(\"AID_PDFPD_OutputToSingleFile\").As.ToggleButton.SetIsChecked(false));\n" +
            "T(\"print button\", () => pv.Find(\"AID_PDFPD_PrintButton\").As.Button.Invoke());\n" +
            "T(\"close\", () => pv.As.Window.Close());\n";

        private static string DwgSteps(string folder, string type) =>
            "T(\"export\", () => dm.Find(\"AID_DOCMAN_ButtonExportDocument\").As.Button.Invoke());\n" +
            "var ev = wpf.View(\"DrawingExport.UIFeature.MainContentView\");\n" +
            "T(\"folder\", () => ev.Find(\"AID_DwgExport_OutputDirectoryTextBox\").As.TextBox.SetText(\"" + Esc(folder) + "\"));\n" +
            "T(\"type\", () => ev.Find(\"AID_DwgExport_FileTypesComboBox\").As.Selector.Select(\"" + type + "\"));\n" +
            "T(\"no open folder\", () => ev.Find(\"AID_DwgExport_OpenFolderWhenDoneCheckbox\").As.ToggleButton.SetIsChecked(false));\n" +
            "T(\"export button\", () => ev.Find(\"AID_DwgExport_ExportButton\").As.Button.Invoke());\n";

        /// <summary>Writes, runs and deletes a temporary WPF macro; returns its step log.</summary>
        private static List<string> RunMacro(string body)
        {
            string log = Path.Combine(Path.GetTempPath(), "teklamcp_macro_" + Guid.NewGuid().ToString("N") + ".log");
            // Fixed name: Tekla caches each compiled macro by file name (%LOCALAPPDATA%\Trimble\...\Cache\MacroCacheFolder), so unique names would pile up.
            string file = "TeklaMcp_Export.cs";
            string path = Path.Combine(TeklaDrawing.WritableMacroDir(), file);
            File.WriteAllText(path,
                "#pragma reference \"Tekla.Macros.Wpf.Runtime\"\n#pragma reference \"Tekla.Macros.Runtime\"\n" +
                "namespace UserMacros { public sealed class Macro {\n" +
                "static string L = \"" + Esc(log) + "\";\n" +
                "static void W(string s) { System.IO.File.AppendAllText(L, s + System.Environment.NewLine); }\n" +
                // A missing control makes each later step wait ~60 s on Tekla's main thread (blocking every API call of the worker),
                // so the first failed step stops the macro.
                "sealed class Stop : System.Exception { }\n" +
                "static void T(string label, System.Action a) { try { a(); W(\"OK \" + label); } catch (System.Exception ex) { W(\" FAIL \" + label + \": \" + ex.Message.Replace(System.Environment.NewLine, \" \")); throw new Stop(); } }\n" +
                "[Tekla.Macros.Runtime.MacroEntryPointAttribute()] public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime) {\n" +
                "var wpf = runtime.Get<Tekla.Macros.Wpf.Runtime.IWpfMacroHost>();\n" +
                "try {\n" + body + "} catch (Stop) { } finally { W(\"END\"); }\n} } }\n");
            try
            {
                if (!Op.RunMacro(file)) throw new TeklaException("tekla_error", "Tekla could not start the export macro.");
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(90) && !(File.Exists(log) && File.ReadAllText(log).Contains("END"))) Thread.Sleep(250);
                var lines = File.Exists(log) ? File.ReadAllLines(log).ToList() : new List<string> { " FAIL macro produced no log" };
                if (!lines.Contains("END")) lines.Add(" FAIL macro did not finish within 90 s");
                return lines;
            }
            finally { try { File.Delete(path); } catch { } try { File.Delete(log); } catch { } }
        }

        private static List<string> WaitForFiles(string folder, HashSet<string> before, string ext, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            List<string> added = new List<string>();
            long lastSize = -1;
            while (sw.Elapsed < timeout)
            {
                added = Directory.GetFiles(folder, "*." + ext).Where(f => !before.Contains(f)).ToList();
                long size = added.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                if (added.Count > 0 && size > 0 && size == lastSize) break;   // file present and no longer growing
                lastSize = size;
                Thread.Sleep(1500);
            }
            return added;
        }

        private static TeklaException Invalid(string msg) => new TeklaException("invalid_arguments", msg);

        /// <summary>Top-level windows of the Tekla process by exact title; closed with WM_CLOSE (no focus needed).</summary>
        private static class Win
        {
            [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
            [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
            [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
            private delegate bool EnumProc(IntPtr h, IntPtr p);

            public static List<IntPtr> Find(int pid, string title)
            {
                var list = new List<IntPtr>();
                EnumWindows((h, p) =>
                {
                    GetWindowThreadProcessId(h, out uint wp);
                    if (wp != pid || !IsWindowVisible(h)) return true;
                    var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                    if (sb.ToString() == title) list.Add(h);
                    return true;
                }, IntPtr.Zero);
                return list;
            }

            public static void Close(IntPtr h) { PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); Thread.Sleep(500); }
        }
    }
}


