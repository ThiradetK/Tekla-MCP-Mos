using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Mcp;
using Xunit;

namespace TeklaMcp.Tests
{
    public class ProtocolTests
    {
        [Fact]
        public void Initialize_EchoesSupportedVersion_AndDeclaresTools()
        {
            var h = new Harness(false, new EchoReadTool());
            var r = h.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}");
            Assert.Equal(1, (int)r["id"]);
            Assert.Equal("2025-06-18", (string)r["result"]["protocolVersion"]);
            Assert.NotNull(r["result"]["capabilities"]["tools"]);
            Assert.Equal("tekla", (string)r["result"]["serverInfo"]["name"]);
        }

        [Fact]
        public void Initialize_UnknownVersion_OffersNewest()
        {
            var h = new Harness();
            var r = h.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}");
            Assert.Equal(Protocol.SupportedVersions[0], (string)r["result"]["protocolVersion"]);
        }

        [Fact]
        public void Ping_ReturnsEmptyResult()
        {
            var r = new Harness().Send("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"ping\"}");
            Assert.Equal("a", (string)r["id"]);
            Assert.Empty((JObject)r["result"]);
        }

        [Fact]
        public void UnknownMethod_IsMethodNotFound()
        {
            var r = new Harness().Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"nope\"}");
            Assert.Equal(JsonRpcCodes.MethodNotFound, (int)r["error"]["code"]);
        }

        [Fact]
        public void BadJson_IsParseError()
        {
            var r = new Harness().Send("{not json");
            Assert.Equal(JsonRpcCodes.ParseError, (int)r["error"]["code"]);
        }

        [Fact]
        public void Notification_GetsNoResponse()
        {
            var h = new Harness();
            Assert.Null(h.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        }

        [Fact]
        public void ToolsList_ContainsSchemaAndHints()
        {
            var h = new Harness(false, new EchoReadTool(), new FakeWriteTool());
            var tools = (JArray)h.Send("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}")["result"]["tools"];
            Assert.Equal(new[] { "tekla_echo", "tekla_fake_write" }, tools.Select(t => (string)t["name"]).ToArray());
            Assert.True((bool)tools[0]["annotations"]["readOnlyHint"]);
            Assert.False((bool)tools[1]["annotations"]["readOnlyHint"]);
            Assert.Contains("confirm_token", ((JObject)tools[1]["inputSchema"]["properties"]).Properties().Select(p => p.Name));
        }

        [Fact]
        public void ToolsCall_ReturnsTextAndStructuredContent()
        {
            var r = new Harness(false, new EchoReadTool()).Call("tekla_echo", new JObject { ["text"] = "hi" });
            Assert.False((bool)r["result"]["isError"]);
            Assert.Equal("hi", (string)r["result"]["structuredContent"]["text"]);
            Assert.StartsWith("echo hi", (string)r["result"]["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_UnknownTool_IsInvalidParams()
        {
            var r = new Harness().Call("tekla_missing", new JObject());
            Assert.Equal(JsonRpcCodes.InvalidParams, (int)r["error"]["code"]);
        }

        [Fact]
        public void ToolsCall_InvalidArguments_IsToolError()
        {
            var r = new Harness(false, new EchoReadTool()).Call("tekla_echo", new JObject { ["text"] = 5, ["extra"] = 1 });
            Assert.True((bool)r["result"]["isError"]);
            Assert.Equal("invalid_arguments", (string)r["result"]["structuredContent"]["error"]["code"]);
        }

        [Fact]
        public void Output_IsOneJsonObjectPerLine()
        {
            var h = new Harness(false, new EchoReadTool());
            h.Call("tekla_echo", new JObject { ["text"] = "line1\nline2" });
            var lines = h.Output.ToString().Split('\n').Where(l => l.Length > 0).ToList();
            Assert.Single(lines);
            JObject.Parse(lines[0]);
        }

        [Fact]
        public void LogNotifications_OnlyAfterSetLevel()
        {
            var h = new Harness();
            h.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            TeklaMcp.Core.Log.Warn("before");
            Assert.DoesNotContain(h.Messages(), m => (string)m["method"] == "notifications/message");
            h.Send("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"logging/setLevel\",\"params\":{\"level\":\"warning\"}}");
            TeklaMcp.Core.Log.Info("ignored");
            TeklaMcp.Core.Log.Warn("after");
            var notes = h.Messages().Where(m => (string)m["method"] == "notifications/message").ToList();
            Assert.Single(notes);
            Assert.Equal("after", (string)notes[0]["params"]["data"]);
        }
    }
}
