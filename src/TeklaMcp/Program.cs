using System;
using System.IO;
using TeklaMcp.Core;
using TeklaMcp.Mcp;
using TeklaMcp.Tekla;
using TeklaMcp.Tools.Session;

namespace TeklaMcp
{
    internal static class Program
    {
        // No Tekla types may appear in this file: in the worker, the AssemblyResolver must be installed before any Tekla type loads.
        private static int Main(string[] args)
        {
            // 1. Protect the MCP channel. Keep the raw stdout stream for MCP; anything written via Console.Out
            //    (including Tekla API noise such as "Connection failed ...") goes to stderr instead.
            Stream stdout = Console.OpenStandardOutput();
            Stream stdin = Console.OpenStandardInput();
            Console.SetOut(Console.Error);

            var settings = Settings.Load();
            Log.Init(Console.Error);
            Log.MinLevel = settings.ParsedLogLevel();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

            if (args.Length == 1 && args[0] == "--worker")
            {
                Log.Tag = "worker";
                return WorkerHost.Run(stdin, stdout);
            }

            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            if (args.Length > 0)
                return Registrar.Run(args, exe, new StreamWriter(stdout) { AutoFlush = true });

            Log.Info("TeklaMcp " + Protocol.ServerVersion + " starting. readOnly=" + settings.ReadOnly + " logDir=" + Log.Directory);

            // 2. Wire up. This process never loads Tekla; all Tekla calls go to the "--worker" child process.
            using (var tekla = new TeklaWorker(exe, TimeSpan.FromSeconds(settings.TeklaTimeoutSeconds)))
            {
                var registry = new ToolRegistry();
                registry.Add(new StatusTool(tekla, settings));
                foreach (var t in Tools.Query.QueryTools.Create(tekla)) registry.Add(t);
                foreach (var t in Tools.Data.DataTools.Create(tekla, settings)) registry.Add(t);
                foreach (var t in Tools.Modify.WriteTools.Create(tekla)) registry.Add(t);
                foreach (var t in Tools.Validation.ValidationTools.Create(tekla, settings)) registry.Add(t);

                var gate = new SafetyGate(settings,
                    new ConfirmTokenStore(TimeSpan.FromMinutes(settings.ConfirmTokenMinutes)),
                    new AuditLog(Log.Directory));

                return McpServer.ForStdio(stdin, stdout, registry, gate).Run();
            }
        }
    }
}
