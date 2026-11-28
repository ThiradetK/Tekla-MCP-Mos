using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tools.Modify;
using Xunit;

namespace TeklaMcp.Tests
{
    public class BatchTests
    {
        /// <summary>Step tool double: records calls; "fail" in args makes it fail; "mismatch" makes read-back differ.</summary>
        private sealed class StepTool : IWriteTool
        {
            private int _n;
            public readonly List<JObject> Applied = new List<JObject>();
            public StepTool(string name) { Name = name; }
            public string Name { get; }
            public string Title => Name;
            public string Description => Name;
            public ToolLevel Level => ToolLevel.Write;
            public JObject InputSchema => SchemaBuilder.ForWrite(new JObject
            {
                ["guid"] = SchemaBuilder.String("guid"), ["fail"] = SchemaBuilder.Boolean("fail"), ["mismatch"] = SchemaBuilder.Boolean("mismatch")
            });
            public ToolResult Preview(JObject a) => (bool?)a["fail"] == true ? ToolResult.Fail("invalid_arguments", "bad") : ToolResult.Ok("preview " + Name, new JObject());
            public ToolResult Apply(JObject a)
            {
                if ((bool?)a["fail"] == true) return ToolResult.Fail("tekla_error", "boom");
                Applied.Add(a);
                _n++;
                return ToolResult.Ok("applied " + Name, new JObject
                {
                    ["affected"] = new JArray(Name + "-" + _n + "-a", Name + "-" + _n + "-b"),
                    ["verification"] = new JObject { ["ok"] = (bool?)a["mismatch"] != true, ["mismatches"] = new JArray() }
                });
            }
        }

        private static (BatchTool batch, StepTool create, StepTool modify) Make()
        {
            var create = new StepTool("tekla_create_beam");
            var modify = new StepTool("tekla_modify_objects");
            return (new BatchTool(new IWriteTool[] { create, modify }), create, modify);
        }

        private static JObject Steps(params (string tool, JObject args)[] s) =>
            new JObject { ["steps"] = new JArray(s.Select(x => new JObject { ["tool"] = x.tool, ["args"] = x.args })) };

        [Fact]
        public void Preview_DefersStepsWithReferences()
        {
            var (b, _, _) = Make();
            var r = b.Preview(Steps(("tekla_create_beam", new JObject()), ("tekla_modify_objects", new JObject { ["guid"] = "{{step1.guid}}" })));
            Assert.False(r.IsError);
            Assert.Equal(1, (int)r.Data["deferred_steps"]);
            Assert.True((bool)r.Data["steps"][1]["deferred"]);
            Assert.NotEmpty((JArray)r.Data["objects"]);   // DESTRUCTIVE previews must list objects
        }

        [Theory]
        [InlineData("{{step2.guid}}", "earlier step")]       // forward reference
        [InlineData("{{step1.guid}}", "creates no new objects")] // step 1 is a modify
        [InlineData("{{stepX}}", "Bad reference")]
        public void Preview_RejectsBadReferences(string reference, string message)
        {
            var (b, _, _) = Make();
            var r = b.Preview(Steps(("tekla_modify_objects", new JObject()), ("tekla_modify_objects", new JObject { ["guid"] = reference })));
            Assert.True(r.IsError);
            Assert.Contains(message, (string)r.Data["error"]["message"]);
        }

        [Fact]
        public void Preview_RejectsUnknownToolsBadArgsAndTooManySteps()
        {
            var (b, _, _) = Make();
            Assert.True(b.Preview(Steps(("tekla_save_model", new JObject()))).IsError);
            Assert.True(b.Preview(Steps(("tekla_create_beam", new JObject { ["nope"] = 1 }))).IsError);
            Assert.True(b.Preview(Steps(("tekla_create_beam", new JObject { ["mode"] = "apply" }))).IsError);
            Assert.True(b.Preview(Steps(Enumerable.Range(0, BatchTool.MaxSteps + 1).Select(i => ("tekla_create_beam", new JObject())).ToArray())).IsError);
            var failing = b.Preview(Steps(("tekla_create_beam", new JObject()), ("tekla_create_beam", new JObject { ["fail"] = true })));
            Assert.True(failing.IsError);
            Assert.Contains("step 2", (string)failing.Data["error"]["message"]);
        }

        [Fact]
        public void Apply_ResolvesReferencesInOrder()
        {
            var (b, create, modify) = Make();
            var r = b.Apply(Steps(("tekla_create_beam", new JObject()), ("tekla_modify_objects", new JObject { ["guid"] = "{{step1.guids[1]}}" })));
            Assert.False(r.IsError);
            Assert.Equal("tekla_create_beam-1-b", (string)modify.Applied[0]["guid"]);
            Assert.Equal(4, ((JArray)r.Data["affected"]).Count);
        }

        [Fact]
        public void Apply_StopsAtFirstErrorAndKeepsDoneSteps()
        {
            var (b, create, modify) = Make();
            var r = b.Apply(Steps(("tekla_create_beam", new JObject()), ("tekla_modify_objects", new JObject { ["fail"] = true }), ("tekla_create_beam", new JObject())));
            Assert.True(r.IsError);
            Assert.Equal(1, (int)r.Data["completed_steps"]);
            Assert.Single(create.Applied);                  // step 3 never ran
            Assert.Contains("STOPPED at step 2", r.Summary);
        }

        [Fact]
        public void Apply_StopsOnReadBackMismatch()
        {
            var (b, create, _) = Make();
            var r = b.Apply(Steps(("tekla_create_beam", new JObject { ["mismatch"] = true }), ("tekla_create_beam", new JObject())));
            Assert.True(r.IsError);
            Assert.Equal("verification_failed", (string)r.Data["error"]["code"]);
            Assert.Single(create.Applied);
        }

        [Fact]
        public void Gate_BatchNeedsTokenAndAuditsOnce()
        {
            var (b, _, _) = Make();
            var h = new Harness(false, b);
            var args = Steps(("tekla_create_beam", new JObject()), ("tekla_modify_objects", new JObject { ["guid"] = "{{step1.guid}}" }));
            args["mode"] = "apply";
            var refused = h.Call("tekla_batch_execute", args);
            Assert.True((bool)refused["result"]["isError"]);
            args["mode"] = "preview";
            var pv = h.Call("tekla_batch_execute", args, 2);
            args["mode"] = "apply"; args["confirm_token"] = (string)pv["result"]["structuredContent"]["confirm_token"];
            var ap = h.Call("tekla_batch_execute", args, 3);
            Assert.False((bool)ap["result"]["isError"]);
            Assert.Contains(h.AuditEntries(), e => (string)e["tool"] == "tekla_batch_execute" && (string)e["outcome"] == "applied");
        }

        [Fact]
        public void References_OnlyToStepsThatCreateNewObjects()
        {
            var asm = new StepTool("tekla_create_assembly");
            var del = new StepTool("tekla_delete_objects");
            var beam = new StepTool("tekla_create_beam");
            var b = new BatchTool(new IWriteTool[] { asm, del, beam });
            // tekla_create_assembly returns the EXISTING assembly and parts as "affected": no references to it.
            var r = b.Preview(Steps(("tekla_create_assembly", new JObject()), ("tekla_delete_objects", new JObject { ["guid"] = "{{step1.guid}}" })));
            Assert.True(r.IsError);
            Assert.Contains("creates no new objects", (string)r.Data["error"]["message"]);
        }

        [Fact]
        public void DeferredStep_MayNotMixReferencesWithExistingObjects()
        {
            var del = new GuidsTool("tekla_delete_objects");
            var b = new BatchTool(new IWriteTool[] { new StepTool("tekla_create_beam"), del });
            var mixed = b.Preview(Steps(("tekla_create_beam", new JObject()), ("tekla_delete_objects", new JObject { ["guids"] = new JArray("{{step1.guid}}", "existing-guid") })));
            Assert.True(mixed.IsError);
            Assert.Contains("mixes references with existing objects", (string)mixed.Data["error"]["message"]);
            var onlyRefs = b.Preview(Steps(("tekla_create_beam", new JObject()), ("tekla_delete_objects", new JObject { ["guids"] = new JArray("{{step1.guid}}", "{{step1.guids[1]}}") })));
            Assert.False(onlyRefs.IsError);
            var listed = ((JArray)onlyRefs.Data["objects"]).Where(o => (string)o["tool"] == "tekla_delete_objects").ToList();
            Assert.Equal(2, listed.Count);                                  // each deleted object is listed with the step that creates it
            Assert.All(listed, o => Assert.Equal(1, (int)o["created_by_step"]));
        }

        private sealed class GuidsTool : IWriteTool
        {
            public GuidsTool(string name) { Name = name; }
            public string Name { get; }
            public string Title => Name;
            public string Description => Name;
            public ToolLevel Level => ToolLevel.Write;
            public JObject InputSchema => SchemaBuilder.ForWrite(new JObject { ["guids"] = SchemaBuilder.Array(SchemaBuilder.String("guid"), "guids") });
            public ToolResult Preview(JObject a) => ToolResult.Ok("preview", new JObject { ["objects"] = new JArray(new JObject { ["guid"] = "x" }) });
            public ToolResult Apply(JObject a) => ToolResult.Ok("applied", new JObject { ["affected"] = a["guids"].DeepClone() });
        }

        [Fact]
        public void RealToolSet_IsRegistrableAndDestructive()
        {
            var steps = WriteTools.BatchSteps(WriteTools.Create(null)).ToList();
            Assert.Equal(WriteTools.BatchStepNames.Length, steps.Count);
            var b = new BatchTool(steps);
            Assert.Equal(ToolLevel.Destructive, b.Level);
            var reg = new ToolRegistry();
            reg.Add(b);
        }
    }
}
