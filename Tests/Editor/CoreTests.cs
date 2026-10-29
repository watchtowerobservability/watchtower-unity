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
}
