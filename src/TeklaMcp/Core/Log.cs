using System;
using System.IO;
using System.Text;

namespace TeklaMcp.Core
{
    internal enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

    /// <summary>
    /// File + stderr logger. Never writes to stdout (stdout carries MCP messages only).
    /// Files: %LOCALAPPDATA%\TeklaMcp\logs\teklamcp-yyyyMMdd.log
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static TextWriter _stderr = TextWriter.Null;
        private static string _dir;

        public static LogLevel MinLevel { get; set; } = LogLevel.Info;

        /// <summary>Process label in each line ("server" or "worker"); both processes share the log file.</summary>
        public static string Tag { get; set; } = "server";

        /// <summary>Optional extra sink (MCP notifications/message). Receives level, message.</summary>
        public static Action<LogLevel, string> Listener { get; set; }

        public static string Directory => _dir;

        public static void Init(TextWriter stderr, string dir = null)
        {
            _stderr = stderr ?? TextWriter.Null;
            _dir = dir ?? Path.Combine(Paths.AppData, "logs");
            try { System.IO.Directory.CreateDirectory(_dir); }
            catch (Exception ex) { _stderr.WriteLine("[TeklaMcp] cannot create log dir " + _dir + ": " + ex.Message); _dir = null; }
        }

        public static void Debug(string msg) => Write(LogLevel.Debug, msg);
        public static void Info(string msg) => Write(LogLevel.Info, msg);
        public static void Warn(string msg) => Write(LogLevel.Warning, msg);
        public static void Error(string msg, Exception ex = null) => Write(LogLevel.Error, ex == null ? msg : msg + " | " + ex);

        public static void Write(LogLevel level, string msg)
        {
            if (level < MinLevel) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level.ToString().ToUpperInvariant() + "] [" + Tag + "] " + msg;
            lock (Sync)
            {
                try { _stderr.WriteLine(line); _stderr.Flush(); } catch { /* stderr closed */ }
                if (_dir != null)
                {
                    try { File.AppendAllText(Path.Combine(_dir, "teklamcp-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine, Encoding.UTF8); }
                    catch { /* disk issue: keep running */ }
                }
            }
            try { Listener?.Invoke(level, msg); } catch { /* never let logging break the server */ }
        }
    }

    internal static class Paths
    {
        public static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeklaMcp");
    }
}
