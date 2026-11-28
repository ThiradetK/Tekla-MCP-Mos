using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    /// <summary>Writes rows to CSV / XLSX / JSON inside the allowed export folder. Never overwrites an existing file.</summary>
    internal static class TableExport
    {
        public static readonly string[] Formats = { "csv", "xlsx", "json" };

        /// <summary>
        /// Resolves a bare file name inside exportDir. Rejects paths, traversal, bad characters and existing files.
        /// Returns the full path or throws ArgumentException with a user-facing message.
        /// </summary>
        public static string ResolvePath(string exportDir, string fileName, string format)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("file_name is empty.");
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains("..") || fileName != Path.GetFileName(fileName))
                throw new ArgumentException("file_name must be a plain file name without folders: " + fileName);
            string ext = "." + format;
            if (!fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) fileName += ext;
            string root = Path.GetFullPath(exportDir);
            string full = Path.GetFullPath(Path.Combine(root, fileName));
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("file_name resolves outside the export folder.");
            if (File.Exists(full)) throw new ArgumentException("File already exists (not overwritten): " + full);
            return full;
        }

        /// <summary>
        /// Resolves a NEW folder "name" inside exportDir for exports that write several files. Same name rules as ResolvePath;
        /// an existing folder is rejected (never overwritten).
        /// </summary>
        public static string ResolveFolder(string exportDir, string name)
        {
            ValidateName(name);
            string root = Path.GetFullPath(exportDir);
            string full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("name resolves outside the export folder.");
            if (Directory.Exists(full) || File.Exists(full)) throw new ArgumentException("Folder already exists (not overwritten): " + full);
            return full;
        }

        /// <summary>Removes one trailing extension (e.g. ".ifc") and nothing else: "tower.v2" stays "tower.v2".</summary>
        public static string TrimExtension(string name, string extension)
        {
            name = (name ?? "").Trim();
            return name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - extension.Length) : name;
        }

        public static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..") || name != Path.GetFileName(name))
                throw new ArgumentException("name must be a plain name without folders: " + name);
        }

        public static void Write(string path, string format, IList<string> columns, IList<JObject> rows, JObject meta)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            switch (format)
            {
                case "csv": WriteCsv(path, columns, rows); break;
                case "json": WriteJson(path, columns, rows, meta); break;
                case "xlsx": WriteXlsx(path, columns, rows); break;
                default: throw new ArgumentException("Unknown format: " + format);
            }
        }

        private static void WriteCsv(string path, IList<string> columns, IList<JObject> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(Csv)));
            foreach (var r in rows) sb.AppendLine(string.Join(",", columns.Select(c => Csv(Cell(r[c])))));
            // UTF-8 with BOM so Excel shows Thai text correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        internal static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private static string Cell(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            if (t.Type == JTokenType.Float) return ((double)t).ToString("R", CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Object || t.Type == JTokenType.Array) return t.ToString(Formatting.None);
            return (string)t;
        }

        private static void WriteJson(string path, IList<string> columns, IList<JObject> rows, JObject meta)
        {
            var doc = new JObject { ["meta"] = meta ?? new JObject(), ["columns"] = new JArray(columns), ["rows"] = new JArray(rows) };
            File.WriteAllText(path, doc.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        private static void WriteXlsx(string path, IList<string> columns, IList<JObject> rows)
        {
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Data");
                for (int c = 0; c < columns.Count; c++) ws.Cell(1, c + 1).Value = columns[c];
                for (int r = 0; r < rows.Count; r++)
                    for (int c = 0; c < columns.Count; c++)
                    {
                        var t = rows[r][columns[c]];
                        var cell = ws.Cell(r + 2, c + 1);
                        if (t == null || t.Type == JTokenType.Null) continue;
                        if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) cell.Value = (double)t;
                        else cell.Value = Cell(t);
                    }
                ws.Row(1).Style.Font.Bold = true;
                ws.SheetView.FreezeRows(1);
                wb.SaveAs(path);
            }
        }
    }
}
