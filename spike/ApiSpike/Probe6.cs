// Phase 5 extra check (read-only): list the text objects on the sheet of the drawing that is open in Tekla.
using System.IO;
using TSD = Tekla.Structures.Drawing;

internal static class Probe6
{
    public static int Run(TextWriter o)
    {
        var dh = new TSD.DrawingHandler();
        if (!dh.GetConnectionStatus() || !dh.IsAnyDrawingOpen()) { o.WriteLine("NO_OPEN_DRAWING"); return 2; }
        var d = dh.GetActiveDrawing();
        o.WriteLine("ACTIVE " + d.Mark);
        var en = d.GetSheet().GetAllObjects(typeof(TSD.Text));
        while (en.MoveNext()) if (en.Current is TSD.Text t) o.WriteLine("TEXT " + t.TextString + " @ " + t.InsertionPoint);
        return 0;
    }
}
