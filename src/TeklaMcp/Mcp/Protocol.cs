using System;
using System.Linq;
using TeklaMcp.Core;

namespace TeklaMcp.Mcp
{
    internal static class Protocol
    {
        /// <summary>MCP protocol revisions this server accepts, newest first.</summary>
        public static readonly string[] SupportedVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        public const string ServerName = "tekla";
        public const string ServerTitle = "Tekla Structures MCP";

        public static string ServerVersion =>
            typeof(Protocol).Assembly.GetName().Version.ToString(3);

        /// <summary>Echo the client's version if supported; otherwise offer our newest (client may then disconnect).</summary>
        public static string Negotiate(string requested) =>
            SupportedVersions.Contains(requested, StringComparer.Ordinal) ? requested : SupportedVersions[0];

        public const string Instructions =
            "Tekla Structures 2026 Open API server. Units: length mm, weight kg, angles degrees. " +
            "Objects are referenced by GUID (ID is secondary and can change). " +
            "Tools that change the model work in two steps: call with mode=\"preview\" first, show the returned changes to the user, " +
            "and call mode=\"apply\" with the confirm_token ONLY after the user explicitly approves in the conversation. " +
            "The Open API has no undo: recommend saving the model before large changes.";

        /// <summary>MCP logging levels (RFC 5424 names) mapped to internal levels.</summary>
        public static bool TryParseLevel(string level, out LogLevel result)
        {
            switch ((level ?? "").ToLowerInvariant())
            {
                case "debug": result = LogLevel.Debug; return true;
                case "info": case "notice": result = LogLevel.Info; return true;
                case "warning": result = LogLevel.Warning; return true;
                case "error": case "critical": case "alert": case "emergency": result = LogLevel.Error; return true;
                default: result = LogLevel.Info; return false;
            }
        }

        public static string LevelName(LogLevel l)
        {
            switch (l)
            {
                case LogLevel.Debug: return "debug";
                case LogLevel.Warning: return "warning";
                case LogLevel.Error: return "error";
                default: return "info";
            }
        }
    }
}
