using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Connection to the running Tekla Structures. Used only inside the worker process (WorkerHost), on its single thread.
    /// Rule (Phase 0): call Model.GetConnectionStatus() before any static Tekla type
    /// (TeklaStructuresInfo / TeklaStructuresSettings); their static constructors fail permanently if Tekla is not running.
    /// </summary>
    internal sealed class TeklaSession
    {
        // Typed as object so loading this class never forces Tekla.Structures.Model.dll to load.
        private object _model;

        /// <summary>Connected model with an open model, or throws TeklaException.</summary>
        public TSM.Model RequireModel()
        {
            if (!AssemblyResolver.IsReady) throw new TeklaException("not_installed", "Tekla Structures 2026 not found: " + AssemblyResolver.Problem);
            var model = _model as TSM.Model;
            if (model == null || !model.GetConnectionStatus())
            {
                model = new TSM.Model();
                if (!model.GetConnectionStatus()) { _model = null; throw new TeklaException("not_connected", "Cannot connect to Tekla Structures 2026. Start Tekla Structures and open a model."); }
                _model = model;
            }
            if (string.IsNullOrEmpty(model.GetInfo().ModelName)) throw new TeklaException("no_model", "Tekla Structures is running but no model is open.");
            return model;
        }

        /// <summary>Read-only status. Never throws for "not connected"; reports it in the result.</summary>
        public JObject GetStatus()
        {
            var r = new JObject
            {
                ["tekla_installed"] = AssemblyResolver.IsReady,
                ["tekla_bin"] = AssemblyResolver.BinPath,
                ["connected"] = false,
                ["model_open"] = false,
                ["units"] = new JObject { ["length"] = "mm", ["weight"] = "kg", ["angle"] = "degree" }
            };
            if (!AssemblyResolver.IsReady) { r["problem"] = AssemblyResolver.Problem; return r; }

            r["api_assembly_version"] = ApiFileVersion();
            var model = new TSM.Model();
            bool connected = model.GetConnectionStatus();
            r["connected"] = connected;
            if (!connected)
            {
                _model = null;
                r["problem"] = "Tekla Structures 2026 is not running (or not reachable).";
                return r;
            }
            _model = model;

            r["program_version"] = global::Tekla.Structures.TeklaStructuresInfo.GetCurrentProgramVersion();
            string xsSystem = Option("XS_SYSTEM");
            r["environment"] = TeklaText.EnvironmentFromSystemPath(xsSystem);
            r["language"] = Option("XS_LANGUAGE");

            var info = model.GetInfo();
            bool open = !string.IsNullOrEmpty(info.ModelName);
            r["model_open"] = open;
            if (open)
            {
                r["model"] = new JObject
                {
                    ["name"] = info.ModelName,
                    ["path"] = info.ModelPath,
                    ["current_phase"] = info.CurrentPhase,
                    ["north_direction_deg"] = info.NorthDirection
                };
            }
            else r["problem"] = "No model is open in Tekla Structures.";
            return r;
        }

        private static string Option(string name)
        {
            // GetAdvancedOption returns true even for unknown names (Phase 0); treat empty as "not set".
            string v = null;
            global::Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption(name, ref v);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        private static string ApiFileVersion()
        {
            try
            {
                string loc = typeof(TSM.Model).Assembly.Location;
                return string.IsNullOrEmpty(loc) ? null : FileVersionInfo.GetVersionInfo(loc).FileVersion;
            }
            catch { return null; }
        }
    }
}
