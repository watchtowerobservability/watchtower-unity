using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Watchtower
{
    /// <summary>OTLP/HTTP JSON logs encoder. No UnityEngine dependency so it is unit-testable anywhere.</summary>
    public static class OtlpJson
    {
        public const string SdkVersion = "0.1.0";

        public static string Esc(string s)
        {
            var b = new StringBuilder(s.Length + 2);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }

        static void Attr(StringBuilder b, string k, string v, ref bool first)
        {
            if (!first) b.Append(',');
            first = false;
            b.Append("{\"key\":\"").Append(Esc(k)).Append("\",\"value\":{\"stringValue\":\"").Append(Esc(v)).Append("\"}}");
        }

        public static string Build(WatchtowerOptions o, IEnumerable<LogRecord> records)
        {
            var b = new StringBuilder();
            b.Append("{\"resourceLogs\":[{\"resource\":{\"attributes\":[");
            var f = true;
            Attr(b, "service.name", o.Service, ref f);
            Attr(b, "deployment.environment.name", o.DeploymentEnvironment, ref f);
            if (!string.IsNullOrEmpty(o.Release)) Attr(b, "service.version", o.Release, ref f);
            Attr(b, "telemetry.sdk.name", "watchtower-unity", ref f);
            Attr(b, "telemetry.sdk.language", "dotnet", ref f);
            Attr(b, "telemetry.sdk.version", SdkVersion, ref f);
            b.Append("]},\"scopeLogs\":[{\"scope\":{\"name\":\"watchtower-unity\",\"version\":\"" + SdkVersion + "\"},\"logRecords\":[");
            var firstRec = true;
            foreach (var r in records)
            {
                if (!firstRec) b.Append(',');
                firstRec = false;
                b.Append("{\"timeUnixNano\":\"").Append(r.TimeUnixNano.ToString(CultureInfo.InvariantCulture))
                 .Append("\",\"severityNumber\":").Append(r.Severity.ToString(CultureInfo.InvariantCulture))
                 .Append(",\"severityText\":\"").Append(SeverityText(r.Severity))
                 .Append("\",\"body\":{\"stringValue\":\"").Append(Esc(r.Body)).Append("\"}");
                if (r.TraceId.Length == 32) b.Append(",\"traceId\":\"").Append(r.TraceId).Append('"');
                if (r.SpanId.Length == 16) b.Append(",\"spanId\":\"").Append(r.SpanId).Append('"');
                b.Append(",\"attributes\":[");
                var fa = true;
                foreach (var kv in r.Attributes)
                {
                    // incoming wt.* attributes are never ours to set; the gateway stamps tenant identity
                    if (kv.Key.StartsWith("wt.")) continue;
                    Attr(b, kv.Key, kv.Value, ref fa);
                }
                b.Append("]}");
            }
            b.Append("]}]}]}");
            return b.ToString();
        }

        public static string SeverityText(int n) => n >= 21 ? "FATAL" : n >= 17 ? "ERROR" : n >= 13 ? "WARN" : n >= 9 ? "INFO" : "DEBUG";

        /// <summary>Parses a W3C traceparent ("00-trace-span-01"); returns false when malformed.</summary>
        public static bool TryParseTraceparent(string tp, out string trace, out string span)
        {
            trace = span = "";
            var p = (tp ?? "").Split('-');
            if (p.Length < 4 || p[1].Length != 32 || p[2].Length != 16) return false;
            foreach (var c in p[1] + p[2]) if (!System.Uri.IsHexDigit(c)) return false;
            trace = p[1].ToLowerInvariant();
            span = p[2].ToLowerInvariant();
            return true;
        }
    }
}
