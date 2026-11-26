using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Validation
{
    /// <summary>Phase 4 read-only validation tools (PLAN §4 "Validation", decision D11).</summary>
    internal static class ValidationTools
    {
        public static IEnumerable<ITool> Create(TeklaWorker w, Settings settings)
        {
            JObject Paging(int max, int def) => new JObject
            {
                ["offset"] = S.Integer("Items to skip (paging). Default 0.", 0),
                ["limit"] = S.Integer("Max items to return, 1-" + max + ". Default " + def + ".", 1, max)
            };

            yield return new WorkerTool(w, "tekla_validate_model", "Validate numbering and assemblies",
                "Check numbering and assemblies of filtered parts (default: all parts): parts whose numbering is not up to date, " +
                "parts without an assembly mark or part mark (Tekla shows \"(?)\"), and model-wide assemblies without a main part. " +
                "Returns counts and a page of the parts with issues. Read-only; does not run numbering.",
                S.Object(ObjectFilter.Merge(ObjectFilter.Properties(), Paging(500, 50))),
                "validate",
                a => ObjectFilter.Merge(ObjectFilter.ToWorker(a), new JObject { ["offset"] = (int?)a["offset"] ?? 0, ["limit"] = (int?)a["limit"] ?? 50 }),
                r => "Checked " + r["checked_parts"] + " part(s): " + r["total_issues"] + " with issues (numbering not up to date " + r["counts"]["numbering_not_up_to_date"] +
                     ", no assembly mark " + r["counts"]["no_assembly_mark"] + ", no part mark " + r["counts"]["no_part_mark"] +
                     "); assemblies without main part: " + r["counts"]["assemblies_without_main_part"] + ". Numbering up to date: " + ((bool)r["numbering_up_to_date_all"] ? "yes" : "no") + ".");

            int wait = System.Math.Max(5, settings.TeklaTimeoutSeconds - 5);
            yield return new WorkerTool(w, "tekla_clash_check", "Clash check",
                "Run Tekla's clash check between native parts of the given objects (guids, or a filter; default: all parts). " +
                "Returns each clash with type (clash, cutthrough, isinside, exactmatch, mindistance, ...) and both objects (no clash size: the API does not provide it). " +
                "min_distance_mm > 0 also reports objects closer than that distance. Tekla checks the objects selected in the model view, " +
                "so the selection is replaced during the run and restored afterwards. The model is not changed. The run is stopped after " + wait +
                " s (settings.json teklaTimeoutSeconds - 5); then results are partial.",
                S.Object(ObjectFilter.Merge(ObjectFilter.Properties(), ObjectFilter.Merge(Paging(500, 100), new JObject
                {
                    ["guids"] = S.Array(S.String("GUID"), "Objects to check (instead of a filter)."),
                    ["min_distance_mm"] = S.Number("Minimum clearance in mm; 0 = report only real clashes. Default 0.", 0)
                }))),
                "clash",
                a =>
                {
                    var r = ObjectFilter.Merge(ObjectFilter.ToWorker(a), new JObject
                    {
                        ["offset"] = (int?)a["offset"] ?? 0, ["limit"] = (int?)a["limit"] ?? 100,
                        ["min_distance_mm"] = (double?)a["min_distance_mm"] ?? 0, ["wait_seconds"] = wait
                    });
                    if (a["guids"] != null) r["guids"] = a["guids"].DeepClone();
                    return r;
                },
                r => "Clash check of " + r["checked_objects"] + " object(s): " + r["clash_count"] + " clash(es)" +
                     ((bool)r["finished"] ? "." : " (STOPPED: time limit, results partial)."));
        }
    }
}

