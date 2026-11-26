// Phase 6 spike (read-only): what Drawing.GetPlotFileName / GetPlotFileNameExt return for each drawing.
using System;
using System.IO;
using TSD = Tekla.Structures.Drawing;

internal static class Probe14
{
    public static int Run(TextWriter o)
    {
        var dh = new TSD.DrawingHandler();
        var en = dh.GetDrawings();
        while (en.MoveNext())
        {
            var d = en.Current;
            string a = "", b = "", c = "";
            try { a = d.GetPlotFileName(false); } catch (Exception ex) { a = "ERR " + ex.Message; }
            try { b = d.GetPlotFileName(true); } catch (Exception ex) { b = "ERR " + ex.Message; }
            foreach (TSD.IncludeRevisionMarkEnum v in Enum.GetValues(typeof(TSD.IncludeRevisionMarkEnum)))
                try { c += v + "='" + d.GetPlotFileNameExt(v) + "' "; } catch (Exception ex) { c += v + "=ERR " + ex.Message + " "; }
            o.WriteLine(d.GetType().Name + " " + d.Mark + " name='" + d.Name + "' -> GetPlotFileName(false)='" + a + "' (true)='" + b + "' Ext: " + c);
        }
        o.WriteLine("IncludeRevisionMarkEnum: " + string.Join(", ", Enum.GetNames(typeof(TSD.IncludeRevisionMarkEnum))));
        return 0;
    }
}
