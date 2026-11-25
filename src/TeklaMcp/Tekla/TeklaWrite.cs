using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using T3D = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Model-changing actions for the worker. Each action runs in two modes with the same validation:
    ///   preview: validate (catalog, referenced GUIDs, geometry) and describe the change. Nothing is inserted or modified.
    ///   apply:   perform, CommitChanges(), read the objects back, compare with the request.
    /// The SafetyGate in the server process decides when apply may run (confirm_token). Coordinates are global, mm.
    /// </summary>
    internal sealed class TeklaWrite
    {
        public const int MaxObjects = 500;
        public const int SaveWarningCount = 50;
        private const double Tol = 0.5; // mm, read-back comparison

        private readonly TeklaSession _session;
        private readonly TeklaQuery _query;
        public TeklaWrite(TeklaSession session, TeklaQuery query) { _session = session; _query = query; }

        /// <summary>args: action, mode ("preview"|"apply"), params</summary>
        public JObject Run(JObject a)
        {
            string action = (string)a["action"];
            bool apply = (string)a["mode"] == "apply";
            var p = a["params"] as JObject ?? new JObject();
            var m = _session.RequireModel();
            if (action == "set_workplane") return SetWorkplane(m, p, apply); // must not be wrapped in InGlobal
            return TeklaQuery.InGlobal(m, () =>
            {
                switch (action)
                {
                    case "create_beam": return CreateBeam(m, p, apply, column: false);
                    case "create_column": return CreateBeam(m, p, apply, column: true);
                    case "create_plate": return CreatePlate(m, p, apply);
                    case "create_bolts": return CreateBolts(m, p, apply);
                    case "create_weld": return CreateWeld(m, p, apply);
                    case "create_rebar": return CreateRebar(m, p, apply);
                    case "create_grid": return CreateGrid(m, p, apply);
                    case "assemble": return Assemble(m, p, apply);
                    case "modify": return Modify(m, p, apply);
                    case "move": return MoveOrCopy(m, p, apply, copy: false);
                    case "copy": return MoveOrCopy(m, p, apply, copy: true);
                    case "delete": return Delete(m, p, apply);
                    case "save": return Save(m, p, apply);
                    default: throw new TeklaException("unknown_action", "Unknown write action: " + action);
                }
            });
        }

        // ================= create =================

        private JObject CreateBeam(TSM.Model m, JObject p, bool apply, bool column)
        {
            var start = Point(p, column ? "base_mm" : "start_mm");
            var end = column && p["top_mm"] == null && p["height_mm"] != null
                ? new T3D.Point(start.X, start.Y, start.Z + Positive(p, "height_mm"))
                : Point(p, column ? "top_mm" : "end_mm");
            if (Dist(start, end) < 1) throw Invalid((column ? "base and top" : "start and end") + " points must differ by at least 1 mm.");
            var attrs = PartAttributes(p);
            var planned = PlannedPart(attrs);
            planned["type"] = column ? "Beam (COLUMN)" : "Beam";
            planned["start_mm"] = TeklaQuery.P(start); planned["end_mm"] = TeklaQuery.P(end);
            planned["length_mm"] = Math.Round(Dist(start, end), 1);
            string what = (column ? "column " : "beam ") + attrs.Profile + " " + attrs.Material + ", length " + planned["length_mm"] + " mm";
            if (!apply) return Preview("Create 1 " + what + ".", planned);

            var b = column ? new TSM.Beam(TSM.Beam.BeamTypeEnum.COLUMN) : new TSM.Beam();
            b.StartPoint = start; b.EndPoint = end;
            ApplyPartAttributes(b, attrs);
            if (!b.Insert()) throw new TeklaException("insert_failed", "Tekla refused to insert the " + (column ? "column." : "beam."));
            return Created(m, b, planned, "Created " + what + ".");
        }

        private JObject CreatePlate(TSM.Model m, JObject p, bool apply)
        {
            var pts = Points(p, "contour_mm", 3, 99);
            var attrs = PartAttributes(p);
            var planned = PlannedPart(attrs);
            planned["type"] = "ContourPlate";
            planned["contour_mm"] = new JArray(pts.Select(TeklaQuery.P));
            string what = "plate " + attrs.Profile + " " + attrs.Material + " with " + pts.Count + " contour points";
            if (!apply) return Preview("Create 1 " + what + ".", planned);

            var cp = new TSM.ContourPlate();
            foreach (var pt in pts) cp.AddContourPoint(new TSM.ContourPoint(pt, null));
            ApplyPartAttributes(cp, attrs);
            if (!cp.Insert()) throw new TeklaException("insert_failed", "Tekla refused to insert the plate.");
            return Created(m, cp, planned, "Created " + what + ".");
        }

        private JObject CreateBolts(TSM.Model m, JObject p, bool apply)
        {
            string kind = (string)p["kind"];
            var to = Part(m, (string)p["part_to_bolt_to_guid"], "part_to_bolt_to_guid");
            var be = p["part_to_be_bolted_guid"] != null ? Part(m, (string)p["part_to_be_bolted_guid"], "part_to_be_bolted_guid") : to;
            var first = Point(p, "first_point_mm"); var second = Point(p, "second_point_mm");
            if (Dist(first, second) < 1) throw Invalid("first_point_mm and second_point_mm must differ.");
            string standard = (string)p["bolt_standard"]; double size = Positive(p, "bolt_size_mm");
            if (!_query.CatalogRows("bolt").Any(r => string.Equals((string)r["standard"], standard, StringComparison.OrdinalIgnoreCase) && Math.Abs((double)r["size_mm"] - size) < 0.01))
                throw Invalid("Bolt " + standard + " M" + size + " is not in the bolt catalog. Use tekla_list_catalog kind=bolt.");
            standard = (string)_query.CatalogRows("bolt").First(r => string.Equals((string)r["standard"], standard, StringComparison.OrdinalIgnoreCase))["standard"];

            List<double> xs = Doubles(p, "dist_x_mm"), ys = Doubles(p, "dist_y_mm");
            int count;
            switch (kind)
            {
                case "array":
                    if (xs.Count == 0 || ys.Count == 0) throw Invalid("array needs dist_x_mm and dist_y_mm (use [0] for a single row).");
                    count = (xs.Count(x => x != 0) + 1) * (ys.Count(y => y != 0) + 1); break;
                case "xy_list":
                    if (xs.Count == 0 || xs.Count != ys.Count) throw Invalid("xy_list needs dist_x_mm and dist_y_mm of equal length (one entry per bolt).");
                    count = xs.Count; break;
                case "circle":
                    count = (int)Positive(p, "number_of_bolts"); Positive(p, "diameter_mm"); break;
                default: throw Invalid("kind must be array, xy_list or circle.");
            }
            var planned = new JObject
            {
                ["type"] = kind == "array" ? "BoltArray" : kind == "xy_list" ? "BoltXYList" : "BoltCircle",
                ["bolt_standard"] = standard, ["bolt_size_mm"] = size,
                ["part_to_bolt_to_guid"] = Guid(to), ["part_to_be_bolted_guid"] = Guid(be),
                ["first_point_mm"] = TeklaQuery.P(first), ["second_point_mm"] = TeklaQuery.P(second),
                ["bolt_count_estimate"] = count,
                ["bolt"] = (bool?)p["bolt"] ?? true, ["nut"] = (bool?)p["nut"] ?? true, ["washers"] = (bool?)p["washers"] ?? false,
            };
            if (p["tolerance_mm"] != null) planned["tolerance_mm"] = (double)p["tolerance_mm"];
            string what = kind + " of ~" + count + " bolt(s) " + standard + " M" + size;
            if (!apply) return Preview("Create bolt group: " + what + ".", planned);

            TSM.BoltGroup g;
            if (kind == "array") { var ba = new TSM.BoltArray(); xs.ForEach(x => ba.AddBoltDistX(x)); ys.ForEach(y => ba.AddBoltDistY(y)); g = ba; }
            else if (kind == "xy_list") { var bl = new TSM.BoltXYList(); xs.ForEach(x => bl.AddBoltDistX(x)); ys.ForEach(y => bl.AddBoltDistY(y)); g = bl; }
            else g = new TSM.BoltCircle { NumberOfBolts = (double)p["number_of_bolts"], Diameter = (double)p["diameter_mm"] };
            g.PartToBoltTo = to; g.PartToBeBolted = be; g.FirstPosition = first; g.SecondPosition = second;
            g.BoltStandard = standard; g.BoltSize = size;
            if (p["tolerance_mm"] != null) g.Tolerance = (double)p["tolerance_mm"];
            g.Bolt = (bool)planned["bolt"]; g.Nut1 = (bool)planned["nut"];
            g.Washer1 = g.Washer2 = g.Washer3 = (bool)planned["washers"];
            g.Hole1 = g.Hole2 = g.Hole3 = g.Hole4 = g.Hole5 = true;
            if (!g.Insert()) throw new TeklaException("insert_failed", "Tekla refused to insert the bolt group.");
            return Created(m, g, planned, "Created bolt group: " + what + ".");
        }

        private JObject CreateWeld(TSM.Model m, JObject p, bool apply)
        {
            var main = Part(m, (string)p["main_part_guid"], "main_part_guid");
            var sec = Part(m, (string)p["secondary_part_guid"], "secondary_part_guid");
            var typeAbove = WeldType((string)p["type_above"] ?? "fillet");
            double sizeAbove = Positive(p, "size_above_mm");
            var planned = new JObject
            {
                ["type"] = "Weld", ["main_object_guid"] = Guid(main), ["secondary_object_guid"] = Guid(sec),
                ["type_above"] = typeAbove.ToString(), ["size_above_mm"] = sizeAbove, ["shop_weld"] = (bool?)p["shop_weld"] ?? true
            };
            TSM.BaseWeld.WeldTypeEnum? typeBelow = null;
            if (p["size_below_mm"] != null) { typeBelow = WeldType((string)p["type_below"] ?? "fillet"); planned["type_below"] = typeBelow.ToString(); planned["size_below_mm"] = Positive(p, "size_below_mm"); }
            string what = typeAbove.ToString().Replace("WELD_TYPE_", "").ToLowerInvariant() + " weld " + sizeAbove + " mm";
            if (!apply) return Preview("Create 1 " + what + ".", planned);

            var w = new TSM.Weld { MainObject = main, SecondaryObject = sec, TypeAbove = typeAbove, SizeAbove = sizeAbove, ShopWeld = (bool)planned["shop_weld"] };
            if (typeBelow.HasValue) { w.TypeBelow = typeBelow.Value; w.SizeBelow = (double)planned["size_below_mm"]; }
            if (!w.Insert()) throw new TeklaException("insert_failed", "Tekla refused to insert the weld.");
            return Created(m, w, planned, "Created " + what + ".");
        }

        private JObject CreateRebar(TSM.Model m, JObject p, bool apply)
        {
            string kind = (string)p["kind"];
            var father = Part(m, (string)p["father_part_guid"], "father_part_guid");
            var pts = Points(p, "points_mm", 2, 99);
            string grade = (string)p["grade"], size = (string)p["size"];
            var cat = _query.CatalogRows("rebar").FirstOrDefault(r => string.Equals((string)r["grade"], grade, StringComparison.OrdinalIgnoreCase) && string.Equals((string)r["size"], size, StringComparison.OrdinalIgnoreCase));
            if (cat == null) throw Invalid("Rebar " + grade + " " + size + " is not in the rebar catalog. Use tekla_list_catalog kind=rebar.");
            grade = (string)cat["grade"]; size = (string)cat["size"];
            var planned = new JObject
            {
                ["type"] = kind == "group" ? "RebarGroup" : "SingleRebar", ["father_part_guid"] = Guid(father),
                ["grade"] = grade, ["size"] = size, ["name"] = (string)p["name"] ?? "", ["class"] = (int?)p["class"] ?? 1,
                ["points_mm"] = new JArray(pts.Select(TeklaQuery.P))
            };
            T3D.Point gs = null, ge = null; double spacing = 0;
            if (kind == "group")
            {
                gs = Point(p, "start_mm"); ge = Point(p, "end_mm"); spacing = Positive(p, "spacing_mm");
                if (Dist(gs, ge) < 1) throw Invalid("start_mm and end_mm must differ.");
                planned["start_mm"] = TeklaQuery.P(gs); planned["end_mm"] = TeklaQuery.P(ge); planned["spacing_mm"] = spacing;
                planned["bar_count_estimate"] = (int)Math.Floor(Dist(gs, ge) / spacing) + 1;
            }
            else if (kind != "single") throw Invalid("kind must be single or group.");
            string what = (kind == "group" ? "rebar group (~" + planned["bar_count_estimate"] + " bars)" : "single rebar") + " " + grade + " " + size;
            if (!apply) return Preview("Create " + what + " in part " + Guid(father) + ".", planned);

            var polygon = new TSM.Polygon();
            foreach (var pt in pts) polygon.Points.Add(pt);
            TSM.Reinforcement r;
            if (kind == "group")
            {
                var g = new TSM.RebarGroup { StartPoint = gs, EndPoint = ge, Size = size, SpacingType = TSM.BaseRebarGroup.RebarGroupSpacingTypeEnum.SPACING_TYPE_TARGET_SPACE };
                g.Polygons.Add(polygon); g.Spacings.Add(spacing);
                r = g;
            }
            else r = new TSM.SingleRebar { Polygon = polygon, Size = size };
            r.Father = father; r.Grade = grade; r.Name = (string)planned["name"]; r.Class = (int)planned["class"];
            r.OnPlaneOffsets = new ArrayList { 0.0 };
            r.RadiusValues = new ArrayList { (double)cat["bend_radius_mm"] }; // RebarGroup.Insert throws "Required information missing - RadiusValues" without it
            if (!r.Insert()) throw new TeklaException("insert_failed", "Tekla refused to insert the rebar.");
            return Created(m, r, planned, "Created " + what + ".");
        }

        private JObject CreateGrid(TSM.Model m, JObject p, bool apply)
        {
            var origin = p["origin_mm"] != null ? Point(p, "origin_mm") : new T3D.Point(0, 0, 0);
            var planned = new JObject { ["type"] = "Grid", ["origin_mm"] = TeklaQuery.P(origin) };
            foreach (var k in new[] { "coordinate_x", "coordinate_y", "coordinate_z", "label_x", "label_y", "label_z" })
            {
                string v = (string)p[k];
                if (string.IsNullOrWhiteSpace(v)) throw Invalid(k + " is required.");
                planned[k] = v;
            }
            if (!apply) return Preview("Create grid X[" + planned["coordinate_x"] + "] Y[" + planned["coordinate_y"] + "] Z[" + planned["coordinate_z"] + "].", planned);

            var g = new TSM.Grid
            {
                Origin = origin,
                CoordinateX = (string)planned["coordinate_x"], CoordinateY = (string)planned["coordinate_y"], CoordinateZ = (string)planned["coordinate_z"],
                LabelX = (string)planned["label_x"], LabelY = (string)planned["label_y"], LabelZ = (string)planned["label_z"]
            };
            if (p["name"] != null) g.Name = (string)p["name"];
            if (!g.Insert()) throw new TeklaException("insert_failed", "Tekla refused to insert the grid.");
            m.CommitChanges();
            g.Select();
            var actual = TeklaQuery.Brief(g);
            var mismatches = Compare(planned, actual, "coordinate_x", "coordinate_y", "coordinate_z", "label_x", "label_y", "label_z");
            return Applied("Created grid.", new[] { g }, new JArray(actual), mismatches);
        }

        private JObject Assemble(TSM.Model m, JObject p, bool apply)
        {
            var targetObj = Single(m, (string)p["target_guid"], "target_guid");
            var asm = targetObj as TSM.Assembly ?? (targetObj as TSM.Part)?.GetAssembly();
            if (asm == null) throw Invalid("target_guid must be a part or an assembly.");
            var (parts, missing) = TeklaQuery.Fetch(m, p["part_guids"]);
            if (missing.Count > 0) throw Invalid("Objects not found: " + string.Join(", ", missing));
            var bad = parts.Where(o => !(o is TSM.Part)).Select(o => Guid(o) + " (" + o.GetType().Name + ")").ToList();
            if (bad.Count > 0) throw Invalid("Only parts can be added: " + string.Join(", ", bad));
            var main = asm.GetMainPart() as TSM.ModelObject;
            var current = new HashSet<string>(asm.GetSecondaries().OfType<TSM.ModelObject>().Select(Guid), StringComparer.OrdinalIgnoreCase);
            // Tested 2026-11-26: Assembly.Add/Modify return true but leave a part that is a secondary of another assembly (e.g. shop-welded) where it is.
            var owned = parts.Where(o => !current.Contains(Guid(o)))
                .Select(o => (part: o, owner: ((TSM.Part)o).GetAssembly()?.GetMainPart() as TSM.ModelObject))
                .Where(x => x.owner != null && !SameText(Guid(x.owner), Guid(x.part)))
                .Select(x => Guid(x.part) + " (secondary of the assembly whose main part is " + Guid(x.owner) + ")").ToList();
            if (owned.Count > 0) throw Invalid("These parts already belong to another assembly, and Tekla does not move them: " + string.Join(", ", owned) + ". Remove them from that assembly first in Tekla (or use a site weld instead of a shop weld).");
            var objects = new JArray(parts.Select(TeklaQuery.Brief));
            var planned = new JObject
            {
                ["assembly_guid"] = Guid(asm), ["assembly_name"] = asm.Name, ["main_part_guid"] = main == null ? null : Guid(main),
                ["add_as_secondary"] = objects, ["already_secondary"] = new JArray(parts.Select(Guid).Where(current.Contains))
            };
            if (p["name"] != null) planned["new_assembly_name"] = (string)p["name"];
            var preview = Preview("Add " + parts.Count + " part(s) to assembly of " + (main == null ? Guid(asm) : Guid(main)) + ".", planned);
            preview["objects"] = objects;
            if (!apply) return preview;

            if (!asm.Add(new ArrayList(parts))) throw new TeklaException("modify_failed", "Tekla refused to add the parts to the assembly.");
            if (p["name"] != null) asm.Name = (string)p["name"];
            if (!asm.Modify()) throw new TeklaException("modify_failed", "Assembly.Modify failed.");
            m.CommitChanges();
            asm.Select();
            var now = new HashSet<string>(asm.GetSecondaries().OfType<TSM.ModelObject>().Select(Guid), StringComparer.OrdinalIgnoreCase);
            var mism = new JArray(parts.Where(o => !now.Contains(Guid(o))).Select(o => "part " + Guid(o) + " is not a secondary of the assembly after apply (its assembly main part is " + Guid((TSM.ModelObject)((TSM.Part)o).GetAssembly().GetMainPart()) + ", target assembly " + Guid(asm) + " main " + Guid((TSM.ModelObject)asm.GetMainPart()) + ")"));
            if (p["name"] != null && asm.Name != (string)p["name"]) mism.Add("assembly name is '" + asm.Name + "'");
            return Applied("Added " + parts.Count + " part(s) to the assembly.", new TSM.ModelObject[] { asm }.Concat(parts), new JArray(TeklaQuery.Brief(asm)), mism);
        }

        // ================= modify / move / copy / delete =================

        private JObject Modify(TSM.Model m, JObject p, bool apply)
        {
            var objs = Targets(m, p);
            var set = p["set"] as JObject ?? throw Invalid("set is required.");
            if (!set.Properties().Any()) throw Invalid("set must contain at least one field.");
            string profile = null, material = null;
            if (set["profile"] != null) profile = Profile((string)set["profile"]);
            if (set["material"] != null) material = Material((string)set["material"]);
            if (set["bolt_standard"] != null || set["bolt_size_mm"] != null) throw Invalid("Changing bolts is not supported by tekla_modify_objects; delete and recreate the bolt group.");

            var changes = new JArray(); var unsupported = new List<string>();
            foreach (var o in objs)
            {
                var c = new JObject { ["guid"] = Guid(o), ["type"] = o.GetType().Name };
                var fields = new JObject();
                switch (o)
                {
                    case TSM.Part pt:
                        if (profile != null) fields["profile"] = Change(pt.Profile.ProfileString, profile);
                        if (material != null) fields["material"] = Change(pt.Material.MaterialString, material);
                        if (set["name"] != null) fields["name"] = Change(pt.Name, (string)set["name"]);
                        if (set["class"] != null) fields["class"] = Change(pt.Class, (string)set["class"]);
                        if (set["finish"] != null) fields["finish"] = Change(pt.Finish, (string)set["finish"]);
                        if (set["position"] is JObject pos) fields["position"] = Change(PositionJson(pt.Position), PositionJson(ParsePosition(pos, pt.Position)));
                        if (set["size"] != null || set["grade"] != null) unsupported.Add(Guid(o) + ": size/grade apply to rebar only");
                        break;
                    case TSM.Reinforcement re:
                        if (set["grade"] != null) fields["grade"] = Change(re.Grade, (string)set["grade"]);
                        if (set["name"] != null) fields["name"] = Change(re.Name, (string)set["name"]);
                        if (set["class"] != null) fields["class"] = Change(re.Class.ToString(), (string)set["class"]);
                        if (set["size"] != null) fields["size"] = Change(RebarSize(re), (string)set["size"]);
                        if (profile != null || material != null || set["position"] != null || set["finish"] != null) unsupported.Add(Guid(o) + ": profile/material/position/finish do not apply to rebar (use size/grade)");
                        break;
                    default:
                        if (set.Properties().Any(x => x.Name != "uda")) unsupported.Add(Guid(o) + ": only uda can be changed on " + o.GetType().Name);
                        break;
                }
                if (set["uda"] is JArray udas) foreach (var u in udas.OfType<JObject>()) fields["uda." + (string)u["name"]] = Change(UserValue(o, (string)u["name"]), (string)u["value"]);
                c["changes"] = fields;
                changes.Add(c);
            }
            if (unsupported.Count > 0) throw Invalid("Not supported: " + string.Join("; ", unsupported.Take(10)));
            if (set["grade"] != null || set["size"] != null)
                foreach (var re in objs.OfType<TSM.Reinforcement>())
                {
                    string g = (string)set["grade"] ?? re.Grade, s = (string)set["size"] ?? RebarSize(re);
                    if (!_query.CatalogRows("rebar").Any(r => string.Equals((string)r["grade"], g, StringComparison.OrdinalIgnoreCase) && string.Equals((string)r["size"], s, StringComparison.OrdinalIgnoreCase)))
                        throw Invalid("Rebar " + g + " " + s + " is not in the rebar catalog.");
                }

            var preview = Preview("Modify " + objs.Count + " object(s): " + string.Join(", ", set.Properties().Select(x => x.Name)) + ".", new JObject { ["changes"] = changes });
            preview["objects"] = new JArray(objs.Select(TeklaQuery.Brief));
            Warn(preview, objs.Count);
            if (!apply) return preview;

            var failed = new JArray();
            foreach (var o in objs)
            {
                switch (o)
                {
                    case TSM.Part pt:
                        if (profile != null) pt.Profile.ProfileString = profile;
                        if (material != null) pt.Material.MaterialString = material;
                        if (set["name"] != null) pt.Name = (string)set["name"];
                        if (set["class"] != null) pt.Class = (string)set["class"];
                        if (set["finish"] != null) pt.Finish = (string)set["finish"];
                        if (set["position"] is JObject pos) pt.Position = ParsePosition(pos, pt.Position);
                        break;
                    case TSM.Reinforcement re:
                        if (set["grade"] != null) re.Grade = (string)set["grade"];
                        if (set["name"] != null) re.Name = (string)set["name"];
                        if (set["class"] != null) re.Class = int.Parse((string)set["class"]);
                        if (set["size"] != null) SetRebarSize(re, (string)set["size"]);
                        break;
                }
                if (set["uda"] is JArray udas) foreach (var u in udas.OfType<JObject>()) SetUda(o, u);
                if (!o.Modify()) failed.Add(Guid(o));
            }
            m.CommitChanges();
            var mismatches = new JArray(failed.Select(g => "Modify() returned false for " + g));
            var after = new JArray();
            foreach (var o in objs)
            {
                o.Select();
                var row = TeklaQuery.Brief(o);
                after.Add(row);
                foreach (var ch in ((JObject)changes.First(c => (string)c["guid"] == Guid(o))["changes"]).Properties())
                {
                    string actual = ch.Name == "position" && o is TSM.Part pp ? PositionJson(pp.Position).ToString(Newtonsoft.Json.Formatting.None)
                                  : ch.Name.StartsWith("uda.") ? UserValue(o, ch.Name.Substring(4))
                                  : ch.Name == "size" ? (string)row["profile"] : ch.Name == "grade" ? (string)row["material"]
                                  : (string)row[ch.Name];
                    string expected = ch.Value["to"].Type == JTokenType.Object ? ch.Value["to"].ToString(Newtonsoft.Json.Formatting.None) : (string)ch.Value["to"];
                    if (!SameText(actual, expected)) mismatches.Add(Guid(o) + " " + ch.Name + ": expected '" + expected + "', read back '" + actual + "'");
                }
            }
            return Applied("Modified " + (objs.Count - failed.Count) + " of " + objs.Count + " object(s).", objs, after, mismatches);
        }

        private JObject MoveOrCopy(TSM.Model m, JObject p, bool apply, bool copy)
        {
            var objs = Targets(m, p);
            var v = Point(p, "vector_mm");
            if (Math.Abs(v.X) + Math.Abs(v.Y) + Math.Abs(v.Z) < 0.01) throw Invalid("vector_mm must not be zero.");
            int count = copy ? (int?)p["count"] ?? 1 : 1;
            if (count < 1 || count > 20) throw Invalid("count must be 1-20.");
            string verb = copy ? "Copy" : "Move";
            var preview = Preview(verb + " " + objs.Count + " object(s) by [" + v.X + ", " + v.Y + ", " + v.Z + "] mm" + (copy ? " x" + count + " (creates " + objs.Count * count + " object(s))" : "") + ".",
                new JObject { ["vector_mm"] = TeklaQuery.P(v), ["count"] = count });
            preview["objects"] = new JArray(objs.Select(TeklaQuery.Brief));
            Warn(preview, objs.Count * count);
            if (!apply) return preview;

            var before = objs.ToDictionary(Guid, BoxMin);
            var created = new List<TSM.ModelObject>(); var mism = new JArray();
            foreach (var o in objs)
            {
                if (copy)
                {
                    for (int i = 1; i <= count; i++)
                    {
                        var c = TSM.Operations.Operation.CopyObject(o, new T3D.Vector(v.X * i, v.Y * i, v.Z * i));
                        if (c == null) mism.Add("copy " + i + " of " + Guid(o) + " failed"); else created.Add(c);
                    }
                }
                else if (!TSM.Operations.Operation.MoveObject(o, new T3D.Vector(v.X, v.Y, v.Z))) mism.Add("move failed for " + Guid(o));
            }
            m.CommitChanges();
            var check = copy ? created : objs;
            var rows = new JArray();
            foreach (var o in check)
            {
                o.Select();
                rows.Add(TeklaQuery.Brief(o));
                if (!copy && before.TryGetValue(Guid(o), out var b0) && b0 != null)
                {
                    var b1 = BoxMin(o);
                    if (b1 == null || Dist(new T3D.Point(b0.X + v.X, b0.Y + v.Y, b0.Z + v.Z), b1) > 1) mism.Add(Guid(o) + " did not move by the vector (bounding box check)");
                }
            }
            if (copy && created.Count != objs.Count * count) mism.Add("expected " + objs.Count * count + " copies, got " + created.Count);
            return Applied((copy ? "Copied " : "Moved ") + objs.Count + " object(s)" + (copy ? ", created " + created.Count : "") + ".", copy ? created : objs, rows, mism);
        }

        private JObject Delete(TSM.Model m, JObject p, bool apply)
        {
            var objs = Targets(m, p);
            var dependents = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var targetSet = new HashSet<string>(objs.Select(Guid), StringComparer.OrdinalIgnoreCase);
            foreach (var pt in objs.OfType<TSM.Part>())
                foreach (var d in TeklaQuery.Each(pt.GetBolts()).Concat(TeklaQuery.Each(pt.GetWelds())).Concat(TeklaQuery.Each(pt.GetReinforcements())))
                    if (!targetSet.Contains(Guid(d)) && !dependents.ContainsKey(Guid(d))) dependents[Guid(d)] = TeklaQuery.Brief(d);
            var preview = Preview("DELETE " + objs.Count + " object(s)" + (dependents.Count > 0 ? "; " + dependents.Count + " attached bolt/weld/rebar object(s) may be deleted with them" : "") + ". This cannot be undone.",
                new JObject { ["attached_objects_may_be_deleted"] = new JArray(dependents.Values) });
            preview["objects"] = new JArray(objs.Select(o => { var r = TeklaQuery.Brief(o); TeklaQuery.AddQuantities(o, r); return r; }));
            Warn(preview, objs.Count + dependents.Count);
            if (!apply) return preview;

            var mism = new JArray();
            var deleted = new List<string>();
            // Delete() returns false for bolts/welds/rebar already removed with their parent part earlier in this loop; judge by read-back.
            foreach (var o in objs) { o.Delete(); deleted.Add(Guid(o)); }
            m.CommitChanges();
            var (stillThere, _) = TeklaQuery.Fetch(m, new JArray(deleted));
            foreach (var o in stillThere) mism.Add(Guid(o) + " still exists after delete");
            var (depLeft, _) = TeklaQuery.Fetch(m, new JArray(dependents.Keys));
            var r2 = new JObject
            {
                ["summary"] = "Deleted " + (deleted.Count - stillThere.Count) + " of " + objs.Count + " object(s).",
                ["applied"] = true, ["affected"] = new JArray(objs.Select(Guid)), ["deleted"] = new JArray(deleted.Except(stillThere.Select(Guid))),
                ["attached_objects_remaining"] = depLeft.Count, ["attached_objects_deleted"] = dependents.Count - depLeft.Count,
                ["verification"] = new JObject { ["ok"] = mism.Count == 0, ["mismatches"] = mism }
            };
            return r2;
        }

        // ================= workplane / save =================

        private JObject SetWorkplane(TSM.Model m, JObject p, bool apply)
        {
            bool global = (bool?)p["global"] ?? false;
            T3D.Point o = null; T3D.Vector x = null, y = null;
            if (!global)
            {
                o = Point(p, "origin_mm"); var xp = Point(p, "axis_x"); var yp = Point(p, "axis_y");
                x = new T3D.Vector(xp.X, xp.Y, xp.Z); y = new T3D.Vector(yp.X, yp.Y, yp.Z);
                if (x.GetLength() < 1e-9 || y.GetLength() < 1e-9) throw Invalid("axis_x and axis_y must be non-zero vectors.");
                if (Math.Abs(T3D.Vector.Dot(x.GetNormal(), y.GetNormal())) > 1e-6) throw Invalid("axis_x and axis_y must be perpendicular.");
            }
            var planned = global ? new JObject { ["global"] = true } : new JObject { ["origin_mm"] = TeklaQuery.P(o), ["axis_x"] = TeklaQuery.P(x), ["axis_y"] = TeklaQuery.P(y) };
            planned["note"] = "Changes only Tekla's current work plane (UI). tekla_* tools keep using global coordinates.";
            if (!apply) return Preview("Set the work plane to " + (global ? "global" : "origin " + planned["origin_mm"]) + ".", planned);

            var h = m.GetWorkPlaneHandler();
            bool ok = h.SetCurrentTransformationPlane(global ? new TSM.TransformationPlane() : new TSM.TransformationPlane(o, x, y));
            var after = _query.Workplane(new JObject());
            var mism = new JArray();
            if (!ok) mism.Add("SetCurrentTransformationPlane returned false");
            if (global && !(bool)after["is_global"]) mism.Add("work plane is not global after apply");
            if (!global && Dist(TeklaQuery.Pt(after["origin_mm"]), o) > Tol) mism.Add("origin differs after apply: " + after["origin_mm"]);
            return new JObject { ["summary"] = "Work plane set.", ["applied"] = true, ["affected"] = new JArray(), ["workplane"] = after, ["verification"] = new JObject { ["ok"] = mism.Count == 0, ["mismatches"] = mism } };
        }

        private JObject Save(TSM.Model m, JObject p, bool apply)
        {
            string comment = (string)p["comment"] ?? "Saved by TeklaMcp";
            var info = m.GetInfo();
            var planned = new JObject { ["model_name"] = info.ModelName, ["model_path"] = info.ModelPath, ["comment"] = comment };
            if (!apply) return Preview("Save model " + info.ModelName + " (" + info.ModelPath + ").", planned);
            bool ok = new TSM.ModelHandler().Save(comment, Environment.UserName);
            bool saved = new TSM.ModelHandler().IsModelSaved();
            var mism = new JArray();
            if (!ok) mism.Add("ModelHandler.Save returned false");
            if (!saved) mism.Add("IsModelSaved() is false after save");
            return new JObject { ["summary"] = ok ? "Model saved." : "Save failed.", ["applied"] = true, ["affected"] = new JArray(), ["model"] = planned, ["verification"] = new JObject { ["ok"] = mism.Count == 0, ["mismatches"] = mism } };
        }

        // ================= helpers =================

        private sealed class PartAttrs { public string Profile, Material, Name, Class, Finish; public JObject Position; public JArray Uda; }

        private PartAttrs PartAttributes(JObject p)
        {
            var a = new PartAttrs
            {
                Profile = Profile((string)p["profile"]), Material = Material((string)p["material"]),
                Name = (string)p["name"], Class = (string)p["class"], Finish = (string)p["finish"],
                Position = p["position"] as JObject, Uda = p["uda"] as JArray
            };
            if (a.Position != null) ParsePosition(a.Position, new TSM.Position()); // validate enum names now
            return a;
        }

        private static JObject PlannedPart(PartAttrs a)
        {
            var j = new JObject { ["profile"] = a.Profile, ["material"] = a.Material };
            if (a.Name != null) j["name"] = a.Name;
            if (a.Class != null) j["class"] = a.Class;
            if (a.Finish != null) j["finish"] = a.Finish;
            if (a.Position != null) j["position"] = PositionJson(ParsePosition(a.Position, new TSM.Position()));
            if (a.Uda != null) j["uda"] = a.Uda.DeepClone();
            return j;
        }

        private static void ApplyPartAttributes(TSM.Part part, PartAttrs a)
        {
            part.Profile.ProfileString = a.Profile;
            part.Material.MaterialString = a.Material;
            if (a.Name != null) part.Name = a.Name;
            if (a.Class != null) part.Class = a.Class;
            if (a.Finish != null) part.Finish = a.Finish;
            if (a.Position != null) part.Position = ParsePosition(a.Position, part.Position);
        }

        private string Profile(string profile)
        {
            var lib = _query.CatalogRows("profile").Select(r => (string)r["name"]);
            return TeklaText.NormalizeProfile(profile, lib, _query.ParametricPrefixes())
                   ?? throw Invalid("Profile '" + profile + "' is not in the profile catalog and is not a valid parametric profile. Use tekla_list_catalog kind=profile.");
        }

        private string Material(string material)
        {
            if (string.IsNullOrWhiteSpace(material)) throw Invalid("material is required.");
            var hit = _query.CatalogRows("material").Select(r => (string)r["name"]).FirstOrDefault(n => string.Equals(n, material.Trim(), StringComparison.OrdinalIgnoreCase));
            return hit ?? throw Invalid("Material '" + material + "' is not in the material catalog. Use tekla_list_catalog kind=material.");
        }

        private static TSM.Position ParsePosition(JObject j, TSM.Position basis)
        {
            var pos = new TSM.Position
            {
                Plane = basis.Plane, PlaneOffset = basis.PlaneOffset, Rotation = basis.Rotation, RotationOffset = basis.RotationOffset,
                Depth = basis.Depth, DepthOffset = basis.DepthOffset
            };
            if (j["plane"] != null) pos.Plane = Enum<TSM.Position.PlaneEnum>((string)j["plane"], "position.plane");
            if (j["rotation"] != null) pos.Rotation = Enum<TSM.Position.RotationEnum>((string)j["rotation"], "position.rotation");
            if (j["depth"] != null) pos.Depth = Enum<TSM.Position.DepthEnum>((string)j["depth"], "position.depth");
            if (j["plane_offset_mm"] != null) pos.PlaneOffset = (double)j["plane_offset_mm"];
            if (j["rotation_offset_deg"] != null) pos.RotationOffset = (double)j["rotation_offset_deg"];
            if (j["depth_offset_mm"] != null) pos.DepthOffset = (double)j["depth_offset_mm"];
            return pos;
        }

        private static JObject PositionJson(TSM.Position p) => new JObject
        {
            ["plane"] = p.Plane.ToString().ToLowerInvariant(), ["plane_offset_mm"] = Math.Round(p.PlaneOffset, 3),
            ["rotation"] = p.Rotation.ToString().ToLowerInvariant(), ["rotation_offset_deg"] = Math.Round(p.RotationOffset, 3),
            ["depth"] = p.Depth.ToString().ToLowerInvariant(), ["depth_offset_mm"] = Math.Round(p.DepthOffset, 3)
        };

        private static TEnum Enum<TEnum>(string value, string field) where TEnum : struct
        {
            if (System.Enum.TryParse(value, true, out TEnum e)) return e;
            throw Invalid(field + " must be one of " + string.Join(", ", System.Enum.GetNames(typeof(TEnum)).Select(n => n.ToLowerInvariant())));
        }

        private static TSM.BaseWeld.WeldTypeEnum WeldType(string s)
        {
            string name = s.Trim().ToUpperInvariant();
            if (!name.StartsWith("WELD_TYPE_") && !name.StartsWith("STEEP_")) name = "WELD_TYPE_" + name;
            return Enum<TSM.BaseWeld.WeldTypeEnum>(name, "weld type");
        }

        private static List<TSM.ModelObject> Targets(TSM.Model m, JObject p)
        {
            var guids = p["guids"] as JArray;
            if (guids == null || guids.Count == 0 || guids.Count > MaxObjects) throw Invalid("guids must contain 1-" + MaxObjects + " items.");
            var (found, missing) = TeklaQuery.Fetch(m, guids);
            if (missing.Count > 0) throw Invalid("Objects not found: " + string.Join(", ", missing.Take(20)));
            return found;
        }

        private static TSM.ModelObject Single(TSM.Model m, string guid, string field)
        {
            if (string.IsNullOrWhiteSpace(guid)) throw Invalid(field + " is required.");
            var (found, _) = TeklaQuery.Fetch(m, new JArray(guid));
            return found.FirstOrDefault() ?? throw Invalid(field + ": object not found: " + guid);
        }

        private static TSM.Part Part(TSM.Model m, string guid, string field) =>
            Single(m, guid, field) as TSM.Part ?? throw Invalid(field + " must be a part (beam, column, plate).");

        private static T3D.Point Point(JObject p, string field)
        {
            if (!(p[field] is JArray a) || a.Count != 3 || a.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float)) throw Invalid(field + " must be [x, y, z] in mm.");
            return TeklaQuery.Pt(a);
        }

        private static List<T3D.Point> Points(JObject p, string field, int min, int max)
        {
            if (!(p[field] is JArray a) || a.Count < min || a.Count > max) throw Invalid(field + " must contain " + min + "-" + max + " points [x, y, z] (mm).");
            var list = new List<T3D.Point>();
            for (int i = 0; i < a.Count; i++)
            {
                if (!(a[i] is JArray q) || q.Count != 3) throw Invalid(field + "[" + i + "] must be [x, y, z].");
                list.Add(TeklaQuery.Pt(q));
            }
            return list;
        }

        private static List<double> Doubles(JObject p, string field) => p[field]?.Values<double>().ToList() ?? new List<double>();

        private static double Positive(JObject p, string field)
        {
            double? v = (double?)p[field];
            if (v == null || v <= 0) throw Invalid(field + " must be a positive number.");
            return v.Value;
        }

        private static double Dist(T3D.Point a, T3D.Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        private static string Guid(TSM.ModelObject o) => o.Identifier.GUID.ToString();
        private static TeklaException Invalid(string msg) => new TeklaException("invalid_arguments", msg);
        private static JObject Change(string from, string to) => new JObject { ["from"] = from ?? "", ["to"] = to ?? "" };
        private static JObject Change(JObject from, JObject to) => new JObject { ["from"] = from, ["to"] = to };
        private static bool SameText(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static string RebarSize(TSM.Reinforcement re) => re is TSM.SingleRebar s ? s.Size : re is TSM.BaseRebarGroup g ? g.Size : null;
        private static void SetRebarSize(TSM.Reinforcement re, string size)
        {
            if (re is TSM.SingleRebar s) s.Size = size; else if (re is TSM.BaseRebarGroup g) g.Size = size;
            else throw Invalid("size cannot be set on " + re.GetType().Name);
        }

        private static string UserValue(TSM.ModelObject o, string name)
        {
            string s = null; if (o.GetUserProperty(name, ref s) && !string.IsNullOrEmpty(s)) return s;
            double d = 0; if (o.GetUserProperty(name, ref d) && d != 0) return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            int i = 0; if (o.GetUserProperty(name, ref i) && i != 0) return i.ToString();
            return "";
        }

        private static void SetUda(TSM.ModelObject o, JObject u)
        {
            string name = (string)u["name"], value = (string)u["value"], type = (string)u["type"] ?? "string";
            bool ok;
            switch (type)
            {
                case "double": ok = o.SetUserProperty(name, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                case "integer": ok = o.SetUserProperty(name, int.Parse(value)); break;
                default: ok = o.SetUserProperty(name, value); break;
            }
            if (!ok) throw new TeklaException("modify_failed", "SetUserProperty failed for '" + name + "' on " + Guid(o));
        }

        private static T3D.Point BoxMin(TSM.ModelObject o)
        {
            try
            {
                switch (o)
                {
                    case TSM.Part p: return p.GetSolid()?.MinimumPoint;
                    case TSM.Reinforcement r: return r.GetSolid()?.MinimumPoint;
                    case TSM.BoltGroup b: return b.GetSolid()?.MinimumPoint;
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static void Warn(JObject preview, int count)
        {
            if (count >= SaveWarningCount)
                ((JArray)preview["warnings"]).Add(count + " objects affected. The Open API has no undo: save the model first (tekla_save_model).");
        }

        private static JObject Preview(string summary, JObject planned)
        {
            var r = new JObject { ["summary"] = summary, ["applied"] = false, ["warnings"] = new JArray() };
            foreach (var p in planned.Properties()) r[p.Name] = p.Value;
            return r;
        }

        /// <summary>After Insert: commit, read back, compare planned fields with what Tekla stored.</summary>
        private JObject Created(TSM.Model m, TSM.ModelObject o, JObject planned, string summary)
        {
            if (o is TSM.Part part && planned["uda"] is JArray udas) { foreach (var u in udas.OfType<JObject>()) SetUda(part, u); part.Modify(); }
            m.CommitChanges();
            o.Select();
            var actual = TeklaQuery.Brief(o);
            TeklaQuery.AddQuantities(o, actual);
            if (o is TSM.RebarGroup rg) { actual["start_mm"] = TeklaQuery.P(rg.StartPoint); actual["end_mm"] = TeklaQuery.P(rg.EndPoint); }
            var mism = Compare(planned, actual, "profile", "material", "name", "class");
            foreach (var k in new[] { "start_mm", "end_mm" })
                if (planned[k] != null && (actual[k] == null || Dist(TeklaQuery.Pt(planned[k]), TeklaQuery.Pt(actual[k])) > Tol))
                    mism.Add(k + ": expected " + planned[k].ToString(Newtonsoft.Json.Formatting.None) + ", read back " + (actual[k]?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"));
            if (o is TSM.Reinforcement re) { if (!SameText(re.Grade, (string)planned["grade"])) mism.Add("grade read back '" + re.Grade + "'"); if (!SameText(RebarSize(re), (string)planned["size"])) mism.Add("size read back '" + RebarSize(re) + "'"); }
            if (o is TSM.BoltGroup bg) { if (!SameText(bg.BoltStandard, (string)planned["bolt_standard"])) mism.Add("bolt_standard read back '" + bg.BoltStandard + "'"); if (Math.Abs(bg.BoltSize - (double)planned["bolt_size_mm"]) > 0.01) mism.Add("bolt_size read back " + bg.BoltSize); }
            if (o is TSM.BaseWeld w && Math.Abs(w.SizeAbove - (double)planned["size_above_mm"]) > 0.01) mism.Add("size_above read back " + w.SizeAbove);
            if (o is TSM.Part pa && planned["uda"] is JArray ud)
                foreach (var u in ud.OfType<JObject>())
                    if (!SameText(UserValue(pa, (string)u["name"]), (string)u["value"])) mism.Add("uda " + u["name"] + " read back '" + UserValue(pa, (string)u["name"]) + "'");
            return Applied(summary, new[] { o }, new JArray(actual), mism);
        }

        private static JArray Compare(JObject planned, JObject actual, params string[] fields)
        {
            var mism = new JArray();
            foreach (var f in fields)
                if (planned[f] != null && !SameText((string)planned[f], (string)actual[f]))
                    mism.Add(f + ": expected '" + planned[f] + "', read back '" + actual[f] + "'");
            return mism;
        }

        private static JObject Applied(string summary, IEnumerable<TSM.ModelObject> affected, JArray objects, JArray mismatches) => new JObject
        {
            ["summary"] = summary + (mismatches.Count == 0 ? " Read-back matches the request." : " READ-BACK MISMATCH: " + mismatches.Count + " issue(s)."),
            ["applied"] = true,
            ["affected"] = new JArray(affected.Select(Guid).Distinct()),
            ["objects"] = objects,
            ["verification"] = new JObject { ["ok"] = mismatches.Count == 0, ["mismatches"] = mismatches }
        };
    }
}
