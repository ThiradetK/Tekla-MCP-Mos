using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Mcp
{
    internal static class JsonRpcCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
    }

    internal sealed class JsonRpcException : Exception
    {
        public int Code { get; }
        public JsonRpcException(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// Writes JSON-RPC messages to stdout as newline-delimited JSON (MCP stdio transport).
    /// One message per line, no embedded newlines, UTF-8 without BOM. Thread-safe.
    /// </summary>
    internal sealed class JsonRpcWriter
    {
        private readonly object _sync = new object();
        private readonly TextWriter _out;

        public JsonRpcWriter(Stream stdout)
        {
            _out = new StreamWriter(stdout, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        /// <summary>For tests.</summary>
        public JsonRpcWriter(TextWriter writer) { _out = writer; }

        public void Send(JObject message)
        {
            string line = message.ToString(Formatting.None);
            lock (_sync)
            {
                _out.WriteLine(line);
                _out.Flush();
            }
        }

        public void Result(JToken id, JToken result) =>
            Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() ?? JValue.CreateNull(), ["result"] = result ?? new JObject() });

        public void Error(JToken id, int code, string message, JToken data = null)
        {
            var err = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) err["data"] = data;
            Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() ?? JValue.CreateNull(), ["error"] = err });
        }

        public void Notify(string method, JObject @params) =>
            Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params ?? new JObject() });
    }
}
