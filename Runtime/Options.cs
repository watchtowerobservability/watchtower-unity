using System;
using System.Collections.Generic;

namespace Watchtower
{
    /// <summary>One log or error record before export.</summary>
    public sealed class LogRecord
    {
        public long TimeUnixNano;
        public int Severity = 9;
        public string Body = "";
        public string TraceId = "";
        public string SpanId = "";
        public Dictionary<string, string> Attributes = new Dictionary<string, string>();
    }

    public sealed class WatchtowerOptions
    {
        /// <summary>Publishable ingest key. Never ship a secret key in a game build.</summary>
        public string Key = System.Environment.GetEnvironmentVariable("WT_PUBLISHABLE_KEY") ?? "";
        public string Endpoint = System.Environment.GetEnvironmentVariable("WT_OTLP_ENDPOINT") ?? "http://localhost:4318";
        public string Service = System.Environment.GetEnvironmentVariable("WT_SERVICE") ?? "unity-game";
        public string DeploymentEnvironment = System.Environment.GetEnvironmentVariable("WT_ENVIRONMENT") ?? "production";
        public string Release = System.Environment.GetEnvironmentVariable("WT_RELEASE") ?? "";
        /// <summary>Capture Debug.Log at Info level too (default: warnings and above).</summary>
        public bool CaptureInfo = false;
        public int MaxBuffered = 500;
        /// <summary>Return null to drop the record; edit it to redact. Runs after the default redaction.</summary>
        public Func<LogRecord, LogRecord> BeforeSend;

        /// <summary>Never prints the key.</summary>
        public override string ToString() =>
            $"WatchtowerOptions(Key={Redact.MaskKey(Key)}, Endpoint={Endpoint}, Service={Service}, Environment={DeploymentEnvironment}, Release={Release})";
    }
}
