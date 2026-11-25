using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeklaMcp.Tekla
{
    /// <summary>Pure helpers on Tekla strings. No Tekla API types (unit-testable without Tekla).</summary>
    internal static class TeklaText
    {
        /// <summary>
        /// The Open API has no "environment name" property (Phase 0). XS_SYSTEM lists the environment folders,
        /// e.g. ...\environments\se_asia\General\ → "se_asia". Returns the first non-"common" folder, or null.
        /// </summary>
        public static string EnvironmentFromSystemPath(string xsSystem)
        {
            if (string.IsNullOrEmpty(xsSystem)) return null;
            foreach (Match m in Regex.Matches(xsSystem, @"\\environments\\([^\\;]+)\\", RegexOptions.IgnoreCase))
            {
                string env = m.Groups[1].Value;
                if (!env.Equals("common", StringComparison.OrdinalIgnoreCase)) return env;
            }
            return null;
        }

        /// <summary>
        /// Total length (mm) of a reinforcement object from its report properties. For rebar groups LENGTH and WEIGHT
        /// describe ONE bar while WEIGHT_TOTAL covers the whole group (Phase 2 probe: 18.96 kg x 125 bars = 2370 kg).
        /// Equal bars: LENGTH x bars. Varying bars: WEIGHT_TOTAL / (WEIGHT / LENGTH), i.e. total weight over one bar's kg per mm.
        /// </summary>
        public static double RebarTotalLength(double length, double lengthMax, double lengthMin, double weight, double weightTotal, int bars)
        {
            if (bars <= 1) return length;
            if (Math.Abs(lengthMax - lengthMin) < 0.5) return length * bars;
            if (weight > 0 && length > 0) return weightTotal / (weight / length);
            return length * bars;
        }

        private static readonly Regex ProfileParams = new Regex(@"^[0-9]+(\.[0-9]+)?([*xX/-][0-9]+(\.[0-9]+)?)*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Profile name check (Profile.ParseProfileString does not validate; Phase 3 probe). Valid when it is a library
        /// profile (case-insensitive; returns the catalog spelling) or a parametric prefix followed by numeric
        /// parameters, e.g. "PL20*200", "D100", or "500*250" (empty prefix). Returns null when not valid.
        /// </summary>
        public static string NormalizeProfile(string profile, IEnumerable<string> libraryNames, IEnumerable<string> parametricPrefixes)
        {
            if (string.IsNullOrWhiteSpace(profile)) return null;
            profile = profile.Trim();
            var lib = libraryNames.FirstOrDefault(n => string.Equals(n, profile, StringComparison.OrdinalIgnoreCase));
            if (lib != null) return lib;
            foreach (var prefix in parametricPrefixes.OrderByDescending(p => p.Length))
                if (profile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && ProfileParams.IsMatch(profile.Substring(prefix.Length)))
                    return profile;
            return null;
        }

        /// <summary>Object type names accepted by query tools (mapped to Tekla classes in TeklaQuery).</summary>
        public static readonly string[] ObjectTypes = { "part", "beam", "polybeam", "contour_plate", "bolt", "weld", "rebar", "rebar_set", "assembly", "component", "grid", "grid_plane" };

        /// <summary>
        /// Grid plane direction from its plane axes: the plane normal (AxisX × AxisY) along global x/y/z.
        /// Returns ("x"|"y"|"z", coordinate along that axis in mm) or ("other", null) for skewed planes.
        /// </summary>
        public static (string axis, double? coordinate) PlaneAxis(double ox, double oy, double oz, double ax, double ay, double az, double bx, double by, double bz)
        {
            double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) return ("other", null);
            nx /= len; ny /= len; nz /= len;
            const double tol = 1e-6;
            if (Math.Abs(Math.Abs(nx) - 1) < tol) return ("x", ox);
            if (Math.Abs(Math.Abs(ny) - 1) < tol) return ("y", oy);
            if (Math.Abs(Math.Abs(nz) - 1) < tol) return ("z", oz);
            return ("other", null);
        }

        /// <summary>
        /// True when the exception chain shows a dead Tekla remoting channel, e.g. after Tekla was restarted:
        /// InvalidOperationException "Cannot find remote object with id ..." (seen in Phase 1) or Trimble.Remoting.* errors.
        /// shortcut: matched by type name/message because Tekla exposes no public exception type for it; recheck on Tekla upgrades.
        /// </summary>
        public static bool IsConnectionLost(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.GetType().FullName.StartsWith("Trimble.Remoting", StringComparison.Ordinal)) return true;
                if ((e.Message ?? "").IndexOf("remote object", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
