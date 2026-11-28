using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using S = TeklaMcp.Core.SchemaBuilder;

namespace TeklaMcp.Tools.Modify
{
    /// <summary>
    /// Phase 7b (decision D15): several model write steps under ONE preview and ONE confirm_token.
    /// Steps reuse the single write tools (same schema, catalog checks, read-back). A later step may use GUIDs created by an earlier
    /// step: "{{stepN.guid}}" (first affected GUID) or "{{stepN.guids[K]}}" (0-based). Such steps can only be argument-checked in
    /// the preview. Apply runs the steps in order and stops at the first error or read-back mismatch; done steps stay (no undo).
    /// </summary>
    internal sealed class BatchTool : IWriteTool
    {
        public const int MaxSteps = 100;

        // Steps whose "affected" GUIDs are NEW objects. Not tekla_create_assembly / tekla_add_to_assembly: their affected list is the
        // existing assembly and parts, so a reference would reach objects the preview never showed (code review 2026-11-28).
        internal static readonly HashSet<string> CreatingTools = new HashSet<string>
        {
            "tekla_create_beam", "tekla_create_column", "tekla_create_plate", "tekla_create_bolts", "tekla_create_weld",
            "tekla_create_rebar", "tekla_create_grid", "tekla_copy_objects"
        };

        // Steps that act on the objects listed in "guids".
        private static readonly HashSet<string> ActOnGuids = new HashSet<string>
        { "tekla_modify_objects", "tekla_move_objects", "tekla_copy_objects", "tekla_delete_objects" };
        private static readonly Regex Ref = new Regex(@"^\{\{step(\d+)\.(guid|guids\[(\d+)\])\}\}$", RegexOptions.Compiled);
        private readonly Dictionary<string, IWriteTool> _tools;

        public BatchTool(IEnumerable<IWriteTool> stepTools)
        {
            _tools = stepTools.ToDictionary(t => t.Name, StringComparer.Ordinal);
            InputSchema = S.ForWrite(new JObject
            {
                ["steps"] = S.Array(S.Object(new JObject
                {
                    ["tool"] = S.String("Write tool to run in this step.", _tools.Keys.OrderBy(k => k)),
                    // Free-form here: each step's args are validated against that step tool's own schema.
                    ["args"] = new JObject { ["type"] = "object", ["description"] = "Arguments of the step tool, without mode/confirm_token." }
                }, "tool", "args"), "1-" + MaxSteps + " steps, run in order. args are that tool's arguments without mode/confirm_token. " +
                    "A GUID argument may be \"{{stepN.guid}}\" or \"{{stepN.guids[K]}}\" to use what step N (1-based, earlier) created.")
            }, "steps");
        }

        public string Name => "tekla_batch_execute";
        public string Title => "Batch of model changes";
        public string Description =>
            "Run up to " + MaxSteps + " model write steps (create beam/column/plate/bolts/weld/rebar/grid, assemblies, modify, move, copy, delete) " +
            "with ONE preview and ONE confirm_token. Later steps can use objects created by earlier steps via \"{{stepN.guid}}\" / \"{{stepN.guids[K]}}\"; " +
            "such steps are only argument-checked in the preview (marked deferred). Apply runs the steps in order and STOPS at the first error or " +
            "read-back mismatch; completed steps stay (no undo). Treated as DESTRUCTIVE: the preview lists every step and every object to be deleted." +
            WriteTools.TwoStep;
        public ToolLevel Level => ToolLevel.Destructive;
        public JObject InputSchema { get; }

        // ================= preview =================

        public ToolResult Preview(JObject args)
        {
            var steps = Steps(args, out var error);
            if (error != null) return error;

            var rows = new JArray();
            var objects = new JArray();
            var warnings = new JArray();
            int deferred = 0, plannedObjects = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                var (toolName, stepArgs) = steps[i];
                var tool = _tools[toolName];
                var row = new JObject { ["step"] = i + 1, ["tool"] = toolName };
                var refs = Refs(stepArgs).ToList();
                if (refs.Count > 0)
                {
                    // References are checked here; the step's own preview (catalog, geometry) runs at apply time.
                    deferred++;
                    row["deferred"] = true;
                    row["references"] = new JArray(refs.Select(r => r.text));
                    row["args"] = stepArgs.DeepClone();
                    row["summary"] = "Deferred: uses objects created by step(s) " + string.Join(", ", refs.Select(r => r.step).Distinct()) + "; checked when applied.";
                    if (toolName == "tekla_delete_objects")
                        // Every target is an object this batch creates (enforced in Steps): list each one with the step that creates it.
                        foreach (var r in refs)
                            objects.Add(new JObject
                            {
                                ["step"] = i + 1, ["tool"] = toolName, ["delete"] = r.text, ["created_by_step"] = r.step,
                                ["created_by"] = rows[r.step - 1]["summary"]?.DeepClone()
                            });
                    else objects.Add(new JObject { ["step"] = i + 1, ["tool"] = toolName, ["summary"] = (string)row["summary"] });
                }
                else
                {
                    ToolResult p;
                    try { p = tool.Preview(stepArgs); }
                    catch (TeklaException ex) { return StepFail(i, toolName, ex.Code, ex.Message, rows); }
                    if (p.IsError) return StepFail(i, toolName, (string)p.Data["error"]?["code"] ?? "invalid_arguments", (string)p.Data["error"]?["message"] ?? p.Summary, rows);
                    row["summary"] = p.Summary;
                    row["preview"] = p.Data;
                    if (p.Data["objects"] is JArray objs)
                    {
                        plannedObjects += objs.Count;
                        foreach (var o in objs) { var c = (JObject)o.DeepClone(); c["step"] = i + 1; c["tool"] = toolName; objects.Add(c); }
                    }
                    else { plannedObjects++; objects.Add(new JObject { ["step"] = i + 1, ["tool"] = toolName, ["summary"] = p.Summary }); }
                    if (p.Data["warnings"] is JArray w) foreach (var x in w) warnings.Add("step " + (i + 1) + ": " + x);
                }
                rows.Add(row);
            }
            if (plannedObjects + deferred > TeklaWrite.SaveWarningCount)
                warnings.Add("This batch touches many objects. The Open API has no undo: save the model first (tekla_save_model).");
            warnings.Add("If a step fails during apply, the batch stops; steps already applied are NOT undone.");
            var data = new JObject
            {
                ["applied"] = false, ["step_count"] = steps.Count, ["deferred_steps"] = deferred,
                ["steps"] = rows, ["objects"] = objects, ["warnings"] = warnings
            };
            return ToolResult.Ok("Batch of " + steps.Count + " step(s): " + (steps.Count - deferred) + " previewed, " + deferred + " deferred (use earlier steps' objects).", data);
        }

        // ================= apply =================

        public ToolResult Apply(JObject args)
        {
            var steps = Steps(args, out var error);
            if (error != null) return error;

            var done = new JArray();
            var created = new List<JArray>();          // affected GUIDs per step (index = step - 1)
            var affected = new JArray();
            for (int i = 0; i < steps.Count; i++)
            {
                var (toolName, rawArgs) = steps[i];
                JObject stepArgs;
                try { stepArgs = (JObject)Resolve(rawArgs, created); }
                catch (ArgumentException ex) { return Stopped(i, toolName, "reference_error", ex.Message, done, affected, steps.Count); }

                ToolResult r;
                try { r = _tools[toolName].Apply(stepArgs); }
                catch (TeklaException ex) { return Stopped(i, toolName, ex.Code, ex.Message, done, affected, steps.Count); }
                if (r.IsError) return Stopped(i, toolName, (string)r.Data["error"]?["code"] ?? "tekla_error", (string)r.Data["error"]?["message"] ?? r.Summary, done, affected, steps.Count);

                var stepAffected = r.Data["affected"] as JArray ?? new JArray();
                created.Add(stepAffected);
                foreach (var g in stepAffected) affected.Add(g.DeepClone());
                done.Add(new JObject
                {
                    ["step"] = i + 1, ["tool"] = toolName, ["summary"] = r.Summary, ["affected"] = stepAffected.DeepClone(),
                    ["verification"] = r.Data["verification"]?.DeepClone()
                });
                if (r.Data["verification"] is JObject v && (bool?)v["ok"] == false)
                    return Stopped(i, toolName, "verification_failed", "Read-back mismatch: " + string.Join("; ", (v["mismatches"] as JArray ?? new JArray()).Values<string>()), done, affected, steps.Count, includeLast: true);
            }
            return ToolResult.Ok("Batch applied: " + steps.Count + " of " + steps.Count + " step(s); every read-back matches.", new JObject
            {
                ["applied"] = true, ["completed_steps"] = steps.Count, ["step_count"] = steps.Count, ["steps"] = done, ["affected"] = affected,
                ["verification"] = new JObject { ["ok"] = true, ["mismatches"] = new JArray() }
            });
        }

        // ================= helpers =================

        /// <summary>Parses and checks the steps: tool allowed, args valid for that tool, references point to earlier steps.</summary>
        private List<(string tool, JObject args)> Steps(JObject args, out ToolResult error)
        {
            error = null;
            var list = new List<(string, JObject)>();
            var arr = args["steps"] as JArray;
            if (arr == null || arr.Count == 0 || arr.Count > MaxSteps) { error = ToolResult.Fail("invalid_arguments", "steps must contain 1-" + MaxSteps + " items."); return list; }
            for (int i = 0; i < arr.Count; i++)
            {
                var s = arr[i] as JObject;
                string name = (string)s?["tool"];
                if (name == null || !_tools.TryGetValue(name, out var tool)) { error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + ": tool '" + name + "' is not allowed in a batch."); return list; }
                var stepArgs = s["args"] as JObject ?? new JObject();
                if (stepArgs[S.ModeProperty] != null || stepArgs[S.TokenProperty] != null) { error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + ": do not put mode/confirm_token in step args."); return list; }
                var check = (JObject)stepArgs.DeepClone(); check[S.ModeProperty] = "preview";
                var errs = SchemaValidator.Validate(check, tool.InputSchema);
                if (errs.Count > 0) { error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + " (" + name + "): " + string.Join("; ", errs)); return list; }
                List<(string text, int step, int index)> refs;
                try { refs = Refs(stepArgs).ToList(); }
                catch (ArgumentException ex) { error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + ": " + ex.Message); return list; }
                foreach (var r in refs)
                {
                    if (r.step < 1 || r.step > i)
                    {
                        error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + ": " + r.text + (i == 0 ? " - the first step cannot use references." : " must refer to an earlier step (1-" + i + ")."));
                        return list;
                    }
                    string src = (string)((JObject)arr[r.step - 1])["tool"];
                    if (!CreatingTools.Contains(src))
                    { error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + ": " + r.text + " refers to step " + r.step + " (" + src + "), which creates no new objects."); return list; }
                }
                // A deferred step gets no preview of its own, so it must not also act on existing objects.
                if (refs.Count > 0 && ActOnGuids.Contains(name) && stepArgs["guids"] is JArray targets &&
                    targets.Any(g => !(g.Type == JTokenType.String && Ref.IsMatch((string)g))))
                {
                    error = ToolResult.Fail("invalid_arguments", "step " + (i + 1) + " (" + name + "): guids mixes references with existing objects. " +
                                            "Put the existing objects in a separate step so they get a full preview.");
                    return list;
                }
                list.Add((name, stepArgs));
            }
            return list;
        }

        internal static IEnumerable<(string text, int step, int index)> Refs(JToken t)
        {
            if (t is JValue v && v.Type == JTokenType.String)
            {
                var m = Ref.Match((string)v);
                if (m.Success) yield return ((string)v, int.Parse(m.Groups[1].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
                else if (((string)v).Contains("{{")) throw new ArgumentException("Bad reference '" + v + "': use {{stepN.guid}} or {{stepN.guids[K]}}.");
            }
            else foreach (var c in t.Children()) foreach (var r in Refs(c)) yield return r;
        }

        /// <summary>Replaces every reference with the GUID created by the referenced step.</summary>
        internal static JToken Resolve(JToken t, IList<JArray> created)
        {
            if (t is JValue v && v.Type == JTokenType.String)
            {
                var m = Ref.Match((string)v);
                if (!m.Success) return v.DeepClone();
                int step = int.Parse(m.Groups[1].Value), k = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                if (step < 1 || step > created.Count) throw new ArgumentException((string)v + ": step " + step + " has not run.");
                var g = created[step - 1];
                if (k >= g.Count) throw new ArgumentException((string)v + ": step " + step + " created " + g.Count + " object(s).");
                return new JValue((string)g[k]);
            }
            if (t is JArray a) return new JArray(a.Select(x => Resolve(x, created)));
            if (t is JObject o) { var r = new JObject(); foreach (var p in o.Properties()) r[p.Name] = Resolve(p.Value, created); return r; }
            return t.DeepClone();
        }

        private static ToolResult StepFail(int i, string tool, string code, string message, JArray rows) =>
            ToolResult.Fail(code, "step " + (i + 1) + " (" + tool + "): " + message + " Nothing was changed.", new JObject { ["previewed_steps"] = rows });

        private static ToolResult Stopped(int i, string tool, string code, string message, JArray done, JArray affected, int total, bool includeLast = false)
        {
            int completed = done.Count;
            var r = ToolResult.Fail(code, "Batch STOPPED at step " + (i + 1) + " (" + tool + "): " + message + " " + (includeLast ? completed - 1 : completed) +
                                          " earlier step(s) were applied and are NOT undone" + (includeLast ? "; step " + (i + 1) + " was applied but its read-back differs" : "") + ".",
                new JObject { ["completed_steps"] = completed, ["step_count"] = total, ["steps"] = done });
            r.Data["applied"] = completed > 0;
            r.Data["completed_steps"] = completed;
            r.Data["step_count"] = total;
            r.Data["affected"] = affected;
            r.Data["steps"] = done;
            return r;
        }
    }
}
