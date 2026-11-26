using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using TeklaMcp.Tools.Modify;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Drawing
{
    /// <summary>Phase 5 tools: drawings (PLAN §4 "Drawing") and numbering (decision D10).</summary>
    internal static class DrawingTools
    {
        private const string IdNote = " Drawings are referenced by drawing_id from tekla_list_drawings (\"type|mark|created\"; the Open API has no drawing GUID).";
        private static readonly string[] Kinds = { "ga", "assembly", "single_part", "cast_unit", "multi" };

        private static JObject Ids(string d) => S.Array(S.String("drawing_id"), d + " (1-" + TeklaDrawing.MaxDrawings + ", from tekla_list_drawings).");

        public static IEnumerable<ITool> Create(TeklaWorker w)
        {
            JObject Paging(int max, int def) => new JObject
            {
                ["offset"] = S.Integer("Items to skip (paging). Default 0.", 0),
                ["limit"] = S.Integer("Max items to return, 1-" + max + ". Default " + def + ".", 1, max)
            };

            // ---------- READ ----------
            yield return new WorkerTool(w, "tekla_list_drawings", "List drawings",
                "List drawings in the open model (drawing list): drawing_id, type (ga, assembly, single_part, cast_unit, multi), mark, name, titles, " +
                "up-to-date status, issued/locked/frozen flags, whether it is open, dates and sheet size in mm. Filters: types, mark/name wildcards, up_to_date, issued." + IdNote,
                S.Object(ObjectFilter.Merge(Paging(500, 50), new JObject
                {
                    ["types"] = S.Array(S.String("Drawing type", Kinds), "Drawing types to include. Default all."),
                    ["mark"] = S.String("Mark filter with * and ? wildcards, e.g. \"[PF.1*\"."),
                    ["name"] = S.String("Name filter with wildcards."),
                    ["up_to_date"] = S.Boolean("true = only up-to-date drawings, false = only out-of-date ones."),
                    ["issued"] = S.Boolean("true = only issued drawings, false = only not issued.")
                })),
                "drawings", null,
                r => "Found " + r["total"] + " drawing(s); returned " + ((JArray)r["items"]).Count + " from offset " + r["offset"] + "." + ((bool)r["any_drawing_open"] ? " A drawing is open in Tekla." : ""));

            yield return new WorkerTool(w, "tekla_get_drawing", "Get drawing details",
                "Details of one drawing: list fields, its views (name, type, scale, size in mm) and the model objects it shows (brief rows, paged)." + IdNote,
                S.Object(ObjectFilter.Merge(Paging(500, 50), new JObject { ["drawing_id"] = S.String("drawing_id from tekla_list_drawings.") }), "drawing_id"),
                "drawing_get", null,
                r => "Drawing " + r["drawing_id"] + ": " + r["model_object_count"] + " model object(s), " + ((r["views"] as JArray)?.Count.ToString() ?? "?") + " view(s), " + r["up_to_date_status"] + ".");

            yield return new WorkerTool(w, "tekla_open_drawing", "Open drawing",
                "Open a drawing in the Tekla drawing editor (UI only; nothing is saved or changed). Needed before tekla_add_drawing_text. " +
                "Close it with tekla_close_drawing." + IdNote,
                S.Object(new JObject { ["drawing_id"] = S.String("drawing_id from tekla_list_drawings.") }, "drawing_id"),
                "drawing_open", null,
                r => "Opened drawing " + r["drawing_id"] + ".");

            // ---------- WRITE ----------
            yield return new WorkerWriteTool(w, "tekla_run_numbering", "Run numbering",
                "Run Tekla numbering with the model's numbering settings. scope=modified (default) numbers new and changed objects; scope=full renumbers everything " +
                "(existing marks may change). Needed before assembly, single-part and cast-unit drawings. The Open API has no numbering call, so this runs Tekla's " +
                "numbering command through a temporary macro file in the XS_MACRO_DIRECTORY modeling folder (deleted afterwards).", ToolLevel.Write,
                new JObject { ["scope"] = S.String("modified (default) or full.", new[] { "modified", "full" }) },
                new string[0], "numbering");

            yield return new WorkerWriteTool(w, "tekla_create_drawing", "Create drawing",
                "Create a drawing with a drawing attributes file (default \"standard\"). kind=ga needs name; assembly, single_part and cast_unit need part_guid " +
                "(the part's assembly / the part / its cast unit) and up-to-date numbering. Optionally open it in the editor.", ToolLevel.Write,
                new JObject
                {
                    ["kind"] = S.String("Drawing type.", new[] { "ga", "assembly", "single_part", "cast_unit" }),
                    ["part_guid"] = S.String("Part GUID (assembly, single_part, cast_unit)."),
                    ["name"] = S.String("GA drawing name (ga)."),
                    ["attributes"] = S.String("Drawing attributes file name without extension. Default \"standard\"."),
                    ["open"] = S.Boolean("Open the new drawing in the editor after creating it. Default false.")
                },
                new[] { "kind" }, "drawing_create");

            yield return new WorkerWriteTool(w, "tekla_update_drawings", "Update drawings",
                "Update drawings (same as Update in the drawing list). The drawings must not be open, and numbering must be up to date.", ToolLevel.Write,
                new JObject { ["drawing_ids"] = Ids("Drawings to update") }, new[] { "drawing_ids" }, "drawing_update");

            yield return new WorkerWriteTool(w, "tekla_issue_drawings", "Issue drawings",
                "Issue (issue=true, default) or unissue (issue=false) drawings.", ToolLevel.Write,
                new JObject { ["drawing_ids"] = Ids("Drawings to issue or unissue"), ["issue"] = S.Boolean("true = issue (default), false = unissue.") },
                new[] { "drawing_ids" }, "drawing_issue");

            yield return new WorkerWriteTool(w, "tekla_modify_drawings", "Modify drawing properties",
                "Change drawing name and titles. The preview shows from -> to for every drawing.", ToolLevel.Write,
                new JObject
                {
                    ["drawing_ids"] = Ids("Drawings to change"),
                    ["set"] = S.Object(new JObject
                    {
                        ["name"] = S.String("Drawing name."), ["title1"] = S.String("Title 1."), ["title2"] = S.String("Title 2."), ["title3"] = S.String("Title 3.")
                    })
                },
                new[] { "drawing_ids", "set" }, "drawing_modify");

            yield return new WorkerWriteTool(w, "tekla_add_drawing_text", "Add text to drawing",
                "Add a text annotation to the sheet of the drawing that is open in the editor, at point_mm [x, y] in sheet coordinates (mm from the sheet's lower-left corner). " +
                "Save it by closing the drawing with save=true.", ToolLevel.Write,
                new JObject
                {
                    ["drawing_id"] = S.String("drawing_id of the open drawing."),
                    ["text"] = S.String("Text to add."),
                    ["point_mm"] = S.Array(S.Number("mm"), "[x, y] on the sheet in mm.")
                },
                new[] { "drawing_id", "text", "point_mm" }, "drawing_add_text");

            yield return new WorkerWriteTool(w, "tekla_close_drawing", "Close drawing",
                "Close the drawing editor. save=true (default) saves the open drawing; save=false discards its unsaved changes.", ToolLevel.Write,
                new JObject { ["save"] = S.Boolean("Save before closing. Default true.") }, new string[0], "drawing_close");

            yield return new WorkerWriteTool(w, "tekla_delete_drawings", "Delete drawings",
                "Delete drawings (max " + TeklaDrawing.MaxDrawings + "). An open drawing cannot be deleted.", ToolLevel.Destructive,
                new JObject { ["drawing_ids"] = Ids("Drawings to delete") }, new[] { "drawing_ids" }, "drawing_delete");
        }
    }
}
