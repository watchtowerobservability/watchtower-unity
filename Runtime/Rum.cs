using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Watchtower
{
    /// <summary>One RUM event in the RumEventV1 wire shape (views, launch and frame vitals).</summary>
    public sealed class RumEvent
    {
        public string Id = Guid.NewGuid().ToString();
        public long TsNs = Records.NowNano();
        public string Type = "";
        public string Name = "";
        public double? Value;
        public Dictionary<string, string> Attrs = new Dictionary<string, string>();
    }

    /// <summary>Counts slow and frozen frames and flags a frame so long it was a hang. Engine-free.</summary>
    public sealed class FrameStats
    {
        public const double SlowSeconds = 1.0 / 60 * 1.5;
        public const double FrozenSeconds = 0.7;
        public int Slow, Frozen;

        /// <summary>Records one frame; returns the hang duration in ms when the frame took at least <paramref name="hangSeconds"/>, else null.</summary>
        public double? Add(double frameSeconds, double hangSeconds)
        {
            if (frameSeconds >= hangSeconds) return frameSeconds * 1000;
            if (frameSeconds > FrozenSeconds) Frozen++;
            else if (frameSeconds > SlowSeconds) Slow++;
            return null;
        }

        /// <summary>The slow_frames / frozen_frames vitals of the screen so far; counters reset.</summary>
        public List<RumEvent> Drain(string screen)
        {
            var l = new List<RumEvent>();
            if (Slow > 0) l.Add(Vital("slow_frames", Slow, screen));
            if (Frozen > 0) l.Add(Vital("frozen_frames", Frozen, screen));
            Slow = Frozen = 0;
            return l;
        }

        public static RumEvent Vital(string name, double value, string screen)
        {
            var e = new RumEvent { Type = "vital", Name = name, Value = value };
            if (!string.IsNullOrEmpty(screen)) e.Attrs["screen"] = screen;
            return e;
        }
    }

    /// <summary>
    /// Watches for a main thread that stopped beating. <see cref="Beat"/> runs on the main thread
    /// (every frame), <see cref="Poll"/> on a watchdog thread. Poll returns the stall so far, once,
    /// when it passes the threshold: a hang that never ends is still reported. Engine-free.
    /// </summary>
    public sealed class HangWatch
    {
        readonly object gate = new object();
        readonly double thresholdMs;
        double last;
        bool reported, paused;

        public HangWatch(double thresholdMs, double startMs) { this.thresholdMs = thresholdMs; last = startMs; }

        public void Beat(double nowMs) { lock (gate) { last = nowMs; reported = false; } }

        /// <summary>The app went to the background: no frames run, which is not a hang.</summary>
        public void SetPaused(bool p, double nowMs) { lock (gate) { paused = p; last = nowMs; reported = false; } }

        public double? Poll(double nowMs)
        {
            lock (gate)
            {
                if (paused || reported || nowMs - last < thresholdMs) return null;
                reported = true;
                return nowMs - last;
            }
        }
    }

    /// <summary>Scene changes as RUM views (route, url, previous route and time on it).</summary>
    public sealed class ViewTracker
    {
        readonly string service;
        string current;
        long sinceMs;

        public ViewTracker(string service) { this.service = service; }

        public string Current => current;

        /// <returns>null when the scene did not change.</returns>
        public RumEvent Appeared(string scene, long nowMs)
        {
            if (string.IsNullOrEmpty(scene) || scene == current) return null;
            var e = new RumEvent { Type = "view", Name = scene };
            e.Attrs["route"] = scene;
            e.Attrs["url"] = "app://" + service + "/" + scene;
            if (current != null)
            {
                e.Attrs["previous_route"] = current;
                e.Attrs["previous_duration_ms"] = (nowMs - sinceMs).ToString(CultureInfo.InvariantCulture);
            }
            current = scene;
            sinceMs = nowMs;
            return e;
        }
    }

    /// <summary>Events kept across an app quit: the newest few, as one batch the next launch sends as is.</summary>
    public static class RumPending
    {
        public const int MaxEvents = 200;

        /// <returns>null when there is nothing to keep.</returns>
        public static string Serialize(WatchtowerOptions o, string sessionId, string os, string model, IList<RumEvent> events)
        {
            if (events.Count == 0) return null;
            var keep = new List<RumEvent>();
            for (var i = Math.Max(0, events.Count - MaxEvents); i < events.Count; i++) keep.Add(events[i]);
            return RumJson.Build(o, sessionId, os, model, keep);
        }
    }

    /// <summary>Encodes RUM batches for POST /v1/rum/events. No UnityEngine dependency.</summary>
    public static class RumJson
    {
        /// <summary>Unity's RuntimePlatform names to the OS names the server classifies mobile by.</summary>
        public static string OsName(string platform)
        {
            if (platform == "IPhonePlayer") return "iOS";
            if (platform == "Android") return "Android";
            return platform;
        }

        /// <summary>The cold launch vital: ms from engine start to the first frame, rated by the server.</summary>
        public static RumEvent ColdLaunch(double engineToFirstFrameMs)
        {
            var e = new RumEvent { Type = "vital", Name = "app_launch_cold", Value = Math.Round(engineToFirstFrameMs) };
            e.Attrs["launch.phase.first_frame_ms"] = Math.Round(engineToFirstFrameMs).ToString(CultureInfo.InvariantCulture);
            return e;
        }

        public static RumEvent Hang(double ms, string screen)
        {
            var e = FrameStats.Vital("app_hang", Math.Round(ms), screen);
            return e;
        }

        public static string Build(WatchtowerOptions o, string sessionId, string os, string deviceModel, IEnumerable<RumEvent> events)
        {
            var b = new StringBuilder();
            b.Append("{\"session_id\":\"").Append(sessionId).Append("\",\"sent_at_ns\":").Append(Records.NowNano().ToString(CultureInfo.InvariantCulture));
            b.Append(",\"sdk\":{\"name\":\"watchtower-unity\",\"version\":\"").Append(OtlpJson.SdkVersion).Append("\"}");
            b.Append(",\"app\":{\"service\":\"").Append(OtlpJson.Esc(o.Service)).Append('"');
            if (!string.IsNullOrEmpty(o.Release)) b.Append(",\"release\":\"").Append(OtlpJson.Esc(o.Release)).Append('"');
            b.Append(",\"env\":\"").Append(OtlpJson.Esc(o.DeploymentEnvironment)).Append("\"}");
            b.Append(",\"page\":{\"url\":\"app://").Append(OtlpJson.Esc(o.Service)).Append("\"}");
            b.Append(",\"device\":{\"ua\":\"").Append(OtlpJson.Esc(deviceModel)).Append("\"}");
            b.Append(",\"attrs\":{\"os.name\":\"").Append(OtlpJson.Esc(os)).Append("\",\"device.model\":\"").Append(OtlpJson.Esc(deviceModel)).Append("\"}");
            b.Append(",\"events\":[");
            var first = true;
            foreach (var e in events)
            {
                if (!first) b.Append(',');
                first = false;
                b.Append("{\"id\":\"").Append(e.Id).Append("\",\"ts_ns\":").Append(e.TsNs.ToString(CultureInfo.InvariantCulture))
                 .Append(",\"type\":\"").Append(e.Type).Append("\",\"name\":\"").Append(OtlpJson.Esc(e.Name)).Append('"');
                if (e.Value.HasValue) b.Append(",\"value\":").Append(e.Value.Value.ToString("R", CultureInfo.InvariantCulture));
                b.Append(",\"attrs\":{");
                var fa = true;
                foreach (var kv in e.Attrs)
                {
                    // the gateway deletes wt.* and stamps tenant identity itself
                    if (kv.Key.StartsWith("wt.")) continue;
                    if (!fa) b.Append(',');
                    fa = false;
                    b.Append('"').Append(OtlpJson.Esc(kv.Key)).Append("\":\"").Append(OtlpJson.Esc(kv.Value)).Append('"');
                }
                b.Append("}}");
            }
            b.Append("]}");
            return b.ToString();
        }
    }
}
