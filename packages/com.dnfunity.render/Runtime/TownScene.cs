using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>地图脚本 [NPC] 列表里的一项:NPC 编号、朝向与世界坐标。</summary>
    public readonly struct TownNpc
    {
        public int Id { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        /// <summary>地图里写的朝向是 [left]。画面是否因此镜像见 <see cref="TownProjection"/> 的镜像重载与证据 E-029。</summary>
        public bool FacingLeft { get; }

        public TownNpc(int id, int x, int y, int z, bool facingLeft = false)
        {
            Id = id;
            X = x;
            Y = y;
            Z = z;
            FacingLeft = facingLeft;
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

        /// <summary>像素块水平翻转后绘制;<see cref="ScreenX"/> 是翻转后像素块的左上角。</summary>
        public bool Mirrored { get; }

        /// <summary>混合方式(<see cref="SpriteBlend.Additive"/> 来自 .ani 的图形特效类型 2,证据 E-030)。</summary>
        public SpriteBlend Blend { get; }

        /// <summary>染色 alpha(0 到 255),已按帧间插值算好;目前只有加法混合会用到。</summary>
        public int TintAlpha { get; }

        /// <summary>
        /// 前后顺序的大类:0 distantback、1 middleback、2 closeback、3 bottom、4 normal(NPC 也在这一类里,类内按世界 y 排)、5 close。
        /// 这个顺序是按图层名的字面含义排的,只有 closeback 在 GateBack 上被截图核对过位置(证据 E-030)。
        /// </summary>
        public int LayerRank { get; }

        public SceneSprite(SpriteFrame frame, int screenX, int screenY, int sortKey, int npcId, string animationPath, int aniFrame, bool mirrored = false,
            SpriteBlend blend = SpriteBlend.Normal, int tintAlpha = 255, int layerRank = TownScene.NormalRank)
        {
            Frame = frame;
            ScreenX = screenX;
            ScreenY = screenY;
            SortKey = sortKey;
            NpcId = npcId;
            AnimationPath = animationPath;
            AniFrame = aniFrame;
            Mirrored = mirrored;
            Blend = blend;
            TintAlpha = tintAlpha;
            LayerRank = layerRank;
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

        /// <summary>NPC 与 [normal] 图层所在的大类编号(见 <see cref="SceneSprite.LayerRank"/>)。</summary>
        public const int NormalRank = 4;

        /// <summary>图层名 → 大类编号;不认识的图层名按 normal 处理。</summary>
        public static int LayerRank(string layer)
        {
            switch (layer)
            {
                case "[distantback]": return 0;
                case "[middleback]": return 1;
                case "[closeback]": return 2;
                case "[bottom]": return 3;
                case "[normal]": return NormalRank;
                case "[close]": return 5;
                default: return NormalRank;
            }
        }

        /// <summary>
        /// 读地图脚本里所有 [NPC] 行。真实数据里每个 NPC 是 5 个值:编号、朝向字符串("[left]" 或 "[right]")、x、y、z;
        /// 没有朝向字符串的 4 个值写法也接受(朝向按右算)。
        /// </summary>
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
                ScriptValue[] v = line.Values;
                int i = 0;
                while (i < v.Length)
                {
                    if (!v[i].IsNumber)
                    {
                        i++;
                        continue;
                    }
                    int id = v[i++].Int;
                    bool left = false;
                    if (i < v.Length && v[i].Kind == ScriptValueKind.String)
                    {
                        left = string.Equals(v[i].Text, "[left]", StringComparison.OrdinalIgnoreCase);
                        i++;
                    }
                    if (i + 2 >= v.Length)
                    {
                        break;
                    }
                    if (!v[i].IsNumber || !v[i + 1].IsNumber || !v[i + 2].IsNumber)
                    {
                        break;
                    }
                    result.Add(new TownNpc(id, v[i].Int, v[i + 1].Int, v[i + 2].Int, left));
                    i += 3;
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

        /// <summary>
        /// 某个 NPC 的 .ani 第 <paramref name="aniFrame"/> 帧对应的精灵;该帧没有图或图缺失时返回 null。
        /// 朝向 [left] 的 NPC 水平镜像(证据 E-029)。
        /// </summary>
        public static SceneSprite SpriteAt(DnfAssets assets, ResolvedNpc r, int aniFrame, TownProjection projection)
        {
            return SpriteAt(assets, r, aniFrame, projection, r.Npc.FacingLeft);
        }

        /// <summary>
        /// 同上,但明确指定"朝向镜像"。最终是否水平翻转 = 朝向镜像 XOR 帧自己的 x 缩放为负
        /// (帧属性 7 的 x 为 -1 表示镜像,两次镜像抵消;赛丽亚的 .ani 每一帧都带 (-1, 1),地图里她朝 [left],所以画面上不翻转)。
        /// 其余的缩放、旋转与染色(帧属性 7、8、9 的其他取值)**还没有实现**。
        /// </summary>
        public static SceneSprite SpriteAt(DnfAssets assets, ResolvedNpc r, int aniFrame, TownProjection projection, bool facingMirror)
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
            bool mirrored = facingMirror ^ (f.TryGetScale(out float scaleX, out _) && scaleX < 0f);
            projection.TopLeft(r.Npc.X, r.Npc.Y, r.Npc.Z, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
            return new SceneSprite(sprite, sx, sy, r.Npc.Y, r.Npc.Id, r.AnimationPath, aniFrame, mirrored);
        }

        /// <summary>一个已经解析好动画的地图图层([animation] 里的一项)。</summary>
        public sealed class ResolvedLayer
        {
            public MapAnimation Animation { get; }
            public AniFile Ani { get; }
            public AniTimeline Timeline { get; }

            public ResolvedLayer(MapAnimation animation, AniFile ani)
            {
                Animation = animation;
                Ani = ani;
                Timeline = new AniTimeline(ani);
            }
        }

        /// <summary>解析地图 [animation] 里的每一项;动画文件缺失或没有帧的被跳过。</summary>
        public static List<ResolvedLayer> ResolveLayers(DnfAssets assets, string mapPath)
        {
            var result = new List<ResolvedLayer>();
            foreach (MapAnimation a in MapScript.ReadAnimations(assets, mapPath))
            {
                AniFile ani = assets.LoadAni(a.AnimationPath);
                if (ani == null || ani.Frames.Length == 0)
                {
                    continue;
                }
                result.Add(new ResolvedLayer(a, ani));
            }
            return result;
        }

        /// <summary>
        /// 地图图层在 <paramref name="timeMs"/> 时刻的精灵:位置模型与 NPC 相同(GateBack、GatePillar 与三个光效层的位置都等于模型的预测,证据 E-030);
        /// 图形特效类型 2 画成加法混合,染色 alpha 在带插值开关的帧之间线性插值。该帧没有图或图缺失时返回 null。
        /// 缩放大小、旋转与染色颜色同 NPC 一样**还没有实现**(只处理 x 缩放为负的水平镜像)。
        /// </summary>
        public static SceneSprite LayerSpriteAt(DnfAssets assets, ResolvedLayer r, long timeMs, TownProjection projection)
        {
            AniSample sample = r.Timeline.Sample(timeMs);
            if (sample.Frame < 0)
            {
                return null;
            }
            AniFrame f = r.Ani.Frames[sample.Frame];
            if (f.ImageIndex < 0 || f.ImageIndex >= r.Ani.ImagePaths.Length)
            {
                return null;
            }
            string img = "sprite/" + r.Ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
            if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
            {
                return null;
            }
            bool mirrored = f.TryGetScale(out float scaleX, out _) && scaleX < 0f;
            MapAnimation a = r.Animation;
            projection.TopLeft(a.X, a.Y, a.Z, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
            bool additive = f.TryGetGraphicEffect(out int effect) && effect == AniGraphicEffect.Additive;
            return new SceneSprite(sprite, sx, sy, a.Y, -1, a.AnimationPath, sample.Frame, mirrored,
                additive ? SpriteBlend.Additive : SpriteBlend.Normal, r.Timeline.TintAlphaAt(timeMs), LayerRank(a.Layer));
        }

        /// <summary>
        /// 整个场景在 <paramref name="timeMs"/> 时刻的绘制列表:地图的 [animation] 图层加上 [NPC],已排好序。
        /// 排序:先按图层大类(distantback 到 close),大类 normal 里(NPC 也在其中)再按世界 y,其余大类保持列表顺序。
        /// 这个顺序的前提是图层名的字面含义;normal 类里的光效与 NPC 谁在上面见证据 E-030 的核对结果。
        /// 没有主角、名牌、界面与特效。
        /// </summary>
        public static List<SceneSprite> BuildScene(DnfAssets assets, string mapPath, TownProjection projection, long timeMs)
        {
            return BuildResolvedScene(assets, ResolveLayers(assets, mapPath), Resolve(assets, ReadMapNpcs(assets, mapPath)), projection, timeMs);
        }

        /// <summary>与 <see cref="BuildScene"/> 相同,但用已经解析好的图层与 NPC(逐帧调用时不必每次重新解析脚本)。</summary>
        public static List<SceneSprite> BuildResolvedScene(DnfAssets assets, IReadOnlyList<ResolvedLayer> layers, IReadOnlyList<ResolvedNpc> npcs,
            TownProjection projection, long timeMs)
        {
            var sprites = new List<SceneSprite>(layers.Count + npcs.Count);
            foreach (ResolvedLayer layer in layers)
            {
                SceneSprite s = LayerSpriteAt(assets, layer, timeMs, projection);
                if (s != null)
                {
                    sprites.Add(s);
                }
            }
            foreach (ResolvedNpc r in npcs)
            {
                SceneSprite s = SpriteAt(assets, r, r.Timeline.FrameAt(timeMs), projection);
                if (s != null)
                {
                    sprites.Add(s);
                }
            }
            return StableSortByDepth(sprites);
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
                int c = x.Value.LayerRank.CompareTo(y.Value.LayerRank);
                if (c != 0)
                {
                    return c;
                }
                // 只有 normal 大类按世界 y 排;其他大类保持列表顺序
                if (x.Value.LayerRank == NormalRank)
                {
                    c = x.Value.SortKey.CompareTo(y.Value.SortKey);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                return x.Key.CompareTo(y.Key);
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
                canvas.Draw(s.Frame, s.ScreenX, s.ScreenY, s.Mirrored, s.Blend, s.TintAlpha);
            }
        }
    }
}
