using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Watchtower
{
    /// <summary>Entry point: <c>WatchtowerSdk.Init(new WatchtowerOptions { Key = "wtp_..." })</c>.</summary>
    public static class WatchtowerSdk
    {
        static WatchtowerOptions opts;
        static RecordBuffer buffer;
        static Pump pump;
        static string traceId = "", spanId = "";
        static string PendingPath => Path.Combine(Application.persistentDataPath, "watchtower-pending.json");

        public static void Init(WatchtowerOptions o)
        {
            if (pump != null) return;
            if (string.IsNullOrEmpty(o.Key)) { Debug.LogWarning("[watchtower] no key set; capture disabled"); return; }
            if (o.Key.StartsWith("wti_")) { Debug.LogError("[watchtower] refusing a secret ingest key in a game build; use a publishable key"); return; }
            if (string.IsNullOrEmpty(o.Release)) o.Release = Application.version;
            opts = o;
            buffer = new RecordBuffer(o.MaxBuffered);
            var go = new GameObject("Watchtower") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            pump = go.AddComponent<Pump>();
            Application.logMessageReceivedThreaded += OnLog;
            pump.StartCoroutine(pump.Loop());
        }

        /// <summary>Link following records to a trace (W3C traceparent), e.g. from your backend's response header.</summary>
        public static void SetTraceContext(string traceparent)
        {
            if (OtlpJson.TryParseTraceparent(traceparent, out var t, out var s)) { traceId = t; spanId = s; }
        }

        public static void CaptureException(System.Exception e)
        {
            Add(Records.FromUnityLog(opts, e.GetType().Name + ": " + e.Message, e.StackTrace, "Exception", traceId, spanId));
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (opts == null || condition.StartsWith("[watchtower]")) return;
            Add(Records.FromUnityLog(opts, condition, stack, type.ToString(), traceId, spanId));
        }

        static void Add(LogRecord r) { if (r != null && buffer != null) buffer.Add(r); }

        sealed class Pump : MonoBehaviour
        {
            bool sentPending;

            public IEnumerator Loop()
            {
                while (true)
                {
                    yield return new WaitForSecondsRealtime(5f);
                    yield return Flush();
                }
            }

            IEnumerator Flush()
            {
                if (!sentPending)
                {
                    sentPending = true;
                    if (File.Exists(PendingPath))
                    {
                        var saved = File.ReadAllText(PendingPath);
                        File.Delete(PendingPath);
                        yield return Post(saved, null);
                    }
                }
                var batch = buffer.Drain(100);
                if (batch.Count == 0) yield break;
                yield return Post(OtlpJson.Build(opts, batch), batch);
            }

            IEnumerator Post(string body, System.Collections.Generic.List<LogRecord> batch)
            {
                using (var req = new UnityWebRequest(opts.Endpoint.TrimEnd('/') + "/v1/logs", "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.SetRequestHeader("Authorization", "Bearer " + opts.Key);
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success && batch != null) buffer.Requeue(batch);
                }
            }

            // Offline buffer: whatever is still queued is written out and sent on the next launch.
            void OnApplicationQuit()
            {
                if (buffer == null || buffer.Count == 0) return;
                File.WriteAllText(PendingPath, OtlpJson.Build(opts, buffer.Drain(opts.MaxBuffered)));
            }
        }
    }
}
