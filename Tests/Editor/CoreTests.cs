using NUnit.Framework;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Watchtower;

public class CoreTests
{
    static WatchtowerOptions O() => new WatchtowerOptions { Key = "wtp_abcdefgh", Service = "g", DeploymentEnvironment = "dev", Release = "1.0" };

    [Test]
    public void KeyIsMaskedInToString()
    {
        Assert.That(O().ToString(), Does.Not.Contain("abcdefgh"));
        Assert.That(O().ToString(), Does.Contain("wtp_****"));
    }

    [Test]
    public void ExceptionRecordHasTypeMessageAndStack()
    {
        var r = Records.FromUnityLog(O(), "NullReferenceException: boom", "at A.B()", "Exception", "", "");
        Assert.AreEqual("NullReferenceException", r.Attributes["exception.type"]);
        Assert.AreEqual("boom", r.Attributes["exception.message"]);
        Assert.AreEqual("at A.B()", r.Attributes["exception.stacktrace"]);
        Assert.AreEqual(17, r.Severity);
    }

    [Test]
    public void InfoIsSkippedUnlessEnabled()
    {
        Assert.IsNull(Records.FromUnityLog(O(), "hi", "", "Log", "", ""));
        var o = O(); o.CaptureInfo = true;
        Assert.IsNotNull(Records.FromUnityLog(o, "hi", "", "Log", "", ""));
    }

    [Test]
    public void SecretsAreScrubbed()
    {
        var r = Records.FromUnityLog(O(), "failed Authorization: Bearer abc.def key wtp_zzz", "", "Error", "", "");
        Assert.That(r.Body, Does.Not.Contain("abc.def"));
        Assert.That(r.Body, Does.Not.Contain("wtp_zzz"));
    }

    [Test]
    public void BeforeSendCanDrop()
    {
        var o = O(); o.BeforeSend = _ => null;
        Assert.IsNull(Records.FromUnityLog(o, "x", "", "Error", "", ""));
    }

    [Test]
    public void OtlpDropsWtAttributesAndEscapes()
    {
        var r = new LogRecord { TimeUnixNano = 1, Body = "a\"b\n", TraceId = new string('a', 32), SpanId = new string('b', 16) };
        r.Attributes["wt.tenant"] = "evil";
        r.Attributes["k"] = "v";
        var j = OtlpJson.Build(O(), new[] { r });
        Assert.That(j, Does.Not.Contain("wt.tenant"));
        Assert.That(j, Does.Contain("a\\\"b\\n"));
        Assert.That(j, Does.Contain("\"traceId\":\"" + new string('a', 32) + "\""));
        Assert.That(j, Does.Contain("\"service.name\""));
        Assert.That(j, Does.Not.Contain("abcdefgh"));
    }

    [Test]
    public void TraceparentParsing()
    {
        Assert.IsTrue(OtlpJson.TryParseTraceparent("00-" + new string('A', 32) + "-" + new string('b', 16) + "-01", out var t, out var s));
        Assert.AreEqual(new string('a', 32), t);
        Assert.IsFalse(OtlpJson.TryParseTraceparent("nope", out _, out _));
    }

    [Test]
    public void BufferDropsOldestAndRequeues()
    {
        var b = new RecordBuffer(2);
        for (var i = 0; i < 3; i++) b.Add(new LogRecord { Body = i.ToString() });
        var batch = b.Drain(10);
        Assert.AreEqual(2, batch.Count);
        Assert.AreEqual("1", batch[0].Body);
        b.Requeue(batch);
        Assert.AreEqual(2, b.Count);
    }

    [Test]
    public void ArtifactUrlAndKinds()
    {
        Assert.AreEqual("https://a/v1/projects/p/artifacts?kind=elf&name=libil2cpp.so&release=1.0",
            ArtifactUpload.Url("https://a/", "p", "elf", "libil2cpp.so", "1.0"));
        Assert.AreEqual("elf", ArtifactUpload.KindFor("x/libil2cpp.so"));
        Assert.IsNull(ArtifactUpload.KindFor("x/readme.txt"));
        Assert.AreEqual("il2cpp", ArtifactUpload.KindFor("Build/il2cppOutput/Symbols/LineNumberMappings.json"));
    }

    sealed class Capture : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen = new List<HttpRequestMessage>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            Seen.Add(r);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    [Test]
    public void UploadRejectsPublishableKeyAndSendsBearer()
    {
        Assert.Throws<System.InvalidOperationException>(() =>
            ArtifactUpload.UploadDir("https://a", "p", "wtp_x", "1", ".").GetAwaiter().GetResult());
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wtsym" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "a.sym"), "MODULE");
        var h = new Capture();
        var errs = ArtifactUpload.UploadDir("https://a", "p", "wti_secret", "1", dir, h).GetAwaiter().GetResult();
        Assert.AreEqual(0, errs.Count);
        Assert.AreEqual(1, h.Seen.Count);
        Assert.AreEqual("Bearer", h.Seen[0].Headers.Authorization.Scheme);
    }

    [Test]
    public void FramesAreCountedAndLongOnesAreHangs()
    {
        var f = new FrameStats();
        Assert.IsNull(f.Add(0.016, 5));
        Assert.IsNull(f.Add(0.03, 5));   // slow
        Assert.IsNull(f.Add(0.9, 5));    // frozen
        Assert.AreEqual(6000.0, f.Add(6, 5).Value, 0.001);
        var v = f.Drain("Menu");
        Assert.AreEqual(new[] { "slow_frames", "frozen_frames" }, v.ConvertAll(e => e.Name).ToArray());
        Assert.AreEqual("Menu", v[0].Attrs["screen"]);
        Assert.AreEqual(0, f.Drain("Menu").Count);
    }

    [Test]
    public void ScenesBecomeViewsWithThePreviousOne()
    {
        var t = new ViewTracker("game");
        var a = t.Appeared("Menu", 1000);
        Assert.AreEqual("app://game/Menu", a.Attrs["url"]);
        Assert.IsFalse(a.Attrs.ContainsKey("previous_route"));
        Assert.IsNull(t.Appeared("Menu", 1500));
        var b = t.Appeared("Level1", 4000);
        Assert.AreEqual("Menu", b.Attrs["previous_route"]);
        Assert.AreEqual("3000", b.Attrs["previous_duration_ms"]);
    }

    [Test]
    public void RumBatchHasTheWireShapeAndNoWtAttributes()
    {
        var launch = RumJson.ColdLaunch(1234.4);
        launch.Attrs["wt.tenant"] = "evil";
        var j = RumJson.Build(O(), "3b241101-e2bb-4255-8caf-4136c566a962", RumJson.OsName("IPhonePlayer"), "iPhone\"15", new[] { launch });
        Assert.That(j, Does.Contain("\"os.name\":\"iOS\""));
        Assert.That(j, Does.Contain("\"name\":\"app_launch_cold\",\"value\":1234"));
        Assert.That(j, Does.Contain("iPhone\\\"15"));
        Assert.That(j, Does.Not.Contain("wt.tenant"));
        Assert.That(j, Does.Not.Contain("abcdefgh"));
        Assert.That(j, Does.Contain("\"service\":\"g\""));
    }

    [Test]
    public void HangWatchReportsAnEndlessStallOnceWithTheDurationSoFar()
    {
        var w = new HangWatch(5000, 0);
        Assert.IsNull(w.Poll(4000));
        w.Beat(4500);
        Assert.IsNull(w.Poll(9000));                 // 4.5 s since the beat
        Assert.AreEqual(5500.0, w.Poll(10000).Value, 0.001); // threshold passed, never beat again
        Assert.IsNull(w.Poll(20000), "reported once per hang");
        w.Beat(21000);
        Assert.AreEqual(5000.0, w.Poll(26000).Value, 0.001); // a new hang is reported again
    }

    [Test]
    public void BackgroundedAppIsNotAHang()
    {
        var w = new HangWatch(5000, 0);
        w.SetPaused(true, 1000);
        Assert.IsNull(w.Poll(60000));
        w.SetPaused(false, 61000);
        Assert.IsNull(w.Poll(62000));
    }

    [Test]
    public void PendingRumKeepsTheNewestEventsAsOneBatch()
    {
        var evs = new List<RumEvent>();
        for (var i = 0; i < RumPending.MaxEvents + 5; i++) evs.Add(new RumEvent { Type = "vital", Name = "v" + i });
        var j = RumPending.Serialize(O(), "3b241101-e2bb-4255-8caf-4136c566a962", "Android", "Pixel", evs);
        Assert.That(j, Does.Contain("\"name\":\"v204\""));
        Assert.That(j, Does.Not.Contain("\"name\":\"v4\""));
        Assert.That(j.Length, Is.LessThan(256 * 1024));
        Assert.IsNull(RumPending.Serialize(O(), "s", "Android", "Pixel", new List<RumEvent>()));
    }
}
