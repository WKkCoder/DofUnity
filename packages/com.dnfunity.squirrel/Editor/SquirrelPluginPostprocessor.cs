using System;
using UnityEditor;

namespace Dnf.Squirrel.Editor
{
    /// <summary>
    /// 按目录给 Plugins/ 下的原生库设置导入选项,这样仓库里不依赖手点的 .meta:
    ///   Plugins/macOS/*.dylib              → 编辑器 + macOS 独立播放器(通用二进制,CPU 不限)
    ///   Plugins/Windows/x86_64/*.dll       → Windows 64 位独立播放器
    ///   Plugins/Android/&lt;abi&gt;/*.so         → Android,按 ABI 目录名定 CPU
    ///   Plugins/iOS/*.{c,cc,cpp,h,mm}      → iOS(源码随 Xcode 工程一起编译)
    /// 其余平台都不勾选。原生库由 tools/build-squirrel.sh 构建。
    /// </summary>
    internal sealed class SquirrelPluginPostprocessor : AssetPostprocessor
    {
        private const string Root = "Packages/com.dnfunity.squirrel/Plugins/";

        private static readonly BuildTarget[] Targets =
        {
            BuildTarget.StandaloneOSX,
            BuildTarget.StandaloneWindows64,
            BuildTarget.StandaloneLinux64,
            BuildTarget.Android,
            BuildTarget.iOS,
        };

        private void OnPreprocessAsset()
        {
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal))
            {
                return;
            }
            var importer = assetImporter as PluginImporter;
            if (importer == null)
            {
                return;
            }
            Configure(importer, assetPath.Substring(Root.Length));
        }

        internal static void Configure(PluginImporter importer, string relativePath)
        {
            string[] parts = relativePath.Split('/');
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            foreach (BuildTarget t in Targets)
            {
                importer.SetCompatibleWithPlatform(t, false);
            }

            switch (parts[0])
            {
                case "macOS":
                    importer.SetCompatibleWithEditor(true);
                    importer.SetEditorData("CPU", "AnyCPU");
                    importer.SetEditorData("OS", "OSX");
                    importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);
                    importer.SetPlatformData(BuildTarget.StandaloneOSX, "CPU", "AnyCPU");
                    break;
                case "Windows":
                    importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
                    importer.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
                    break;
                case "Android":
                    importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
                    importer.SetPlatformData(BuildTarget.Android, "CPU", AndroidCpu(parts.Length > 1 ? parts[1] : string.Empty));
                    break;
                case "iOS":
                    importer.SetCompatibleWithPlatform(BuildTarget.iOS, true);
                    importer.SetPlatformData(BuildTarget.iOS, "CPU", "ARM64");
                    break;
            }
        }

        private static string AndroidCpu(string abi)
        {
            switch (abi)
            {
                case "arm64-v8a": return "ARM64";
                case "armeabi-v7a": return "ARMv7";
                case "x86_64": return "X86_64";
                case "x86": return "X86";
                default: return "ARM64";
            }
        }
    }
}
