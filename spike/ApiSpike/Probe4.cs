// Phase 4 spike (read-only on the model): clash check events from an external process; numbering/assembly-mark checks.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TSM = Tekla.Structures.Model;

internal static class Probe4
{
    public static int Run(TextWriter o, int maxParts)
    {
        var m = new TSM.Model();
        if (!m.GetConnectionStatus()) { o.WriteLine("not connected"); return 1; }
        var sw = Stopwatch.StartNew();

        // --- numbering
        o.WriteLine("IsNumberingUpToDateAll=" + TSM.Operations.Operation.IsNumberingUpToDateAll() + " (" + sw.ElapsedMilliseconds + " ms)");
        var parts = new List<TSM.Part>();
        var en = m.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSM.Part) });
        while (en.MoveNext()) if (en.Current is TSM.Part p) parts.Add(p);
        o.WriteLine("parts=" + parts.Count + " (" + sw.ElapsedMilliseconds + " ms)");
        sw.Restart(); int notUpToDate = 0;
        foreach (var p in parts) if (!TSM.Operations.Operation.IsNumberingUpToDate(p)) notUpToDate++;
        o.WriteLine("per-part IsNumberingUpToDate false=" + notUpToDate + " (" + sw.ElapsedMilliseconds + " ms)");
        sw.Restart(); int noAsm = 0, noPart = 0; var samples = new List<string>();
        foreach (var p in parts)
        {
            string ap = "", pp = "";
            p.GetReportProperty("ASSEMBLY_POS", ref ap); p.GetReportProperty("PART_POS", ref pp);
            if (string.IsNullOrWhiteSpace(ap) || ap.Contains("?")) { noAsm++; if (samples.Count < 3) samples.Add(p.Identifier.GUID + " asm='" + ap + "' part='" + pp + "'"); }
            if (string.IsNullOrWhiteSpace(pp) || pp.Contains("?")) noPart++;
        }
        o.WriteLine("per-part marks: no assembly mark=" + noAsm + " no part mark=" + noPart + " (" + sw.ElapsedMilliseconds + " ms) " + string.Join(" | ", samples));
        sw.Restart(); int asmCount = 0, emptyAsm = 0;
        var ae = m.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSM.Assembly) });
        while (ae.MoveNext()) if (ae.Current is TSM.Assembly a) { asmCount++; if (a.GetMainPart() == null) emptyAsm++; }
        o.WriteLine("assemblies=" + asmCount + " without main part=" + emptyAsm + " (" + sw.ElapsedMilliseconds + " ms)");

        // --- read-only: do any part pairs actually intersect? (bounding-box neighbours + GetIntersectionBoundingBoxes)
        sw.Restart(); int pairs = 0, hits = 0; var hitSamples = new List<string>();
        var cch = m.GetClashCheckHandler();
        foreach (var p in parts.Take(150))
        {
            var bb = p.GetSolid(); var near = m.GetModelObjectSelector().GetObjectsByBoundingBox(bb.MinimumPoint, bb.MaximumPoint);
            while (near.MoveNext())
            {
                if (!(near.Current is TSM.Part q) || q.Identifier.ID <= p.Identifier.ID) continue;
                pairs++;
                var boxes = cch.GetIntersectionBoundingBoxes(p.Identifier, q.Identifier);
                if (boxes != null && boxes.Count > 0) { hits++; if (hitSamples.Count < 3) hitSamples.Add(p.Name + "(" + p.Identifier.ID + ") x " + q.Name + "(" + q.Identifier.ID + ") boxes=" + boxes.Count); }
            }
        }
        o.WriteLine("bbox-neighbour pairs=" + pairs + " intersecting=" + hits + " (" + sw.ElapsedMilliseconds + " ms) " + string.Join(" | ", hitSamples));

        // --- clash check on up to maxParts parts, previous selection restored
        var ui = new TSM.UI.ModelObjectSelector();
        var before = new ArrayList(); var se = ui.GetSelectedObjects(); while (se.MoveNext()) before.Add(se.Current);
        var subset = new ArrayList(parts.Take(maxParts).ToList());
        var clashes = new List<TSM.ClashCheckData>(); int doneCount = -1;
        var done = new ManualResetEventSlim(false);
        var ev = new TSM.Events();
        ev.ClashDetected += d => { lock (clashes) clashes.Add(d); };
        ev.ClashCheckDone += n => { doneCount = n; done.Set(); };
        ev.Register();
        try
        {
            o.WriteLine("select " + subset.Count + " parts: " + ui.Select(subset));
            var runs = new (string, Func<bool>)[] {
                ("RunClashCheck()", () => m.GetClashCheckHandler().RunClashCheck()),
                ("WithOptions(4 args, min 10)", () => m.GetClashCheckHandler().RunClashCheckWithOptions(false, false, 10.0, true)),
                ("WithOptions(5 args, min 0)", () => m.GetClashCheckHandler().RunClashCheckWithOptions(false, false, false, 0.0, true)),
            };
            foreach (var (label, run) in runs)
            {
                lock (clashes) clashes.Clear(); done.Reset(); doneCount = -1;
                sw.Restart();
                bool started = run();
                bool finished = done.Wait(TimeSpan.FromMinutes(5));
                long t = sw.ElapsedMilliseconds;
                Thread.Sleep(5000);
                o.WriteLine(label + ": started=" + started + " done=" + finished + " reported=" + doneCount + " received=" + clashes.Count + " (" + t + " ms; +5 s later " + clashes.Count + ")");
            }
            foreach (var c in clashes.Take(5))
                o.WriteLine("  " + c.Type + " overlap=" + c.Overlap + " " + (c.Object1 == null ? "null" : c.Object1.GetType().Name + " " + c.Object1.Identifier.GUID) + " / " + (c.Object2 == null ? "null" : c.Object2.GetType().Name + " " + c.Object2.Identifier.GUID));
            o.WriteLine("types: " + string.Join(", ", clashes.GroupBy(c => c.Type).Select(g => g.Key + "=" + g.Count())));
        }
        finally
        {
            ev.UnRegister();
            o.WriteLine("restore selection (" + before.Count + "): " + ui.Select(before));
        }
        return 0;
    }
}
