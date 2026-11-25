using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TeklaMcp.Core
{
    internal enum TokenCheck { Ok, Missing, Unknown, Expired, WrongTool, ArgumentsChanged }

    /// <summary>
    /// Issues single-use confirm tokens bound to (tool name, canonical arguments) with an expiry.
    /// In-memory only: restarting the server invalidates all tokens.
    /// </summary>
    internal sealed class ConfirmTokenStore
    {
        private sealed class Entry { public string Tool; public string ArgsHash; public DateTime ExpiresUtc; }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly TimeSpan _lifetime;
        private readonly Func<DateTime> _utcNow;

        public ConfirmTokenStore(TimeSpan lifetime, Func<DateTime> utcNow = null)
        {
            _lifetime = lifetime;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public string Issue(string tool, JObject args, out DateTime expiresUtc)
        {
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = "ct_" + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            expiresUtc = _utcNow() + _lifetime;
            lock (_sync)
            {
                Purge();
                _entries[token] = new Entry { Tool = tool, ArgsHash = HashArgs(args), ExpiresUtc = expiresUtc };
            }
            return token;
        }

        /// <summary>Checks and, on success, consumes the token. A failed check does not consume it (except when expired).</summary>
        public TokenCheck Consume(string token, string tool, JObject args)
        {
            if (string.IsNullOrWhiteSpace(token)) return TokenCheck.Missing;
            lock (_sync)
            {
                if (!_entries.TryGetValue(token, out var e)) return TokenCheck.Unknown;
                if (_utcNow() > e.ExpiresUtc) { _entries.Remove(token); return TokenCheck.Expired; }
                if (!string.Equals(e.Tool, tool, StringComparison.Ordinal)) return TokenCheck.WrongTool;
                if (!string.Equals(e.ArgsHash, HashArgs(args), StringComparison.Ordinal)) return TokenCheck.ArgumentsChanged;
                _entries.Remove(token);
                return TokenCheck.Ok;
            }
        }

        private void Purge()
        {
            var now = _utcNow();
            foreach (var k in _entries.Where(kv => now > kv.Value.ExpiresUtc).Select(kv => kv.Key).ToList()) _entries.Remove(k);
        }

        /// <summary>SHA-256 of the arguments without mode/confirm_token, with object keys sorted.</summary>
        public static string HashArgs(JObject args)
        {
            var copy = (JObject)(args ?? new JObject()).DeepClone();
            copy.Remove(SchemaBuilder.ModeProperty);
            copy.Remove(SchemaBuilder.TokenProperty);
            string canonical = Canonical(copy).ToString(Formatting.None);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "");
        }

        private static JToken Canonical(JToken t)
        {
            if (t is JObject o)
                return new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))));
            if (t is JArray a) return new JArray(a.Select(Canonical));
            return t.DeepClone();
        }
    }
}
