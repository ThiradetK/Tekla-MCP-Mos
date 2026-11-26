// Phase 6 spike: set DWG/DXF file type in the open export window, export, then close every export window with WM_CLOSE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe11
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr p);

    internal static List<IntPtr> Windows(int pid, string title)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => { GetWindowThreadProcessId(h, out uint wp); var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            if (wp == pid && IsWindowVisible(h) && sb.ToString() == title) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static int Run(TextWriter o, string outDir, string fileType)
    {
        int tekla = Process.GetProcessesByName("TeklaStructures").First().Id;
        const string title = "Export drawings as DWG/DXF/DGN";
        o.WriteLine("export windows open: " + Windows(tekla, title).Count);
        string log = Path.Combine(Path.GetTempPath(), "teklamcp_macro_probe.log");
        if (File.Exists(log)) File.Delete(log);
        Directory.CreateDirectory(outDir);
        string macroDir = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        string modeling = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).First(Directory.Exists);
        string file = "TeklaMcp_DwgProbe3.cs";
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
    var ev = wpf.View(""DrawingExport.UIFeature.MainContentView"");
    T(""output dir"", () => ev.Find(""AID_DwgExport_OutputDirectoryTextBox"").As.TextBox.SetText(""" + esc(outDir) + @"""));
    if (!T(""file type via selector"", () => ev.Find(""AID_DwgExport_FileTypesComboBox"").As.Selector.Select(""" + esc(fileType) + @""")))
      T(""file type via ExportOptionsView combobox"", () => wpf.View(""DrawingExport.UIFeature.ExportOptionsView"").Find(""AID_DwgExport_FileTypesComboBox"").As.ComboBox.SetText(""" + esc(fileType) + @"""));
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
        foreach (var h in Windows(tekla, title)) PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(1500);
        o.WriteLine("export windows open after WM_CLOSE: " + Windows(tekla, title).Count);
        return 0;
    }
}
