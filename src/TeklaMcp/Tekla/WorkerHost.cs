using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Mcp;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// "TeklaMcp.exe --worker": the only process that loads the Tekla Open API.
    /// Reads one JSON request per line on stdin, runs it on this (single) thread, writes one JSON response per line.
    /// The Tekla API keeps its remoting connection in process-wide static state that never resets
    /// (Phase 1 test), so the server replaces this process when the connection is missing or stale.
    ///   request:  {"id":1,"op":"status","args":{}}
    ///   response: {"id":1,"ok":true,"result":{...},"reset":false} | {"id":1,"ok":false,"code":"...","message":"...","reset":true}
    ///   reset=true: this worker cannot reach Tekla; the server should start a fresh worker.
    /// </summary>
    internal static class WorkerHost
    {
        public static int Run(Stream stdin, Stream stdout)
        {
            AssemblyResolver.Install();
            var session = new TeklaSession();
            var query = new TeklaQuery(session);
            var write = new TeklaWrite(session, query);
            var validate = new TeklaValidate(session);
            var drawing = new TeklaDrawing(session);
            var export = new TeklaExport(session);
            var drawingExport = new TeklaDrawingExport(session);
            var output = new JsonRpcWriter(stdout);
            var input = new StreamReader(stdin, new UTF8Encoding(false));
            Log.Info("Tekla worker started.");
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject req;
                try { req = JObject.Parse(line); }
                catch (JsonException ex) { Log.Error("worker: bad request line", ex); continue; }
                output.Send(Handle(session, query, write, validate, drawing, export, drawingExport, req));
            }
            Log.Info("Tekla worker stopped (stdin closed).");
            return 0;
        }

        private static JObject Handle(TeklaSession session, TeklaQuery query, TeklaWrite write, TeklaValidate validate, TeklaDrawing drawing, TeklaExport export, TeklaDrawingExport drawingExport, JObject req)
        {
            var resp = new JObject { ["id"] = req["id"] };
            var args = req["args"] as JObject ?? new JObject();
            try
            {
                JObject result;
                switch ((string)req["op"])
                {
                    case "status":
                        result = session.GetStatus();
                        resp["reset"] = (bool)result["tekla_installed"] && !(bool)result["connected"];
                        break;
                    case "query": result = query.Query(args); break;
                    case "get": result = query.Get(args); break;
                    case "report_properties": result = query.ReportProperties(args); break;
                    case "select": result = query.Select(args); break;
                    case "assembly": result = query.Assembly(args); break;
                    case "grids": result = query.Grids(args); break;
                    case "workplane": result = query.Workplane(args); break;
                    case "catalog": result = query.Catalog(args); break;
                    case "run_report": result = query.RunReport(args); break;
                    case "write":
                        string action = (string)args["action"] ?? "";
                        result = action.StartsWith("drawing_") || action == "numbering" ? drawing.Write(args) : write.Run(args);
                        break;
                    case "drawings": result = drawing.List(args); break;
                    case "drawing_get": result = drawing.Get(args); break;
                    case "drawing_open": result = drawing.Open(args); break;
                    case "export_ifc": result = export.Ifc(args); break;
                    case "export_nc": result = export.Nc(args); break;
                    case "export_drawings": result = drawingExport.Export(args); break;
                    case "validate": result = validate.Validate(args); break;
                    case "clash": result = validate.Clash(args); break;
                    default:
                        throw new TeklaException("unknown_op", "Unknown worker operation: " + req["op"]);
                }
                resp["ok"] = true;
                resp["result"] = result;
            }
            catch (TeklaException ex)
            {
                Fail(resp, ex.Code, ex.Message, ex.Code == "not_connected");
            }
            catch (Exception ex)
            {
                bool lost = TeklaText.IsConnectionLost(ex);
                Log.Error("worker: op " + req["op"] + " failed", ex);
                Fail(resp, lost ? "connection_lost" : "tekla_error",
                     lost ? "Connection to Tekla Structures was lost (Tekla restarted?)." : ex.GetBaseException().Message, lost);
            }
            return resp;
        }

        private static void Fail(JObject resp, string code, string message, bool reset)
        {
            resp["ok"] = false;
            resp["code"] = code;
            resp["message"] = message;
            resp["reset"] = reset;
        }
    }
}




