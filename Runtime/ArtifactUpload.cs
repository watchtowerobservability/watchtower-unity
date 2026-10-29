using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Watchtower
{
    /// <summary>Same request shape as <c>wtctl</c>: POST /v1/projects/{prj}/artifacts?kind=&amp;name=&amp;release=, gzip body, secret API key.</summary>
    public static class ArtifactUpload
    {
        /// <summary>Maps a file to an artifact kind, or null when we do not upload it.</summary>
        public static string KindFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".so": return "elf";
                case ".dylib": case ".dsym": return "macho";
                case ".pdb": return "pdb";
                case ".sym": return "breakpad";
                default: return null;
            }
        }

        public static string Url(string api, string project, string kind, string name, string release)
        {
            var u = api.TrimEnd('/') + "/v1/projects/" + Uri.EscapeDataString(project) + "/artifacts?kind=" + kind +
                    "&name=" + Uri.EscapeDataString(name);
            return string.IsNullOrEmpty(release) ? u : u + "&release=" + Uri.EscapeDataString(release);
        }

        /// <summary>Uploads every symbol file under <paramref name="dir"/>; returns the failures.</summary>
        public static async Task<List<string>> UploadDir(string api, string project, string apiKey, string release, string dir, HttpMessageHandler handler = null)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("WT_API_KEY (secret API key) is required to upload symbols");
            if (apiKey.StartsWith("wtp_")) throw new InvalidOperationException("refusing to upload with a publishable key; use a secret API key");
            var errors = new List<string>();
            using (var http = handler == null ? new HttpClient() : new HttpClient(handler))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var kind = KindFor(f);
                    if (kind == null) continue;
                    try
                    {
                        var ms = new MemoryStream();
                        using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) { var b = File.ReadAllBytes(f); gz.Write(b, 0, b.Length); }
                        var content = new ByteArrayContent(ms.ToArray());
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        content.Headers.ContentEncoding.Add("gzip");
                        var res = await http.PostAsync(Url(api, project, kind, Path.GetFileName(f), release), content);
                        if (!res.IsSuccessStatusCode) errors.Add(Path.GetFileName(f) + ": HTTP " + (int)res.StatusCode);
                    }
                    catch (Exception e) { errors.Add(Path.GetFileName(f) + ": " + e.Message); }
                }
            }
            return errors;
        }
    }
}
