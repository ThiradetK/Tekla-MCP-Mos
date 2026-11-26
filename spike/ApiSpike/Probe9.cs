// Phase 6 spike: export a drawing to DWG/DXF with a WPF macro (Document manager > Export), and close the print dialog.
// Logs to %TEMP%\teklamcp_macro_probe.log. Writes only DWG/DXF files; the model is not changed.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe9
{
    public static int Run(TextWriter o, string mark, string outDir, string fileType)
    {
        string log = Path.Combine(Path.GetTempPath(), "teklamcp_macro_probe.log");
        if (File.Exists(log)) File.Delete(log);
        Directory.CreateDirectory(outDir);
        string macroDir = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        string modeling = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).First(Directory.Exists);
        string file = "TeklaMcp_DwgProbe.cs";
        string esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string src = @"#pragma reference ""Tekla.Macros.Wpf.Runtime""
#pragma reference ""Tekla.Macros.Akit""
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
    T(""open document manager"", () => wpf.InvokeCommand(""CommandRepository"", ""Drawing.DrawingList""));
    var dm = wpf.View(""DocumentManager.MainWindow"");
    T(""clear search"", () => dm.Find(""AID_DOCMAN_SearchBox"").As.TextBox.SetText(""""));
    System.Threading.Thread.Sleep(2000);
    T(""select row index"", () => dm.Find(""AID_DOCMAN_DataGridControl"").As.DataGrid.NewSelection.With(" + Environment.GetEnvironmentVariable("PROBE9_ROW") + @").Invoke());

    T(""press export"", () => dm.Find(""AID_DOCMAN_ButtonExportDocument"").As.Button.Invoke());
    string[] names = { ""DrawingExport.UIFeature.MainContentView"" };
    string found = null;
    foreach (var n in names) { if (T(""find export button in "" + n, () => wpf.View(n).Find(""AID_DwgExport_ExportButton""))) { found = n; break; } }
    if (found == null) { W(""no export view""); W(""end""); return; }
    var ev = wpf.View(found);
    T(""file type"", () => ev.Find(""AID_DwgExport_FileTypesComboBox"").As.Selector.Select(""" + esc(fileType) + @"""));
    T(""output dir"", () => ev.Find(""AID_DwgExport_OutputDirectoryTextBox"").As.TextBox.SetText(""" + esc(outDir) + @"""));
    T(""open folder when done off"", () => ev.Find(""AID_DwgExport_OpenFolderWhenDoneCheckbox"").As.ToggleButton.SetIsChecked(false));
    T(""export"", () => ev.Find(""AID_DwgExport_ExportButton"").As.Button.Invoke());
    W(""end"");
  }
}
}";
        string path = Path.Combine(modeling, file);
        File.WriteAllText(path, src);
        try
        {
            o.WriteLine("RunMacro=" + Op.RunMacro(file));
            for (int i = 0; i < 400; i++) { Thread.Sleep(500); if (File.Exists(log) && File.ReadAllText(log).Contains(" end")) break; }
            Thread.Sleep(5000);
        }
        finally { File.Delete(path); }
        o.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "NO LOG");
        o.WriteLine("files in out: " + string.Join(", ", Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(f => f.Substring(outDir.Length) + " " + new FileInfo(f).Length)));
        return 0;
    }
}




