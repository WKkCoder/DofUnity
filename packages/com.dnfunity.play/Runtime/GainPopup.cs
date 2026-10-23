using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

// 证据:E-116(客户端 0x00860db0 是"获得类飘字"的生成函数:按种类选图包 与 格式串——金币是 Common/Etc/MoneyUp.img 与 "%dG";
// 字符到帧的对应:数字 0 到 9 是第 0 到 9 帧,'+' 是第 11 帧,结尾的 E、G、M、P 标记都是第 10 帧;整串按各帧宽度相加后居中)。
namespace Dnf.Play
{
    /// <summary>
    /// 拾取金币后主角头顶升起的数字(客户端的"获得金币"飘字):金额的十进制数字,后面跟图包里第 10 帧的结尾标记。
    /// 每个字符在客户端里其实是三层叠起来的动画(起始时刻错开 0、300、600 毫秒),这里只画一层;
    /// 上升距离、停留时间与淡出是<b>推断</b>(没有读到那三层动画的运动)。
    /// </summary>
    public sealed class GainPopup
    {
        /// <summary>金币飘字的图包(客户端 <c>Common/Etc/MoneyUp.img</c> 的小写路径)。</summary>
        public const string ImagePath = "sprite/common/etc/moneyup.img";

        /// <summary>结尾标记所在的帧(客户端里 E、G、M、P 四个结尾字母都映射到第 10 帧)。</summary>
        public const int LabelFrame = 10;

        /// <summary>整个飘字存在的时间(毫秒;推断)。</summary>
        public const double LifeMs = 1100.0;

        /// <summary>淡出开始的时刻占寿命的比例(推断)。</summary>
        public const double FadeStart = 0.65;

        /// <summary>从头顶升到的高度(像素;推断)。</summary>
        public const int RisePixels = 28;

        /// <summary>主角头顶离脚底的高度(像素;推断:取一个比常见身高略高的值)。</summary>
        public const int HeadHeight = 118;

        public GainPopup(int amount, double worldX, double worldY, int z)
        {
            Amount = amount;
            WorldX = worldX;
            WorldY = worldY;
            Z = z;
        }

        public int Amount { get; }

        public double WorldX { get; }

        public double WorldY { get; }

        public int Z { get; }

        public double AgeMs { get; set; }

        public bool Finished => AgeMs >= LifeMs;

        /// <summary>飘字的文本形式(客户端的格式串 <c>%dG</c>;没有图时的占位文字用它)。</summary>
        public string Text => Amount.ToString(CultureInfo.InvariantCulture) + "G";

        /// <summary>淡出的不透明度(0 到 255)。</summary>
        public int AlphaAt(double ageMs)
        {
            double t = ageMs / LifeMs;
            if (t <= FadeStart)
            {
                return 255;
            }
            if (t >= 1.0)
            {
                return 0;
            }
            return (int)Math.Round(255.0 * (1.0 - (t - FadeStart) / (1.0 - FadeStart)));
        }

        /// <summary>此刻升高的像素数。</summary>
        public int RiseAt(double ageMs)
        {
            double t = Math.Min(1.0, Math.Max(0.0, ageMs / LifeMs));
            return (int)Math.Round(RisePixels * t);
        }

        /// <summary>
        /// 用图包里的数字帧拼出飘字并追加到 <paramref name="into"/>(<paramref name="order"/> 逐个加一);
        /// 图包缺任何一帧时什么也不加并返回 false(调用方退回占位文字)。
        /// </summary>
        public bool Build(List<SceneSprite> into, ref int order, ISpriteSource sprites, TownProjection projection)
        {
            if (into == null || sprites == null)
            {
                return false;
            }
            string digits = Amount.ToString(CultureInfo.InvariantCulture);
            var frames = new SpriteFrame[digits.Length + 1];
            int total = 0;
            for (int i = 0; i < frames.Length; i++)
            {
                int index = i < digits.Length ? digits[i] - '0' : LabelFrame;
                if (!sprites.TryGetFrame(ImagePath, index, out SpriteFrame f) || f == null)
                {
                    return false;
                }
                frames[i] = f;
                total += f.CanvasWidth;
            }
            int alpha = AlphaAt(AgeMs);
            if (alpha <= 0)
            {
                return true;
            }
            int x = (int)Math.Floor(WorldX) - projection.CameraX - total / 2;
            int top = (int)Math.Floor(WorldY) + projection.GroundY - Z - HeadHeight - RiseAt(AgeMs);
            foreach (SpriteFrame f in frames)
            {
                into.Add(OverlayArt.Place(f, x + f.OffsetX, top + f.OffsetY, order++, "gain", alpha));
                x += f.CanvasWidth;
            }
            return true;
        }
    }
}
