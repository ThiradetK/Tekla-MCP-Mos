// Read-only probe: catalog contents + model object counts. No Insert/Modify/Delete/Save/Commit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tekla.Structures.Catalogs;
using TSM = Tekla.Structures.Model;

internal static class Probe
{
    public static int Run(TextWriter outw, string expectedModelPath)
    {
        var model = new TSM.Model();
        if (!model.GetConnectionStatus()) { outw.WriteLine("Not connected"); return 1; }
        var info = model.GetInfo();
        string actual = Path.GetFullPath(info.ModelPath).TrimEnd('\\');
        string expected = Path.GetFullPath(expectedModelPath).TrimEnd('\\');
        outw.WriteLine("Model: " + info.ModelName + " | " + actual);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        { outw.WriteLine("ABORT: open model path does not match expected " + expected); return 3; }

        var cat = new CatalogHandler();
        outw.WriteLine("Catalog connected: " + cat.GetConnectionStatus());

        var profiles = new List<string>();
        var pe = cat.GetLibraryProfileItems();
        while (pe.MoveNext()) { var lp = pe.Current as LibraryProfileItem; if (lp != null) profiles.Add(lp.ProfileName); }
        Report(outw, "Profiles", profiles, new[] { "H", "I", "C", "L", "PIPE", "RHS", "SHS", "WF" });

        var mats = new List<string>();
        var me = cat.GetMaterialItems();
        while (me.MoveNext()) mats.Add(me.Current.MaterialName + " [" + me.Current.Type + "]");
        Report(outw, "Materials", mats, null);
        outw.WriteLine("  ALL: " + string.Join(", ", mats.Select(m => m.Substring(0, m.IndexOf(" [")))));
        outw.WriteLine("  Profiles matching H/WF/C/LA (JIS-style) sample: " + string.Join(", ", profiles.Where(p => p.StartsWith("H") || p.StartsWith("C") || p.StartsWith("LA") || p.StartsWith("[")).Take(20)));

        var bolts = new List<string>();
        var be = cat.GetBoltItems();
        while (be.MoveNext()) bolts.Add(be.Current.Standard + " M" + be.Current.Size);
        Report(outw, "Bolts (standard+size)", bolts, null);
        outw.WriteLine("  Bolt standards: " + string.Join(", ", bolts.Select(b => b.Substring(0, b.LastIndexOf(" M"))).Distinct()));

        var rebars = new List<string>();
        var re = cat.GetRebarItems();
        while (re.MoveNext()) rebars.Add(re.Current.Grade + " / " + re.Current.Size + " / " + re.Current.Usage);
        Report(outw, "Rebars (grade/size/usage)", rebars, null);
        outw.WriteLine("  Rebar grades: " + string.Join(", ", rebars.Select(r => r.Split('/')[0].Trim()).Distinct()));

        var counts = new SortedDictionary<string, int>();
        var all = model.GetModelObjectSelector().GetAllObjects();
        int total = 0;
        while (all.MoveNext()) { var o = all.Current; if (o == null) continue; total++; string k = o.GetType().Name; counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1; }
        outw.WriteLine("Model objects total: " + total);
        foreach (var kv in counts) outw.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }

    private static void Report(TextWriter outw, string title, List<string> items, string[] prefixes)
    {
        outw.WriteLine(title + ": " + items.Count);
        outw.WriteLine("  sample: " + string.Join(" | ", items.Take(25)));
        if (prefixes != null)
        {
            var byPrefix = items.GroupBy(p => new string(p.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant())
                                .OrderByDescending(g => g.Count()).Take(20)
                                .Select(g => g.Key + "=" + g.Count());
            outw.WriteLine("  top prefixes: " + string.Join(", ", byPrefix));
        }
    }
}
