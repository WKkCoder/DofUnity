using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>地图脚本 [NPC] 列表里的一项:NPC 编号与世界坐标。</summary>
    public readonly struct TownNpc
    {
        public int Id { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public TownNpc(int id, int x, int y, int z)
        {
            Id = id;
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>一个已经定位好的精灵:要画的像素块与它在屏幕上的左上角。</summary>
    public sealed class SceneSprite
    {
        public SpriteFrame Frame { get; }
        public int ScreenX { get; }
        public int ScreenY { get; }

        /// <summary>画家算法的排序键:世界 y 越小越靠后(先画)。</summary>
        public int SortKey { get; }
        public int NpcId { get; }
        public string AnimationPath { get; }
        public int AniFrame { get; }

        public SceneSprite(SpriteFrame frame, int screenX, int screenY, int sortKey, int npcId, string animationPath, int aniFrame)
        {
            Frame = frame;
            ScreenX = screenX;
            ScreenY = screenY;
            SortKey = sortKey;
            NpcId = npcId;
            AnimationPath = animationPath;
            AniFrame = aniFrame;
        }
    }

    /// <summary>
    /// 城镇里 NPC 的场景合成:地图的 [NPC] 列表 → npc.lst → .npc 的 [field animation] → .ani 的当前帧 → IMG 帧,
    /// 再按 <see cref="TownProjection"/> 放到屏幕坐标。链条的来源与限制见证据 E-024。
    /// 只做了"画面里能确认的部分":没有地图背景、没有主角、没有名牌与特效;
    /// 部分 NPC 在真实画面里用的不是 [field animation] 指向的外观(原因未知),这里照 [field animation] 画。
    /// </summary>
    public static class TownScene
    {
        public const string NpcCatalogPath = "npc/npc.lst";

        /// <summary>读地图脚本里所有 [NPC] 行,每 4 个整数一个 NPC:编号、x、y、z。</summary>
        public static List<TownNpc> ReadMapNpcs(DnfAssets assets, string mapPath)
        {
            ScriptLine[] doc = assets.ReadScript(mapPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有地图脚本: " + mapPath);
            }
            var result = new List<TownNpc>();
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[NPC]"))
                {
                    continue;
                }
                List<int> n = line.Ints();
                for (int i = 0; i + 3 < n.Count; i += 4)
                {
                    result.Add(new TownNpc(n[i], n[i + 1], n[i + 2], n[i + 3]));
                }
            }
            return result;
        }

        /// <summary>npc.lst:没有标签,值是 "编号 路径" 一对一对。</summary>
        public static Dictionary<int, string> ReadNpcCatalog(DnfAssets assets)
        {
            ScriptLine[] doc = assets.ReadScript(NpcCatalogPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有 " + NpcCatalogPath);
            }
            var map = new Dictionary<int, string>();
            foreach (ScriptLine line in doc)
            {
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (v[i].IsNumber && v[i + 1].Kind == ScriptValueKind.String && v[i + 1].Text != null)
                    {
                        map[v[i].Int] = v[i + 1].Text;
                        i++;
                    }
                }
            }
            return map;
        }

        /// <summary>某个 NPC 脚本的 [field animation] 指向的 .ani 的 PVF 路径(相对 .npc 所在目录解析);没有则返回 null。</summary>
        public static string FieldAnimationPath(DnfAssets assets, string npcScriptPath)
        {
            ScriptLine[] doc = assets.ReadScript(npcScriptPath);
            if (doc == null)
            {
                return null;
            }
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[field animation]"))
                {
                    string rel = line.FirstString();
                    return rel == null ? null : PvfPath.ResolveRelativeTo(npcScriptPath, rel);
                }
            }
            return null;
        }

        /// <summary>一个已经解析好脚本与动画的 NPC。</summary>
        public sealed class ResolvedNpc
        {
            public TownNpc Npc { get; }
            public string AnimationPath { get; }
            public AniFile Ani { get; }
            public AniTimeline Timeline { get; }

            public ResolvedNpc(TownNpc npc, string animationPath, AniFile ani)
            {
                Npc = npc;
                AnimationPath = animationPath;
                Ani = ani;
                Timeline = new AniTimeline(ani);
            }
        }

        /// <summary>
        /// 为地图里的每个 NPC 找到它的 .ani;脚本、动画缺失或动画没有帧的 NPC 被跳过(调用方可以对比数量)。
        /// </summary>
        public static List<ResolvedNpc> Resolve(DnfAssets assets, IReadOnlyList<TownNpc> npcs)
        {
            Dictionary<int, string> catalog = ReadNpcCatalog(assets);
            var result = new List<ResolvedNpc>(npcs.Count);
            foreach (TownNpc npc in npcs)
            {
                if (!catalog.TryGetValue(npc.Id, out string file))
                {
                    continue;
                }
                string scriptPath = PvfPath.ResolveRelativeTo("npc/", file);
                string aniPath = FieldAnimationPath(assets, scriptPath);
                if (aniPath == null)
                {
                    continue;
                }
                AniFile ani = assets.LoadAni(aniPath);
                if (ani == null || ani.Frames.Length == 0)
                {
                    continue;
                }
                result.Add(new ResolvedNpc(npc, aniPath, ani));
            }
            return result;
        }

        /// <summary>某个 NPC 的 .ani 第 <paramref name="aniFrame"/> 帧对应的精灵;该帧没有图或图缺失时返回 null。</summary>
        public static SceneSprite SpriteAt(DnfAssets assets, ResolvedNpc r, int aniFrame, TownProjection projection)
        {
            if ((uint)aniFrame >= (uint)r.Ani.Frames.Length)
            {
                return null;
            }
            AniFrame f = r.Ani.Frames[aniFrame];
            if (f.ImageIndex < 0 || f.ImageIndex >= r.Ani.ImagePaths.Length)
            {
                return null;
            }
            string img = "sprite/" + r.Ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
            if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
            {
                return null;
            }
            projection.TopLeft(r.Npc.X, r.Npc.Y, r.Npc.Z, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, out int sx, out int sy);
            return new SceneSprite(sprite, sx, sy, r.Npc.Y, r.Npc.Id, r.AnimationPath, aniFrame);
        }

        /// <summary>
        /// 构造 <paramref name="timeMs"/> 时刻的绘制列表,已按画家算法排好序(先画的在前;同一个 y 的保持列表顺序)。
        /// </summary>
        public static List<SceneSprite> Build(DnfAssets assets, IReadOnlyList<TownNpc> npcs, TownProjection projection, long timeMs)
        {
            return BuildResolved(assets, Resolve(assets, npcs), projection, timeMs);
        }

        /// <summary>与 <see cref="Build"/> 相同,但用已经解析好的 NPC 列表(逐帧调用时不必每次重新解析脚本)。</summary>
        public static List<SceneSprite> BuildResolved(DnfAssets assets, IReadOnlyList<ResolvedNpc> resolved, TownProjection projection, long timeMs)
        {
            var sprites = new List<SceneSprite>(resolved.Count);
            foreach (ResolvedNpc r in resolved)
            {
                SceneSprite s = SpriteAt(assets, r, r.Timeline.FrameAt(timeMs), projection);
                if (s != null)
                {
                    sprites.Add(s);
                }
            }
            return StableSortByDepth(sprites);
        }

        private static List<SceneSprite> StableSortByDepth(List<SceneSprite> sprites)
        {
            var order = new List<KeyValuePair<int, SceneSprite>>(sprites.Count);
            for (int i = 0; i < sprites.Count; i++)
            {
                order.Add(new KeyValuePair<int, SceneSprite>(i, sprites[i]));
            }
            order.Sort((x, y) =>
            {
                int c = x.Value.SortKey.CompareTo(y.Value.SortKey);
                return c != 0 ? c : x.Key.CompareTo(y.Key);
            });
            var sorted = new List<SceneSprite>(order.Count);
            foreach (KeyValuePair<int, SceneSprite> kv in order)
            {
                sorted.Add(kv.Value);
            }
            return sorted;
        }

        public static void Render(RgbaCanvas canvas, IReadOnlyList<SceneSprite> sprites)
        {
            foreach (SceneSprite s in sprites)
            {
                canvas.Draw(s.Frame, s.ScreenX, s.ScreenY);
            }
        }
    }
}
