// Phase 6 spike: find the macro view name of the open "Export drawings as DWG/DXF/DGN" window, then export and close.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe10
{
    public static int Run(TextWriter o, string outDir, string fileType)
    {
        string log = Path.Combine(Path.GetTempPath(), "teklamcp_macro_probe.log");
        if (File.Exists(log)) File.Delete(log);
        Directory.CreateDirectory(outDir);
        string macroDir = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        string modeling = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).First(Directory.Exists);
        string file = "TeklaMcp_DwgProbe2.cs";
        string esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string src = @"#pragma reference ""Tekla.Macros.Wpf.Runtime""
#pragma reference ""Tekla.Macros.Runtime""
namespace UserMacros {
public sealed class Macro {
  static string L = """ + esc(log) + @""";
  static void W(string s) { System.IO.File.AppendAllText(L, System.DateTime.Now.ToString(""HH:mm:ss.fff"") + "" "" + s + System.Environment.NewLine); }
  static bool T(string label, System.Action a) { try { a(); W(""OK   "" + label); return true; } catch (System.Exception ex) { W(""FAIL "" + label + "": "" + ex.GetType().Name + "": "" + ex.Message.Replace(System.Environment.NewLine, "" "")); return false; } }
  [Tekla.Macros.Runtime.MacroEntryPointAttribute()]
  public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime) {
    var wpf = runtime.Get<Tekla.Macros.Wpf.Runtime.IWpfMacroHost>();
    W(""start"");
    string[] names = { ""DrawingExport.UIFeature.Views.MainContentView"", ""DrawingExport.UIFeature.MainContentView"", ""DrawingExport.UIFeature.Views.ExportOptionsView"" };
    string found = null;
    foreach (var n in names) { if (T(""output dir via "" + n, () => wpf.View(n).Find(""AID_DwgExport_OutputDirectoryTextBox"").As.TextBox.SetText(""" + esc(outDir) + @"""))) { found = n; break; } }
    if (found == null) { W(""no view""); W(""end""); return; }
    var ev = wpf.View(found);
    T(""file type"", () => ev.Find(""AID_DwgExport_FileTypesComboBox"").As.ComboBox.SetText(""" + esc(fileType) + @"""));
    T(""open folder off"", () => ev.Find(""AID_DwgExport_OpenFolderWhenDoneCheckbox"").As.ToggleButton.SetIsChecked(false));
    T(""export"", () => ev.Find(""AID_DwgExport_ExportButton"").As.Button.Invoke());
    System.Threading.Thread.Sleep(5000);
    T(""close"", () => ev.As.Window.Close());
    W(""end"");
  }
}
}";
        string path = Path.Combine(modeling, file);
        File.WriteAllText(path, src);
        try
        {
            o.WriteLine("RunMacro=" + Op.RunMacro(file));
            for (int i = 0; i < 600; i++) { Thread.Sleep(500); if (File.Exists(log) && File.ReadAllText(log).Contains(" end")) break; }
            Thread.Sleep(5000);
        }
        finally { File.Delete(path); }
        o.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "NO LOG");
        o.WriteLine("files in out: " + string.Join(", ", Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(f => f.Substring(outDir.Length) + " " + new FileInfo(f).Length)));
        return 0;
    }
}
