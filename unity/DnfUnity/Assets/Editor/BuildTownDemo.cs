using System;
using System.IO;
using Dnf.Client;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DnfUnity.Editor
{
    /// <summary>
    /// 批处理构建入口:构建"城镇演示"播放器(<see cref="TownDemo"/>:运行时读取用户自备的 Script.pvf 与 ImagePacks2)。
    /// 用法见 tools/unity-build-town.sh。输出在 unity/DnfUnity/Builds/(已 gitignore)。
    /// 构建过程中临时改的 PlayerSettings 在结束时还原,不留在 ProjectSettings 里。
    /// </summary>
    public static class BuildTownDemo
    {
        private const string ScenePath = "Assets/Demo/TownDemo.unity";
        private const string ProductName = "DnfTownDemo";

        public static void BuildMacOS()
        {
            Build(BuildTarget.StandaloneOSX, NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x,
                "macos/" + ProductName + ".app", mobile: false);
        }

        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x,
                "windows/" + ProductName + ".exe", mobile: false);
        }

        public static void BuildAndroid()
        {
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            Build(BuildTarget.Android, NamedBuildTarget.Android, ScriptingImplementation.IL2CPP,
                "android/" + ProductName + ".apk", mobile: true);
        }

        private static void EnsureScene()
        {
            if (File.Exists(ScenePath))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("TownDemo");
            go.AddComponent<TownDemo>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
        }

        private static void Build(BuildTarget target, NamedBuildTarget named, ScriptingImplementation backend,
            string relativeOutput, bool mobile)
        {
            EnsureScene();

            string oldProduct = PlayerSettings.productName;
            int oldWidth = PlayerSettings.defaultScreenWidth;
            int oldHeight = PlayerSettings.defaultScreenHeight;
            FullScreenMode oldMode = PlayerSettings.fullScreenMode;
            bool oldResizable = PlayerSettings.resizableWindow;
            bool oldBackground = PlayerSettings.runInBackground;
            UIOrientation oldOrientation = PlayerSettings.defaultInterfaceOrientation;
            bool oldPortrait = PlayerSettings.allowedAutorotateToPortrait;
            bool oldPortraitUpsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
            bool oldLandscapeLeft = PlayerSettings.allowedAutorotateToLandscapeLeft;
            bool oldLandscapeRight = PlayerSettings.allowedAutorotateToLandscapeRight;
            try
            {
                PlayerSettings.productName = ProductName;
                PlayerSettings.SetApplicationIdentifier(named, "com.dnfunity.client");
                PlayerSettings.SetScriptingBackend(named, backend);
                PlayerSettings.runInBackground = true;
                if (mobile)
                {
                    PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
                    PlayerSettings.allowedAutorotateToPortrait = false;
                    PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
                    PlayerSettings.allowedAutorotateToLandscapeLeft = true;
                    PlayerSettings.allowedAutorotateToLandscapeRight = true;
                }
                else
                {
                    // 窗口默认 800×600(与原版客户端一样),可以拖大;画面按整数倍放大
                    PlayerSettings.defaultScreenWidth = 800;
                    PlayerSettings.defaultScreenHeight = 600;
                    PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                    PlayerSettings.resizableWindow = true;
                }

                string output = Path.GetFullPath(Path.Combine("Builds", relativeOutput));
                Directory.CreateDirectory(Path.GetDirectoryName(output));

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = output,
                    target = target,
                    options = BuildOptions.None,
                };

                Debug.Log("DNF_BUILD start demo target=" + target + " backend=" + backend + " out=" + output);
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary s = report.summary;
                Debug.Log("DNF_BUILD result=" + s.result + " errors=" + s.totalErrors + " warnings=" + s.totalWarnings +
                          " size=" + s.totalSize + " seconds=" + s.totalTime.TotalSeconds.ToString("F1"));
                if (s.result != BuildResult.Succeeded)
                {
                    EditorApplication.Exit(1);
                }
            }
            finally
            {
                PlayerSettings.productName = oldProduct;
                PlayerSettings.defaultScreenWidth = oldWidth;
                PlayerSettings.defaultScreenHeight = oldHeight;
                PlayerSettings.fullScreenMode = oldMode;
                PlayerSettings.resizableWindow = oldResizable;
                PlayerSettings.runInBackground = oldBackground;
                PlayerSettings.defaultInterfaceOrientation = oldOrientation;
                PlayerSettings.allowedAutorotateToPortrait = oldPortrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = oldPortraitUpsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = oldLandscapeLeft;
                PlayerSettings.allowedAutorotateToLandscapeRight = oldLandscapeRight;
            }
        }
    }
}
