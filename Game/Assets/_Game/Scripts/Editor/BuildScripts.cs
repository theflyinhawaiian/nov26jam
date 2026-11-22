using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// Batch builds into ../Builds (repo root, gitignored):
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Jam.EditorTools.BuildScripts.BuildServer
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Jam.EditorTools.BuildScripts.BuildWeb
    /// </summary>
    public static class BuildScripts
    {
        private static readonly string BuildsRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Builds"));
        private static readonly string[] Scenes = { ProjectBuilder.BootScenePath, ProjectBuilder.ArenaScenePath, ProjectBuilder.TwoLaneScenePath };

        [MenuItem("nov26jam/Build/Dedicated Server (Linux)")]
        public static void BuildServer()
        {
            Build(new BuildPlayerOptions
            {
                scenes = Scenes,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                locationPathName = Path.Combine(BuildsRoot, "Server", "Server.x86_64"),
            });
        }

        [MenuItem("nov26jam/Build/Web")]
        public static void BuildWeb()
        {
            Build(new BuildPlayerOptions
            {
                scenes = Scenes,
                target = BuildTarget.WebGL,
                locationPathName = Path.Combine(BuildsRoot, "Web"),
            });
        }

        /// <summary>Local smoke tests only: one exe runs as server (-server) or bot client (-client -smoketest).</summary>
        [MenuItem("nov26jam/Build/Windows (dev, smoke tests)")]
        public static void BuildWindowsDev()
        {
            Build(new BuildPlayerOptions
            {
                scenes = Scenes,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                locationPathName = Path.Combine(BuildsRoot, "Win", "Game.exe"),
                options = BuildOptions.Development,
            });
        }

        /// <summary>
        /// Headless bot client for production smoke tests from WSL, avoiding Windows-side TLS
        /// interception (Avast): ./Game.x86_64 -batchmode -nographics -client -connect host -smoketest
        /// </summary>
        [MenuItem("nov26jam/Build/Linux client (dev, smoke tests)")]
        public static void BuildLinuxClientDev()
        {
            Build(new BuildPlayerOptions
            {
                scenes = Scenes,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                locationPathName = Path.Combine(BuildsRoot, "LinuxClient", "Game.x86_64"),
                options = BuildOptions.Development,
            });
        }

        private const string BuildInfoPath = "Assets/_Game/Resources/" + BuildInfo.ResourceName + ".txt";

        private static void Build(BuildPlayerOptions options)
        {
            WriteBuildInfo();

            string outDir = options.target == BuildTarget.WebGL ? options.locationPathName : Path.GetDirectoryName(options.locationPathName);
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log($"[BuildScripts] {options.target}: {s.result} in {s.totalTime}, {s.totalSize / (1024 * 1024)} MB → {options.locationPathName}");
            if (s.result != BuildResult.Succeeded && Application.isBatchMode)
                EditorApplication.Exit(1);
        }

        /// <summary>Stamps "&lt;git hash&gt;[-dirty] &lt;UTC time&gt;" into Resources so builds can show it at runtime.</summary>
        private static void WriteBuildInfo()
        {
            string hash = Git("rev-parse --short HEAD") ?? "nogit";
            string dirty = string.IsNullOrEmpty(Git("status --porcelain -- .")) ? "" : "-dirty";
            string stamp = $"{hash}{dirty} {System.DateTime.UtcNow:yyyy-MM-dd HH:mm}Z";

            Directory.CreateDirectory(Path.GetDirectoryName(BuildInfoPath));
            File.WriteAllText(BuildInfoPath, stamp);
            AssetDatabase.ImportAsset(BuildInfoPath, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[BuildScripts] Build stamp: {stamp}");
        }

        private static string Git(string args)
        {
            try
            {
                using System.Diagnostics.Process p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return p.ExitCode == 0 ? output : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
