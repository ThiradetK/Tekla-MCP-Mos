using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Mcp;

namespace TeklaMcp.Tests
{
    internal sealed class EchoReadTool : IReadTool
    {
        public string Name => "tekla_echo";
        public string Title => "Echo";
        public string Description => "Echo test tool";
        public ToolLevel Level => ToolLevel.Read;
        public JObject InputSchema => SchemaBuilder.Object(new JObject { ["text"] = SchemaBuilder.String("text") }, "text");
        public ToolResult Execute(JObject args) => ToolResult.Ok("echo " + args["text"], new JObject { ["text"] = args["text"] });
    }

    internal sealed class FakeWriteTool : IWriteTool
    {
        public FakeWriteTool(ToolLevel level = ToolLevel.Write, bool listObjects = true) { Level = level; _listObjects = listObjects; }
        private readonly bool _listObjects;
        public int PreviewCalls, ApplyCalls;
        public string Name => "tekla_fake_write";
        public string Title => "Fake write";
        public string Description => "Fake write tool";
        public ToolLevel Level { get; }
        public JObject InputSchema => SchemaBuilder.ForWrite(new JObject { ["value"] = SchemaBuilder.Number("value mm") }, "value");

        public ToolResult Preview(JObject args)
        {
            PreviewCalls++;
            var data = new JObject { ["changes"] = new JArray("set value " + args["value"]) };
            if (_listObjects) data["objects"] = new JArray(new JObject { ["guid"] = "00000000-0000-0000-0000-000000000001" });
            return ToolResult.Ok("would set value", data);
        }

        public ToolResult Apply(JObject args)
        {
            ApplyCalls++;
            return ToolResult.Ok("applied", new JObject { ["affected"] = new JArray("00000000-0000-0000-0000-000000000001") });
        }
    }

    /// <summary>Server wired to an in-memory output for protocol tests.</summary>
    internal sealed class Harness
    {
        public readonly StringWriter Output = new StringWriter();
        public readonly McpServer Server;
        public readonly Settings Settings;
        public readonly string AuditDir;

        public Harness(bool readOnly = false, params ITool[] tools)
        {
            Settings = new Settings { ReadOnly = readOnly };
            AuditDir = Path.Combine(Path.GetTempPath(), "teklamcp-tests", System.Guid.NewGuid().ToString("N"));
            var reg = new ToolRegistry();
            foreach (var t in tools) reg.Add(t);
            var gate = new SafetyGate(Settings, new ConfirmTokenStore(System.TimeSpan.FromMinutes(10)), new AuditLog(AuditDir));
            Server = new McpServer(new StringReader(""), new JsonRpcWriter(Output), reg, gate);
        }

        public List<JObject> Messages()
        {
            Server.Drain();
            return Output.ToString().Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => JObject.Parse(l.TrimEnd('\r'))).ToList();
        }

        public JObject Send(string json)
        {
            int before = Messages().Count;
            Server.HandleLine(json);
            var all = Messages();
            return all.Count > before ? all[all.Count - 1] : null;
        }

        public JObject Call(string tool, JObject args, int id = 1) =>
            Send(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = tool, ["arguments"] = args }
            }.ToString(Newtonsoft.Json.Formatting.None));

        public IEnumerable<JObject> AuditEntries()
        {
            if (!Directory.Exists(AuditDir)) return Enumerable.Empty<JObject>();
            return Directory.GetFiles(AuditDir, "audit-*.jsonl")
                            .SelectMany(File.ReadAllLines).Where(l => l.Length > 0).Select(JObject.Parse).ToList();
        }
    }
}
