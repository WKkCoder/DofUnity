using System;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 界面上播放的 .ani(选角界面背景的光束与粒子,证据 E-096):原点放在屏幕坐标 (<see cref="X"/>, <see cref="Y"/>),
    /// 每帧的精灵位置 = 原点 + 帧偏移 + 图自带偏移;帧带图形特效 2 时用加法混合,染色 alpha 与帧间插值同 <see cref="AniTimeline"/>。
    /// 帧偏移在两帧之间的插值(光束左右飘动)没有被核对过:基线截图上光束正好停在关键帧附近,中间值只是推断(与染色 alpha 的插值同一个开关)。
    /// 与世界里的 <see cref="AnimatedEffect"/> 不同,这里没有世界坐标、相机与镜像,也不分图层大类:精灵直接是叠加层大类 <see cref="OverlayArt.UiRank"/>。
    /// </summary>
    public sealed class UiAnimation
    {
        private readonly AniFile _ani;
        private readonly AniTimeline _timeline;
        private readonly ISpriteSource _sprites;

        /// <summary>.ani 的 PVF 路径(用来标记精灵)。</summary>
        public string Path { get; }

        public int X { get; }

        public int Y { get; }

        private UiAnimation(ISpriteSource sprites, AniFile ani, string path, int x, int y)
        {
            _sprites = sprites;
            _ani = ani;
            _timeline = new AniTimeline(ani);
            Path = path;
            X = x;
            Y = y;
        }

        /// <summary>读 <paramref name="path"/> 的动画放在屏幕 (x, y);读不到或没有帧时返回 null。</summary>
        public static UiAnimation Create(ISpriteSource sprites, Func<string, AniFile> loadAni, string path, int x, int y)
        {
            if (sprites == null)
            {
                throw new ArgumentNullException(nameof(sprites));
            }
            if (loadAni == null || string.IsNullOrEmpty(path))
            {
                return null;
            }
            AniFile ani = loadAni(path);
            return ani == null || ani.Frames.Length == 0 ? null : new UiAnimation(sprites, ani, path, x, y);
        }

        /// <summary>整个动画的时长(毫秒)。</summary>
        public long DurationMs => _timeline.TotalMilliseconds;

        /// <summary>
        /// 时刻 <paramref name="ageMs"/>(从界面打开算起,循环的动画按周期取模)的精灵;当前帧没有图时返回 null。
        /// <paramref name="order"/> 是叠加层里的先后(同大类按列表顺序画)。
        /// </summary>
        public SceneSprite Sprite(long ageMs, int order)
        {
            AniSample sample = _timeline.Sample(ageMs);
            if (sample.Frame < 0)
            {
                return null;
            }
            AniFrame f = _ani.Frames[sample.Frame];
            if (f.ImageIndex < 0 || f.ImageIndex >= _ani.ImagePaths.Length)
            {
                return null;
            }
            string image = "sprite/" + _ani.ImagePaths[f.ImageIndex].Replace('\\', '/').ToLowerInvariant();
            if (!_sprites.TryGetFrame(image, f.FrameIndex, out SpriteFrame sprite) || sprite.Width == 0 || sprite.Height == 0)
            {
                return null;
            }
            int fx = f.X;
            int fy = f.Y;
            if (sample.Interpolates)
            {
                AniFrame next = _ani.Frames[sample.NextFrame];
                fx = (int)Math.Round(f.X + (next.X - f.X) * sample.Progress, MidpointRounding.AwayFromZero);
                fy = (int)Math.Round(f.Y + (next.Y - f.Y) * sample.Progress, MidpointRounding.AwayFromZero);
            }
            bool additive = f.TryGetGraphicEffect(out int effect) && effect == AniGraphicEffect.Additive;
            return new SceneSprite(sprite, X + fx + sprite.OffsetX, Y + fy + sprite.OffsetY, order, -3, Path, sample.Frame, false,
                additive ? SpriteBlend.Additive : SpriteBlend.Normal, _timeline.TintAlphaAt(ageMs), OverlayArt.UiRank);
        }
    }
}
