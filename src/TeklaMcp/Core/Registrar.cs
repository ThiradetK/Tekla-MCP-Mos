using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TeklaMcp.Tekla;

namespace TeklaMcp.Core
{
    /// <summary>
    /// Command-line maintenance used by the installer and by users:
    ///   --register desktop|code|all, --unregister desktop|code|all, --readonly on|off, --doctor
    /// No Tekla types here: --doctor talks to Tekla only through a "--worker" child process.
    /// </summary>
    internal static class Registrar
    {
        public const string ServerName = "tekla";

        public static int Run(string[] args, string exe, TextWriter o)
        {
            string cmd = args[0], arg = args.Length > 1 ? args[1].ToLowerInvariant() : null;
            try
            {
                switch (cmd)
                {
                    case "--register":
                    case "--unregister":
                        bool add = cmd == "--register";
                        if (arg != "desktop" && arg != "code" && arg != "all") return Usage(o);
                        int rc = 0;
                        if (arg != "code") rc |= add ? RegisterDesktop(exe, o) : UnregisterDesktop(exe, o);
                        if (arg != "desktop") rc |= add ? RegisterCode(exe, o) : UnregisterCode(o);
                        return rc;
                    case "--readonly":
                        if (arg != "on" && arg != "off") return Usage(o);
                        return SetReadOnly(arg == "on", o);
                    case "--doctor":
                        return Doctor(exe, o);
                    default:
                        return Usage(o);
                }
            }
            catch (Exception ex)
            {
                Log.Error(cmd + " failed", ex);
                o.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int Usage(TextWriter o)
        {
            o.WriteLine("TeklaMcp " + Mcp.Protocol.ServerVersion);
            o.WriteLine("  TeklaMcp.exe                              start the MCP server (stdio; started by Claude)");
            o.WriteLine("  TeklaMcp.exe --register desktop|code|all  add the 'tekla' server to Claude Desktop / Claude Code");
            o.WriteLine("  TeklaMcp.exe --unregister desktop|code|all");
            o.WriteLine("  TeklaMcp.exe --readonly on|off            block / allow all tools that change the model");
            o.WriteLine("  TeklaMcp.exe --doctor                     check Tekla, the API connection and the Claude config");
            return 2;
        }

        // ---------------- Claude Desktop ----------------

        /// <summary>
        /// Config files Claude Desktop reads. MS Store build: %LOCALAPPDATA%\Claude-Data\claude_*\ (package suffix differs per machine).
        /// Classic installer build: %APPDATA%\Claude\. Returns the Store paths if any exist, else the classic path.
        /// </summary>
        internal static List<string> DesktopConfigPaths(string localAppData, string appData)
        {
            var list = new List<string>();
            string store = Path.Combine(localAppData, "Claude-Data");
            if (Directory.Exists(store))
                list.AddRange(Directory.GetDirectories(store, "claude_*").Select(d => Path.Combine(d, "claude_desktop_config.json")));
            if (list.Count == 0) list.Add(Path.Combine(appData, "Claude", "claude_desktop_config.json"));
            return list;
        }

        private static List<string> DesktopConfigPaths() => DesktopConfigPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        /// <summary>Adds or replaces mcpServers.tekla; every other key is kept.</summary>
        internal static string AddEntry(string json, string exe)
        {
            var root = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
            if (!(root["mcpServers"] is JObject servers)) root["mcpServers"] = servers = new JObject();
            servers[ServerName] = new JObject { ["command"] = exe, ["args"] = new JArray() };
            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Removes mcpServers.tekla when it points to this exe or to a file that no longer exists
        /// (an entry for another copy, e.g. a dev build, is kept). Returns null when nothing was removed.
        /// </summary>
        internal static string RemoveEntry(string json, string exe, Func<string, bool> fileExists = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var root = JObject.Parse(json);
            if (!(root["mcpServers"] is JObject servers) || servers[ServerName] == null) return null;
            string cmd = (string)servers[ServerName]["command"];
            if (cmd != null && !SamePath(cmd, exe) && (fileExists ?? File.Exists)(cmd)) return null;
            servers.Remove(ServerName);
            return root.ToString(Formatting.Indented);
        }

        private static int RegisterDesktop(string exe, TextWriter o)
        {
            var paths = DesktopConfigPaths();
            int done = 0;
            foreach (var path in paths)
            {
                if (!Directory.Exists(Path.GetDirectoryName(path)))
                {
                    o.WriteLine("Claude Desktop: config folder not found (" + Path.GetDirectoryName(path) + "). Is Claude Desktop installed and started once?");
                    continue;
                }
                string old = File.Exists(path) ? File.ReadAllText(path) : null;
                WriteWithBackup(path, old, AddEntry(old, exe));
                o.WriteLine("Claude Desktop: registered 'tekla' in " + path);
                Log.Info("register desktop: " + path + " -> " + exe);
                done++;
            }
            if (done > 0) o.WriteLine("Restart Claude Desktop (Quit from the tray icon, then start it) to load the server.");
            return done > 0 ? 0 : 1;
        }

        private static int UnregisterDesktop(string exe, TextWriter o)
        {
            foreach (var path in DesktopConfigPaths().Where(File.Exists))
            {
                string old = File.ReadAllText(path);
                string updated = RemoveEntry(old, exe);
                if (updated == null) { o.WriteLine("Claude Desktop: no 'tekla' entry for this exe in " + path); continue; }
                WriteWithBackup(path, old, updated);
                o.WriteLine("Claude Desktop: removed 'tekla' from " + path);
                Log.Info("unregister desktop: " + path);
            }
            return 0;
        }

        private static void WriteWithBackup(string path, string old, string updated)
        {
            if (old != null) File.WriteAllText(path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), old);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, updated);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        // ---------------- Claude Code ----------------

        private static int RegisterCode(string exe, TextWriter o)
        {
            if (Claude("--version").exit != 0) { o.WriteLine("Claude Code: 'claude' command not found. Skipped."); return 1; }
            Claude("mcp remove --scope user " + ServerName);   // replace an older entry; fails harmlessly when absent
            var r = Claude("mcp add --scope user " + ServerName + " -- \"" + exe + "\"");
            o.WriteLine(r.exit == 0 ? "Claude Code: registered 'tekla' (user scope)." : "Claude Code: 'claude mcp add' failed: " + r.output.Trim());
            Log.Info("register code: exit " + r.exit + " " + r.output.Trim());
            return r.exit == 0 ? 0 : 1;
        }

        private static int UnregisterCode(TextWriter o)
        {
            if (Claude("--version").exit != 0) { o.WriteLine("Claude Code: 'claude' command not found. Skipped."); return 0; }
            var r = Claude("mcp remove --scope user " + ServerName);
            o.WriteLine(r.exit == 0 ? "Claude Code: removed 'tekla' (user scope)." : "Claude Code: no 'tekla' entry in user scope.");
            Log.Info("unregister code: exit " + r.exit + " " + r.output.Trim());
            return 0;
        }

        /// <summary>Runs the claude CLI through cmd.exe so npm .cmd shims resolve.</summary>
        private static (int exit, string output) Claude(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/d /c claude " + arguments)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
                    return (p.ExitCode, outp + err.Result);
                }
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        // ---------------- settings ----------------

        private static int SetReadOnly(bool on, TextWriter o)
        {
            string path = Settings.DefaultPath;
            var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            root["readOnly"] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, root.ToString(Formatting.Indented));
            o.WriteLine("readOnly = " + on.ToString().ToLowerInvariant() + " in " + path + ". Restart Claude to apply.");
            Log.Info("readOnly set to " + on);
            return 0;
        }

        // ---------------- doctor ----------------

        private static int Doctor(string exe, TextWriter o)
        {
            int problems = 0;
            void Line(bool ok, string text) { o.WriteLine((ok ? "[ OK ] " : "[FAIL] ") + text); if (!ok) problems++; }
            void Info(string text) => o.WriteLine("[INFO] " + text);

            Info("TeklaMcp " + Mcp.Protocol.ServerVersion + " at " + exe);
            Line(Environment.Is64BitOperatingSystem && Environment.Is64BitProcess, "64-bit Windows and process");
            int release = (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0) ?? 0;
            Line(release >= 528040, ".NET Framework 4.8 or later (release key " + release + ")");

            string bin = AssemblyResolver.FindBin(out string binProblem);
            Line(bin != null, "Tekla Structures 2026 installation: " + (bin ?? binProblem));
            if (bin != null)
            {
                try
                {
                    using (var w = new TeklaWorker(exe, TimeSpan.FromSeconds(60)))
                    {
                        var s = w.Call("status");
                        bool connected = (bool?)s["connected"] == true;
                        Line(connected, "Tekla Open API connection" + (connected ? "" : " (start Tekla Structures 2026 and open a model)"));
                        Info("status: " + s.ToString(Formatting.None));
                    }
                }
                catch (Exception ex) { Line(false, "Tekla worker: " + ex.Message); }
            }

            var settings = Settings.Load();
            Info("settings: " + Settings.DefaultPath + (File.Exists(Settings.DefaultPath) ? "" : " (not present, defaults)") + "; readOnly=" + settings.ReadOnly.ToString().ToLowerInvariant());
            Info("logs: " + Log.Directory);

            bool anyDesktop = false;
            foreach (var path in DesktopConfigPaths())
            {
                if (!File.Exists(path)) { Info("Claude Desktop config not found: " + path); continue; }
                JObject entry = null;
                try { entry = JObject.Parse(File.ReadAllText(path))["mcpServers"]?[ServerName] as JObject; }
                catch (Exception ex) { Line(false, "Claude Desktop config is not valid JSON: " + path + " (" + ex.Message + ")"); continue; }
                if (entry == null) { Info("Claude Desktop: no 'tekla' entry in " + path + " (run --register desktop)"); continue; }
                anyDesktop = true;
                string cmd = (string)entry["command"];
                Line(cmd != null && File.Exists(cmd), "Claude Desktop entry in " + path + " -> " + cmd + (SamePath(cmd, exe) ? "" : " (not this exe)"));
            }
            if (!anyDesktop) Info("Claude Desktop: not registered.");

            var code = Claude("mcp get " + ServerName);
            Info(code.exit == 0 ? "Claude Code: " + string.Join(" | ", code.output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4))
                                : "Claude Code: 'tekla' not registered or 'claude' not found.");

            o.WriteLine(problems == 0 ? "No problems found." : problems + " problem(s) found.");
            return problems == 0 ? 0 : 1;
        }

        private static bool SamePath(string a, string b) =>
            a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
