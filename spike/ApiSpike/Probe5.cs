// Phase 5 spike (does not change the model): macro folder + harmless RunMacro, list drawings, drawing attribute files.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TSD = Tekla.Structures.Drawing;
using TSM = Tekla.Structures.Model;

internal static class Probe5
{
    public static int Run(TextWriter o)
    {
        var m = new TSM.Model();
        if (!m.GetConnectionStatus()) { o.WriteLine("not connected"); return 1; }
        string macroDir = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        o.WriteLine("XS_MACRO_DIRECTORY=" + macroDir);
        foreach (var d in macroDir.Split(';').Where(s => s.Trim().Length > 0))
        {
            string modeling = Path.Combine(d.Trim(), "modeling");
            bool writable = false;
            try { Directory.CreateDirectory(modeling); string t = Path.Combine(modeling, "_teklamcp_probe.tmp"); File.WriteAllText(t, "x"); File.Delete(t); writable = true; } catch { }
            o.WriteLine("  " + modeling + " exists=" + Directory.Exists(modeling) + " writable=" + writable);
        }

        // Harmless macro: writes a marker file to %TEMP%. Uses the first writable modeling folder.
        string target = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).FirstOrDefault(p => { try { File.WriteAllText(Path.Combine(p, "_w.tmp"), "x"); File.Delete(Path.Combine(p, "_w.tmp")); return true; } catch { return false; } });
        if (target != null)
        {
            string marker = Path.Combine(Path.GetTempPath(), "teklamcp_macro_marker.txt");
            if (File.Exists(marker)) File.Delete(marker);
            string macroPath = Path.Combine(target, "TeklaMcp_Probe.cs");
            File.WriteAllText(macroPath,
                "namespace Tekla.Technology.Akit.UserScript { public class Script { public static void Run(Tekla.Technology.Akit.IScript akit) { " +
                "System.IO.File.WriteAllText(@\"" + marker + "\", \"ran \" + System.DateTime.Now.ToString(\"o\")); } } }");
            bool started = TSM.Operations.Operation.RunMacro("TeklaMcp_Probe.cs");
            int waited = 0; while (TSM.Operations.Operation.IsMacroRunning() && waited < 30000) { Thread.Sleep(200); waited += 200; }
            Thread.Sleep(500);
            o.WriteLine("RunMacro=" + started + " waited=" + waited + " ms marker=" + (File.Exists(marker) ? File.ReadAllText(marker) : "MISSING"));
            File.Delete(macroPath);
        }

        // Drawings
        var dh = new TSD.DrawingHandler();
        o.WriteLine("drawing connection=" + dh.GetConnectionStatus() + " anyOpen=" + dh.IsAnyDrawingOpen());
        var en = dh.GetDrawings();
        o.WriteLine("drawings=" + en.GetSize());
        int i = 0;
        while (en.MoveNext() && i++ < 8)
        {
            var d = en.Current;
            o.WriteLine("  " + d.GetType().Name + " mark='" + d.Mark + "' name='" + d.Name + "' title1='" + d.Title1 + "' status=" + d.UpToDateStatus + " issued=" + d.IsIssued + " locked=" + d.IsLocked + " frozen=" + d.IsFrozen + " created=" + d.CreationDate.ToString("s") + " ids=" + dh.GetModelObjectIdentifiers(d).Count);
        }

        // Drawing attribute files: model attributes folder + environment search path
        string modelPath = m.GetInfo().ModelPath;
        foreach (var ext in new[] { "*.gd", "*.ad", "*.wd", "*.cud" })
        {
            var files = Directory.GetFiles(Path.Combine(modelPath, "attributes"), ext).Select(Path.GetFileNameWithoutExtension).ToList();
            o.WriteLine("model attributes " + ext + ": " + files.Count + " " + string.Join(", ", files.Take(8)));
        }
        return 0;
    }
}
