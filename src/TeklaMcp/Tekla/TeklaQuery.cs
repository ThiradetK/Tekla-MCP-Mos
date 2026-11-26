using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using T3D = Tekla.Structures.Geometry3d;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Read-only Tekla queries for the worker process. Coordinates are returned in the GLOBAL system
    /// (the work plane is switched to global during each query and restored afterwards).
    /// Units: mm, kg, mm2, mm3 (values as the Open API returns them; verified in Phase 2 probe).
    /// </summary>
    internal sealed class TeklaQuery
    {
        private readonly TeklaSession _session;
        public TeklaQuery(TeklaSession session) { _session = session; }

        private static readonly Dictionary<string, Type> TypeMap = new Dictionary<string, Type>
        {
            ["part"] = typeof(TSM.Part), ["beam"] = typeof(TSM.Beam), ["polybeam"] = typeof(TSM.PolyBeam),
            ["contour_plate"] = typeof(TSM.ContourPlate), ["bolt"] = typeof(TSM.BoltGroup), ["weld"] = typeof(TSM.BaseWeld),
            ["rebar"] = typeof(TSM.Reinforcement), ["rebar_set"] = typeof(TSM.RebarSet), ["assembly"] = typeof(TSM.Assembly),
            ["component"] = typeof(TSM.BaseComponent), ["grid"] = typeof(TSM.Grid), ["grid_plane"] = typeof(TSM.GridPlane),
        };

        // ---------- ops ----------

        /// <summary>args: types[], filter{}, selection, bbox{min,max}, offset, limit (null = all), include_quantities</summary>
        public JObject Query(JObject a)
        {
            var m = _session.RequireModel();
            return InGlobal(m, () =>
            {
                bool qty = (bool?)a["include_quantities"] ?? false;
                int offset = (int?)a["offset"] ?? 0;
                int? limit = (int?)a["limit"];
                var filter = a["filter"] as JObject ?? new JObject();
                int total = 0;
                var page = new List<(JObject row, TSM.ModelObject obj)>();
                foreach (var o in Enumerate(m, a))
                {
                    var row = Brief(o);
                    if (!Matches(o, row, filter)) continue;
                    total++;
                    if (total <= offset || (limit.HasValue && page.Count >= limit.Value)) continue;
                    page.Add((row, o));
                }
                if (qty) AddQuantitiesBulk(m, page);
                return new JObject { ["total"] = total, ["offset"] = offset, ["items"] = new JArray(page.Select(p => p.row)) };
            });
        }

        /// <summary>args: guids[], report_properties[]</summary>
        public JObject Get(JObject a)
        {
            var m = _session.RequireModel();
            return InGlobal(m, () =>
            {
                var (found, missing) = Fetch(m, a["guids"]);
                var names = a["report_properties"]?.Values<string>().ToList() ?? new List<string>();
                var items = new JArray();
                foreach (var o in found)
                {
                    var row = Brief(o);
                    AddQuantities(o, row);
                    AddGeometry(o, row);
                    var uda = new Hashtable();
                    if (o.GetAllUserProperties(ref uda) && uda.Count > 0)
                        row["user_properties"] = new JObject(uda.Keys.Cast<object>().OrderBy(k => k.ToString()).Select(k => new JProperty(k.ToString(), JToken.FromObject(uda[k] ?? ""))));
                    if (names.Count > 0) row["report_properties"] = ReportValues(o, names);
                    items.Add(row);
                }
                return new JObject { ["items"] = items, ["not_found"] = new JArray(missing) };
            });
        }

        /// <summary>args: guids[], names[]</summary>
        public JObject ReportProperties(JObject a)
        {
            var m = _session.RequireModel();
            var (found, missing) = Fetch(m, a["guids"]);
            var names = a["names"].Values<string>().ToList();
            var items = new JArray(found.Select(o => new JObject
            {
                ["guid"] = o.Identifier.GUID.ToString(), ["id"] = o.Identifier.ID, ["type"] = o.GetType().Name,
                ["values"] = ReportValues(o, names)
            }));
            return new JObject { ["items"] = items, ["not_found"] = new JArray(missing) };
        }

        /// <summary>args: guids[] — changes the selection in the Tekla UI only.</summary>
        public JObject Select(JObject a)
        {
            var m = _session.RequireModel();
            var (found, missing) = Fetch(m, a["guids"]);
            bool ok = new TSM.UI.ModelObjectSelector().Select(new ArrayList(found));
            return new JObject { ["selected"] = ok ? found.Count : 0, ["select_ok"] = ok, ["not_found"] = new JArray(missing) };
        }

        /// <summary>args: guid (assembly, part or rebar)</summary>
        public JObject Assembly(JObject a)
        {
            var m = _session.RequireModel();
            return InGlobal(m, () =>
            {
                var (found, missing) = Fetch(m, new JArray(a["guid"]));
                if (found.Count == 0) throw new TeklaException("not_found", "Object not found: " + a["guid"]);
                var o = found[0];
                TSM.Assembly asm = o as TSM.Assembly ?? (o as TSM.Part)?.GetAssembly() ?? (o as TSM.Reinforcement)?.GetAssembly();
                if (asm == null) throw new TeklaException("no_assembly", "Object has no assembly: " + o.GetType().Name);

                var main = asm.GetMainPart() as TSM.ModelObject;
                var parts = new List<TSM.Part>();
                if (main is TSM.Part mp) parts.Add(mp);
                var secondaries = new JArray();
                foreach (var s in asm.GetSecondaries().OfType<TSM.ModelObject>())
                {
                    var row = Brief(s); AddQuantities(s, row); secondaries.Add(row);
                    if (s is TSM.Part sp) parts.Add(sp);
                }
                var bolts = new Dictionary<Guid, JObject>(); var welds = new Dictionary<Guid, JObject>();
                int rebarCount = 0; double rebarKg = 0;
                foreach (var p in parts)
                {
                    foreach (var b in Each(p.GetBolts())) if (!bolts.ContainsKey(b.Identifier.GUID)) bolts[b.Identifier.GUID] = Brief(b);
                    foreach (var w in Each(p.GetWelds())) if (!welds.ContainsKey(w.Identifier.GUID)) welds[w.Identifier.GUID] = Brief(w);
                    foreach (var r in Each(p.GetReinforcements())) { rebarCount++; double kg = 0; if (r.GetReportProperty("WEIGHT_TOTAL", ref kg)) rebarKg += kg; }
                }
                var asmRow = Brief(asm); AddQuantities(asm, asmRow);
                JObject mainRow = null; if (main != null) { mainRow = Brief(main); AddQuantities(main, mainRow); }
                return new JObject
                {
                    ["assembly"] = asmRow,
                    ["main_part"] = mainRow,
                    ["secondaries"] = secondaries,
                    ["sub_assemblies"] = new JArray(asm.GetSubAssemblies().OfType<TSM.ModelObject>().Select(Brief)),
                    ["bolts"] = new JArray(bolts.Values),
                    ["welds"] = new JArray(welds.Values),
                    ["reinforcement"] = new JObject { ["object_count"] = rebarCount, ["total_weight_kg"] = Math.Round(rebarKg, 3) }
                };
            });
        }

        public JObject Grids(JObject a)
        {
            var m = _session.RequireModel();
            return InGlobal(m, () =>
            {
                var sel = m.GetModelObjectSelector();
                var planes = Each(sel.GetAllObjectsWithType(new[] { typeof(TSM.GridPlane) })).OfType<TSM.GridPlane>().ToList();
                var grids = new JArray();
                foreach (var g in Each(sel.GetAllObjectsWithType(new[] { typeof(TSM.Grid) })).OfType<TSM.Grid>())
                {
                    var row = Brief(g);
                    row["planes"] = new JArray(planes.Where(p => p.Parent != null && p.Parent.Identifier.GUID == g.Identifier.GUID)
                        .Select(PlaneRow)
                        .OrderBy(p => (string)p["axis"]).ThenBy(p => (double?)p["coordinate_mm"] ?? 0));
                    grids.Add(row);
                }
                return new JObject { ["grids"] = grids };
            });
        }

        public JObject Workplane(JObject a)
        {
            var m = _session.RequireModel();
            var mx = m.GetWorkPlaneHandler().GetCurrentTransformationPlane().TransformationMatrixToGlobal;
            JArray Row(int r) => new JArray(R(mx[r, 0]), R(mx[r, 1]), R(mx[r, 2]));
            bool global = true;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 3; c++) if (Math.Abs(mx[r, c] - (r == c ? 1 : 0)) > 1e-9) global = false;
            return new JObject
            {
                ["is_global"] = global,
                ["origin_mm"] = Row(3), ["axis_x"] = Row(0), ["axis_y"] = Row(1), ["axis_z"] = Row(2),
                ["note"] = "Work plane expressed in global coordinates (origin in mm, axes as unit vectors). Other tekla_* tools always use global coordinates."
            };
        }

        /// <summary>args: kind (profile|material|bolt|rebar), filter, offset, limit</summary>
        public JObject Catalog(JObject a)
        {
            _session.RequireModel();
            string kind = (string)a["kind"], filter = (string)a["filter"];
            int offset = (int?)a["offset"] ?? 0, limit = (int?)a["limit"] ?? 100;
            var rows = CatalogRows(kind);
            var matched = rows.Where(r => Wildcard.IsMatch((string)r["name"], filter)).ToList();
            return new JObject
            {
                ["kind"] = kind, ["total"] = matched.Count, ["offset"] = offset,
                ["items"] = new JArray(matched.Skip(offset).Take(limit).Select(r => r.DeepClone()))
            };
        }

        // shortcut: catalog lists are cached for the life of this worker (a Tekla restart replaces the worker); catalog edits made in Tekla meanwhile are not seen.
        private readonly Dictionary<string, List<JObject>> _catalogCache = new Dictionary<string, List<JObject>>();

        private List<string> _parametricPrefixes;

        /// <summary>Parametric profile prefixes from the catalog ("" = plain numeric, e.g. "500*250"). Cached like CatalogRows.</summary>
        internal List<string> ParametricPrefixes()
        {
            if (_parametricPrefixes != null) return _parametricPrefixes;
            var list = new List<string>();
            var pe = new global::Tekla.Structures.Catalogs.CatalogHandler().GetParametricProfileItems();
            while (pe.MoveNext()) if (pe.Current is global::Tekla.Structures.Catalogs.ParametricProfileItem pp) list.Add(pp.ProfilePrefix ?? "");
            return _parametricPrefixes = list.Distinct().ToList();
        }

        internal List<JObject> CatalogRows(string kind)
        {
            if (_catalogCache.TryGetValue(kind ?? "", out var cached)) return cached;
            var cat = new global::Tekla.Structures.Catalogs.CatalogHandler();
            if (!cat.GetConnectionStatus()) throw new TeklaException("not_connected", "Catalog is not available.");
            var rows = new List<JObject>();
            switch (kind)
            {
                case "profile":
                    var pe = cat.GetLibraryProfileItems();
                    while (pe.MoveNext()) if (pe.Current is global::Tekla.Structures.Catalogs.LibraryProfileItem lp)
                        rows.Add(new JObject { ["name"] = lp.ProfileName, ["profile_type"] = lp.ProfileItemType.ToString() });
                    break;
                case "material":
                    var me = cat.GetMaterialItems();
                    while (me.MoveNext()) rows.Add(new JObject
                    {
                        ["name"] = me.Current.MaterialName, ["material_type"] = me.Current.Type.ToString(),
                        ["profile_density_kg_m3"] = me.Current.ProfileDensity, ["plate_density_kg_m3"] = me.Current.PlateDensity
                    });
                    break;
                case "bolt":
                    var be = cat.GetBoltItems();
                    while (be.MoveNext()) rows.Add(new JObject
                    {
                        ["name"] = be.Current.Standard + " M" + be.Current.Size, ["standard"] = be.Current.Standard, ["size_mm"] = be.Current.Size,
                        ["lengths_mm"] = new JArray(be.Current.Lengths ?? new List<double>())
                    });
                    break;
                case "rebar":
                    var re = cat.GetRebarItems();
                    while (re.MoveNext()) rows.Add(new JObject
                    {
                        ["name"] = re.Current.Grade + " " + re.Current.Size, ["grade"] = re.Current.Grade, ["size"] = re.Current.Size, ["usage"] = re.Current.Usage,
                        ["nominal_diameter_mm"] = re.Current.NominalDiameter, ["bend_radius_mm"] = re.Current.BendRadius, ["weight_kg_m"] = re.Current.WeightPerLenght, ["area_mm2"] = re.Current.CrossSectionArea
                    });
                    break;
                default: throw new TeklaException("invalid_kind", "kind must be profile, material, bolt or rebar.");
            }
            _catalogCache[kind] = rows;
            return rows;
        }

        /// <summary>args: template, file_name, selected, title1..3. Writes into the model's Reports folder; never overwrites.</summary>
        public JObject RunReport(JObject a)
        {
            var m = _session.RequireModel();
            string template = ((string)a["template"] ?? "").Trim();
            if (template.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase)) template = template.Substring(0, template.Length - 4);
            var available = ReportTemplates(m);
            if (!available.Contains(template, StringComparer.OrdinalIgnoreCase))
                throw new TeklaException("unknown_template", "Report template not found: '" + template + "'. Available: " + string.Join(", ", available));

            string reportsDir = Path.Combine(m.GetInfo().ModelPath, "Reports");
            string defaultExt = template.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) ? "" : ".xsr";
            string name = (string)a["file_name"];
            if (string.IsNullOrWhiteSpace(name)) name = template + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + defaultExt;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name))
                throw new TeklaException("invalid_file_name", "file_name must be a plain file name without folders.");
            string path = Path.Combine(reportsDir, name);
            if (File.Exists(path)) throw new TeklaException("file_exists", "File already exists (not overwritten): " + path);

            bool selected = (bool?)a["selected"] ?? false;
            string t1 = (string)a["title1"] ?? "", t2 = (string)a["title2"] ?? "", t3 = (string)a["title3"] ?? "";
            bool ok = selected
                ? TSM.Operations.Operation.CreateReportFromSelected(template, path, t1, t2, t3)
                : TSM.Operations.Operation.CreateReportFromAll(template, path, t1, t2, t3);
            var r = new JObject { ["template"] = template, ["scope"] = selected ? "selected" : "all", ["create_ok"] = ok, ["path"] = path, ["exists"] = File.Exists(path) };
            if (File.Exists(path))
            {
                var info = new FileInfo(path); r["size_bytes"] = info.Length;
                var bytes = File.ReadAllBytes(path);
                bool text = !bytes.Take(4096).Contains((byte)0);
                if (text)
                {
                    var lines = File.ReadAllLines(path);
                    r["line_count"] = lines.Length;
                    r["preview"] = string.Join("\n", lines.Take(80));
                }
            }
            return r;
        }

        // ---------- helpers ----------

        internal static T InGlobal<T>(TSM.Model m, Func<T> f)
        {
            var h = m.GetWorkPlaneHandler();
            var current = h.GetCurrentTransformationPlane();
            h.SetCurrentTransformationPlane(new TSM.TransformationPlane());
            try { return f(); }
            finally { h.SetCurrentTransformationPlane(current); }
        }

        internal static IEnumerable<TSM.ModelObject> Each(TSM.ModelObjectEnumerator en)
        {
            if (en == null) yield break;
            while (en.MoveNext()) if (en.Current != null) yield return en.Current;
        }

        /// <summary>Objects matching the worker filter args (types/selection/bbox/filter) with their brief rows.</summary>
        internal static List<(JObject row, TSM.ModelObject obj)> Filtered(TSM.Model m, JObject a)
        {
            var filter = a["filter"] as JObject ?? new JObject();
            var list = new List<(JObject, TSM.ModelObject)>();
            foreach (var o in Enumerate(m, a)) { var row = Brief(o); if (Matches(o, row, filter)) list.Add((row, o)); }
            return list;
        }

        private static IEnumerable<TSM.ModelObject> Enumerate(TSM.Model m, JObject a)
        {
            var names = a["types"]?.Values<string>().ToList() ?? new List<string>();
            bool selection = (bool?)a["selection"] ?? false;
            if (names.Count == 0 && !selection) names.Add("part");
            var types = names.Select(n => TypeMap.TryGetValue(n, out var t) ? t : throw new TeklaException("invalid_type", "Unknown object type: " + n)).ToArray();

            TSM.ModelObjectEnumerator en;
            if (selection) en = new TSM.UI.ModelObjectSelector().GetSelectedObjects();
            else if (a["bbox"] is JObject bb) en = m.GetModelObjectSelector().GetObjectsByBoundingBox(Pt(bb["min"]), Pt(bb["max"]));
            else en = m.GetModelObjectSelector().GetAllObjectsWithType(types);

            bool checkType = selection || a["bbox"] is JObject;
            foreach (var o in Each(en))
                if (!checkType || types.Length == 0 || types.Any(t => t.IsInstanceOfType(o))) yield return o;
        }

        private static bool Matches(TSM.ModelObject o, JObject row, JObject f)
        {
            foreach (var key in new[] { "profile", "material", "name", "class" })
                if (f[key] != null && !Wildcard.IsMatch((string)row[key], (string)f[key])) return false;
            if (f["phase"] != null && (int?)row["phase"] != (int)f["phase"]) return false;
            if (f["uda"] is JArray udas)
                foreach (var u in udas.OfType<JObject>())
                    if (!Wildcard.IsMatch(UserValue(o, (string)u["name"]), (string)u["value"])) return false;
            return true;
        }

        private static string UserValue(TSM.ModelObject o, string name)
        {
            string s = null; if (o.GetUserProperty(name, ref s) && !string.IsNullOrEmpty(s)) return s;
            double d = 0; if (o.GetUserProperty(name, ref d) && d != 0) return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            int i = 0; if (o.GetUserProperty(name, ref i) && i != 0) return i.ToString();
            return "";
        }

        internal static (List<TSM.ModelObject> found, List<string> missing) Fetch(TSM.Model m, JToken guids)
        {
            var wanted = guids.Values<string>().Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var got = m.FetchModelObjects(wanted, true).Where(o => o != null).ToList();
            var gotSet = new HashSet<string>(got.Select(o => o.Identifier.GUID.ToString()), StringComparer.OrdinalIgnoreCase);
            return (got, wanted.Where(g => !gotSet.Contains(g)).ToList());
        }

        private static JObject ReportValues(TSM.ModelObject o, IEnumerable<string> names)
        {
            var v = new JObject();
            foreach (var n in names)
            {
                double d = 0; int i = 0; string s = null;
                if (o.GetReportProperty(n, ref d)) v[n] = d;
                else if (o.GetReportProperty(n, ref i)) v[n] = i;
                else if (o.GetReportProperty(n, ref s)) v[n] = s;
                else v[n] = null;
            }
            return v;
        }

        internal static JObject Brief(TSM.ModelObject o)
        {
            var r = new JObject { ["guid"] = o.Identifier.GUID.ToString(), ["id"] = o.Identifier.ID, ["type"] = o.GetType().Name };
            if (o.GetPhase(out TSM.Phase ph)) r["phase"] = ph.PhaseNumber;
            switch (o)
            {
                case TSM.Part p:
                    r["name"] = p.Name; r["profile"] = p.Profile.ProfileString; r["material"] = p.Material.MaterialString; r["class"] = p.Class;
                    // Tekla reports group by the PROFILE report property, which can differ from the entered profile (e.g. "800*800" -> "800x200").
                    r["report_profile"] = Str(o, "PROFILE") ?? p.Profile.ProfileString;
                    r["assembly_pos"] = Str(o, "ASSEMBLY_POS"); r["part_pos"] = Str(o, "PART_POS");
                    if (p is TSM.Beam b) { r["start_mm"] = P(b.StartPoint); r["end_mm"] = P(b.EndPoint); }
                    break;
                case TSM.Reinforcement re:
                    r["name"] = re.Name; r["material"] = re.Grade; r["profile"] = Str(o, "SIZE"); r["report_profile"] = r["profile"]; r["class"] = re.Class.ToString();
                    r["note"] = "rebar: profile = bar size, material = grade";
                    if (re.Father != null) r["father_guid"] = re.Father.Identifier.GUID.ToString();
                    break;
                case TSM.RebarSet rs:
                    r["name"] = rs.RebarProperties?.Name; r["material"] = rs.RebarProperties?.Grade; r["profile"] = rs.RebarProperties?.Size;
                    r["class"] = rs.RebarProperties?.Class.ToString();
                    if (rs.FatherPart != null) r["father_guid"] = rs.FatherPart.Identifier.GUID.ToString();
                    break;
                case TSM.BoltGroup bg:
                    r["name"] = bg.BoltStandard + " M" + bg.BoltSize; r["profile"] = "M" + bg.BoltSize; r["material"] = bg.BoltStandard;
                    r["bolt_standard"] = bg.BoltStandard; r["bolt_size_mm"] = bg.BoltSize; r["bolt_count"] = bg.BoltPositions?.Count ?? 0;
                    if (bg.PartToBoltTo != null) r["part_to_bolt_to_guid"] = bg.PartToBoltTo.Identifier.GUID.ToString();
                    if (bg.PartToBeBolted != null) r["part_to_be_bolted_guid"] = bg.PartToBeBolted.Identifier.GUID.ToString();
                    break;
                case TSM.BaseWeld w:
                    r["size_above_mm"] = w.SizeAbove; r["type_above"] = w.TypeAbove.ToString(); r["shop_weld"] = w.ShopWeld;
                    if (w.MainObject != null) r["main_object_guid"] = w.MainObject.Identifier.GUID.ToString();
                    if (w.SecondaryObject != null) r["secondary_object_guid"] = w.SecondaryObject.Identifier.GUID.ToString();
                    break;
                case TSM.Assembly asm:
                    r["name"] = asm.Name; r["assembly_pos"] = Str(o, "ASSEMBLY_POS");
                    if (asm.GetMainPart() is TSM.ModelObject main) r["main_part_guid"] = main.Identifier.GUID.ToString();
                    break;
                case TSM.BaseComponent c:
                    r["name"] = c.Name; r["number"] = c.Number;
                    break;
                case TSM.Grid g:
                    r["coordinate_x"] = g.CoordinateX; r["coordinate_y"] = g.CoordinateY; r["coordinate_z"] = g.CoordinateZ;
                    r["label_x"] = g.LabelX; r["label_y"] = g.LabelY; r["label_z"] = g.LabelZ;
                    break;
                case TSM.GridPlane gp:
                    foreach (var p in PlaneRow(gp).Properties()) r[p.Name] = p.Value;
                    break;
            }
            return r;
        }

        private static readonly (string prop, string field, int digits)[] QtyProps =
            { ("WEIGHT", "weight_kg", 4), ("LENGTH", "length_mm", 2), ("AREA", "area_mm2", 1), ("VOLUME", "volume_mm3", 1) };

        /// <summary>
        /// Quantities for many objects with one bulk Open API call per property (Model.GetReportPropertyDouble).
        /// The bulk call silently drops objects that lack the property (Phase 2 probe), so a chunk whose result
        /// count differs from its input count falls back to per-object calls.
        /// </summary>
        private static readonly string[] RebarProps = { "WEIGHT_TOTAL", "WEIGHT", "LENGTH", "LENGTH_MAX", "LENGTH_MIN" };

        private static void AddQuantitiesBulk(TSM.Model m, List<(JObject row, TSM.ModelObject obj)> items)
        {
            var rebars = items.Where(p => p.obj is TSM.Reinforcement).ToList();
            var others = items.Where(p => !(p.obj is TSM.Reinforcement)).ToList();
            foreach (var (prop, field, digits) in QtyProps)
            {
                var values = BulkDoubles(m, others, prop);
                for (int i = 0; i < others.Count; i++) if (values[i].HasValue) others[i].row[field] = Math.Round(values[i].Value, digits);
            }
            var rv = RebarProps.ToDictionary(p => p, p => BulkDoubles(m, rebars, p));
            for (int i = 0; i < rebars.Count; i++)
                SetRebar(rebars[i].row, (TSM.Reinforcement)rebars[i].obj, k => rv[k][i] ?? 0);
        }

        /// <summary>
        /// One Model.GetReportPropertyDouble call per 2000 objects. That bulk call silently drops objects lacking the
        /// property (Phase 2 probe), so a chunk whose result count differs falls back to per-object calls (null = missing).
        /// </summary>
        private static double?[] BulkDoubles(TSM.Model m, List<(JObject row, TSM.ModelObject obj)> items, string prop)
        {
            var result = new double?[items.Count];
            const int chunk = 2000;
            for (int start = 0; start < items.Count; start += chunk)
            {
                var part = items.Skip(start).Take(chunk).ToList();
                var values = m.GetReportPropertyDouble(part.Select(p => p.obj.Identifier).ToList(), prop);
                for (int i = 0; i < part.Count; i++)
                {
                    if (values != null && values.Count == part.Count) result[start + i] = values[i];
                    else { double v = 0; if (part[i].obj.GetReportProperty(prop, ref v)) result[start + i] = v; }
                }
            }
            return result;
        }

        internal static void AddQuantities(TSM.ModelObject o, JObject r)
        {
            if (o is TSM.Reinforcement re)
            {
                SetRebar(r, re, k => { double v = 0; o.GetReportProperty(k, ref v); return v; });
                return;
            }
            foreach (var (prop, field, digits) in QtyProps)
            {
                double v = 0; if (o.GetReportProperty(prop, ref v)) r[field] = Math.Round(v, digits);
            }
        }

        /// <summary>Rebar quantities: weight = WEIGHT_TOTAL (whole group), length = total of all bars, bar_length_mm = one bar.</summary>
        private static void SetRebar(JObject r, TSM.Reinforcement re, Func<string, double> prop)
        {
            int bars = re.GetNumberOfRebars();
            double length = prop("LENGTH");
            r["number_of_bars"] = bars;
            r["bar_length_mm"] = Math.Round(length, 2);
            r["weight_kg"] = Math.Round(prop("WEIGHT_TOTAL"), 4);
            r["length_mm"] = Math.Round(TeklaText.RebarTotalLength(length, prop("LENGTH_MAX"), prop("LENGTH_MIN"), prop("WEIGHT"), prop("WEIGHT_TOTAL"), bars), 2);
        }

        private static void AddGeometry(TSM.ModelObject o, JObject r)
        {
            TSM.Solid solid = null;
            switch (o)
            {
                case TSM.ContourPlate cp: r["contour_mm"] = new JArray(cp.Contour.ContourPoints.OfType<T3D.Point>().Select(P)); solid = cp.GetSolid(); break;
                case TSM.PolyBeam pb: r["contour_mm"] = new JArray(pb.Contour.ContourPoints.OfType<T3D.Point>().Select(P)); solid = pb.GetSolid(); break;
                case TSM.Part p: solid = p.GetSolid(); break;
                case TSM.Reinforcement re: solid = re.GetSolid(); break;
                case TSM.BoltGroup bg: r["bolt_positions_mm"] = new JArray(bg.BoltPositions.OfType<T3D.Point>().Select(P)); solid = bg.GetSolid(); break;
            }
            if (solid != null) r["bounding_box_mm"] = new JObject { ["min"] = P(solid.MinimumPoint), ["max"] = P(solid.MaximumPoint) };
        }

        private static JObject PlaneRow(TSM.GridPlane gp)
        {
            var o = gp.Plane.Origin; var ax = gp.Plane.AxisX; var ay = gp.Plane.AxisY;
            var (axis, coord) = TeklaText.PlaneAxis(o.X, o.Y, o.Z, ax.X, ax.Y, ax.Z, ay.X, ay.Y, ay.Z);
            return new JObject { ["label"] = gp.Label, ["axis"] = axis, ["coordinate_mm"] = coord == null ? null : (JToken)R(coord.Value), ["origin_mm"] = P(o) };
        }

        private static List<string> ReportTemplates(TSM.Model m)
        {
            var dirs = new List<string> { m.GetInfo().ModelPath };
            foreach (var opt in new[] { "XS_TEMPLATE_DIRECTORY", "XS_TEMPLATE_DIRECTORY_SYSTEM" })
            {
                string v = null; global::Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption(opt, ref v);
                if (!string.IsNullOrEmpty(v)) dirs.AddRange(v.Split(';').Where(d => d.Trim().Length > 0));
            }
            return dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.rpt"))
                       .Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        }

        private static string Str(TSM.ModelObject o, string name) { string s = null; return o.GetReportProperty(name, ref s) ? s : null; }
        private static double R(double v) => Math.Round(v, 3);
        internal static JArray P(T3D.Point p) => p == null ? null : new JArray(R(p.X), R(p.Y), R(p.Z));
        internal static T3D.Point Pt(JToken t) { var a = t.Values<double>().ToArray(); return new T3D.Point(a[0], a[1], a[2]); }
    }
}
