using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    /// <summary>
    /// Groups object rows (from the worker "query" op with quantities) and sums their quantities.
    /// Row fields used: the group_by fields, plus weight_kg, length_mm, area_mm2, volume_mm3, number_of_bars.
    /// </summary>
    internal static class Quantity
    {
        public static readonly string[] GroupFields = { "type", "report_profile", "profile", "material", "name", "class", "phase", "assembly_pos", "part_pos" };

        public static JObject Summarize(IEnumerable<JObject> rows, IList<string> groupBy)
        {
            var groups = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var order = new List<string>();
            var total = NewSums();
            foreach (var row in rows)
            {
                string key = string.Join("\u001f", groupBy.Select(f => (string)row[f] ?? ""));
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new JObject();
                    foreach (var f in groupBy) g[f] = row[f]?.DeepClone() ?? "";
                    foreach (var p in NewSums().Properties()) g[p.Name] = p.Value;
                    groups[key] = g; order.Add(key);
                }
                Add(g, row); Add(total, row);
            }
            var list = order.Select(k => groups[k])
                            .OrderBy(g => string.Join("\u001f", groupBy.Select(f => (string)g[f] ?? "")), StringComparer.OrdinalIgnoreCase)
                            .Select(Round).ToList();
            return new JObject
            {
                ["group_by"] = new JArray(groupBy),
                ["groups"] = new JArray(list),
                ["total"] = Round(total)
            };
        }

        private static JObject NewSums() => new JObject
        {
            ["object_count"] = 0,
            ["bar_count"] = 0,
            ["total_length_mm"] = 0.0,
            ["total_weight_kg"] = 0.0,
            ["total_area_mm2"] = 0.0,
            ["total_volume_mm3"] = 0.0
        };

        private static void Add(JObject sums, JObject row)
        {
            sums["object_count"] = (long)sums["object_count"] + 1;
            sums["bar_count"] = (long)sums["bar_count"] + ((long?)row["number_of_bars"] ?? 0);
            sums["total_length_mm"] = (double)sums["total_length_mm"] + ((double?)row["length_mm"] ?? 0);
            sums["total_weight_kg"] = (double)sums["total_weight_kg"] + ((double?)row["weight_kg"] ?? 0);
            sums["total_area_mm2"] = (double)sums["total_area_mm2"] + ((double?)row["area_mm2"] ?? 0);
            sums["total_volume_mm3"] = (double)sums["total_volume_mm3"] + ((double?)row["volume_mm3"] ?? 0);
        }

        private static JObject Round(JObject g)
        {
            foreach (var f in new[] { "total_length_mm", "total_area_mm2", "total_volume_mm3" }) g[f] = Math.Round((double)g[f], 1);
            g["total_weight_kg"] = Math.Round((double)g["total_weight_kg"], 3);
            return g;
        }
    }
}
