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
        private static readonly string[] Scenes = { ProjectBuilder.BootScenePath, ProjectBuilder.ArenaScenePath };

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
                locationPathName = Path.Combine(BuildsRoot, "Win", "Game.exe"),
                options = BuildOptions.Development,
            });
        }

        private static void Build(BuildPlayerOptions options)
        {
            string outDir = options.target == BuildTarget.WebGL ? options.locationPathName : Path.GetDirectoryName(options.locationPathName);
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, true);

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log($"[BuildScripts] {options.target}: {s.result} in {s.totalTime}, {s.totalSize / (1024 * 1024)} MB → {options.locationPathName}");
            if (s.result != BuildResult.Succeeded && Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }
}
