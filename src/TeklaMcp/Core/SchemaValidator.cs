using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    /// <summary>
    /// Validates tool arguments against the subset of JSON Schema produced by SchemaBuilder:
    /// type, properties, required, additionalProperties=false, enum, minimum, maximum, items.
    /// </summary>
    internal static class SchemaValidator
    {
        public static IList<string> Validate(JToken value, JObject schema)
        {
            var errors = new List<string>();
            Check(value, schema, "$", errors);
            return errors;
        }

        private static void Check(JToken value, JObject schema, string path, List<string> errors)
        {
            if (schema == null) return;
            string type = (string)schema["type"];
            if (type != null && !TypeMatches(value, type))
            {
                errors.Add(path + ": expected " + type + ", got " + Describe(value));
                return;
            }

            if (schema["enum"] is JArray en && !en.Any(e => JToken.DeepEquals(e, value)))
                errors.Add(path + ": must be one of " + string.Join(", ", en.Select(e => e.ToString(Newtonsoft.Json.Formatting.None))));

            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                double d = value.Value<double>();
                if (schema["minimum"] != null && d < schema.Value<double>("minimum")) errors.Add(path + ": must be >= " + schema["minimum"]);
                if (schema["maximum"] != null && d > schema.Value<double>("maximum")) errors.Add(path + ": must be <= " + schema["maximum"]);
            }

            if (value is JObject obj)
            {
                var props = schema["properties"] as JObject ?? new JObject();
                if (schema["required"] is JArray req)
                    foreach (var r in req.Values<string>())
                        if (obj[r] == null || obj[r].Type == JTokenType.Null) errors.Add(path + ": missing required property '" + r + "'");

                bool noExtra = schema["additionalProperties"] is JValue ap && ap.Type == JTokenType.Boolean && !(bool)ap;
                foreach (var p in obj.Properties())
                {
                    if (props[p.Name] is JObject ps)
                    {
                        if (p.Value.Type != JTokenType.Null) Check(p.Value, ps, path + "." + p.Name, errors);
                    }
                    else if (noExtra) errors.Add(path + ": unknown property '" + p.Name + "'");
                }
            }

            if (value is JArray arr && schema["items"] is JObject items)
                for (int i = 0; i < arr.Count; i++) Check(arr[i], items, path + "[" + i + "]", errors);
        }

        private static bool TypeMatches(JToken v, string type)
        {
            switch (type)
            {
                case "object": return v.Type == JTokenType.Object;
                case "array": return v.Type == JTokenType.Array;
                case "string": return v.Type == JTokenType.String;
                case "boolean": return v.Type == JTokenType.Boolean;
                case "integer": return v.Type == JTokenType.Integer || (v.Type == JTokenType.Float && Math.Abs(v.Value<double>() % 1) < double.Epsilon);
                case "number": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case "null": return v.Type == JTokenType.Null;
                default: return true;
            }
        }

        private static string Describe(JToken v) => v == null ? "nothing" : v.Type.ToString().ToLowerInvariant();
    }
}
