using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    /// <summary>Helpers to build JSON Schemas for tool inputs.</summary>
    internal static class SchemaBuilder
    {
        public static JObject Object(JObject properties = null, params string[] required)
        {
            var s = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? new JObject(),
                ["additionalProperties"] = false
            };
            if (required != null && required.Length > 0) s["required"] = new JArray(required.Cast<object>().ToArray());
            return s;
        }

        public static JObject String(string description, IEnumerable<string> enumValues = null)
        {
            var s = new JObject { ["type"] = "string", ["description"] = description };
            if (enumValues != null) s["enum"] = new JArray(enumValues.Cast<object>().ToArray());
            return s;
        }

        public static JObject Number(string description, double? min = null, double? max = null)
        {
            var s = new JObject { ["type"] = "number", ["description"] = description };
            if (min.HasValue) s["minimum"] = min.Value;
            if (max.HasValue) s["maximum"] = max.Value;
            return s;
        }

        public static JObject Integer(string description, long? min = null, long? max = null)
        {
            var s = new JObject { ["type"] = "integer", ["description"] = description };
            if (min.HasValue) s["minimum"] = min.Value;
            if (max.HasValue) s["maximum"] = max.Value;
            return s;
        }

        public static JObject Boolean(string description) => new JObject { ["type"] = "boolean", ["description"] = description };

        public static JObject Array(JObject items, string description) => new JObject { ["type"] = "array", ["items"] = items, ["description"] = description };

        public const string ModeProperty = "mode";
        public const string TokenProperty = "confirm_token";

        /// <summary>Adds the two-step safety properties (mode, confirm_token) to a WRITE/DESTRUCTIVE schema.</summary>
        public static JObject ForWrite(JObject properties, params string[] required)
        {
            var props = (JObject)(properties ?? new JObject()).DeepClone();
            props[ModeProperty] = String("\"preview\" returns the planned changes and a confirm_token without changing the model. \"apply\" makes the changes; requires the confirm_token from a preview with identical arguments.", new[] { "preview", "apply" });
            props[TokenProperty] = String("Token from the preview response. Required when mode is \"apply\". Single use; expires.");
            var req = new List<string>(required ?? new string[0]) { ModeProperty };
            return Object(props, req.ToArray());
        }
    }
}
