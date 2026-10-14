using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Dnf.Squirrel.Editor
{
    /// <summary>
    /// 把 Plugins/ 下每个原生库的导入选项按目录写进 .meta(与 <see cref="SquirrelPluginPostprocessor"/> 用同一份规则),并打印结果。
    /// 批处理里运行:Unity -batchmode -nographics -projectPath unity/DnfUnity -executeMethod Dnf.Squirrel.Editor.SquirrelPluginSetup.Apply -quit
    /// </summary>
    public static class SquirrelPluginSetup
    {
        private const string Root = "Packages/com.dnfunity.squirrel/Plugins/";

        [MenuItem("DNF/Squirrel/Apply plugin import settings")]
        public static void Apply()
        {
            int configured = 0;
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { Root.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }
                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer == null)
                {
                    Debug.Log("DNF_SQUIRREL_PLUGIN skip (not a plugin) " + path);
                    continue;
                }
                SquirrelPluginPostprocessor.Configure(importer, path.Substring(Root.Length));
                importer.SaveAndReimport();
                configured++;
                Debug.Log("DNF_SQUIRREL_PLUGIN " + Describe(path));
            }
            Debug.Log("DNF_SQUIRREL_PLUGIN configured=" + configured);
        }

        private static string Describe(string path)
        {
            var importer = (PluginImporter)AssetImporter.GetAtPath(path);
            var sb = new StringBuilder(path);
            sb.Append(" any=").Append(importer.GetCompatibleWithAnyPlatform());
            sb.Append(" editor=").Append(importer.GetCompatibleWithEditor());
            foreach (BuildTarget t in new[] { BuildTarget.StandaloneOSX, BuildTarget.StandaloneWindows64, BuildTarget.Android, BuildTarget.iOS })
            {
                sb.Append(' ').Append(t).Append('=').Append(importer.GetCompatibleWithPlatform(t));
                if (importer.GetCompatibleWithPlatform(t))
                {
                    sb.Append("(cpu=").Append(importer.GetPlatformData(t, "CPU")).Append(')');
                }
            }
            return sb.ToString();
        }
    }
}
