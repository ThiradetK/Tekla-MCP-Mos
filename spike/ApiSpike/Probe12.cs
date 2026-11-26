// Phase 6 helper: run a short WPF macro body (T("label", () => ...) steps, variables wpf/dm) through RunMacro and print its log.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe12
{
    public static int Run(TextWriter o, string body)
    {
        string log = Path.Combine(Path.GetTempPath(), "teklamcp_macro_probe12.log");
        if (File.Exists(log)) File.Delete(log);
        string macroDir = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref macroDir);
        string modeling = macroDir.Split(';').Select(s => Path.Combine(s.Trim(), "modeling")).First(Directory.Exists);
        string file = "TeklaMcp_Probe12.cs";
        string src = "#pragma reference \"Tekla.Macros.Wpf.Runtime\"\n#pragma reference \"Tekla.Macros.Runtime\"\n" +
            "namespace UserMacros { public sealed class Macro {\n" +
            "static string L = @\"" + log + "\";\n" +
            "static void W(string s) { System.IO.File.AppendAllText(L, System.DateTime.Now.ToString(\"HH:mm:ss.fff\") + \" \" + s + System.Environment.NewLine); }\n" +
            "static bool T(string label, System.Action a) { try { a(); W(\"OK   \" + label); return true; } catch (System.Exception ex) { W(\"FAIL \" + label + \": \" + ex.Message.Replace(System.Environment.NewLine, \" \")); return false; } }\n" +
            "[Tekla.Macros.Runtime.MacroEntryPointAttribute()] public static void Run(Tekla.Macros.Runtime.IMacroRuntime runtime) {\n" +
            "var wpf = runtime.Get<Tekla.Macros.Wpf.Runtime.IWpfMacroHost>();\n" +
            "T(\"open document manager\", () => wpf.InvokeCommand(\"CommandRepository\", \"Drawing.DrawingList\"));\n" +
            "var dm = wpf.View(\"DocumentManager.MainWindow\");\n" + body + "\nW(\"END\");\n} } }\n";
        string path = Path.Combine(modeling, file);
        File.WriteAllText(path, src);
        try
        {
            o.WriteLine("RunMacro=" + Op.RunMacro(file));
            for (int i = 0; i < 400; i++) { Thread.Sleep(500); if (File.Exists(log) && File.ReadAllText(log).Contains(" END")) break; }
        }
        finally { File.Delete(path); }
        o.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "NO LOG");
        return 0;
    }
}
