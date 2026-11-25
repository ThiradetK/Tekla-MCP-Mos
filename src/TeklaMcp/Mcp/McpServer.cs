using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;

namespace TeklaMcp.Mcp
{
    /// <summary>
    /// MCP server over stdio: initialize, ping, tools/list, tools/call, logging/setLevel.
    /// tools/call runs on the thread pool (Tekla calls themselves are serialized by TeklaThread).
    /// </summary>
    internal sealed class McpServer
    {
        private readonly TextReader _in;
        private readonly JsonRpcWriter _out;
        private readonly ToolRegistry _tools;
        private readonly SafetyGate _gate;
        private readonly ConcurrentDictionary<int, Task> _inflight = new ConcurrentDictionary<int, Task>();
        private int _nextTask;
        private volatile bool _initialized;
        private volatile int _clientLogLevel = -1; // -1: client did not ask for log notifications
        [ThreadStatic] private static bool _inLogNotify;

        public McpServer(TextReader input, JsonRpcWriter output, ToolRegistry tools, SafetyGate gate)
        {
            _in = input; _out = output; _tools = tools; _gate = gate;
            Log.Listener = ForwardLog;
        }

        public static McpServer ForStdio(Stream stdin, Stream stdout, ToolRegistry tools, SafetyGate gate) =>
            new McpServer(new StreamReader(stdin, new UTF8Encoding(false)), new JsonRpcWriter(stdout), tools, gate);

        /// <summary>Reads until stdin closes. Returns process exit code.</summary>
        public int Run()
        {
            Log.Info("MCP server started (stdio).");
            string line;
            while ((line = _in.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                HandleLine(line);
            }
            Log.Info("stdin closed; waiting for in-flight requests.");
            Task.WaitAll(_inflight.Values.ToArray(), TimeSpan.FromSeconds(5));
            Log.Info("MCP server stopped.");
            return 0;
        }

        /// <summary>Waits for in-flight tool calls (tests).</summary>
        public void Drain() => Task.WaitAll(_inflight.Values.ToArray(), TimeSpan.FromSeconds(30));

        public void HandleLine(string line)
        {
            JToken msg;
            try { msg = JToken.Parse(line); }
            catch (JsonException ex)
            {
                Log.Warn("Parse error: " + ex.Message);
                _out.Error(null, JsonRpcCodes.ParseError, "Parse error");
                return;
            }

            if (!(msg is JObject obj) || (string)obj["jsonrpc"] != "2.0")
            {
                _out.Error(null, JsonRpcCodes.InvalidRequest, msg is JArray ? "Batch requests are not supported" : "Invalid Request");
                return;
            }

            string method = (string)obj["method"];
            JToken id = obj["id"];
            if (method == null)
            {
                // A response to a server->client request. This server sends none, so ignore.
                Log.Debug("Ignoring message without method.");
                return;
            }

            bool isNotification = id == null;
            if (isNotification) { HandleNotification(method, obj["params"] as JObject); return; }

            if (method == "tools/call")
            {
                int key = Interlocked.Increment(ref _nextTask);
                var task = Task.Run(() => SafeHandle(id, method, obj["params"]));
                _inflight[key] = task;
                task.ContinueWith(_ => _inflight.TryRemove(key, out Task __));
            }
            else SafeHandle(id, method, obj["params"]);
        }

        private void SafeHandle(JToken id, string method, JToken @params)
        {
            try
            {
                JToken result = Dispatch(method, @params as JObject ?? new JObject());
                _out.Result(id, result);
            }
            catch (JsonRpcException ex) { _out.Error(id, ex.Code, ex.Message); }
            catch (Exception ex)
            {
                Log.Error("Unhandled error in " + method, ex);
                _out.Error(id, JsonRpcCodes.InternalError, "Internal error: " + ex.Message);
            }
        }

        private JToken Dispatch(string method, JObject p)
        {
            switch (method)
            {
                case "initialize": return Initialize(p);
                case "ping": return new JObject();
                case "tools/list": return ListTools();
                case "tools/call": return CallTool(p);
                case "logging/setLevel": return SetLevel(p);
                default: throw new JsonRpcException(JsonRpcCodes.MethodNotFound, "Method not found: " + method);
            }
        }

        private void HandleNotification(string method, JObject p)
        {
            switch (method)
            {
                case "notifications/initialized": _initialized = true; Log.Info("Client initialized."); break;
                case "notifications/cancelled": Log.Info("Client cancelled request " + p?["requestId"] + " (running Tekla calls cannot be interrupted)."); break;
                default: Log.Debug("Ignored notification " + method); break;
            }
        }

        private JObject Initialize(JObject p)
        {
            string requested = (string)p["protocolVersion"];
            string version = Protocol.Negotiate(requested);
            var client = p["clientInfo"];
            Log.Info("initialize from " + (client?["name"] ?? "?") + " " + (client?["version"] ?? "") + "; protocol requested=" + requested + " using=" + version);
            return new JObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                    ["logging"] = new JObject()
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = Protocol.ServerName,
                    ["title"] = Protocol.ServerTitle,
                    ["version"] = Protocol.ServerVersion
                },
                ["instructions"] = Protocol.Instructions
            };
        }

        private JObject ListTools()
        {
            var arr = new JArray();
            foreach (var t in _tools.All)
            {
                arr.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["title"] = t.Title,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema,
                    ["annotations"] = new JObject
                    {
                        ["title"] = t.Title,
                        ["readOnlyHint"] = t.Level == ToolLevel.Read,
                        ["destructiveHint"] = t.Level == ToolLevel.Destructive,
                        ["openWorldHint"] = false
                    }
                });
            }
            return new JObject { ["tools"] = arr };
        }

        private JObject CallTool(JObject p)
        {
            string name = (string)p["name"];
            if (!_tools.TryGet(name, out ITool tool))
                throw new JsonRpcException(JsonRpcCodes.InvalidParams, "Unknown tool: " + name);

            JToken rawArgs = p["arguments"];
            if (rawArgs != null && rawArgs.Type != JTokenType.Null && rawArgs.Type != JTokenType.Object)
                return ToMcp(ToolResult.Fail("invalid_arguments", "arguments must be an object"));
            var args = rawArgs as JObject ?? new JObject();

            var errors = SchemaValidator.Validate(args, tool.InputSchema);
            if (errors.Count > 0)
                return ToMcp(ToolResult.Fail("invalid_arguments", string.Join("; ", errors)));

            ToolResult result;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { result = _gate.Run(tool, args); }
            catch (TeklaException ex) { result = ToolResult.Fail(ex.Code, ex.Message); }
            catch (Exception ex)
            {
                Log.Error("Tool " + name + " failed", ex);
                result = ToolResult.Fail("internal_error", ex.GetType().Name + ": " + ex.Message);
            }
            Log.Info("tools/call " + name + " -> " + (result.IsError ? "error" : "ok") + " in " + sw.ElapsedMilliseconds + " ms");
            return ToMcp(result);
        }

        internal static JObject ToMcp(ToolResult r)
        {
            string json = r.Data.ToString(Formatting.Indented);
            return new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = r.Summary + "\n\n" + json }),
                ["structuredContent"] = r.Data,
                ["isError"] = r.IsError
            };
        }

        private JObject SetLevel(JObject p)
        {
            if (!Protocol.TryParseLevel((string)p["level"], out LogLevel lvl))
                throw new JsonRpcException(JsonRpcCodes.InvalidParams, "Unknown log level: " + p["level"]);
            _clientLogLevel = (int)lvl;
            return new JObject();
        }

        private void ForwardLog(LogLevel level, string message)
        {
            if (!_initialized || _clientLogLevel < 0 || (int)level < _clientLogLevel || _inLogNotify) return;
            _inLogNotify = true;
            try
            {
                _out.Notify("notifications/message", new JObject
                {
                    ["level"] = Protocol.LevelName(level),
                    ["logger"] = "teklamcp",
                    ["data"] = message
                });
            }
            catch { /* stdout closed */ }
            finally { _inLogNotify = false; }
        }
    }
}
