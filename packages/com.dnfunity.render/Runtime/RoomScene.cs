using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 一个房间的全部可画内容:地图、已经解析好动画的背景与图层、被动物件(障碍物)与出口(门)。逐帧合成时不必每次重新解析脚本。
    /// 粒子光源(被动物件里的 [string data] 一类)还没有实现,会被跳过。
    /// </summary>
    public sealed class RoomContent
    {
        public RoomMap Map { get; }
        public IReadOnlyList<RoomScene.ResolvedBackground> Backgrounds { get; }
        public IReadOnlyList<TownScene.ResolvedLayer> Layers { get; }
        public IReadOnlyList<RoomObject> Objects { get; }
        public IReadOnlyList<RoomGate> Gates { get; }

        public RoomContent(RoomMap map, IReadOnlyList<RoomScene.ResolvedBackground> backgrounds, IReadOnlyList<TownScene.ResolvedLayer> layers,
            IReadOnlyList<RoomObject> objects, IReadOnlyList<RoomGate> gates)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Backgrounds = backgrounds ?? new List<RoomScene.ResolvedBackground>();
            Layers = layers ?? new List<TownScene.ResolvedLayer>();
            Objects = objects ?? new List<RoomObject>();
            Gates = gates ?? new List<RoomGate>();
        }

        /// <summary>读出房间的背景、图层与被动物件;出口由调用方给(按这个房间的邻居决定)。</summary>
        public static RoomContent Load(DnfAssets assets, RoomMap map, IReadOnlyList<RoomGate> gates = null)
        {
            var objects = new List<RoomObject>();
            if (map.PassiveObjects.Count > 0)
            {
                PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
                var defs = new Dictionary<int, PassiveObjectDef>();
                foreach (PassivePlacement p in map.PassiveObjects)
                {
                    if (!defs.TryGetValue(p.Id, out PassiveObjectDef def))
                    {
                        def = PassiveObjectDef.Read(assets, catalog, p.Id);
                        defs[p.Id] = def;
                    }
                    RoomObject o = def == null ? null : RoomObject.Create(assets, def, p);
                    if (o != null)
                    {
                        objects.Add(o);
                    }
                }
            }
            return new RoomContent(map, RoomScene.ResolveBackgrounds(assets, map), RoomScene.ResolveLayers(assets, map), objects, gates);
        }
    }

    /// <summary>
    /// 副本房间的场景合成(证据 E-067):远景/中景/近景背景动画按视差横向滚动并平铺,地面瓦片按 224 像素一块排开,
    /// 再加上地图 [animation] 的各层动画、被动物件(障碍物)与出口(门);垂直位置以 <see cref="RoomMap.DefaultBackgroundPos"/> 为基准。
    /// 洛兰第一个房间在镜头 x 取最大值的原版截图上量到:
    ///   瓦片图像左上角 = (224 × 块号 − 镜头 x, 80 + 帧头 y);
    ///   远景与中景的左上角 x = −⌊镜头 x × 视差系数 / 100⌋ + 动画偏移 + 帧头 x,y = 80 + 动画偏移 y + 帧头 y(系数取自地图的 [far sight scroll] 与 [middle sight scroll]);
    ///   [animation] 里的动画与城镇一样,x 为世界 x − 镜头 x,y 为世界 y + 200(两层视差之外的层都不滚动)。
    /// 背景图宽 640,向右按 640 像素的周期重复。视差系数取整的方式(截断 / 四舍五入)只在一个镜头位置上量过,没有区分它们的样本;
    /// 之后又在另一个房间的镜头 x 为 0、58、320 三处核对了位置(证据 E-068)。
    /// </summary>
    public static class RoomScene
    {
        /// <summary>房间场景里地面瓦片所在的大类(与中景同类,画在中景之后、closeback 之前;相对 closeback 的先后见证据 E-067)。</summary>
        public const int TileRank = 1;

        /// <summary>一个已经解析好动画的背景(远景、中景或近景)。</summary>
        public sealed class ResolvedBackground
        {
            public RoomBackground Background { get; }
            public AniFile Ani { get; }
            public AniTimeline Timeline { get; }

            public ResolvedBackground(RoomBackground background, AniFile ani)
            {
                Background = background;
                Ani = ani;
                Timeline = new AniTimeline(ani);
            }
        }

        /// <summary>解析地图的 [background animation];动画缺失或没有帧的被跳过。</summary>
        public static List<ResolvedBackground> ResolveBackgrounds(DnfAssets assets, RoomMap map)
        {
            var result = new List<ResolvedBackground>();
            foreach (RoomBackground b in map.Backgrounds)
            {
                AniFile ani = assets.LoadAni(b.AnimationPath);
                if (ani == null || ani.Frames.Length == 0)
                {
                    continue;
                }
                result.Add(new ResolvedBackground(b, ani));
            }
            return result;
        }

        /// <summary>解析地图 [animation] 里的每一项(与 <see cref="TownScene.ResolveLayers"/> 相同,但用已经读好的 <see cref="RoomMap"/>)。</summary>
        public static List<TownScene.ResolvedLayer> ResolveLayers(DnfAssets assets, RoomMap map)
        {
            var result = new List<TownScene.ResolvedLayer>();
            foreach (MapAnimation a in map.Animations)
            {
                AniFile ani = assets.LoadAni(a.AnimationPath);
                if (ani == null || ani.Frames.Length == 0)
                {
                    continue;
                }
                result.Add(new TownScene.ResolvedLayer(a, ani));
            }
            return result;
        }

        /// <summary>某一层背景的视差系数(百分数):远景、中景、近景([close])各用地图里对应的一项;别的图层不滚动返回 100。</summary>
        public static int ScrollPercent(RoomMap map, string layer)
        {
            switch (layer)
            {
                case "[distantback]": return map.FarScroll;
                case "[middleback]": return map.MiddleScroll;
                case "[close]": return map.NearScroll;
                default: return 100;
            }
        }

        /// <summary>
        /// 视差位移:镜头 x 乘系数除以 100,向零截断。量过的那个点上截断、向下取整与四舍五入结果相同,没有区分它们的样本。
        /// </summary>
        public static int ParallaxOffset(int cameraX, int percent)
        {
            return (int)((long)cameraX * percent / 100);
        }

        /// <summary>
        /// 背景动画在 <paramref name="timeMs"/> 时刻的精灵,横向按图宽平铺到铺满屏幕。
        /// 返回的精灵按从左到右排;图层大类用 <see cref="TownScene.LayerRank"/>(远景 0、中景 1、近景 [close] 5)。
        /// </summary>
        public static List<SceneSprite> BackgroundSprites(DnfAssets assets, RoomMap map, IReadOnlyList<ResolvedBackground> backgrounds,
            int cameraX, long timeMs, int backgroundPos = RoomMap.DefaultBackgroundPos)
        {
            var result = new List<SceneSprite>();
            foreach (ResolvedBackground r in backgrounds)
            {
                AniSample sample = r.Timeline.Sample(timeMs);
                if (sample.Frame < 0)
                {
                    continue;
                }
                AniFrame f = r.Ani.Frames[sample.Frame];
                if (f.ImageIndex < 0 || f.ImageIndex >= r.Ani.ImagePaths.Length)
                {
                    continue;
                }
                string img = "sprite/" + r.Ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
                if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
                {
                    continue;
                }
                int period = sprite.CanvasWidth > 0 ? sprite.CanvasWidth : sprite.Width;
                if (period <= 0)
                {
                    continue;
                }
                int offset = ParallaxOffset(cameraX, ScrollPercent(map, r.Background.Layer));
                int x0 = -offset + f.X + sprite.OffsetX;
                int y = backgroundPos + f.Y + sprite.OffsetY;
                // 找到第一块与屏幕相交的重复
                int k = (int)Math.Floor((0 - x0 - sprite.Width) / (double)period);
                int rank = TownScene.LayerRank(r.Background.Layer);
                bool additive = f.TryGetGraphicEffect(out int effect) && effect == AniGraphicEffect.Additive;
                for (int guard = 0; guard < 64; guard++, k++)
                {
                    int x = x0 + k * period;
                    if (x >= RoomMap.ScreenWidth)
                    {
                        break;
                    }
                    if (x + sprite.Width <= 0)
                    {
                        continue;
                    }
                    result.Add(new SceneSprite(sprite, x, y, 0, -1, r.Background.AnimationPath, sample.Frame, false,
                        additive ? SpriteBlend.Additive : SpriteBlend.Normal, r.Timeline.TintAlphaAt(timeMs), rank));
                }
            }
            return result;
        }

        /// <summary>地面瓦片的精灵(只给与屏幕相交的块)。</summary>
        public static List<SceneSprite> TileSprites(DnfAssets assets, RoomMap map, int cameraX, int rank = TileRank)
        {
            var result = new List<SceneSprite>();
            for (int i = 0; i < map.Tiles.Count; i++)
            {
                int left = i * TileFile.TileWidth - cameraX;
                if (left >= RoomMap.ScreenWidth || left + TileFile.TileWidth <= 0)
                {
                    continue;
                }
                TileFile tile = map.Tiles[i];
                foreach (KeyValuePair<string, int> image in tile.Images)
                {
                    if (!assets.TryGetFrame(image.Key, image.Value, out SpriteFrame sprite))
                    {
                        continue;
                    }
                    result.Add(new SceneSprite(sprite, left + sprite.OffsetX, tile.ImagePosY + sprite.OffsetY, 0, -1, tile.Path, image.Value, false,
                        SpriteBlend.Normal, 255, rank));
                }
            }
            return result;
        }

        /// <summary>
        /// 整个房间在 <paramref name="timeMs"/> 时刻的绘制列表:背景、瓦片、[animation] 图层、被动物件、出口,加上 <paramref name="extra"/>(主角、怪物等);
        /// 先按图层大类、normal 大类内按世界 y 稳定排序(同城镇)。门物件的图层大类取自它自己的 [layer],在同一大类里画在图层与物件之后。
        /// </summary>
        public static List<SceneSprite> Build(DnfAssets assets, RoomContent content, int cameraX, int groundY, long timeMs,
            IReadOnlyList<SceneSprite> extra)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }
            var sprites = new List<SceneSprite>();
            sprites.AddRange(BackgroundSprites(assets, content.Map, content.Backgrounds, cameraX, timeMs));
            sprites.AddRange(TileSprites(assets, content.Map, cameraX));
            var projection = new TownProjection(cameraX, groundY);
            foreach (TownScene.ResolvedLayer layer in content.Layers)
            {
                SceneSprite s = TownScene.LayerSpriteAt(assets, layer, timeMs, projection);
                if (s != null)
                {
                    sprites.Add(s);
                }
            }
            foreach (RoomObject o in content.Objects)
            {
                SceneSprite s = o.SpriteAt(assets, projection, timeMs);
                if (s != null)
                {
                    sprites.Add(s);
                }
            }
            foreach (RoomGate gate in content.Gates)
            {
                sprites.AddRange(gate.Sprites(assets, projection, timeMs));
            }
            if (extra != null)
            {
                sprites.AddRange(extra);
            }
            return TownScene.SortByDepth(sprites);
        }
    }
}
