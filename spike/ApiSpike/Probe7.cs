// Phase 6 spike (writes files only; the model is not changed): IFC4 export, PDF print of a drawing, NC (DSTV) files.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TSD = Tekla.Structures.Drawing;
using TSM = Tekla.Structures.Model;
using Op = Tekla.Structures.Model.Operations.Operation;

internal static class Probe7
{
    public static int Run(TextWriter o, string outDir)
    {
        var m = new TSM.Model();
        if (!m.GetConnectionStatus()) { o.WriteLine("not connected"); return 1; }
        Directory.CreateDirectory(outDir);
        var sw = Stopwatch.StartNew();
        if (Environment.GetEnvironmentVariable("PROBE7_PDF_ONLY") == "1") return Pdf(o, outDir);
        if (Environment.GetEnvironmentVariable("PROBE7_NC_ONLY") == "1")
        {
            foreach (var s in new[] { "DSTV for Profile", "DSTV for Plate", "DSTV for Angle" })
            {
                string ncDir = Path.Combine(outDir, "nc_" + s.Replace(" ", "_"));
                Directory.CreateDirectory(ncDir);
                var t = Stopwatch.StartNew();
                bool ok = Op.CreateNCFilesFromAll(s, ncDir);
                o.WriteLine("NC '" + s + "' ok=" + ok + " (" + t.ElapsedMilliseconds + " ms) files=" + Directory.GetFiles(ncDir, "*", SearchOption.AllDirectories).Length);
            }
            return 0;
        }

        // IFC4, all objects, no extra property sets
        string ifc = Path.Combine(outDir, "probe_all.ifc");
        var flags = new Op.IFCExportFlags { IsBoltsEnabled = true, IsWeldsEnabled = false, IsRebarsEnabled = true, IsGridsEnabled = true, IsAssembliesEnabled = true };
        try
        {
            bool ok = Op.CreateIFC4ExportFromAll(ifc, Op.IFCExportViewTypeEnum.REFERENCE_VIEW, Op.IFCExportVersionEnum.IFC4, new List<string>(), new List<string>(), Op.ExportBasePoint.GLOBAL, "__Name__", "ByObjectClass", flags, string.Empty);
            o.WriteLine("IFC4 ok=" + ok + " (" + sw.ElapsedMilliseconds + " ms) exists=" + File.Exists(ifc) + " size=" + (File.Exists(ifc) ? new FileInfo(ifc).Length : 0));
        }
        catch (Exception ex) { o.WriteLine("IFC4 threw: " + ex.GetBaseException().Message); }

        // PDF of the first drawing
        sw.Restart();
        var dh = new TSD.DrawingHandler();
        var en = dh.GetDrawings(); TSD.Drawing d = null; while (en.MoveNext()) { d = en.Current; if (d is TSD.GADrawing) break; }
        if (d != null)
        {
            string pdf = Path.Combine(outDir, "probe_drawing.pdf");
            var pa = new TSD.DPMPrinterAttributes { PrinterName = "Microsoft Print to PDF", OutputType = TSD.DotPrintOutputType.PDF, OutputFileName = pdf, OpenFileWhenFinished = false, NumberOfCopies = 1 };
            try
            {
                bool ok = dh.PrintDrawing(d, pa);
                System.Threading.Thread.Sleep(3000);
                o.WriteLine("PDF " + d.Mark + " ok=" + ok + " (" + sw.ElapsedMilliseconds + " ms) exists=" + File.Exists(pdf) + " size=" + (File.Exists(pdf) ? new FileInfo(pdf).Length : 0));
                o.WriteLine("  files in out: " + string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName)));
            }
            catch (Exception ex) { o.WriteLine("PDF threw: " + ex.GetBaseException().Message); }
        }

        // NC, all parts, DSTV for profiles
        sw.Restart();
        string nc = Path.Combine(outDir, "nc");
        Directory.CreateDirectory(nc);
        try
        {
            bool ok = Op.CreateNCFilesFromAll("DSTV for profiles", nc);
            o.WriteLine("NC profiles ok=" + ok + " (" + sw.ElapsedMilliseconds + " ms) files=" + Directory.GetFiles(nc, "*", SearchOption.AllDirectories).Length);
            ok = Op.CreateNCFilesFromAll("DSTV for plates", nc);
            o.WriteLine("NC plates ok=" + ok + " files=" + Directory.GetFiles(nc, "*", SearchOption.AllDirectories).Length);
            try { Op.CreateNCFilesFromAll("NO_SUCH_SETTINGS", nc); o.WriteLine("NC bad settings: no exception"); } catch (Exception ex) { o.WriteLine("NC bad settings threw " + ex.GetType().Name + ": " + ex.Message); }
        }
        catch (Exception ex) { o.WriteLine("NC threw: " + ex.GetBaseException().Message); }
        return 0;
    }

    private static int Pdf(TextWriter o, string outDir)
    {
        var dh = new TSD.DrawingHandler();
        var en = dh.GetDrawings(); TSD.Drawing d = null; while (en.MoveNext()) { d = en.Current; if (d is TSD.CastUnitDrawing) break; }
        o.WriteLine("drawing " + d.GetType().Name + " " + d.Mark);
        var variants = new (string, Func<string, bool>)[] {
            ("open first, 3-arg", f => { dh.SetActiveDrawing(d, false); try { return dh.PrintDrawing(d, new TSD.DPMPrinterAttributes { OutputType = TSD.DotPrintOutputType.PDF, OpenFileWhenFinished = false }, f); } finally { dh.CloseActiveDrawing(false); } }),
            ("open visible first, 3-arg", f => { dh.SetActiveDrawing(d, true); try { return dh.PrintDrawing(d, new TSD.DPMPrinterAttributes { OutputType = TSD.DotPrintOutputType.PDF, OpenFileWhenFinished = false }, f); } finally { dh.CloseActiveDrawing(false); } }),
            ("3-arg, no printer name", f => dh.PrintDrawing(d, new TSD.DPMPrinterAttributes { OutputType = TSD.DotPrintOutputType.PDF, OpenFileWhenFinished = false }, f)),
            ("2-arg, OutputFileName, no printer name", f => dh.PrintDrawing(d, new TSD.DPMPrinterAttributes { OutputType = TSD.DotPrintOutputType.PDF, OutputFileName = f, OpenFileWhenFinished = false })),
        };
        int i = 0;
        foreach (var (label, run) in variants)
        {
            string f = Path.Combine(Environment.GetEnvironmentVariable("PROBE7_PDF_DIR") ?? outDir, "probe_pdf_" + (++i) + ".pdf");
            var sw = Stopwatch.StartNew();
            try
            {
                bool ok = run(f);
                long t = sw.ElapsedMilliseconds;
                for (int k = 0; k < 20 && !File.Exists(f); k++) System.Threading.Thread.Sleep(500);
                o.WriteLine(label + ": ok=" + ok + " (" + t + " ms) exists=" + File.Exists(f) + " size=" + (File.Exists(f) ? new FileInfo(f).Length : 0));
            }
            catch (Exception ex) { o.WriteLine(label + " threw " + ex.GetType().Name + ": " + ex.Message); }
        }
        o.WriteLine("files: " + string.Join(", ", Directory.GetFiles(outDir, "*.pdf").Select(Path.GetFileName)));
        return 0;
    }
}


