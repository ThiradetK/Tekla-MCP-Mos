// Phase 6 spike: print a drawing to PDF with a WPF macro run through Operation.RunMacro (no window focus needed).
// The macro logs each step to %TEMP%\teklamcp_macro_probe.log. Writes only a PDF; the model is not changed.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe8
{
    public static int Run(TextWriter o, string mark, string outDir)
    {
        string log = Path.Combine(Path.GetTempPath(), "teklamcp_macro_probe.log");
        if (File.Exists(log)) File.Delete(log);
        Directory.CreateDirectory(outDir);
        string macroDir = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        string modeling = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).First(Directory.Exists);
        string file = "TeklaMcp_PdfProbe.cs";
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
    T(""search " + esc(mark) + @""", () => dm.Find(""AID_DOCMAN_SearchBox"").As.TextBox.SetText(""" + esc(mark) + @"""));
    T(""select row 0"", () => dm.Find(""AID_DOCMAN_DataGridControl"").As.DataGrid.NewSelection.With(0).Invoke());
    T(""press print"", () => dm.Find(""AID_DOCMAN_ButtonPrintDocument"").As.Button.Invoke());
    string[] names = { ""DPMPrinterFeature.DPMPrinterViewWindow"", ""DPMPrinterFeature.DpmPrinterViewWindow"", ""DPMPrinterFeature.DpmPrinterView"" };
    string found = null;
    foreach (var n in names) { if (T(""find print button in "" + n, () => wpf.View(n).Find(""AID_PDFPD_PrintButton""))) { found = n; break; } }
    if (found == null) { W(""no print view""); W(""end""); return; }
    var pv = wpf.View(found);
    T(""pdf radio"", () => pv.Find(""AID_PDFPD_PDFRadio"").As.ToggleButton.SetIsChecked(true));
    T(""file location"", () => pv.Find(""AID_PDFPD_FileLocation"").As.TextBox.SetText(""" + esc(outDir) + @"""));
    T(""open file when finished off"", () => pv.Find(""AID_PDFPD_OpenFileWhenFinished"").As.ToggleButton.SetIsChecked(false));
    T(""single file on"", () => pv.Find(""AID_PDFPD_OutputToSingleFile"").As.ToggleButton.SetIsChecked(true));
    T(""single file name"", () => pv.Find(""AID_PDFPD_PDFSingleFileOutput"").As.TextBox.SetText(""probe_macro""));
    T(""print"", () => pv.Find(""AID_PDFPD_PrintButton"").As.Button.Invoke());
    T(""close print dialog"", () => pv.Find(""AID_PDFPD_ParentStackPanel"").As.Window.Close());
    W(""end"");
  }
}
}";
        string path = Path.Combine(modeling, file);
        File.WriteAllText(path, src);
        try
        {
            o.WriteLine("RunMacro=" + Op.RunMacro(file));
            for (int i = 0; i < 240; i++) { Thread.Sleep(500); if (File.Exists(log) && File.ReadAllText(log).Contains(" end")) break; }
            Thread.Sleep(3000);
        }
        finally { File.Delete(path); }
        o.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "NO LOG");
        o.WriteLine("files in out: " + string.Join(", ", Directory.GetFiles(outDir).Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length)));
        return 0;
    }
}
