using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Watchtower
{
    /// <summary>Entry point: <c>WatchtowerSdk.Init(new WatchtowerOptions { Key = "wtp_..." })</c>.</summary>
    public static class WatchtowerSdk
    {
        static WatchtowerOptions opts;
        static RecordBuffer buffer;
        static Pump pump;
        static string traceId = "", spanId = "";
        // RUM (scene views, launch, frames, hangs): in memory, sent with the logs; the pending file holds logs only
        static readonly List<RumEvent> rumQueue = new List<RumEvent>();
        static readonly string sessionId = Guid.NewGuid().ToString();
        static readonly FrameStats frames = new FrameStats();
        static ViewTracker views;
        static HangWatch hangWatch;
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static string RumPendingPath => Path.Combine(Application.persistentDataPath, "watchtower-rum-pending.json");
        static bool launched;
        static string PendingPath => Path.Combine(Application.persistentDataPath, "watchtower-pending.json");

        public static void Init(WatchtowerOptions o)
        {
            if (pump != null) return;
            if (string.IsNullOrEmpty(o.Key)) { Debug.LogWarning("[watchtower] no key set; capture disabled"); return; }
            if (o.Key.StartsWith("wti_")) { Debug.LogError("[watchtower] refusing a secret ingest key in a game build; use a publishable key"); return; }
            if (string.IsNullOrEmpty(o.Release)) o.Release = Application.version;
            if (string.IsNullOrEmpty(o.AppId)) o.AppId = Application.identifier;
            opts = o;
            buffer = new RecordBuffer(o.MaxBuffered);
            var go = new GameObject("Watchtower") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            pump = go.AddComponent<Pump>();
            Application.logMessageReceivedThreaded += OnLog;
            if (o.TrackRum)
            {
                views = new ViewTracker(o.Service);
                SceneManager.sceneLoaded += (scene, mode) => OnScene(scene.name);
                OnScene(SceneManager.GetActiveScene().name);
                hangWatch = new HangWatch(o.HangSeconds * 1000, clock.ElapsedMilliseconds);
                var t = new System.Threading.Thread(WatchHangs) { IsBackground = true, Name = "wt-hang" };
                t.Start();
            }
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

        static void AddRum(RumEvent e)
        {
            lock (rumQueue) { rumQueue.Add(e); if (rumQueue.Count > opts.MaxBuffered) rumQueue.RemoveAt(0); }
        }

        // A watchdog thread: the main thread beats every frame, so a stall (even one that never ends)
        // is reported once it passes HangSeconds, with its duration so far.
        static void WatchHangs()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(1000);
                var ms = hangWatch.Poll(clock.ElapsedMilliseconds);
                if (ms.HasValue) AddRum(RumJson.Hang(ms.Value, views != null ? views.Current : null));
            }
        }

        static void OnScene(string name)
        {
            var prev = views.Current;
            var v = views.Appeared(name, (long)(Time.realtimeSinceStartup * 1000));
            if (v == null) return;
            // frame counters belong to the screen that just ended
            foreach (var f in frames.Drain(prev)) AddRum(f);
            AddRum(v);
        }

        sealed class Pump : MonoBehaviour
        {
            bool sentPending, sentRumPending;

            public IEnumerator Loop()
            {
                while (true)
                {
                    yield return new WaitForSecondsRealtime(5f);
                    yield return Flush();
                }
            }

            void Update()
            {
                if (opts == null || !opts.TrackRum) return;
                if (!launched)
                {
                    launched = true;
                    AddRum(RumJson.ColdLaunch(Time.realtimeSinceStartup * 1000.0));
                }
                hangWatch.Beat(clock.ElapsedMilliseconds);
                frames.Add(Time.unscaledDeltaTime, double.MaxValue); // slow and frozen counts; hangs come from the watchdog
            }

            // Mobile apps are paused, not quit: keep the RUM events on disk, and send them next launch if the process dies.
            void OnApplicationPause(bool paused)
            {
                if (opts == null || !opts.TrackRum) return;
                hangWatch.SetPaused(paused, clock.ElapsedMilliseconds);
                if (paused) PersistRum();
                else if (File.Exists(RumPendingPath)) File.Delete(RumPendingPath);
            }

            void PersistRum()
            {
                string body;
                lock (rumQueue) body = RumPending.Serialize(opts, sessionId, RumJson.OsName(Application.platform.ToString()), SystemInfo.deviceModel, rumQueue);
                if (body != null) File.WriteAllText(RumPendingPath, body);
            }

            IEnumerator FlushRum()
            {
                if (!sentRumPending)
                {
                    sentRumPending = true;
                    if (File.Exists(RumPendingPath))
                    {
                        var saved = File.ReadAllText(RumPendingPath);
                        File.Delete(RumPendingPath);
                        yield return Post("/v1/rum/events", saved, null);
                    }
                }
                List<RumEvent> batch;
                lock (rumQueue)
                {
                    batch = rumQueue.GetRange(0, Math.Min(200, rumQueue.Count));
                    rumQueue.RemoveRange(0, batch.Count);
                }
                if (batch.Count == 0) yield break;
                var body = RumJson.Build(opts, sessionId, RumJson.OsName(Application.platform.ToString()), SystemInfo.deviceModel, batch);
                yield return Post("/v1/rum/events", body, () => { lock (rumQueue) rumQueue.InsertRange(0, batch); });
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
                        yield return Post("/v1/logs", saved, null);
                    }
                }
                var batch = buffer.Drain(100);
                if (batch.Count > 0) yield return Post("/v1/logs", OtlpJson.Build(opts, batch), () => buffer.Requeue(batch));
                if (opts.TrackRum) yield return FlushRum();
            }

            IEnumerator Post(string path, string body, Action onFail)
            {
                using (var req = new UnityWebRequest(opts.Endpoint.TrimEnd('/') + path, "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.SetRequestHeader("Authorization", "Bearer " + opts.Key);
                    // an app sends no Origin: the publishable key must list this app id
                    if (!string.IsNullOrEmpty(opts.AppId)) req.SetRequestHeader("X-Watchtower-App-Id", opts.AppId);
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success && onFail != null) onFail();
                }
            }

            // Offline buffer: whatever is still queued is written out and sent on the next launch.
            void OnApplicationQuit()
            {
                if (opts != null && opts.TrackRum) PersistRum();
                if (buffer == null || buffer.Count == 0) return;
                File.WriteAllText(PendingPath, OtlpJson.Build(opts, buffer.Drain(opts.MaxBuffered)));
            }
        }
    }
}
