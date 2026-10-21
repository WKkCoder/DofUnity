using System;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 世界里的一次性动画特效(技能的斩击光影之类,证据 E-076):一个 .ani 从头播一遍,放在世界 (x, y, z),朝左时镜像(.ani 按朝右画),
    /// 播完就结束(<see cref="Finished"/>)。位置按 <see cref="TownProjection"/> 摆,与主角、怪物同一套;
    /// 帧自带的图形特效(类型 2 = 加法混合)与染色 alpha 照 .ani 里的来(与地图图层动画同一条路径)。
    /// 放置点与帧偏移的关系与主角的动作动画相同(帧偏移加图块自带偏移,以放置点为原点)——这一点只在鬼斩上看过截图,其他特效没核对。
    /// 常驻的动画(<see cref="Persistent"/>,营火、场景里的 NPC 这类):播完不消失——不循环的停在最后一帧(<see cref="AniTimeline"/> 的规则),循环的一直循环。
    /// 图层大类(<see cref="LayerRank"/>)默认是 normal(与主角、怪物一起按世界 y 排);营火那种要盖在整屏黑幕之上的常驻动画给更大的大类。
    /// </summary>
    public sealed class AnimatedEffect
    {
        private readonly AniFile _ani;
        private readonly AniTimeline _timeline;

        /// <summary>.ani 的 PVF 路径(只用来标记精灵)。</summary>
        public string Path { get; }

        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public bool FacingLeft { get; }

        /// <summary>画家算法的排序键:世界 y 加这个偏移;比主角大 1 就画在主角前面。</summary>
        public int SortBias { get; }

        /// <summary>精灵的图层大类(<see cref="SceneSprite.LayerRank"/>);默认 <see cref="TownScene.NormalRank"/>。</summary>
        public int LayerRank { get; }

        /// <summary>已经播放的毫秒数。</summary>
        public double AgeMs { get; private set; }

        /// <summary>整个动画的时长(毫秒)。</summary>
        public long DurationMs => _timeline.TotalMilliseconds;

        /// <summary>常驻:播完也不消失(见类说明)。</summary>
        public bool Persistent { get; }

        public bool Finished => !Persistent && AgeMs >= DurationMs;

        private AnimatedEffect(AniFile ani, string path, double x, double y, double z, bool facingLeft, int sortBias, bool persistent, int layerRank)
        {
            _ani = ani;
            _timeline = new AniTimeline(ani);
            Path = path;
            X = x;
            Y = y;
            Z = z;
            FacingLeft = facingLeft;
            SortBias = sortBias;
            Persistent = persistent;
            LayerRank = layerRank;
        }

        /// <summary>读 <paramref name="path"/> 的动画放在 (x, y, z);读不到或没有帧时返回 null。</summary>
        public static AnimatedEffect Create(DnfAssets assets, string path, double x, double y, double z, bool facingLeft, int sortBias = 1, bool persistent = false,
            int layerRank = TownScene.NormalRank)
        {
            if (assets == null || string.IsNullOrEmpty(path))
            {
                return null;
            }
            return CreateFromAni(assets.LoadAni(path), path, x, y, z, facingLeft, sortBias, persistent, layerRank);
        }

        /// <summary>用已经读好的动画 <paramref name="ani"/> 放在 (x, y, z);<paramref name="path"/> 只用来标记精灵。动画为 null 或没有帧时返回 null。</summary>
        public static AnimatedEffect CreateFromAni(AniFile ani, string path, double x, double y, double z, bool facingLeft, int sortBias = 1, bool persistent = false,
            int layerRank = TownScene.NormalRank)
        {
            return ani == null || ani.Frames.Length == 0 ? null : new AnimatedEffect(ani, path, x, y, z, facingLeft, sortBias, persistent, layerRank);
        }

        /// <summary>把特效挪到世界 (x, y, z)(飞行的投射物每一步换一次位置;朝向不变)。</summary>
        public void MoveTo(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>播放推进 <paramref name="deltaMs"/> 毫秒。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                AgeMs += deltaMs;
            }
        }

        /// <summary>此刻的精灵;当前帧没有图(空帧、IMG 缺失)或已经播完时返回 null。</summary>
        public SceneSprite Sprite(DnfAssets assets, TownProjection projection)
        {
            if (Finished)
            {
                return null;
            }
            long t = (long)AgeMs;
            AniSample sample = _timeline.Sample(t);
            if (sample.Frame < 0)
            {
                return null;
            }
            AniFrame f = _ani.Frames[sample.Frame];
            if (f.ImageIndex < 0 || f.ImageIndex >= _ani.ImagePaths.Length)
            {
                return null;
            }
            string img = "sprite/" + _ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
            if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite) || sprite.Width == 0 || sprite.Height == 0)
            {
                return null;
            }
            bool mirrored = FacingLeft ^ (f.TryGetScale(out float scaleX, out _) && scaleX < 0f);
            projection.TopLeft((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Round(Z), f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
            bool additive = f.TryGetGraphicEffect(out int effect) && effect == AniGraphicEffect.Additive;
            return new SceneSprite(sprite, sx, sy, (int)Math.Floor(Y) + SortBias, -1, Path, sample.Frame, mirrored,
                additive ? SpriteBlend.Additive : SpriteBlend.Normal, _timeline.TintAlphaAt(t), LayerRank);
        }
    }
}
