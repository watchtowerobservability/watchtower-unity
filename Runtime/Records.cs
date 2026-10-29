using System;
using System.Collections.Generic;

namespace Watchtower
{
    /// <summary>Turns Unity log callbacks into records and keeps a bounded, thread-safe buffer.</summary>
    public static class Records
    {
        public static long NowNano() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;

        /// <param name="logType">UnityEngine.LogType name: Error, Assert, Warning, Log, Exception.</param>
        /// <returns>null when the record is below the capture level.</returns>
        public static LogRecord FromUnityLog(WatchtowerOptions o, string condition, string stack, string logType, string traceId, string spanId)
        {
            var sev = logType == "Warning" ? 13 : logType == "Log" ? 9 : 17;
            if (sev == 9 && !o.CaptureInfo) return null;
            var r = new LogRecord { TimeUnixNano = NowNano(), Severity = sev, TraceId = traceId ?? "", SpanId = spanId ?? "" };
            condition = Redact.Scrub(condition);
            r.Body = condition;
            if (logType == "Exception")
            {
                // Unity formats exceptions as "Type: message"
                var i = condition.IndexOf(':');
                r.Attributes["exception.type"] = i > 0 ? condition.Substring(0, i) : "Exception";
                r.Attributes["exception.message"] = i > 0 ? condition.Substring(i + 1).Trim() : condition;
                r.Attributes["exception.stacktrace"] = Redact.Scrub(stack);
                r.Attributes["event.name"] = "exception";
            }
            else if (sev >= 17) r.Attributes["log.stacktrace"] = Redact.Scrub(stack);
            r.Attributes["unity.log_type"] = logType;
            if (o.BeforeSend != null) r = o.BeforeSend(r);
            return r;
        }
    }

    public sealed class RecordBuffer
    {
        readonly object gate = new object();
        readonly LinkedList<LogRecord> q = new LinkedList<LogRecord>();
        readonly int max;
        public RecordBuffer(int max) { this.max = max; }

        public int Count { get { lock (gate) return q.Count; } }

        /// <summary>Drops the oldest record when full.</summary>
        public void Add(LogRecord r)
        {
            lock (gate) { q.AddLast(r); while (q.Count > max) q.RemoveFirst(); }
        }

        public List<LogRecord> Drain(int n)
        {
            lock (gate)
            {
                var l = new List<LogRecord>();
                while (l.Count < n && q.Count > 0) { l.Add(q.First.Value); q.RemoveFirst(); }
                return l;
            }
        }

        /// <summary>Put a failed batch back in front, still bounded.</summary>
        public void Requeue(List<LogRecord> batch)
        {
            lock (gate)
            {
                for (var i = batch.Count - 1; i >= 0; i--) q.AddFirst(batch[i]);
                while (q.Count > max) q.RemoveFirst();
            }
        }
    }
}
