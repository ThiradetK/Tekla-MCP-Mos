using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Query
{
    /// <summary>Phase 2 read-only query tools (PLAN §4 "Query").</summary>
    internal static class QueryTools
    {
        private const string Global = " Coordinates are global, in mm.";

        public static IEnumerable<ITool> Create(TeklaWorker w)
        {
            JObject Paging(int max) => new JObject
            {
                ["offset"] = S.Integer("Items to skip (paging). Default 0.", 0),
                ["limit"] = S.Integer("Max items to return, 1-" + max + ". Default 50.", 1, max)
            };

            yield return new WorkerTool(w, "tekla_find_objects", "Find model objects",
                "Find objects in the open Tekla model by type, profile, material, name, class, phase, user attribute, bounding box or current selection. " +
                "Returns a page of brief rows (guid, id, type, name, profile, material, class, phase, positions) plus the total match count. " +
                "Set include_quantities for weight_kg, length_mm, area_mm2, volume_mm3 per object (slower)." + Global,
                S.Object(ObjectFilter.Merge(ObjectFilter.Properties(), ObjectFilter.Merge(Paging(500), new JObject
                {
                    ["include_quantities"] = S.Boolean("Add weight_kg, length_mm, area_mm2, volume_mm3 (and number_of_bars for rebar).")
                }))),
                "query",
                a => ObjectFilter.Merge(ObjectFilter.ToWorker(a), new JObject
                {
                    ["offset"] = (int?)a["offset"] ?? 0, ["limit"] = (int?)a["limit"] ?? 50,
                    ["include_quantities"] = (bool?)a["include_quantities"] ?? false
                }),
                r => "Found " + r["total"] + " object(s); returned " + ((JArray)r["items"]).Count + " from offset " + r["offset"] + ".");

            yield return new WorkerTool(w, "tekla_get_objects", "Get object details",
                "Full details for objects by GUID: brief fields, quantities (weight_kg, length_mm, area_mm2, volume_mm3), geometry " +
                "(beam start/end, plate contour, bolt positions, bounding box), all user-defined attributes, and optional report properties." + Global,
                S.Object(new JObject
                {
                    ["guids"] = S.Array(S.String("GUID"), "Object GUIDs (1-100)."),
                    ["report_properties"] = S.Array(S.String("Report property name"), "Extra Tekla report properties to read, e.g. [\"PART_POS\",\"WEIGHT_GROSS\"].")
                }, "guids"),
                "get",
                a => { Count(a["guids"], 1, 100, "guids"); return a; },
                r => "Returned " + ((JArray)r["items"]).Count + " object(s)" + NotFound(r) + ".");

            yield return new WorkerTool(w, "tekla_get_selection", "Get selected objects",
                "List the objects currently selected in the Tekla model view (brief rows, paged)." + Global,
                S.Object(ObjectFilter.Merge(Paging(500), new JObject
                {
                    ["include_quantities"] = S.Boolean("Add weight_kg, length_mm, area_mm2, volume_mm3.")
                })),
                "query",
                a => new JObject
                {
                    ["selection"] = true, ["filter"] = new JObject(),
                    ["offset"] = (int?)a["offset"] ?? 0, ["limit"] = (int?)a["limit"] ?? 50,
                    ["include_quantities"] = (bool?)a["include_quantities"] ?? false
                },
                r => r["total"] + " object(s) selected; returned " + ((JArray)r["items"]).Count + ".");

            yield return new WorkerTool(w, "tekla_select_objects", "Select objects in Tekla",
                "Select objects by GUID in the Tekla model view (replaces the current selection). Changes only the UI selection, not the model.",
                S.Object(new JObject { ["guids"] = S.Array(S.String("GUID"), "Object GUIDs to select (1-1000).") }, "guids"),
                "select",
                a => { Count(a["guids"], 1, 1000, "guids"); return a; },
                r => "Selected " + r["selected"] + " object(s)" + NotFound(r) + ".");

            yield return new WorkerTool(w, "tekla_get_assembly", "Get assembly",
                "Assembly of a part, rebar or assembly GUID: the assembly, its main part, secondary parts (with quantities), sub-assemblies, bolts, welds, " +
                "and the count/weight of reinforcement in its parts." + Global,
                S.Object(new JObject { ["guid"] = S.String("GUID of an assembly, part or rebar.") }, "guid"),
                "assembly", null,
                r => "Assembly " + (r["assembly"]?["assembly_pos"] ?? r["assembly"]?["name"]) + ": main part + " + ((JArray)r["secondaries"]).Count +
                     " secondary part(s), " + ((JArray)r["bolts"]).Count + " bolt group(s), " + ((JArray)r["welds"]).Count + " weld(s).");

            yield return new WorkerTool(w, "tekla_list_grids", "List grids",
                "List grids with their raw coordinate/label strings and every grid plane (label, axis x/y/z, coordinate_mm along that axis)." + Global,
                S.Object(), "grids", null,
                r => ((JArray)r["grids"]).Count + " grid(s), " + r["grids"].Sum(g => ((JArray)g["planes"]).Count) + " grid plane(s).");

            yield return new WorkerTool(w, "tekla_get_workplane", "Get current work plane",
                "Current Tekla work plane (origin in mm and unit axes, expressed in global coordinates) and whether it is the global plane.",
                S.Object(), "workplane", null,
                r => (bool)r["is_global"] ? "Work plane is global." : "Work plane is local; origin " + r["origin_mm"].ToString(Newtonsoft.Json.Formatting.None) + " mm.");

            yield return new WorkerTool(w, "tekla_get_report_properties", "Read report properties",
                "Read any Tekla report properties for objects by GUID (e.g. WEIGHT kg, WEIGHT_GROSS kg, LENGTH mm, AREA mm2, VOLUME mm3, PROFILE, MATERIAL, PART_POS, ASSEMBLY_POS). " +
                "Each value is returned as number or text as Tekla provides it; null when the property is not available for that object.",
                S.Object(new JObject
                {
                    ["guids"] = S.Array(S.String("GUID"), "Object GUIDs (1-500)."),
                    ["names"] = S.Array(S.String("Report property name"), "Property names (1-30).")
                }, "guids", "names"),
                "report_properties",
                a => { Count(a["guids"], 1, 500, "guids"); Count(a["names"], 1, 30, "names"); return a; },
                r => "Read properties for " + ((JArray)r["items"]).Count + " object(s)" + NotFound(r) + ".");

            yield return new WorkerTool(w, "tekla_list_catalog", "List catalog items",
                "List profiles, materials, bolts or rebar sizes available in the current Tekla environment (SE_Asia), with wildcard name filter and paging. " +
                "Use it to check a profile/material name before creating objects.",
                S.Object(ObjectFilter.Merge(new JObject
                {
                    ["kind"] = S.String("Catalog to list.", new[] { "profile", "material", "bolt", "rebar" }),
                    ["filter"] = S.String("Name filter with * and ? wildcards (e.g. \"WF400*\", \"SS*\").")
                }, Paging(1000)), "kind"),
                "catalog",
                a => new JObject { ["kind"] = a["kind"], ["filter"] = a["filter"], ["offset"] = (int?)a["offset"] ?? 0, ["limit"] = (int?)a["limit"] ?? 50 },
                r => r["total"] + " " + r["kind"] + " item(s) match; returned " + ((JArray)r["items"]).Count + ".");
        }

        internal static void Count(JToken arr, int min, int max, string name)
        {
            int n = (arr as JArray)?.Count ?? 0;
            if (n < min || n > max) throw new TeklaException("invalid_arguments", name + " must contain " + min + "-" + max + " items (got " + n + ").");
        }

        private static string NotFound(JObject r) => ((JArray)r["not_found"]).Count > 0 ? "; not found: " + ObjectFilter.Guids(r["not_found"]) : "";
    }
}
