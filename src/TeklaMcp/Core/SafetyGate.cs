using System;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    /// <summary>
    /// Runs a tool according to its level.
    /// READ: run. WRITE/DESTRUCTIVE: refused in read-only mode; "preview" returns changes + confirm_token;
    /// "apply" needs a valid token issued for the same tool and identical arguments. Every apply attempt is audited.
    /// </summary>
    internal sealed class SafetyGate
    {
        private readonly Settings _settings;
        private readonly ConfirmTokenStore _tokens;
        private readonly AuditLog _audit;

        public SafetyGate(Settings settings, ConfirmTokenStore tokens, AuditLog audit)
        {
            _settings = settings; _tokens = tokens; _audit = audit;
        }

        public ToolResult Run(ITool tool, JObject args)
        {
            args = args ?? new JObject();
            if (tool.Level == ToolLevel.Read) return ((IReadTool)tool).Execute(args);

            var wt = (IWriteTool)tool;
            if (_settings.ReadOnly)
            {
                var r = ToolResult.Fail("read_only", "Server is in read-only mode (settings.json readOnly=true). Write tools are disabled.");
                if ((string)args[SchemaBuilder.ModeProperty] == "apply") _audit.Write(tool.Name, tool.Level, "refused", args, null, "read_only");
                return r;
            }

            string mode = (string)args[SchemaBuilder.ModeProperty];
            if (mode == "preview") return Preview(wt, args);
            if (mode == "apply") return Apply(wt, args);
            return ToolResult.Fail("invalid_mode", "Argument 'mode' must be \"preview\" or \"apply\".");
        }

        private ToolResult Preview(IWriteTool tool, JObject args)
        {
            if (!string.IsNullOrEmpty((string)args[SchemaBuilder.TokenProperty]))
                return ToolResult.Fail("unexpected_token", "Do not send confirm_token with mode \"preview\".");

            var result = tool.Preview(args);
            if (result.IsError) return result;

            if (tool.Level == ToolLevel.Destructive && !(result.Data["objects"] is JArray objs && objs.Count > 0))
                return ToolResult.Fail("preview_incomplete", "Internal error: destructive preview did not list the affected objects. Nothing was changed.");

            string token = _tokens.Issue(tool.Name, args, out DateTime expires);
            result.Data["confirm_token"] = token;
            result.Data["confirm_token_expires_utc"] = expires.ToString("o");
            result.Data["next_step"] = "Show these changes to the user. Only after the user explicitly approves, call " + tool.Name +
                                       " again with the same arguments, mode=\"apply\" and this confirm_token.";
            result.Summary = "PREVIEW ONLY - model not changed. " + result.Summary;
            return result;
        }

        private ToolResult Apply(IWriteTool tool, JObject args)
        {
            string token = (string)args[SchemaBuilder.TokenProperty];
            var check = _tokens.Consume(token, tool.Name, args);
            if (check != TokenCheck.Ok)
            {
                _audit.Write(tool.Name, tool.Level, "refused", args, null, "token_" + check.ToString().ToLowerInvariant());
                return ToolResult.Fail("confirm_token_" + Snake(check), TokenMessage(check));
            }

            ToolResult result;
            try { result = tool.Apply(args); }
            catch (Exception ex)
            {
                _audit.Write(tool.Name, tool.Level, "failed", args, null, ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            _audit.Write(tool.Name, tool.Level, result.IsError ? "failed" : "applied", args, result.Data);
            return result;
        }

        private static string TokenMessage(TokenCheck c)
        {
            switch (c)
            {
                case TokenCheck.Missing: return "mode \"apply\" requires confirm_token. Call with mode \"preview\" first.";
                case TokenCheck.Unknown: return "confirm_token is unknown or already used. Run preview again.";
                case TokenCheck.Expired: return "confirm_token expired. Run preview again.";
                case TokenCheck.WrongTool: return "confirm_token was issued for a different tool. Run preview again.";
                case TokenCheck.ArgumentsChanged: return "Arguments differ from the preview. Run preview again with the final arguments.";
                default: return c.ToString();
            }
        }

        private static string Snake(TokenCheck c)
        {
            switch (c)
            {
                case TokenCheck.ArgumentsChanged: return "arguments_changed";
                case TokenCheck.WrongTool: return "wrong_tool";
                default: return c.ToString().ToLowerInvariant();
            }
        }
    }
}
