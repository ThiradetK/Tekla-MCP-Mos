// Phase 6 spike: select a drawing in the Document manager through its model object (sort-independent), then export DWG.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using TSD = Tekla.Structures.Drawing;
using TSM = Tekla.Structures.Model;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe13
{
    public static int Run(TextWriter o, string mark, string outDir)
    {
        var m = new TSM.Model();
        var dh = new TSD.DrawingHandler();
        var en = dh.GetDrawings(); TSD.Drawing d = null; while (en.MoveNext()) if (en.Current.Mark == mark) d = en.Current;
        Tekla.Structures.Identifier id = d is TSD.CastUnitDrawing cu ? cu.CastUnitIdentifier : d is TSD.AssemblyDrawing ad ? ad.AssemblyIdentifier : d is TSD.SinglePartDrawing sp ? sp.PartIdentifier : null;
        var obj = m.SelectModelObject(id);
        o.WriteLine("drawing " + d.GetType().Name + " " + d.Mark + " -> model object " + obj?.GetType().Name + " " + obj?.Identifier.GUID);
        var ui = new TSM.UI.ModelObjectSelector();
        var before = new ArrayList(); var se = ui.GetSelectedObjects(); while (se.MoveNext()) before.Add(se.Current);
        o.WriteLine("select in model: " + ui.Select(new ArrayList { obj }));
        Directory.CreateDirectory(outDir);
        string log = Path.Combine(Path.GetTempPath(), "teklamcp_probe13.log"); if (File.Exists(log)) File.Delete(log);
        string macroDir = ""; Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        string modeling = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).First(Directory.Exists);
        string file = "TeklaMcp_Probe13.cs";
        string src = "#pragma reference \"Tekla.Macros.Wpf.Runtime\"\n#pragma reference \"Tekla.Macros.Runtime\"\n" +
            "namespace UserMacros { public sealed class Macro {\n" +
            "static string L = @\"" + log + "\";\n" +
            "static void W(string s) { System.IO.File.AppendAllText(L, System.DateTime.Now.ToString(\"HH:mm:ss.fff\") + \" \" + s + System.Environment.NewLine); }\n" +
            "sealed class Stop : System.Exception { }\n" +
            "static void T(string label, System.Action a) { try { a(); W(\"OK   \" + label); } catch (System.Exception ex) { W(\"FAIL \" + label + \": \" + ex.Message); throw new Stop(); } }\n" +
            "[Tekla.Macros.Runtime.MacroEntryPointAttribute()] public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime) {\n" +
            "var wpf = runtime.Get<Tekla.Macros.Wpf.Runtime.IWpfMacroHost>();\n" +
            "try {\n" +
            "T(\"open document manager\", () => wpf.InvokeCommand(\"CommandRepository\", \"Drawing.DrawingList\"));\n" +
            "var dm = wpf.View(\"DocumentManager.MainWindow\");\n" +
            "T(\"select drawings of selected objects\", () => dm.Find(\"AID_DOCMAN_ButtonSelectDrawings\").As.Button.Invoke());\n" +
            "System.Threading.Thread.Sleep(1500);\n" +
            "T(\"export\", () => dm.Find(\"AID_DOCMAN_ButtonExportDocument\").As.Button.Invoke());\n" +
            "var ev = wpf.View(\"DrawingExport.UIFeature.MainContentView\");\n" +
            "T(\"folder\", () => ev.Find(\"AID_DwgExport_OutputDirectoryTextBox\").As.TextBox.SetText(@\"" + outDir + "\"));\n" +
            "T(\"type\", () => ev.Find(\"AID_DwgExport_FileTypesComboBox\").As.Selector.Select(\"DWG\"));\n" +
            "T(\"export button\", () => ev.Find(\"AID_DwgExport_ExportButton\").As.Button.Invoke());\n" +
            "} catch (Stop) { } finally { W(\"END\"); }\n} } }\n";
        string path = Path.Combine(modeling, file);
        File.WriteAllText(path, src);
        try
        {
            o.WriteLine("RunMacro=" + Op.RunMacro(file));
            for (int i = 0; i < 200; i++) { Thread.Sleep(500); if (File.Exists(log) && File.ReadAllText(log).Contains(" END")) break; }
            Thread.Sleep(6000);
        }
        finally { File.Delete(path); o.WriteLine("restore model selection: " + ui.Select(before)); }
        o.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "NO LOG");
        o.WriteLine("files: " + string.Join(", ", Directory.GetFiles(outDir).Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length)));
        return 0;
    }
}
