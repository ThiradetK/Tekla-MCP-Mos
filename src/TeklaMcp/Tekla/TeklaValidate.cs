using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla
{
    /// <summary>Phase 4 (read-only): model validation (numbering / assemblies) and clash check.</summary>
    internal sealed class TeklaValidate
    {
        private readonly TeklaSession _session;
        public TeklaValidate(TeklaSession session) { _session = session; }

        /// <summary>args: worker filter (default all parts), offset, limit.</summary>
        public JObject Validate(JObject a)
        {
            var m = _session.RequireModel();
            return TeklaQuery.InGlobal(m, () =>
            {
                int offset = (int?)a["offset"] ?? 0, limit = (int?)a["limit"] ?? 50;
                var parts = TeklaQuery.Filtered(m, a).Where(x => x.obj is TSM.Part).ToList();
                var counts = new JObject { ["numbering_not_up_to_date"] = 0, ["no_assembly_mark"] = 0, ["no_part_mark"] = 0 };
                var issues = new List<JObject>();
                foreach (var (row, o) in parts)
                {
                    var found = new JArray();
                    if (!TSM.Operations.Operation.IsNumberingUpToDate(o)) found.Add("numbering_not_up_to_date");
                    if (Unnumbered((string)row["assembly_pos"])) found.Add("no_assembly_mark");
                    if (Unnumbered((string)row["part_pos"])) found.Add("no_part_mark");
                    if (found.Count == 0) continue;
                    foreach (var f in found) counts[(string)f] = (int)counts[(string)f] + 1;
                    row["issues"] = found;
                    issues.Add(row);
                }

                // Model-wide: assemblies that lost their main part.
                var emptyAsm = new JArray();
                foreach (var asm in TeklaQuery.Each(m.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSM.Assembly) })).OfType<TSM.Assembly>())
                    if (asm.GetMainPart() == null) emptyAsm.Add(TeklaQuery.Brief(asm));
                counts["assemblies_without_main_part"] = emptyAsm.Count;

                bool upToDate = TSM.Operations.Operation.IsNumberingUpToDateAll();
                var r = new JObject
                {
                    ["numbering_up_to_date_all"] = upToDate,
                    ["checked_parts"] = parts.Count,
                    ["counts"] = counts,
                    ["total_issues"] = issues.Count,
                    ["offset"] = offset,
                    ["items"] = new JArray(issues.Skip(offset).Take(limit)),
                    ["assemblies_without_main_part"] = new JArray(emptyAsm.Take(50))
                };
                if (!upToDate || issues.Count > 0)
                    r["advice"] = "Numbering is not up to date: run numbering in Tekla before creating assembly or single-part drawings or reports that use marks.";
                return r;
            });
        }

        // Tekla shows "(?)" in marks whose numbering is not up to date.
        internal static bool Unnumbered(string mark) => string.IsNullOrWhiteSpace(mark) || mark.Contains("?");

        /// <summary>
        /// args: guids or worker filter (default all parts), min_distance_mm, wait_seconds, offset, limit.
        /// Tekla clash-checks the objects selected in the model view, so the selection is replaced for the run and restored afterwards.
        /// </summary>
        public JObject Clash(JObject a)
        {
            var m = _session.RequireModel();
            return TeklaQuery.InGlobal(m, () =>
            {
                List<TSM.ModelObject> objs;
                if (a["guids"] is JArray g && g.Count > 0)
                {
                    var (found, missing) = TeklaQuery.Fetch(m, g);
                    if (missing.Count > 0) throw new TeklaException("invalid_arguments", "Objects not found: " + string.Join(", ", missing.Take(20)));
                    objs = found;
                }
                else objs = TeklaQuery.Filtered(m, a).Select(x => x.obj).ToList();
                if (objs.Count == 0) throw new TeklaException("invalid_arguments", "No objects to check.");

                double minDistance = (double?)a["min_distance_mm"] ?? 0;
                int wait = Math.Max(5, (int?)a["wait_seconds"] ?? 55);
                int offset = (int?)a["offset"] ?? 0, limit = (int?)a["limit"] ?? 100;

                var ui = new TSM.UI.ModelObjectSelector();
                var before = new ArrayList(TeklaQuery.Each(ui.GetSelectedObjects()).ToList());
                var clashes = new List<TSM.ClashCheckData>();
                int reported = -1;
                var done = new ManualResetEventSlim(false);
                var events = new TSM.Events();
                events.ClashDetected += d => { lock (clashes) clashes.Add(d); };
                events.ClashCheckDone += n => { reported = n; done.Set(); };
                events.Register();
                bool finished;
                try
                {
                    if (!ui.Select(new ArrayList(objs))) throw new TeklaException("tekla_error", "Tekla refused to select the objects for the clash check.");
                    if (!m.GetClashCheckHandler().RunClashCheckWithOptions(false, false, minDistance, true))
                        throw new TeklaException("tekla_error", "Tekla refused to start the clash check.");
                    finished = done.Wait(TimeSpan.FromSeconds(wait));
                    if (!finished) m.GetClashCheckHandler().StopClashCheck();
                }
                finally
                {
                    events.UnRegister();
                    ui.Select(before);
                }

                List<TSM.ClashCheckData> list; lock (clashes) list = clashes.ToList();
                var byType = new JObject();
                foreach (var grp in list.GroupBy(c => TypeName(c.Type))) byType[grp.Key] = grp.Count();
                var r = new JObject
                {
                    ["checked_objects"] = objs.Count,
                    ["finished"] = finished,
                    ["clash_count"] = list.Count,
                    ["reported_by_tekla"] = reported,
                    ["min_distance_mm"] = minDistance,
                    ["by_type"] = byType,
                    ["offset"] = offset,
                    ["items"] = new JArray(list.Skip(offset).Take(limit).Select(c => ClashRow(m, c)))
                };
                if (!finished) r["warning"] = "Clash check did not finish within " + wait + " s and was stopped; results are partial. Check fewer objects or raise teklaTimeoutSeconds.";
                return r;
            });
        }

        private static JObject ClashRow(TSM.Model m, TSM.ClashCheckData c)
        {
            // Event objects carry only the identifier (name read back as the default "BEAM"): load them before reading fields.
            var o1 = c.Object1 == null ? null : m.SelectModelObject(c.Object1.Identifier);
            var o2 = c.Object2 == null ? null : m.SelectModelObject(c.Object2.Identifier);
            // Tested 2026-11-26 on two crossing beams: Overlap = 0 and GetIntersectionBoundingBoxes returns an empty list,
            // so no clash size is reported; tekla_overlap is Tekla's raw value (no documented unit).
            return new JObject
            {
                ["type"] = TypeName(c.Type),
                ["tekla_overlap"] = c.Overlap,
                ["object1"] = o1 == null ? null : TeklaQuery.Brief(o1),
                ["object2"] = o2 == null ? null : TeklaQuery.Brief(o2)
            };
        }

        private static string TypeName(TSM.ClashCheckData.ClashTypeEnum t) => t.ToString().Replace("CLASH_TYPE_", "").ToLowerInvariant();
    }
}

