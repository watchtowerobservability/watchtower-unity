using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Watchtower.Editor
{
    /// <summary>
    /// After a player build, uploads native/IL2CPP symbols (.so, .dSYM, .pdb, .sym) in the build output.
    /// Enabled only when WT_API_URL, WT_PROJECT and WT_API_KEY (secret) are set, so local builds stay quiet.
    /// Menu: Watchtower/Upload symbols from build folder.
    /// </summary>
    public sealed class SymbolUploadPostprocess : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            var api = Environment.GetEnvironmentVariable("WT_API_URL");
            var project = Environment.GetEnvironmentVariable("WT_PROJECT");
            var key = Environment.GetEnvironmentVariable("WT_API_KEY");
            if (string.IsNullOrEmpty(api) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(key)) return;
            Run(api, project, key, report.summary.outputPath);
        }

        [MenuItem("Watchtower/Upload symbols from build folder")]
        static void Menu()
        {
            var dir = EditorUtility.OpenFolderPanel("Build folder", "", "");
            var api = Environment.GetEnvironmentVariable("WT_API_URL");
            var project = Environment.GetEnvironmentVariable("WT_PROJECT");
            var key = Environment.GetEnvironmentVariable("WT_API_KEY");
            if (!string.IsNullOrEmpty(dir)) Run(api, project, key, dir);
        }

        static void Run(string api, string project, string key, string dir)
        {
            var release = Environment.GetEnvironmentVariable("WT_RELEASE");
            if (string.IsNullOrEmpty(release)) release = Application.version;
            if (!System.IO.Directory.Exists(dir)) dir = System.IO.Path.GetDirectoryName(dir);
            var errors = ArtifactUpload.UploadDir(api, project, key, release, dir).GetAwaiter().GetResult();
            foreach (var e in errors) Debug.LogWarning("[watchtower] symbol upload: " + e);
            if (errors.Count == 0) Debug.Log("[watchtower] symbols uploaded for release " + release);
        }
    }
}
