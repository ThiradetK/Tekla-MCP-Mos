using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    internal enum ToolLevel
    {
        /// <summary>Runs immediately.</summary>
        Read,
        /// <summary>Two steps: preview → apply with confirm_token.</summary>
        Write,
        /// <summary>Two steps; preview must list every affected object in data.objects.</summary>
        Destructive
    }

    /// <summary>Result of a tool call: short summary text + structured JSON data.</summary>
    internal sealed class ToolResult
    {
        public string Summary { get; set; }
        public JObject Data { get; set; }
        public bool IsError { get; set; }

        public static ToolResult Ok(string summary, JObject data = null) => new ToolResult { Summary = summary, Data = data ?? new JObject() };

        public static ToolResult Fail(string code, string message, JObject details = null)
        {
            var data = new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = message } };
            if (details != null) data["error"]["details"] = details;
            return new ToolResult { Summary = "Error (" + code + "): " + message, Data = data, IsError = true };
        }
    }

    internal interface ITool
    {
        /// <summary>Starts with "tekla_".</summary>
        string Name { get; }
        string Title { get; }
        string Description { get; }
        ToolLevel Level { get; }
        /// <summary>JSON Schema of the arguments. For WRITE/DESTRUCTIVE, mode/confirm_token are added by SchemaBuilder.ForWrite.</summary>
        JObject InputSchema { get; }
    }

    /// <summary>READ tool.</summary>
    internal interface IReadTool : ITool
    {
        ToolResult Execute(JObject args);
    }

    /// <summary>WRITE / DESTRUCTIVE tool. The SafetyGate decides which method runs.</summary>
    internal interface IWriteTool : ITool
    {
        /// <summary>Validate and compute changes. Must not change the model. Data should contain "changes" (and "objects" for DESTRUCTIVE).</summary>
        ToolResult Preview(JObject args);

        /// <summary>Apply, CommitChanges, read back and compare. Called only with a valid confirm_token.</summary>
        ToolResult Apply(JObject args);
    }
}
