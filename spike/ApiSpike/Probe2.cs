// Read-only probe for Phase 2: report property values/types per object kind, bulk report API, template folders.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tekla.Structures;
using TSM = Tekla.Structures.Model;

internal static class Probe2
{
    private static readonly string[] Props = { "WEIGHT", "WEIGHT_NET", "WEIGHT_GROSS", "LENGTH", "AREA", "VOLUME", "PROFILE", "MATERIAL", "NAME", "CLASS", "PART_POS", "ASSEMBLY_POS", "PHASE", "SIZE", "GRADE", "NUMBER", "MAIN_PART" };

    public static int Run(TextWriter o)
    {
        var model = new TSM.Model();
        if (!model.GetConnectionStatus()) { o.WriteLine("Not connected"); return 1; }
        var sel = model.GetModelObjectSelector();
        foreach (var kind in new[] { TSM.ModelObject.ModelObjectEnum.BEAM, TSM.ModelObject.ModelObjectEnum.CONTOURPLATE, TSM.ModelObject.ModelObjectEnum.REBARGROUP, TSM.ModelObject.ModelObjectEnum.REBAR_SET, TSM.ModelObject.ModelObjectEnum.ASSEMBLY, TSM.ModelObject.ModelObjectEnum.GRIDPLANE, TSM.ModelObject.ModelObjectEnum.POLYBEAM, TSM.ModelObject.ModelObjectEnum.BOLT_ARRAY, TSM.ModelObject.ModelObjectEnum.WELD })
        {
            var en = sel.GetAllObjectsWithType(kind);
            int n = en.GetSize();
            o.WriteLine("== " + kind + " count(GetSize)=" + n);
            if (!en.MoveNext() || en.Current == null) continue;
            var mo = en.Current;
            o.WriteLine("  type=" + mo.GetType().Name + " guid=" + mo.Identifier.GUID + " id=" + mo.Identifier.ID);
            foreach (var p in Props)
            {
                double d = 0; int i = 0; string s = null;
                bool bd = mo.GetReportProperty(p, ref d), bi = mo.GetReportProperty(p, ref i), bs = mo.GetReportProperty(p, ref s);
                if (bd || bi || bs) o.WriteLine("  " + p + ": double=" + (bd ? d.ToString("G10") : "-") + " int=" + (bi ? i.ToString() : "-") + " str=" + (bs ? "'" + s + "'" : "-"));
            }
            TSM.Phase ph; if (mo.GetPhase(out ph)) o.WriteLine("  phase=" + ph.PhaseNumber + " '" + ph.PhaseName + "'");
            if (mo is TSM.Part part) o.WriteLine("  part: profile=" + part.Profile.ProfileString + " material=" + part.Material.MaterialString + " name=" + part.Name + " class=" + part.Class);
            if (mo is TSM.GridPlane gp) o.WriteLine("  gridplane: label=" + gp.Label + " origin=" + gp.Plane.Origin + " ax=" + gp.Plane.AxisX + " ay=" + gp.Plane.AxisY);
            var bulk = model.GetReportPropertyDouble(new List<Identifier> { mo.Identifier }, "WEIGHT");
            o.WriteLine("  bulk WEIGHT -> " + string.Join(",", bulk) + " (List<double>)");
            var fetched = model.FetchModelObjects(new List<string> { mo.Identifier.GUID.ToString() }, false);
            o.WriteLine("  FetchModelObjects -> " + fetched.Count + " " + (fetched.Count > 0 ? fetched[0]?.GetType().Name : ""));
        }
        // RebarSet: where are the generated bars and their weight?
        var rsEnum = sel.GetAllObjectsWithType(TSM.ModelObject.ModelObjectEnum.REBAR_SET);
        int sets = 0, bars = 0; double w = 0; var barTypes = new Dictionary<string, int>();
        while (rsEnum.MoveNext() && sets < 1744)
        {
            if (!(rsEnum.Current is TSM.RebarSet rs)) continue;
            sets++;
            var re = rs.GetReinforcements();
            while (re.MoveNext())
            {
                var r = re.Current; if (r == null) continue;
                bars++; string k = r.GetType().Name; barTypes[k] = barTypes.TryGetValue(k, out var c) ? c + 1 : 1;
                double rw = 0; r.GetReportProperty("WEIGHT", ref rw); w += rw;
                if (bars == 1) { string sz = null, gr = null; r.GetReportProperty("SIZE", ref sz); r.GetReportProperty("GRADE", ref gr); int nb = 0; r.GetReportProperty("NUMBER", ref nb); o.WriteLine("  first set bar: type=" + k + " size=" + sz + " grade=" + gr + " number=" + nb + " weight=" + rw + " guid=" + r.Identifier.GUID); }
            }
        }
        o.WriteLine("RebarSets=" + sets + " generated reinforcements=" + bars + " types=" + string.Join(",", barTypes.Select(kv => kv.Key + ":" + kv.Value)) + " totalWeight=" + w.ToString("F1") + " kg");
        double rgW = 0; var rgEnum = sel.GetAllObjectsWithType(TSM.ModelObject.ModelObjectEnum.REBARGROUP);
        while (rgEnum.MoveNext()) { double x = 0; rgEnum.Current?.GetReportProperty("WEIGHT", ref x); rgW += x; }
        o.WriteLine("REBARGROUP (selector) totalWeight=" + rgW.ToString("F1") + " kg");
        var allR = sel.GetAllObjectsWithType(new[] { typeof(TSM.Reinforcement) }); int nR = 0; while (allR.MoveNext()) nR++;
        o.WriteLine("GetAllObjectsWithType(typeof(Reinforcement)) count=" + nR);
        foreach (var opt in new[] { "XS_TEMPLATE_DIRECTORY", "XS_TEMPLATE_DIRECTORY_SYSTEM", "XS_FIRM", "XS_PROJECT" })
        {
            string v = null; Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption(opt, ref v);
            o.WriteLine(opt + " = " + v);
        }
        var tp = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
        var mx = tp.TransformationMatrixToGlobal;
        o.WriteLine("workplane toGlobal row0=" + mx[0, 0] + "," + mx[0, 1] + "," + mx[0, 2] + " row3=" + mx[3, 0] + "," + mx[3, 1] + "," + mx[3, 2]);
        o.WriteLine("model path: " + model.GetInfo().ModelPath);
        return 0;
    }
}
