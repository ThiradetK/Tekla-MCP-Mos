using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using T3D = Tekla.Structures.Geometry3d;
using TSD = Tekla.Structures.Drawing;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Phase 5: drawings and numbering. Drawings have no public GUID in the Open API, so they are referenced by
    /// drawing_id = "type|mark|creation time (yyyyMMddHHmmss)" and resolved by enumerating the drawing list.
    /// Write actions follow TeklaWrite: preview validates and describes; apply performs, commits and reads back.
    /// </summary>
    internal sealed class TeklaDrawing
    {
        public const int MaxDrawings = 200;
        private readonly TeklaSession _session;
        public TeklaDrawing(TeklaSession session) { _session = session; }

        // ================= read =================

        /// <summary>args: types[], mark, name (wildcards), up_to_date, issued, offset, limit</summary>
        public JObject List(JObject a)
        {
            var dh = Handler();
            var active = Active(dh);
            var types = a["types"]?.Values<string>().ToList();
            int offset = (int?)a["offset"] ?? 0, limit = (int?)a["limit"] ?? 50;
            var rows = new List<JObject>();
            foreach (var d in All(dh))
            {
                var row = Row(d, active);
                if (types != null && types.Count > 0 && !types.Contains((string)row["type"])) continue;
                if (a["mark"] != null && !Wildcard.IsMatch((string)row["mark"], (string)a["mark"])) continue;
                if (a["name"] != null && !Wildcard.IsMatch((string)row["name"], (string)a["name"])) continue;
                if (a["up_to_date"] != null && (bool)row["up_to_date"] != (bool)a["up_to_date"]) continue;
                if (a["issued"] != null && (bool)row["issued"] != (bool)a["issued"]) continue;
                rows.Add(row);
            }
            return new JObject
            {
                ["total"] = rows.Count, ["offset"] = offset, ["any_drawing_open"] = active != null,
                ["items"] = new JArray(rows.Skip(offset).Take(limit))
            };
        }

        /// <summary>args: drawing_id, offset, limit (model objects page)</summary>
        public JObject Get(JObject a)
        {
            var m = _session.RequireModel();
            var dh = Handler();
            var d = Find(dh, (string)a["drawing_id"]);
            int offset = (int?)a["offset"] ?? 0, limit = (int?)a["limit"] ?? 50;
            var row = Row(d, Active(dh));
            var ids = dh.GetModelObjectIdentifiers(d);
            row["model_object_count"] = ids.Count;
            row["model_objects"] = new JArray(ids.Skip(offset).Take(limit).Select(id => m.SelectModelObject(id)).Where(o => o != null).Select(TeklaQuery.Brief));
            row["offset"] = offset;
            try
            {
                var views = new JArray();
                var en = d.GetSheet().GetAllViews();
                while (en.MoveNext())
                    if (en.Current is TSD.View v)
                        views.Add(new JObject { ["name"] = v.Name, ["view_type"] = v.ViewType.ToString(), ["scale"] = v.Attributes.Scale, ["width_mm"] = Math.Round(v.Width, 1), ["height_mm"] = Math.Round(v.Height, 1) });
                row["views"] = views;
            }
            catch (Exception ex) { row["views_error"] = ex.GetBaseException().Message; }
            return row;
        }

        /// <summary>args: drawing_id. Opens the drawing editor in Tekla (UI only; nothing is saved).</summary>
        public JObject Open(JObject a)
        {
            var dh = Handler();
            var d = Find(dh, (string)a["drawing_id"]);
            bool ok;
            try { ok = dh.SetActiveDrawing(d, true); }
            catch (TSD.CannotPerformOperationDrawingNotUpToDateException) { throw Invalid("The drawing is not up to date; update it first (tekla_update_drawings)."); }
            if (!ok) throw new TeklaException("tekla_error", "Tekla could not open the drawing.");
            return Row(d, Active(dh));
        }

        // ================= write =================

        /// <summary>Worker "write" op for drawing_* and numbering actions. args: action, mode, params</summary>
        public JObject Write(JObject a)
        {
            string action = (string)a["action"];
            bool apply = (string)a["mode"] == "apply";
            var p = a["params"] as JObject ?? new JObject();
            var m = _session.RequireModel();
            switch (action)
            {
                case "numbering": return Numbering(m, p, apply);
                case "drawing_create": return Create(m, p, apply);
                case "drawing_update": return Update(p, apply);
                case "drawing_issue": return Issue(p, apply);
                case "drawing_modify": return ModifyDrawings(p, apply);
                case "drawing_delete": return DeleteDrawings(p, apply);
                case "drawing_close": return Close(p, apply);
                case "drawing_add_text": return AddText(p, apply);
                default: throw new TeklaException("unknown_action", "Unknown drawing action: " + action);
            }
        }

        private JObject Create(TSM.Model m, JObject p, bool apply)
        {
            string kind = (string)p["kind"];
            string attributes = string.IsNullOrWhiteSpace((string)p["attributes"]) ? "standard" : ((string)p["attributes"]).Trim();
            var planned = new JObject { ["kind"] = kind, ["attributes"] = attributes };
            TSM.Part part = null; TSM.Assembly asm = null;
            if (kind == "ga")
            {
                string name = (string)p["name"];
                if (string.IsNullOrWhiteSpace(name)) throw Invalid("name is required for a GA drawing.");
                planned["name"] = name;
            }
            else if (kind == "assembly" || kind == "single_part" || kind == "cast_unit")
            {
                var (found, _) = TeklaQuery.Fetch(m, new JArray((string)p["part_guid"] ?? ""));
                part = found.FirstOrDefault() as TSM.Part ?? throw Invalid("part_guid must be an existing part.");
                if (!TSM.Operations.Operation.IsNumberingUpToDate(part))
                    throw Invalid("Numbering is not up to date for part " + Guid(part) + " (marks show \"(?)\"). Run tekla_run_numbering first.");
                asm = part.GetAssembly();
                planned["part"] = TeklaQuery.Brief(part);
            }
            else throw Invalid("kind must be ga, assembly, single_part or cast_unit.");

            string what = kind + " drawing (" + attributes + ")" + (part != null ? " of " + (kind == "single_part" ? "part " + planned["part"]["part_pos"] : "assembly " + planned["part"]["assembly_pos"]) : " '" + planned["name"] + "'");
            if (!apply) return Preview("Create " + what + ".", planned);

            var dh = Handler();
            var before = new HashSet<string>(All(dh).Select(Id));
            TSD.Drawing d;
            switch (kind)
            {
                case "ga": d = new TSD.GADrawing((string)planned["name"], attributes); break;
                case "assembly": d = new TSD.AssemblyDrawing(asm.Identifier, attributes); break;
                case "single_part": d = new TSD.SinglePartDrawing(part.Identifier, attributes); break;
                default: d = new TSD.CastUnitDrawing(asm.Identifier, attributes); break;
            }
            if (!d.Insert()) throw new TeklaException("insert_failed", "Tekla refused to create the drawing (check the attributes file name '" + attributes + "').");
            var created = All(dh).Where(x => !before.Contains(Id(x))).ToList();
            var mism = new JArray();
            if (created.Count != 1) mism.Add("expected 1 new drawing in the drawing list, found " + created.Count);
            else if (Kind(created[0]) != kind) mism.Add("new drawing type is " + Kind(created[0]));
            if ((bool?)p["open"] == true && created.Count == 1) dh.SetActiveDrawing(created[0], true);
            var active = Active(dh);
            return Applied("Created " + what + ".", created.Select(Id), new JArray(created.Select(x => Row(x, active))), mism);
        }

        private JObject Update(JObject p, bool apply)
        {
            var dh = Handler();
            var (list, active) = Targets(dh, p);
            RefuseActive(list, active, "update");
            var preview = Preview("Update " + list.Count + " drawing(s).", new JObject());
            preview["objects"] = new JArray(list.Select(d => Row(d, active)));
            if (!apply) return preview;
            var mism = new JArray();
            var ids = list.Select(Id).ToList();
            foreach (var d in list)
            {
                try { if (!dh.UpdateDrawing(d)) mism.Add(Id(d) + ": UpdateDrawing returned false"); }
                catch (TSD.CannotPerformOperationNumberingNotUpToDate) { mism.Add(Id(d) + ": numbering is not up to date (run tekla_run_numbering)"); }
            }
            var after = ReadBack(dh, ids, mism);
            foreach (var r in after.Where(r => !(bool)r["up_to_date"])) mism.Add(r["drawing_id"] + " is still " + r["up_to_date_status"]);
            return Applied("Updated " + (list.Count - mism.Count) + " of " + list.Count + " drawing(s).", ids, new JArray(after), mism);
        }

        private JObject Issue(JObject p, bool apply)
        {
            var dh = Handler();
            var (list, active) = Targets(dh, p);
            bool issue = (bool?)p["issue"] ?? true;
            var preview = Preview((issue ? "Issue " : "Unissue ") + list.Count + " drawing(s).", new JObject { ["issue"] = issue });
            preview["objects"] = new JArray(list.Select(d => Row(d, active)));
            if (!apply) return preview;
            var mism = new JArray();
            var ids = list.Select(Id).ToList();
            foreach (var d in list)
                if (!(issue ? dh.IssueDrawing(d) : dh.UnissueDrawing(d))) mism.Add(Id(d) + ": " + (issue ? "IssueDrawing" : "UnissueDrawing") + " returned false");
            var after = ReadBack(dh, ids, mism);
            foreach (var r in after.Where(r => (bool)r["issued"] != issue)) mism.Add(r["drawing_id"] + " issued=" + r["issued"]);
            return Applied((issue ? "Issued " : "Unissued ") + list.Count + " drawing(s).", ids, new JArray(after), mism);
        }

        private static readonly string[] Editable = { "name", "title1", "title2", "title3" };

        private JObject ModifyDrawings(JObject p, bool apply)
        {
            var dh = Handler();
            var (list, active) = Targets(dh, p);
            var set = p["set"] as JObject ?? throw Invalid("set is required.");
            var bad = set.Properties().Select(x => x.Name).Where(n => !Editable.Contains(n)).ToList();
            if (bad.Count > 0 || set.Count == 0) throw Invalid("set may contain only " + string.Join(", ", Editable) + (bad.Count > 0 ? " (got " + string.Join(", ", bad) + ")" : "") + ".");
            var changes = new JArray(list.Select(d =>
            {
                var c = new JObject { ["drawing_id"] = Id(d) };
                foreach (var f in set.Properties()) c[f.Name] = new JObject { ["from"] = Get(d, f.Name), ["to"] = (string)f.Value };
                return c;
            }));
            var preview = Preview("Modify " + list.Count + " drawing(s): " + string.Join(", ", set.Properties().Select(x => x.Name)) + ".", new JObject { ["changes"] = changes });
            if (!apply) return preview;
            var mism = new JArray();
            var ids = list.Select(Id).ToList();
            foreach (var d in list)
            {
                foreach (var f in set.Properties()) Set(d, f.Name, (string)f.Value);
                if (!d.Modify()) mism.Add(Id(d) + ": Modify returned false");
                d.CommitChanges();
            }
            // Name/titles are not part of drawing_id, so read back by id.
            var after = ReadBack(dh, ids, mism);
            foreach (var r in after)
                foreach (var f in set.Properties())
                    if (!string.Equals(((string)r[f.Name] ?? "").Trim(), ((string)f.Value ?? "").Trim(), StringComparison.Ordinal)) mism.Add(r["drawing_id"] + " " + f.Name + " read back '" + r[f.Name] + "'");
            return Applied("Modified " + list.Count + " drawing(s).", ids, new JArray(after), mism);
        }

        private JObject DeleteDrawings(JObject p, bool apply)
        {
            var dh = Handler();
            var (list, active) = Targets(dh, p);
            RefuseActive(list, active, "delete");
            var preview = Preview("DELETE " + list.Count + " drawing(s). This cannot be undone.", new JObject());
            preview["objects"] = new JArray(list.Select(d => Row(d, active)));
            if (!apply) return preview;
            var ids = list.Select(Id).ToList();
            var mism = new JArray();
            foreach (var d in list) if (!d.Delete()) mism.Add(Id(d) + ": Delete returned false");
            var left = new HashSet<string>(All(dh).Select(Id));
            foreach (var id in ids.Where(left.Contains)) mism.Add(id + " still exists after delete");
            return Applied("Deleted " + ids.Count(i => !left.Contains(i)) + " of " + ids.Count + " drawing(s).", ids, new JArray(), mism);
        }

        private JObject Close(JObject p, bool apply)
        {
            var dh = Handler();
            var active = Active(dh) ?? throw Invalid("No drawing is open.");
            bool save = (bool?)p["save"] ?? true;
            var preview = Preview("Close the open drawing " + Id(active) + (save ? " and save it." : " WITHOUT saving (unsaved changes are lost)."), new JObject { ["save"] = save, ["drawing"] = Row(active, active) });
            if (!apply) return preview;
            string id = Id(active);
            var mism = new JArray();
            if (!dh.CloseActiveDrawing(save)) mism.Add("CloseActiveDrawing returned false");
            if (dh.IsAnyDrawingOpen()) mism.Add("a drawing is still open after close");
            return Applied("Closed drawing " + id + (save ? " (saved)." : " (not saved)."), new[] { id }, new JArray(), mism);
        }

        private JObject AddText(JObject p, bool apply)
        {
            var dh = Handler();
            var active = Active(dh) ?? throw Invalid("No drawing is open. Open it first with tekla_open_drawing.");
            string id = (string)p["drawing_id"];
            if (!string.Equals(Id(active), id, StringComparison.Ordinal)) throw Invalid("Drawing " + id + " is not the open drawing (" + Id(active) + "). Open it first with tekla_open_drawing.");
            string text = (string)p["text"];
            if (string.IsNullOrEmpty(text)) throw Invalid("text is required.");
            var pt = p["point_mm"] as JArray;
            if (pt == null || pt.Count != 2) throw Invalid("point_mm must be [x, y] in sheet mm (origin at the lower-left corner of the sheet).");
            var planned = new JObject { ["drawing_id"] = id, ["text"] = text, ["point_mm"] = pt.DeepClone() };
            if (!apply) return Preview("Add text \"" + text + "\" to the open drawing at [" + pt[0] + ", " + pt[1] + "] mm.", planned);
            var t = new TSD.Text(active.GetSheet(), new T3D.Point((double)pt[0], (double)pt[1], 0), text);
            var mism = new JArray();
            if (!t.Insert()) mism.Add("Text.Insert returned false");
            active.CommitChanges();
            if (!t.Select() || t.TextString != text) mism.Add("text read back '" + t.TextString + "'");
            var r = Applied("Added text to drawing " + id + ". Close the drawing with save=true (tekla_close_drawing) to keep it.", new[] { id }, new JArray(), mism);
            return r;
        }

        // ================= numbering =================

        private JObject Numbering(TSM.Model m, JObject p, bool apply)
        {
            string scope = (string)p["scope"] ?? "modified";
            if (scope != "modified" && scope != "full") throw Invalid("scope must be modified or full.");
            var parts = TeklaQuery.Filtered(m, new JObject()).Select(x => x.obj).OfType<TSM.Part>().ToList();
            int notUpToDate = parts.Count(x => !TSM.Operations.Operation.IsNumberingUpToDate(x));
            string macroDir = WritableMacroDir();
            var planned = new JObject
            {
                ["scope"] = scope, ["parts"] = parts.Count, ["parts_not_up_to_date"] = notUpToDate,
                ["numbering_up_to_date_all"] = TSM.Operations.Operation.IsNumberingUpToDateAll(),
                ["macro_folder"] = macroDir
            };
            var preview = Preview((scope == "full" ? "Run FULL numbering (all parts are renumbered; existing marks may change)" : "Number modified objects (" + notUpToDate + " part(s) not up to date)") +
                                  " with Tekla's numbering settings.", planned);
            if (scope == "full") ((JArray)preview["warnings"]).Add("Full numbering can change marks of every part and assembly, and drawings may need updating. Save the model first (tekla_save_model).");
            if (!apply) return preview;

            // No Open API runs numbering (D10): run Tekla's own commands through a temporary macro.
            // Commands taken from Tekla's shipped macros: CleanUpNumberingBeforeCreatingDrawings.cs ("") and Diagnose and Repair Model.cs ("full").
            string file = "TeklaMcp_Numbering_" + System.Guid.NewGuid().ToString("N") + ".cs";
            string path = Path.Combine(macroDir, file);
            string body = scope == "full"
                ? "akit.Callback(\"acmd_partnumbers_all\", \"full\", \"main_frame\"); akit.PushButton(\"warning_ok\", \"full_numbering_ok\");"
                : "akit.Callback(\"acmd_partnumbers_all\", \"\", \"main_frame\");";
            File.WriteAllText(path, "namespace Tekla.Technology.Akit.UserScript { public class Script { public static void Run(Tekla.Technology.Akit.IScript akit) { " + body + " } } }");
            var mism = new JArray();
            // Proof that Tekla ran numbering: it rewrites <model>\logs\numberingresults.log (checked 2026-11-26; IsMacroRunning() is
            // false right after RunMacro, and IsNumberingUpToDateAll() is already true when nothing needs numbering).
            string log = Path.Combine(m.GetInfo().ModelPath, "logs", "numberingresults.log");
            DateTime started = DateTime.Now.AddSeconds(-1);
            bool ran = false;
            try
            {
                if (!TSM.Operations.Operation.RunMacro(file)) throw new TeklaException("tekla_error", "Tekla could not start the numbering macro.");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(45))
                {
                    ran = File.Exists(log) && File.GetLastWriteTime(log) >= started && FinishedLog(log);
                    if (ran && !TSM.Operations.Operation.IsMacroRunning()) break;
                    Thread.Sleep(250);
                }
            }
            finally { try { File.Delete(path); } catch { } }
            int after = parts.Count(x => !TSM.Operations.Operation.IsNumberingUpToDate(x));
            bool all = TSM.Operations.Operation.IsNumberingUpToDateAll();
            if (!ran) mism.Add("Tekla's numbering log (" + log + ") was not written within 45 s: numbering may not have run (a Tekla dialog may be waiting)");
            if (!all) mism.Add("numbering is not up to date after the run");
            if (after > 0) mism.Add(after + " part(s) still not up to date");
            var summary = ran ? File.ReadAllLines(log).Where(l => l.StartsWith("Numbering ") || l.Contains("Numbering:")).Select(l => l.Replace("NUMBERING_HISTORY ", "").Trim()).ToList() : new List<string>();
            var r = Applied("Numbering (" + scope + ") " + (ran ? "ran" : "NOT CONFIRMED") + ": " + (notUpToDate - after) + " previously unnumbered part(s) now numbered; up to date: " + (all ? "yes" : "no") +
                            (summary.Count > 0 ? "; Tekla log: " + string.Join("; ", summary) : "") + ".", new string[0], new JArray(), mism);
            r["numbering_up_to_date_all"] = all; r["parts_not_up_to_date"] = after; r["tekla_numbering_log"] = new JArray(summary);
            return r;
        }

        // Tekla writes the results log in steps; the run is complete when the last-step line is present.
        private static bool FinishedLog(string log)
        {
            try
            {
                using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string text = sr.ReadToEnd();
                    return text.Contains("Assembly numbering done") || text.Contains("Part numbering done");
                }
            }
            catch (IOException) { return false; }
        }

        private static string WritableMacroDir()
        {
            string dirs = "";
            global::Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref dirs);
            foreach (var d in (dirs ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                string modeling = Path.Combine(d, "modeling");
                try
                {
                    if (!Directory.Exists(modeling)) continue;
                    string t = Path.Combine(modeling, "_teklamcp_write_test.tmp");
                    File.WriteAllText(t, ""); File.Delete(t);
                    return modeling;
                }
                catch { }
            }
            throw new TeklaException("tekla_error", "No writable 'modeling' folder in XS_MACRO_DIRECTORY (" + dirs + "); numbering needs one for a temporary macro.");
        }

        // ================= helpers =================

        private TSD.DrawingHandler Handler()
        {
            _session.RequireModel();
            var dh = new TSD.DrawingHandler();
            if (!dh.GetConnectionStatus()) throw new TeklaException("not_connected", "The Tekla drawing API is not available.");
            return dh;
        }

        private static IEnumerable<TSD.Drawing> All(TSD.DrawingHandler dh)
        {
            var en = dh.GetDrawings();
            while (en.MoveNext()) if (en.Current != null) yield return en.Current;
        }

        private static TSD.Drawing Active(TSD.DrawingHandler dh) => dh.IsAnyDrawingOpen() ? dh.GetActiveDrawing() : null;

        internal static string Kind(TSD.Drawing d) =>
            d is TSD.GADrawing ? "ga" : d is TSD.AssemblyDrawing ? "assembly" : d is TSD.SinglePartDrawing ? "single_part" :
            d is TSD.CastUnitDrawing ? "cast_unit" : d is TSD.MultiDrawing ? "multi" : d.GetType().Name;

        internal static string Id(string kind, string mark, DateTime created) => kind + "|" + (mark ?? "") + "|" + created.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        private static string Id(TSD.Drawing d) => Id(Kind(d), d.Mark, d.CreationDate);

        private static TSD.Drawing Find(TSD.DrawingHandler dh, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw Invalid("drawing_id is required (from tekla_list_drawings).");
            return All(dh).FirstOrDefault(d => Id(d) == id) ?? throw Invalid("Drawing not found: " + id + ". Use tekla_list_drawings.");
        }

        private static (List<TSD.Drawing> list, TSD.Drawing active) Targets(TSD.DrawingHandler dh, JObject p)
        {
            var ids = (p["drawing_ids"] as JArray)?.Values<string>().Distinct().ToList() ?? new List<string>();
            if (ids.Count == 0 || ids.Count > MaxDrawings) throw Invalid("drawing_ids must contain 1-" + MaxDrawings + " items.");
            var all = All(dh).ToList();
            var missing = ids.Where(i => !all.Any(d => Id(d) == i)).ToList();
            if (missing.Count > 0) throw Invalid("Drawings not found: " + string.Join(", ", missing.Take(20)) + ". Use tekla_list_drawings.");
            return (all.Where(d => ids.Contains(Id(d))).ToList(), Active(dh));
        }

        private static void RefuseActive(List<TSD.Drawing> list, TSD.Drawing active, string verb)
        {
            if (active != null && list.Any(d => Id(d) == Id(active)))
                throw Invalid("Cannot " + verb + " the drawing that is open in Tekla (" + Id(active) + "). Close it first (tekla_close_drawing).");
        }

        private static List<JObject> ReadBack(TSD.DrawingHandler dh, List<string> ids, JArray mism)
        {
            var active = Active(dh);
            var all = All(dh).ToList();
            var rows = new List<JObject>();
            foreach (var id in ids)
            {
                var d = all.FirstOrDefault(x => Id(x) == id);
                if (d == null) mism.Add(id + " not found after apply"); else rows.Add(Row(d, active));
            }
            return rows;
        }

        private static string Get(TSD.Drawing d, string f) => f == "name" ? d.Name : f == "title1" ? d.Title1 : f == "title2" ? d.Title2 : d.Title3;
        private static void Set(TSD.Drawing d, string f, string v)
        {
            switch (f) { case "name": d.Name = v; break; case "title1": d.Title1 = v; break; case "title2": d.Title2 = v; break; default: d.Title3 = v; break; }
        }

        internal static JObject Row(TSD.Drawing d, TSD.Drawing active)
        {
            var r = new JObject
            {
                ["drawing_id"] = Id(d), ["type"] = Kind(d), ["mark"] = d.Mark, ["name"] = d.Name,
                ["title1"] = d.Title1, ["title2"] = d.Title2, ["title3"] = d.Title3,
                ["up_to_date_status"] = d.UpToDateStatus.ToString(),
                ["up_to_date"] = d.UpToDateStatus == TSD.DrawingUpToDateStatus.DrawingIsUpToDate,
                ["issued"] = d.IsIssued, ["issued_but_modified"] = d.IsIssuedButModified, ["ready_for_issue"] = d.IsReadyForIssue,
                ["locked"] = d.IsLocked, ["frozen"] = d.IsFrozen,
                ["is_open"] = active != null && Id(active) == Id(d),
                ["created"] = d.CreationDate.ToString("s"), ["modified"] = d.ModificationDate.ToString("s")
            };
            if (d.IsIssued) r["issued_date"] = d.IssuingDate.ToString("s");
            try { var s = d.Layout.SheetSize; r["sheet_size_mm"] = new JArray(Math.Round(s.Width, 1), Math.Round(s.Height, 1)); } catch { }
            return r;
        }

        private static string Guid(TSM.ModelObject o) => o.Identifier.GUID.ToString();
        private static TeklaException Invalid(string msg) => new TeklaException("invalid_arguments", msg);

        private static JObject Preview(string summary, JObject planned)
        {
            var r = new JObject { ["summary"] = summary, ["applied"] = false, ["warnings"] = new JArray() };
            foreach (var x in planned.Properties()) r[x.Name] = x.Value;
            return r;
        }

        private static JObject Applied(string summary, IEnumerable<string> affected, JArray objects, JArray mism) => new JObject
        {
            ["summary"] = summary + (mism.Count > 0 ? " READ-BACK MISMATCH: " + mism.Count + " issue(s)." : " Read-back matches the request."),
            ["applied"] = true, ["affected"] = new JArray(affected), ["objects"] = objects,
            ["verification"] = new JObject { ["ok"] = mism.Count == 0, ["mismatches"] = mism }
        };
    }
}

