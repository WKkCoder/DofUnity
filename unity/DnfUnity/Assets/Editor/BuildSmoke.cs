using System;
using System.IO;
using DnfUnity.Smoke;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DnfUnity.Editor
{
    /// <summary>
    /// 批处理构建入口:为每个目标平台构建一个只含 SmokeRunner 的最小播放器,验证"四平台能不能出包"。
    /// 用法见 tools/unity-build-smoke.sh。输出在 unity/DnfUnity/Builds/(已 gitignore)。
    /// </summary>
    public static class BuildSmoke
    {
        private const string ScenePath = "Assets/Smoke/Smoke.unity";

        public static void BuildMacOS()
        {
            Build(BuildTarget.StandaloneOSX, NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x,
                "macos/DnfSmoke.app");
        }

        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x,
                "windows/DnfSmoke.exe");
        }

        public static void BuildAndroid()
        {
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            Build(BuildTarget.Android, NamedBuildTarget.Android, ScriptingImplementation.IL2CPP,
                "android/DnfSmoke.apk");
        }

        private static void EnsureScene()
        {
            if (File.Exists(ScenePath))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("SmokeRunner");
            go.AddComponent<SmokeRunner>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
        }

        private static void Build(BuildTarget target, NamedBuildTarget named, ScriptingImplementation backend,
            string relativeOutput)
        {
            EnsureScene();
            PlayerSettings.SetApplicationIdentifier(named, "com.dnfunity.client");
            PlayerSettings.SetScriptingBackend(named, backend);

            string output = Path.GetFullPath(Path.Combine("Builds", relativeOutput));
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = target,
                options = BuildOptions.None,
            };

            Debug.Log("DNF_BUILD start target=" + target + " backend=" + backend + " out=" + output);
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log("DNF_BUILD result=" + s.result + " errors=" + s.totalErrors + " warnings=" + s.totalWarnings +
                      " size=" + s.totalSize + " seconds=" + s.totalTime.TotalSeconds.ToString("F1"));
            if (s.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
