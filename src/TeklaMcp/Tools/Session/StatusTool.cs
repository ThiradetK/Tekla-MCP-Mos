using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;

namespace TeklaMcp.Tools.Session
{
    internal sealed class StatusTool : IReadTool
    {
        private readonly TeklaWorker _tekla;
        private readonly Settings _settings;

        public StatusTool(TeklaWorker tekla, Settings settings)
        {
            _tekla = tekla; _settings = settings;
        }

        public string Name => "tekla_status";
        public string Title => "Tekla connection status";
        public string Description =>
            "Check the connection to Tekla Structures 2026: whether Tekla is running, program version, environment, " +
            "the open model (name, path, current phase), units (mm, kg, degree), and whether this server is read-only. " +
            "Call this first if other tools report connection errors. Read-only; does not change the model.";
        public ToolLevel Level => ToolLevel.Read;
        public JObject InputSchema => SchemaBuilder.Object();

        public ToolResult Execute(JObject args)
        {
            JObject s = _tekla.Call("status");
            s["server"] = new JObject
            {
                ["version"] = Mcp.Protocol.ServerVersion,
                ["read_only"] = _settings.ReadOnly,
                ["log_dir"] = Log.Directory
            };

            string summary;
            if (!(bool)s["tekla_installed"]) summary = "Tekla Structures 2026 is not installed or not found.";
            else if (!(bool)s["connected"]) summary = "Not connected: Tekla Structures 2026 is not running.";
            else if (!(bool)s["model_open"]) summary = "Connected to " + s["program_version"] + ", but no model is open.";
            else summary = "Connected to " + s["program_version"] + " | model " + s["model"]["name"] + " (" + s["model"]["path"] + ") | environment " + (s["environment"] ?? "unknown") + (_settings.ReadOnly ? " | server READ-ONLY" : "");
            return ToolResult.Ok(summary, s);
        }
    }
}
