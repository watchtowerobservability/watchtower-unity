using System.Text.RegularExpressions;

namespace Watchtower
{
    public static class Redact
    {
        static readonly Regex Secret = new Regex(@"(?i)(bearer\s+[A-Za-z0-9._~+/=-]+|wt[pi]_[A-Za-z0-9_]+)", RegexOptions.Compiled);

        public static string MaskKey(string key) =>
            string.IsNullOrEmpty(key) ? "" : key.Length <= 8 ? "****" : key.Substring(0, 4) + "****";

        /// <summary>Strips bearer tokens and Watchtower keys from free text (messages, stack traces).</summary>
        public static string Scrub(string s) => s == null ? "" : Secret.Replace(s, "[redacted]");
    }
}
