using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>地图脚本 [animation] 列表里的一项:动画文件、图层、世界坐标。</summary>
    public readonly struct MapAnimation
    {
        /// <summary>动画文件的 PVF 路径(已按地图所在目录解析,小写)。</summary>
        public string AnimationPath { get; }

        /// <summary>地图里写的原始相对路径,例如 "Animation/GateBack.ani"。</summary>
        public string RawPath { get; }

        /// <summary>图层名,小写,带方括号,例如 "[closeback]"。已知取值见 <see cref="MapScript.KnownLayers"/>。</summary>
        public string Layer { get; }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public MapAnimation(string animationPath, string rawPath, string layer, int x, int y, int z)
        {
            AnimationPath = animationPath;
            RawPath = rawPath;
            Layer = layer;
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>读地图脚本里与场景布置有关的标签。字段含义里"已核对"以外的部分来自对全库的统计,见证据台账。</summary>
    public static class MapScript
    {
        /// <summary>全库 2,948 个 .map 里 [animation] 出现过的 6 种图层名(出现次数:bottom 11,489 / normal 11,301 / closeback 10,095 / middleback 4,362 / close 3,671 / distantback 2,722)。</summary>
        public static readonly string[] KnownLayers =
        {
            "[distantback]", "[middleback]", "[closeback]", "[bottom]", "[normal]", "[close]",
        };

        /// <summary>
        /// 读地图脚本的 [animation] 行。每一组是 5 个值:动画路径(字符串)、图层(字符串)、x、y、z;
        /// 全库 2,693 条 [animation] 都能按这个形状整齐地切开。路径相对 .map 所在目录解析,找不到文件时仍然返回这一项(路径照解析后的写)。
        /// </summary>
        public static List<MapAnimation> ReadAnimations(DnfAssets assets, string mapPath)
        {
            ScriptLine[] doc = assets.ReadScript(mapPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有地图脚本: " + mapPath);
            }
            var result = new List<MapAnimation>();
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[animation]"))
                {
                    continue;
                }
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 4 < v.Length; i += 5)
                {
                    if (v[i].Kind != ScriptValueKind.String || v[i + 1].Kind != ScriptValueKind.String
                        || !v[i + 2].IsNumber || !v[i + 3].IsNumber || !v[i + 4].IsNumber)
                    {
                        break;
                    }
                    string raw = v[i].Text ?? string.Empty;
                    string layer = (v[i + 1].Text ?? string.Empty).ToLowerInvariant();
                    result.Add(new MapAnimation(PvfPath.ResolveRelativeTo(mapPath, raw), raw, layer, v[i + 2].Int, v[i + 3].Int, v[i + 4].Int));
                }
            }
            return result;
        }
    }
}
