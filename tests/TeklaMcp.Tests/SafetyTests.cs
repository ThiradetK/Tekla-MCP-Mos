using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests
{
    public class SafetyGateTests
    {
        private static JObject Sc(JObject response) => (JObject)response["result"]["structuredContent"];

        [Fact]
        public void Preview_IssuesToken_AndDoesNotApply()
        {
            var tool = new FakeWriteTool();
            var h = new Harness(false, tool);
            var r = h.Call("tekla_fake_write", new JObject { ["value"] = 10, ["mode"] = "preview" });
            Assert.False((bool)r["result"]["isError"]);
            Assert.StartsWith("ct_", (string)Sc(r)["confirm_token"]);
            Assert.Equal(1, tool.PreviewCalls);
            Assert.Equal(0, tool.ApplyCalls);
            Assert.Empty(h.AuditEntries());
        }

        [Fact]
        public void Apply_WithoutToken_IsRefusedAndAudited()
        {
            var tool = new FakeWriteTool();
            var h = new Harness(false, tool);
            var r = h.Call("tekla_fake_write", new JObject { ["value"] = 10, ["mode"] = "apply" });
            Assert.True((bool)r["result"]["isError"]);
            Assert.Equal("confirm_token_missing", (string)Sc(r)["error"]["code"]);
            Assert.Equal(0, tool.ApplyCalls);
            Assert.Equal("refused", (string)h.AuditEntries().Single()["outcome"]);
        }

        [Fact]
        public void Apply_WithToken_Applies_Once()
        {
            var tool = new FakeWriteTool();
            var h = new Harness(false, tool);
            string token = (string)Sc(h.Call("tekla_fake_write", new JObject { ["value"] = 10, ["mode"] = "preview" }))["confirm_token"];

            var ok = h.Call("tekla_fake_write", new JObject { ["value"] = 10, ["mode"] = "apply", ["confirm_token"] = token }, 2);
            Assert.False((bool)ok["result"]["isError"]);
            Assert.Equal(1, tool.ApplyCalls);

            var again = h.Call("tekla_fake_write", new JObject { ["value"] = 10, ["mode"] = "apply", ["confirm_token"] = token }, 3);
            Assert.Equal("confirm_token_unknown", (string)Sc(again)["error"]["code"]);
            Assert.Equal(1, tool.ApplyCalls);

            var audit = h.AuditEntries().ToList();
            Assert.Equal(new[] { "applied", "refused" }, audit.Select(a => (string)a["outcome"]).ToArray());
            Assert.Equal("***", (string)audit[0]["input"]["confirm_token"]);
            Assert.NotNull(audit[0]["affected"]);
        }

        [Fact]
        public void Apply_WithChangedArguments_IsRefused()
        {
            var tool = new FakeWriteTool();
            var h = new Harness(false, tool);
            string token = (string)Sc(h.Call("tekla_fake_write", new JObject { ["value"] = 10, ["mode"] = "preview" }))["confirm_token"];
            var r = h.Call("tekla_fake_write", new JObject { ["value"] = 11, ["mode"] = "apply", ["confirm_token"] = token }, 2);
            Assert.Equal("confirm_token_arguments_changed", (string)Sc(r)["error"]["code"]);
            Assert.Equal(0, tool.ApplyCalls);
        }

        [Fact]
        public void ReadOnly_RefusesPreviewAndApply()
        {
            var tool = new FakeWriteTool();
            var h = new Harness(true, tool);
            Assert.Equal("read_only", (string)Sc(h.Call("tekla_fake_write", new JObject { ["value"] = 1, ["mode"] = "preview" }))["error"]["code"]);
            Assert.Equal("read_only", (string)Sc(h.Call("tekla_fake_write", new JObject { ["value"] = 1, ["mode"] = "apply", ["confirm_token"] = "ct_x" }, 2))["error"]["code"]);
            Assert.Equal(0, tool.PreviewCalls + tool.ApplyCalls);
        }

        [Fact]
        public void MissingMode_IsInvalidArguments()
        {
            var h = new Harness(false, new FakeWriteTool());
            var r = h.Call("tekla_fake_write", new JObject { ["value"] = 1 });
            Assert.Equal("invalid_arguments", (string)Sc(r)["error"]["code"]);
        }

        [Fact]
        public void DestructivePreview_WithoutObjectList_IsRejected()
        {
            var h = new Harness(false, new FakeWriteTool(ToolLevel.Destructive, listObjects: false));
            var r = h.Call("tekla_fake_write", new JObject { ["value"] = 1, ["mode"] = "preview" });
            Assert.Equal("preview_incomplete", (string)Sc(r)["error"]["code"]);
            Assert.Null(Sc(r)["confirm_token"]);
        }
    }

    public class ConfirmTokenStoreTests
    {
        [Fact]
        public void Token_Expires()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new ConfirmTokenStore(TimeSpan.FromMinutes(10), () => now);
            var args = new JObject { ["a"] = 1 };
            string t = store.Issue("tekla_x", args, out _);
            now = now.AddMinutes(11);
            Assert.Equal(TokenCheck.Expired, store.Consume(t, "tekla_x", args));
        }

        [Fact]
        public void Token_WrongTool()
        {
            var store = new ConfirmTokenStore(TimeSpan.FromMinutes(10));
            var args = new JObject { ["a"] = 1 };
            string t = store.Issue("tekla_x", args, out _);
            Assert.Equal(TokenCheck.WrongTool, store.Consume(t, "tekla_y", args));
            Assert.Equal(TokenCheck.Ok, store.Consume(t, "tekla_x", args));
        }

        [Fact]
        public void Hash_IgnoresKeyOrderModeAndToken()
        {
            var a = JObject.Parse("{\"x\":1,\"y\":{\"b\":2,\"a\":1},\"mode\":\"preview\"}");
            var b = JObject.Parse("{\"y\":{\"a\":1,\"b\":2},\"x\":1,\"mode\":\"apply\",\"confirm_token\":\"ct_1\"}");
            Assert.Equal(ConfirmTokenStore.HashArgs(a), ConfirmTokenStore.HashArgs(b));
            Assert.NotEqual(ConfirmTokenStore.HashArgs(a), ConfirmTokenStore.HashArgs(JObject.Parse("{\"x\":2}")));
        }
    }

    public class SchemaAndRegistryTests
    {
        [Fact]
        public void Validator_ChecksTypesRequiredEnumRange()
        {
            var schema = SchemaBuilder.Object(new JObject
            {
                ["n"] = SchemaBuilder.Integer("n", 1, 10),
                ["m"] = SchemaBuilder.String("m", new[] { "a", "b" }),
                ["list"] = SchemaBuilder.Array(SchemaBuilder.Number("x"), "list")
            }, "n");
            Assert.Empty(SchemaValidator.Validate(JObject.Parse("{\"n\":5,\"m\":\"a\",\"list\":[1,2.5]}"), schema));
            var errs = SchemaValidator.Validate(JObject.Parse("{\"m\":\"c\",\"list\":[\"x\"],\"zz\":1}"), schema);
            Assert.Contains(errs, e => e.Contains("missing required property 'n'"));
            Assert.Contains(errs, e => e.Contains("must be one of"));
            Assert.Contains(errs, e => e.Contains("$.list[0]: expected number"));
            Assert.Contains(errs, e => e.Contains("unknown property 'zz'"));
            Assert.Contains(SchemaValidator.Validate(JObject.Parse("{\"n\":11}"), schema), e => e.Contains("<= 10"));
        }

        [Fact]
        public void Registry_RejectsNameWithoutPrefix()
        {
            var reg = new ToolRegistry();
            Assert.Throws<ArgumentException>(() => reg.Add(new BadNameTool()));
        }

        [Fact]
        public void Environment_ParsedFromXsSystem()
        {
            string xs = @"C:\ProgramData\Trimble\Tekla Structures\2026.0\\environments\se_asia\Steel\drawing_settings\;C:\x\environments\common\system\";
            Assert.Equal("se_asia", TeklaMcp.Tekla.TeklaText.EnvironmentFromSystemPath(xs));
            Assert.Null(TeklaMcp.Tekla.TeklaText.EnvironmentFromSystemPath(@"C:\x\environments\common\system\"));
            Assert.Null(TeklaMcp.Tekla.TeklaText.EnvironmentFromSystemPath(null));
        }

        [Fact]
        public void ConnectionLost_DetectedInExceptionChain()
        {
            var stale = new System.Reflection.TargetInvocationException(new InvalidOperationException("Cannot find remote object with id 409e77c4"));
            Assert.True(TeklaMcp.Tekla.TeklaText.IsConnectionLost(stale));
            Assert.False(TeklaMcp.Tekla.TeklaText.IsConnectionLost(new InvalidOperationException("Profile not found")));
        }

        private sealed class BadNameTool : IReadTool
        {
            public string Name => "status";
            public string Title => "";
            public string Description => "";
            public ToolLevel Level => ToolLevel.Read;
            public JObject InputSchema => SchemaBuilder.Object();
            public ToolResult Execute(JObject args) => ToolResult.Ok("");
        }
    }
}
