using System;
using System.Text.RegularExpressions;

namespace TeklaMcp.Core
{
    /// <summary>Case-insensitive match with * and ? wildcards. A pattern without wildcards must match the whole value.</summary>
    internal static class Wildcard
    {
        public static bool IsMatch(string value, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            string rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(value ?? "", rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
