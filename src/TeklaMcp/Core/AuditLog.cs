using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    /// <summary>
    /// Append-only JSON Lines audit log of every WRITE/DESTRUCTIVE apply attempt (accepted or refused).
    /// File: %LOCALAPPDATA%\TeklaMcp\logs\audit-yyyyMM.jsonl
    /// </summary>
    internal sealed class AuditLog
    {
        private readonly object _sync = new object();
        private readonly string _dir;

        public AuditLog(string dir) { _dir = dir; }

        public string CurrentFile => _dir == null ? null : Path.Combine(_dir, "audit-" + DateTime.Now.ToString("yyyyMM") + ".jsonl");

        /// <param name="outcome">applied | failed | refused</param>
        public void Write(string tool, ToolLevel level, string outcome, JObject args, JObject result, string reason = null)
        {
            var entry = new JObject
            {
                ["time"] = DateTimeOffset.Now.ToString("o"),
                ["tool"] = tool,
                ["level"] = level.ToString().ToUpperInvariant(),
                ["outcome"] = outcome,
                ["user"] = Environment.UserName,
                ["machine"] = Environment.MachineName,
                ["input"] = Redact(args),
            };
            if (reason != null) entry["reason"] = reason;
            if (result != null)
            {
                // Affected GUIDs are expected in result.affected (array). Keep the whole result for traceability.
                if (result["affected"] != null) entry["affected"] = result["affected"].DeepClone();
                entry["result"] = result.DeepClone();
            }
            string line = entry.ToString(Formatting.None);
            if (CurrentFile == null) { Log.Error("AUDIT (no file): " + line); return; }
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(CurrentFile, line + "\n", new UTF8Encoding(false));
                }
                catch (Exception ex) { Log.Error("Audit write failed; entry: " + line, ex); }
            }
        }

        private static JObject Redact(JObject args)
        {
            var copy = (JObject)(args ?? new JObject()).DeepClone();
            if (copy[SchemaBuilder.TokenProperty] != null) copy[SchemaBuilder.TokenProperty] = "***";
            return copy;
        }
    }
}
