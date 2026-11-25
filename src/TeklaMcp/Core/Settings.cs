using System;
using System.IO;
using Newtonsoft.Json;

namespace TeklaMcp.Core
{
    /// <summary>
    /// User settings from %LOCALAPPDATA%\TeklaMcp\settings.json. Missing file = defaults. Only "--readonly on|off" (Registrar) writes it.
    /// </summary>
    internal sealed class Settings
    {
        /// <summary>true = WRITE and DESTRUCTIVE tools are refused.</summary>
        [JsonProperty("readOnly")] public bool ReadOnly { get; set; }

        /// <summary>debug | info | warning | error</summary>
        [JsonProperty("logLevel")] public string LogLevel { get; set; } = "info";

        /// <summary>Max time for one Tekla API call before the tool returns an error (seconds).</summary>
        [JsonProperty("teklaTimeoutSeconds")] public int TeklaTimeoutSeconds { get; set; } = 60;

        /// <summary>Lifetime of a preview confirm_token (minutes).</summary>
        [JsonProperty("confirmTokenMinutes")] public int ConfirmTokenMinutes { get; set; } = 10;

        /// <summary>Folder for tekla_export_data files. Default: Documents\TeklaMcp\exports.</summary>
        [JsonProperty("exportDir")] public string ExportDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeklaMcp", "exports");

        public static string DefaultPath => Path.Combine(Paths.AppData, "settings.json");

        public static Settings Load(string path = null)
        {
            path = path ?? DefaultPath;
            if (!File.Exists(path)) return new Settings();
            try
            {
                var s = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path)) ?? new Settings();
                if (s.TeklaTimeoutSeconds <= 0) s.TeklaTimeoutSeconds = 60;
                if (s.ConfirmTokenMinutes <= 0) s.ConfirmTokenMinutes = 10;
                return s;
            }
            catch (Exception ex)
            {
                // Fail safe: a broken settings file switches the server to read-only.
                Log.Error("settings.json unreadable, using defaults with readOnly=true: " + path, ex);
                return new Settings { ReadOnly = true };
            }
        }

        public LogLevel ParsedLogLevel()
        {
            switch ((LogLevel ?? "").Trim().ToLowerInvariant())
            {
                case "debug": return Core.LogLevel.Debug;
                case "warning": case "warn": return Core.LogLevel.Warning;
                case "error": return Core.LogLevel.Error;
                default: return Core.LogLevel.Info;
            }
        }
    }
}
