// Phase 3 diagnostic: assembly add + rebar group RadiusValues. Creates MCP_PROBE objects at x>=200000 mm, deletes them at the end.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

internal static class Probe3
{
    public static int Run(TextWriter o)
    {
        var m = new TSM.Model();
        if (!m.GetConnectionStatus()) { o.WriteLine("not connected"); return 1; }
        var made = new List<TSM.ModelObject>();
        TSM.Beam B(double x0, double y0, double z0, double x1, double y1, double z1, string prof, string mat)
        {
            var b = new TSM.Beam(new Point(x0, y0, z0), new Point(x1, y1, z1)) { Name = "MCP_PROBE" };
            b.Profile.ProfileString = prof; b.Material.MaterialString = mat;
            o.WriteLine("  insert beam " + b.Insert()); made.Add(b); return b;
        }
        try
        {
            // A: Add(ArrayList)
            var b1 = B(200000, 0, 0, 206000, 0, 0, "WF400X200X8X13", "SS400");
            var b2 = B(200000, 0, 0, 200000, 0, 3000, "WF400X200X8X13", "SS400");
            var a = b1.GetAssembly();
            o.WriteLine("A asm id=" + a.Identifier.ID);
            o.WriteLine("A Add(ArrayList)=" + a.Add(new ArrayList { b2 }) + " Modify=" + a.Modify()); m.CommitChanges();
            o.WriteLine("A secondaries same=" + a.GetSecondaries().Count + " fresh=" + b1.GetAssembly().GetSecondaries().Count + " b2.asm main=" + ((TSM.Part)b2.GetAssembly().GetMainPart()).Identifier.ID + " b1=" + b1.Identifier.ID);

            // B: Add(IAssemblable)
            var b3 = B(210000, 0, 0, 216000, 0, 0, "WF400X200X8X13", "SS400");
            var b4 = B(210000, 0, 0, 210000, 0, 3000, "WF400X200X8X13", "SS400");
            var a2 = b3.GetAssembly();
            o.WriteLine("B Add(single)=" + a2.Add(b4) + " Modify=" + a2.Modify()); m.CommitChanges();
            o.WriteLine("B secondaries fresh=" + b3.GetAssembly().GetSecondaries().Count);

            // B2: Add(single) then Modify on a freshly selected assembly
            var b5 = B(230000, 0, 0, 236000, 0, 0, "WF400X200X8X13", "SS400");
            var b6 = B(230000, 0, 0, 230000, 0, 3000, "WF400X200X8X13", "SS400");
            var a3 = b5.GetAssembly(); o.WriteLine("B2 select=" + a3.Select());
            o.WriteLine("B2 Add(single)=" + a3.Add(b6) + " Modify=" + a3.Modify()); m.CommitChanges();
            o.WriteLine("B2 secondaries fresh=" + b5.GetAssembly().GetSecondaries().Count);

            // C: rebar group
            var c = B(220000, 0, 0, 226000, 0, 0, "500*250", "C40"); m.CommitChanges();
            TSM.RebarGroup G()
            {
                var poly = new TSM.Polygon(); poly.Points.Add(new Point(220050, 0, -200)); poly.Points.Add(new Point(220050, 0, 200));
                var g = new TSM.RebarGroup { StartPoint = new Point(220050, 0, 0), EndPoint = new Point(225950, 0, 0), Size = "9", Grade = "SR24", Father = c, Class = 1, Name = "MCP_PROBE",
                    SpacingType = TSM.BaseRebarGroup.RebarGroupSpacingTypeEnum.SPACING_TYPE_TARGET_SPACE };
                g.Polygons.Add(poly); g.Spacings.Add(200.0); g.OnPlaneOffsets = new ArrayList { 0.0 };
                return g;
            }
            var g1 = G(); o.WriteLine("C RadiusValues default=" + (g1.RadiusValues == null ? "null" : g1.RadiusValues.Count.ToString()));
            try { o.WriteLine("C insert without radius=" + g1.Insert()); } catch (Exception ex) { o.WriteLine("C without radius threw: " + ex.GetBaseException().Message); }
            Action<TSM.RebarGroup> R = g => { g.RadiusValues = new ArrayList { 20.0 }; g.StartHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; g.EndHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; };
            var variants = new (string, Action<TSM.RebarGroup>)[] {
                ("SD40 16", g => { R(g); g.Grade = "SD40"; g.Size = "16"; }),
                ("2 polys, no start/end", g => { R(g); g.StartPoint = null; g.EndPoint = null; var p2 = new TSM.Polygon(); p2.Points.Add(new Point(225950, 0, -200)); p2.Points.Add(new Point(225950, 0, 200)); g.Polygons.Add(p2); }),
                ("3-pt polygon", g => { R(g); g.Polygons.Clear(); var p3 = new TSM.Polygon(); p3.Points.Add(new Point(220050, -80, -200)); p3.Points.Add(new Point(220050, -80, 200)); p3.Points.Add(new Point(220050, 80, 200)); g.Polygons.Add(p3); g.OnPlaneOffsets = new ArrayList { 0.0, 0.0 }; }),
                ("start/end on polygon plane", g => { R(g); g.StartPoint = new Point(220050, 0, -200); g.EndPoint = new Point(225950, 0, -200); }),
                ("spacing exact", g => { R(g); g.SpacingType = TSM.BaseRebarGroup.RebarGroupSpacingTypeEnum.SPACING_TYPE_EXACT_SPACINGS; }),
                ("radius only", g => { g.RadiusValues = new ArrayList { 20.0 }; }),
                ("radius+hooks NO_HOOK", g => { g.RadiusValues = new ArrayList { 20.0 }; g.StartHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; g.EndHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; }),
                ("radius+hooks+exclude none", g => { g.RadiusValues = new ArrayList { 20.0 }; g.StartHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; g.EndHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; g.ExcludeType = TSM.BaseRebarGroup.ExcludeTypeEnum.EXCLUDE_TYPE_NONE; }),
                ("two polygons (docs style)", g => { g.RadiusValues = new ArrayList { 20.0 }; g.StartHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK; g.EndHook.Shape = TSM.RebarHookData.RebarHookShapeEnum.NO_HOOK;
                    var p2 = new TSM.Polygon(); p2.Points.Add(new Point(225950, 0, -200)); p2.Points.Add(new Point(225950, 0, 200)); g.Polygons.Add(p2); g.StartPoint = new Point(); g.EndPoint = new Point(); }),
            };
            foreach (var (label, set) in variants)
            {
                var gv = G(); set(gv);
                try { bool ok = gv.Insert(); o.WriteLine("C [" + label + "] insert=" + ok + (ok ? " bars=" + gv.GetNumberOfRebars() : "")); if (ok) made.Add(gv); }
                catch (Exception ex) { o.WriteLine("C [" + label + "] threw: " + ex.GetBaseException().Message); }
            }
            m.CommitChanges();
            // D: plate welded to a column, then added to another beam's assembly
            var col = B(240000, 0, 0, 240000, 0, 3000, "WF400X200X8X13", "SS400");
            var bm = B(240000, 0, 3000, 246000, 0, 3000, "WF400X200X8X13", "SS400");
            var pl = new TSM.ContourPlate { Name = "MCP_PROBE" }; pl.Profile.ProfileString = "PL20"; pl.Material.MaterialString = "SS400";
            foreach (var q in new[] { new Point(240000, -300, 3500), new Point(240600, -300, 3500), new Point(240600, 300, 3500), new Point(240000, 300, 3500) }) pl.AddContourPoint(new TSM.ContourPoint(q, null));
            o.WriteLine("  insert plate " + pl.Insert()); made.Add(pl);
            var w = new TSM.Weld { MainObject = col, SecondaryObject = pl, SizeAbove = 6, TypeAbove = TSM.BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET };
            o.WriteLine("  insert weld " + w.Insert()); m.CommitChanges();
            o.WriteLine("D plate asm main=" + ((TSM.Part)pl.GetAssembly().GetMainPart()).Identifier.ID + " col=" + col.Identifier.ID + " col secondaries=" + col.GetAssembly().GetSecondaries().Count);
            var ab = bm.GetAssembly();
            o.WriteLine("D Add(plate)=" + ab.Add(new ArrayList { pl }) + " Modify=" + ab.Modify()); m.CommitChanges();
            o.WriteLine("D beam secondaries=" + bm.GetAssembly().GetSecondaries().Count + " plate asm main=" + ((TSM.Part)pl.GetAssembly().GetMainPart()).Identifier.ID);
            // E: test sequence: column+plate bolted and welded, beam separate, then add plate to beam asm with Name set
            var col2 = B(250000, 0, 0, 250000, 0, 3000, "WF400X200X8X13", "SS400");
            var bm2 = B(250000, 0, 3000, 256000, 0, 3000, "WF400X200X8X13", "SS400");
            var pl2 = new TSM.ContourPlate { Name = "MCP_PROBE" }; pl2.Profile.ProfileString = "PL20"; pl2.Material.MaterialString = "SS400";
            foreach (var q in new[] { new Point(250000, -300, 3500), new Point(250600, -300, 3500), new Point(250600, 300, 3500), new Point(250000, 300, 3500) }) pl2.AddContourPoint(new TSM.ContourPoint(q, null));
            pl2.Insert(); made.Add(pl2);
            var bg = new TSM.BoltArray { PartToBoltTo = col2, PartToBeBolted = pl2, FirstPosition = new Point(250100, 0, 3500), SecondPosition = new Point(250500, 0, 3500), BoltStandard = "8.8XOX", BoltSize = 20 };
            bg.AddBoltDistX(100); bg.AddBoltDistX(100); bg.AddBoltDistY(150);
            o.WriteLine("  insert bolts " + bg.Insert());
            var w2 = new TSM.Weld { MainObject = col2, SecondaryObject = pl2, SizeAbove = 6, TypeAbove = TSM.BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET };
            o.WriteLine("  insert weld " + w2.Insert()); m.CommitChanges();
            o.WriteLine("E before: plate asm main=" + ((TSM.Part)pl2.GetAssembly().GetMainPart()).Identifier.ID + " col=" + col2.Identifier.ID + " beam=" + bm2.Identifier.ID);
            var ae = bm2.GetAssembly();
            o.WriteLine("E Add=" + ae.Add(new ArrayList { pl2 })); ae.Name = "MCP_PROBE_ASM"; o.WriteLine("E Modify=" + ae.Modify()); m.CommitChanges();
            o.WriteLine("E beam secondaries=" + bm2.GetAssembly().GetSecondaries().Count + " plate asm main=" + ((TSM.Part)pl2.GetAssembly().GetMainPart()).Identifier.ID);
            var ae2 = bm2.GetAssembly(); o.WriteLine("E2 retry without Name: Add=" + ae2.Add(new ArrayList { pl2 }) + " Modify=" + ae2.Modify()); m.CommitChanges();
            o.WriteLine("E2 beam secondaries=" + bm2.GetAssembly().GetSecondaries().Count);
            // F: same as A but parts fetched by GUID via FetchModelObjects (the tool's path)
            var f1 = B(260000, 0, 0, 266000, 0, 0, "WF400X200X8X13", "SS400");
            var f2 = B(260000, 0, 0, 260000, 0, 3000, "WF400X200X8X13", "SS400");
            m.CommitChanges();
            var fetched = m.FetchModelObjects(new List<string> { f1.Identifier.GUID.ToString(), f2.Identifier.GUID.ToString() }, true);
            var ff1 = (TSM.Part)fetched[0]; var ff2 = (TSM.Part)fetched[1];
            o.WriteLine("F fetched ids " + ff1.Identifier.ID + "/" + ff2.Identifier.ID + " (orig " + f1.Identifier.ID + "/" + f2.Identifier.ID + ")");
            var af = ff1.GetAssembly(); o.WriteLine("F asm id=" + af.Identifier.ID + " guid=" + af.Identifier.GUID);
            o.WriteLine("F Add=" + af.Add(new ArrayList { ff2 }) + " Modify=" + af.Modify()); m.CommitChanges();
            o.WriteLine("F secondaries=" + f1.GetAssembly().GetSecondaries().Count);
            // G: inspect an existing rebar group in the model, then clone it into probe beam c
            var sel = m.GetModelObjectSelector().GetAllObjectsWithType(new[] { typeof(TSM.RebarGroup) });
            TSM.RebarGroup ex0 = null; while (sel.MoveNext()) { if (sel.Current is TSM.RebarGroup rg && rg.Polygons.Count == 1) { ex0 = rg; break; } }
            if (ex0 != null)
            {
                o.WriteLine("G existing: polys=" + ex0.Polygons.Count + " pts=" + ((TSM.Polygon)ex0.Polygons[0]).Points.Count + " radius=" + string.Join(",", ex0.RadiusValues.Cast<object>()) + " spacings=" + string.Join(",", ex0.Spacings.Cast<object>()) + " spType=" + ex0.SpacingType + " excl=" + ex0.ExcludeType
                    + " onPlane=" + string.Join(",", ex0.OnPlaneOffsets.Cast<object>()) + " fromPlane=" + ex0.FromPlaneOffset + " startOff=" + ex0.StartPointOffsetType + ":" + ex0.StartPointOffsetValue + " endOff=" + ex0.EndPointOffsetType + ":" + ex0.EndPointOffsetValue
                    + " hooks=" + ex0.StartHook.Shape + "/" + ex0.EndHook.Shape + " start=" + ex0.StartPoint + " end=" + ex0.EndPoint + " poly0=" + string.Join(" ", ((TSM.Polygon)ex0.Polygons[0]).Points.Cast<Point>().Select(q => q.ToString())));
                // H: reuse the existing group's non-geometric settings with our geometry
                var h = G(); h.RadiusValues = ex0.RadiusValues; h.StartHook = ex0.StartHook; h.EndHook = ex0.EndHook; h.ExcludeType = ex0.ExcludeType;
                h.StartPointOffsetType = ex0.StartPointOffsetType; h.StartPointOffsetValue = ex0.StartPointOffsetValue; h.EndPointOffsetType = ex0.EndPointOffsetType; h.EndPointOffsetValue = ex0.EndPointOffsetValue; h.FromPlaneOffset = ex0.FromPlaneOffset;
                bool hok = h.Insert(); o.WriteLine("H insert with existing settings=" + hok); if (hok) made.Add(h);
                var h2 = G(); h2.RadiusValues = ex0.RadiusValues; h2.StartPointOffsetType = ex0.StartPointOffsetType; h2.EndPointOffsetType = ex0.EndPointOffsetType;
                bool h2ok = h2.Insert(); o.WriteLine("H2 radius+offset types only=" + h2ok); if (h2ok) made.Add(h2);
            }
            else o.WriteLine("G no single-polygon RebarGroup in model");        }
        finally
        {
            int d = 0; foreach (var x in made) if (x.Delete()) d++;
            m.CommitChanges();
            o.WriteLine("cleanup: deleted " + d + " of " + made.Count + " probe parts");
        }
        return 0;
    }
}



