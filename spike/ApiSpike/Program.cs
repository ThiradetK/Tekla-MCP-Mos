// Phase 0 API spike. Not shipped.
// 1) Compiles against NuGet 2026.0.3 using the members planned for Phase 1-3 (ApiSurface is never called).
// 2) At runtime: resolves Tekla DLLs from the local install and checks the connection (read-only).
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Tekla.Structures.Catalogs;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;
using TSM = Tekla.Structures.Model;

internal static class Program
{
    private static string[] _probeDirs = new string[0];

    private static int Main(string[] args)
    {
        string bin = FindTeklaBin();
        Console.Error.WriteLine("Tekla bin: " + (bin ?? "<not found>"));
        if (bin == null) return 2;
        _probeDirs = new[] { bin, Path.Combine(bin, "Net48Runtime") };
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        if (args.Length >= 2 && args[0] == "options") return Options(args);
        if (args.Length >= 2 && args[0] == "profile") return ProfileProbe(args);
        if (args.Length == 1 && args[0] == "probe14")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe14.Run(realOut); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 3 && args[0] == "probe13")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe13.Run(realOut, args[1], args[2]); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "probe12")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe12.Run(realOut, File.ReadAllText(args[1])); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 3 && args[0] == "probe11")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe11.Run(realOut, args[1], args[2]); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 3 && args[0] == "probe10")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe10.Run(realOut, args[1], args[2]); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 4 && args[0] == "probe9")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe9.Run(realOut, args[1], args[2], args[3]); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 3 && args[0] == "probe8")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe8.Run(realOut, args[1], args[2]); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "probe7")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe7.Run(realOut, args[1]); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 1 && args[0] == "probe6")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe6.Run(realOut); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 1 && args[0] == "probe5")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe5.Run(realOut); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "probe4")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe4.Run(realOut, int.Parse(args[1])); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 1 && args[0] == "probe3")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe3.Run(realOut); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 1 && args[0] == "probe2")
        {
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe2.Run(realOut); } catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "probe")
        {
            // Mitigation test: keep raw stdout for our output, send Console.Out (Tekla noise) to stderr.
            var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            try { return Probe.Run(realOut, args[1]); }
            catch (Exception ex) { realOut.WriteLine("Error: " + ex); return 1; }
        }
        return Run();
    }

    // Kept in a separate method so Tekla types are only JIT-loaded after the resolver is attached.
    private static int Run()
    {
        try
        {
            var model = new TSM.Model();
            bool connected = model.GetConnectionStatus();
            Console.WriteLine("Connected: " + connected);
            if (connected)
            {
                ModelInfo info = model.GetInfo();
                Console.WriteLine("Model: " + info.ModelName + " | " + info.ModelPath);
            }
            Console.WriteLine("Version: " + Tekla.Structures.TeklaStructuresInfo.GetCurrentProgramVersion());
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.GetType().FullName + ": " + ex.Message);
            return 1;
        }
    }

    private static int ProfileProbe(string[] args)
    {
        var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(Console.Error);
        if (!new TSM.Model().GetConnectionStatus()) { realOut.WriteLine("Not connected"); return 1; }
        var pe = new CatalogHandler().GetParametricProfileItems();
        var prefixes = new System.Collections.Generic.List<string>();
        while (pe.MoveNext()) if (pe.Current is ParametricProfileItem pp) prefixes.Add(pp.ProfilePrefix + "(" + pp.ProfileItemType + ")");
        realOut.WriteLine("parametric prefixes (" + prefixes.Count + "): " + string.Join(" ", prefixes));
        for (int k = 1; k < args.Length; k++)
            realOut.WriteLine("'" + args[k] + "' parse='" + TSM.Profile.ParseProfileString(args[k]) + "' format='" + TSM.Profile.FormatProfileString(args[k]) + "'");
        return 0;
    }

    private static int Options(string[] args)
    {
        var realOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(Console.Error);
        if (!new TSM.Model().GetConnectionStatus()) { realOut.WriteLine("Not connected"); return 1; }
        for (int k = 1; k < args.Length; k++)
        {
            string v = null;
            bool ok = Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption(args[k], ref v);
            realOut.WriteLine(args[k] + " ok=" + ok + " value=" + v);
        }
        return 0;
    }

    private static string FindTeklaBin()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Trimble\Tekla Structures\2026.0\setup"))
        {
            var main = key?.GetValue("MainDir") as string;
            var ver = key?.GetValue("TSVersionDir") as string;
            if (main == null || ver == null) return null;
            string bin = Path.Combine(main, ver, "bin");
            return File.Exists(Path.Combine(bin, "Tekla.Structures.Model.dll")) ? bin : null;
        }
    }

    private static System.Reflection.Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        foreach (var dir in _probeDirs)
        {
            string p = Path.Combine(dir, name + ".dll");
            if (File.Exists(p)) { Console.Error.WriteLine("resolve " + name + " -> " + p); return System.Reflection.Assembly.LoadFrom(p); }
        }
        return null;
    }

    // Compile-time check only. Never executed.
    private static void ApiSurface(TSM.Model m)
    {
        m.CommitChanges(); m.CommitChanges("x");
        ProjectInfo pi = m.GetProjectInfo();
        PhaseCollection phases = m.GetPhases();
        TSM.ModelObjectSelector sel = m.GetModelObjectSelector();
        ModelObjectEnumerator e1 = sel.GetAllObjects();
        ModelObjectEnumerator e2 = sel.GetAllObjectsWithType(TSM.ModelObject.ModelObjectEnum.BEAM);
        ModelObjectEnumerator e3 = sel.GetObjectsByBoundingBox(new Point(0, 0, 0), new Point(1, 1, 1));
        ModelObjectEnumerator e4 = sel.GetObjectsByFilterName("standard");
        ModelObjectEnumerator e5 = new TSM.UI.ModelObjectSelector().GetSelectedObjects();
        new TSM.UI.ModelObjectSelector().Select(new ArrayList());
        TSM.ModelObject mo = m.SelectModelObject(new Tekla.Structures.Identifier(Guid.Empty));
        Guid g = mo.Identifier.GUID; int id = mo.Identifier.ID;
        string s = null; double d = 0; int i = 0; Hashtable h = new Hashtable();
        mo.GetReportProperty("WEIGHT", ref d); mo.GetReportProperty("NAME", ref s); mo.GetReportProperty("ID", ref i);
        mo.GetUserProperty("X", ref s); mo.SetUserProperty("X", "y");
        mo.GetAllUserProperties(ref h);
        mo.GetAllReportProperties(new ArrayList(), new ArrayList(), new ArrayList(), ref h);

        new ModelHandler().Save("comment", "user");
        bool saved = new ModelHandler().IsModelSaved();

        var beam = new Beam(new Point(0, 0, 0), new Point(1000, 0, 0));
        beam.Profile.ProfileString = "H200*100*5.5*8"; beam.Material.MaterialString = "SS400";
        beam.Class = "1"; beam.Name = "BEAM"; beam.Finish = ""; beam.Insert(); beam.Modify(); beam.Delete();
        var plate = new ContourPlate(); plate.AddContourPoint(new ContourPoint(new Point(0, 0, 0), null));
        var bolts = new BoltArray(); bolts.AddBoltDistX(50); bolts.AddBoltDistY(0);
        bolts.BoltStandard = "8.8XOX"; bolts.BoltSize = 20; bolts.PartToBeBolted = beam; bolts.PartToBoltTo = beam;
        var weld = new Weld { MainObject = beam, SecondaryObject = beam, SizeAbove = 6, TypeAbove = BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET };
        var rebar = new SingleRebar { Father = beam, Size = "12", Grade = "SD40" }; Polygon poly = rebar.Polygon;
        var rg = new RebarGroup { Size = "12", Grade = "SD40", StartPoint = new Point(), EndPoint = new Point() }; ArrayList rgp = rg.Polygons; ArrayList sp = rg.Spacings;
        var grid = new Grid { CoordinateX = "0 6000", CoordinateY = "0", CoordinateZ = "0", LabelX = "A B", LabelY = "1", LabelZ = "+0" };
        TSM.Assembly a = beam.GetAssembly(); a.Add(beam); a.SetMainPart(beam); ModelObject mp = a.GetMainPart();
        ArrayList sec = a.GetSecondaries(); ArrayList subs = a.GetSubAssemblies();
        ModelObjectEnumerator pb = beam.GetBolts(); ModelObjectEnumerator pw = beam.GetWelds(); Solid sol = beam.GetSolid();

        Operation.MoveObject(beam, new Vector(100, 0, 0)); ModelObject copy = Operation.CopyObject(beam, new Vector(100, 0, 0));
        Operation.RunMacro("x.cs"); Operation.DisplayPrompt("x"); bool running = Operation.IsMacroRunning();
        Operation.CreateReportFromAll("template", "out.xsr", "title", "t1", "t2");
        WorkPlaneHandler wph = m.GetWorkPlaneHandler(); TransformationPlane tp = wph.GetCurrentTransformationPlane(); wph.SetCurrentTransformationPlane(tp);

        var cat = new CatalogHandler(); bool cc = cat.GetConnectionStatus();
        ProfileItemEnumerator pe = cat.GetProfileItems(); MaterialItemEnumerator me = cat.GetMaterialItems();
        BoltItemEnumerator be = cat.GetBoltItems(); RebarItemEnumerator re = cat.GetRebarItems();

        Tekla.Structures.Drawing.DrawingEnumerator de = new Tekla.Structures.Drawing.DrawingHandler().GetDrawings();
        string ver = Tekla.Structures.TeklaStructuresInfo.GetCurrentProgramVersion();
        bool b = false; Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_X", ref b);
    }
}
