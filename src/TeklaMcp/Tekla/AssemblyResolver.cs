using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using TeklaMcp.Core;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Loads Tekla Open API DLLs from the local Tekla Structures 2026 installation (no Trimble DLLs are shipped).
    /// Install path: HKLM\SOFTWARE\Trimble\Tekla Structures\2026.0\setup  MainDir + TSVersionDir + "\bin".
    /// Probed folders: bin\ and bin\Net48Runtime\ (Tekla.Structures.dll lives in Net48Runtime in 2026).
    /// Must be installed before any Tekla type is touched.
    /// </summary>
    internal static class AssemblyResolver
    {
        public const string RegistryKey = @"SOFTWARE\Trimble\Tekla Structures\2026.0\setup";

        private static string[] _probe = new string[0];

        public static string BinPath { get; private set; }
        public static string Problem { get; private set; }
        public static bool IsReady => BinPath != null;

        public static void Install()
        {
            BinPath = FindBin(out string problem);
            Problem = problem;
            if (BinPath == null) { Log.Warn("Tekla Structures 2026 not found: " + problem); return; }
            _probe = new[] { BinPath, Path.Combine(BinPath, "Net48Runtime") };
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            Log.Info("Tekla bin: " + BinPath);
        }

        internal static string FindBin(out string problem)
        {
            problem = null;
            try
            {
                using (var hklm = RegistryKey64())
                using (var key = hklm.OpenSubKey(RegistryKey))
                {
                    if (key == null) { problem = "registry key HKLM\\" + RegistryKey + " missing"; return null; }
                    var main = key.GetValue("MainDir") as string;
                    var ver = key.GetValue("TSVersionDir") as string;
                    if (string.IsNullOrEmpty(main) || string.IsNullOrEmpty(ver)) { problem = "MainDir/TSVersionDir not set in registry"; return null; }
                    string bin = Path.Combine(main, ver, "bin");
                    if (!File.Exists(Path.Combine(bin, "Tekla.Structures.Model.dll"))) { problem = "Tekla.Structures.Model.dll not found in " + bin; return null; }
                    return bin;
                }
            }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        private static RegistryKey RegistryKey64() => Microsoft.Win32.RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        private static Assembly OnResolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var dir in _probe)
            {
                string p = Path.Combine(dir, name + ".dll");
                if (File.Exists(p))
                {
                    Log.Debug("resolve " + name + " -> " + p);
                    return Assembly.LoadFrom(p);
                }
            }
            return null;
        }
    }
}
