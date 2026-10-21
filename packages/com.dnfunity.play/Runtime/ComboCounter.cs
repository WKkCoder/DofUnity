using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 连击计数("N HitCombo")的状态机(证据 E-108)。
    /// 观察(读 DNF.exe 的界面窗口类,登记函数 0x008ef980、绘制函数 0x008ee7f0):
    /// 主角(或主角的子弹、特效)每打中一个怪物,计数加 1;计数从 <see cref="ShowFrom"/> 起才显示;
    /// 每次命中(计数不小于 <see cref="ShowFrom"/> 时)重启显示计时器,计时器走满之后计数清零。
    /// 画法:颜色档按计数分(不大于 10 青色、11 到 20 黄色、不小于 21 红色);标签左边缘 x 在 <see cref="BaseX"/> 到 <see cref="RestX"/> 之间随计时器动:
    /// 第一次显示(计数恰为 2)从右边 <see cref="SlideFrom"/> 像素外滑进来,之后每多一击抖动一下,抖动与滑入用的是客户端的二次缓动(<see cref="Ease"/>,0x011b9a50)。
    /// 推断(没有读到来源):窗口的毫秒数在客户端是按职业查的一张表(登记函数里 <c>this+0x5e8</c>,表在全局配置对象的向量里,没找到装载它的代码);
    /// <see cref="DefaultWindowMs"/> 取自基线(S07、S09 的连击在最后一击后 1.15 到 1.27 秒消失)标定的值。
    /// 没做:第二个计数槽(登记函数里的第二个槽,受击者种类为 4 时才加,语义没读通)、结束时的技巧点数与语音、PVP 与特殊关卡里的另一套位置。
    /// </summary>
    public sealed class ComboCounter
    {
        /// <summary>连击窗口的缺省毫秒数(基线标定,见类说明)。</summary>
        public const int DefaultWindowMs = 1200;

        /// <summary>计数从第几击起显示(客户端阈值表 <c>0x015d45bc</c> 的第 0 项)。</summary>
        public const int ShowFrom = 2;

        /// <summary>计数不小于它时换黄色一档(绘制函数里 <c>count &lt; 11</c> 判断)。</summary>
        public const int YellowFrom = 11;

        /// <summary>计数不小于它时换红色一档(绘制函数里 <c>count &gt; 20</c> 判断)。</summary>
        public const int RedFrom = 21;

        /// <summary>标签左边缘的最小 x(滑入的终点与抖动的下限,常数 0x29e)。</summary>
        public const int BaseX = 670;

        /// <summary>静止时标签左边缘的 x(常数 0x2a3,比 <see cref="BaseX"/> 大 5)。</summary>
        public const int RestX = 675;

        /// <summary>第一次显示时从多远之外滑进来(像素,常数 0x6e)。</summary>
        public const int SlideFrom = 110;

        /// <summary>滑入的毫秒数(常数 0xfa),缓动方式为先快后慢。</summary>
        public const int SlideMs = 250;

        /// <summary>滑入之后"落回"静止位置的毫秒数(常数 0x32),缓动方式为先慢后快;落回总共 5 像素。</summary>
        public const int SettleMs = 50;

        /// <summary>每多一击的抖动:先用 <see cref="SlideMs"/> 的缓动从 5 像素往回收,收到第 <see cref="PulseMs"/> 毫秒换成落回,到第 <see cref="PulseMs"/> + <see cref="SettleMs"/> 毫秒结束。</summary>
        public const int PulseMs = 50;

        /// <summary>抖动与落回的像素数(常数 5)。</summary>
        public const int Shake = 5;

        private int _count;
        private double _nowMs;
        private double _lastHitMs;
        private double _showMs;

        /// <summary>连击窗口(毫秒)。</summary>
        public int WindowMs { get; }

        public ComboCounter(int windowMs = DefaultWindowMs)
        {
            WindowMs = windowMs < 1 ? 1 : windowMs;
        }

        /// <summary>累计命中数:窗口内每一击加 1,窗口走满清零。</summary>
        public int Count => _count;

        /// <summary>屏幕上显示的数;计数没到 <see cref="ShowFrom"/> 为 0。</summary>
        public int Shown => _count >= ShowFrom ? _count : 0;

        /// <summary>显示计时器的年龄(毫秒):距最近一次刷新显示(计数不小于 <see cref="ShowFrom"/> 的那一击)。</summary>
        public int AgeMs
        {
            get
            {
                double age = Math.Floor(_nowMs - _showMs);
                return age < 0 ? 0 : age > int.MaxValue ? int.MaxValue : (int)age;
            }
        }

        /// <summary>颜色档:0 青、1 黄、2 红。</summary>
        public int Tier => TierOf(Shown);

        /// <summary>现在标签的左边缘 x。</summary>
        public int LabelX => LabelXAt(Shown, AgeMs);

        /// <summary>记一击命中;<paramref name="nowMs"/> 是界面时钟(毫秒)。</summary>
        public void Hit(double nowMs)
        {
            _nowMs = nowMs;
            _count = _count == int.MaxValue ? _count : _count + 1;
            _lastHitMs = nowMs;
            if (_count >= ShowFrom)
            {
                _showMs = nowMs;
            }
        }

        /// <summary>推进界面时钟;最后一击之后走满窗口,计数清零。</summary>
        public void Update(double nowMs)
        {
            _nowMs = nowMs;
            if (_count > 0 && nowMs - _lastHitMs >= WindowMs)
            {
                _count = 0;
            }
        }

        /// <summary>清零(换场景用)。</summary>
        public void Reset()
        {
            _count = 0;
        }

        /// <summary>某个显示数落在哪一档:不大于 10 → 0,11 到 20 → 1,不小于 21 → 2。</summary>
        public static int TierOf(int shown)
        {
            return shown < YellowFrom ? 0 : shown < RedFrom ? 1 : 2;
        }

        /// <summary>
        /// 标签左边缘的 x:<paramref name="shown"/> 是显示数,<paramref name="ageMs"/> 是显示计时器的年龄(毫秒)。
        /// 显示数恰为 2 时是第一次出现的滑入:年龄不到 250 毫秒用 <see cref="Ease"/>(110 → 0,先快后慢),250 到 300 毫秒用(0 → 5,先慢后快),之后静止;
        /// 其余显示数是每多一击的抖动:年龄不到 50 毫秒用(5 → 0,250 毫秒的先快后慢,只走了开头),50 到 100 毫秒用(0 → 5,先慢后快),之后静止。
        /// 结果都加上 <see cref="BaseX"/>,静止值是 <see cref="RestX"/>。
        /// </summary>
        public static int LabelXAt(int shown, int ageMs)
        {
            if (shown == ShowFrom)
            {
                if (ageMs < SlideMs)
                {
                    return BaseX + Ease(SlideFrom, 0, ageMs, SlideMs, true);
                }
                if (ageMs < SlideMs + SettleMs)
                {
                    return BaseX + Ease(0, Shake, ageMs - SlideMs, SettleMs, false);
                }
                return RestX;
            }
            if (ageMs < PulseMs)
            {
                return BaseX + Ease(Shake, 0, ageMs, SlideMs, true);
            }
            if (ageMs < PulseMs + SettleMs)
            {
                return BaseX + Ease(0, Shake, ageMs - PulseMs, SettleMs, false);
            }
            return RestX;
        }

        /// <summary>
        /// 客户端的缓动(0x011b9a50 加上取整的外壳 0x011b9ef0):从 <paramref name="from"/> 走到 <paramref name="to"/>,已过 <paramref name="elapsedMs"/> 毫秒、总长 <paramref name="durationMs"/>。
        /// 已过时间不小于总长返回 <paramref name="to"/>,不到 1 返回 <paramref name="from"/>;
        /// <paramref name="easeOut"/> 为真时位移比例是 1 − (1 − t)²(先快后慢),为假时是 t²(先慢后快),t = 已过 / 总长;最后向零取整。
        /// 浮点步骤按客户端:差值乘 2 除以总长(单精度),再按 x87 的扩展精度做二次式。
        /// </summary>
        public static int Ease(int from, int to, int elapsedMs, int durationMs, bool easeOut)
        {
            if (durationMs <= elapsedMs)
            {
                return to;
            }
            if (elapsedMs < 1)
            {
                return from;
            }
            float start = from;
            float end = to;
            int e = elapsedMs;
            if (!easeOut)
            {
                e = durationMs - elapsedMs;
                start = to;
                end = from;
            }
            float total = durationMs;
            float twice = Math.Abs(end - start) * 2.0f / total;
            double moved = ((double)(-(twice / total)) * 0.5 * e + twice) * e;
            double result = start < end ? moved + start : start - moved;
            return (int)result;
        }
    }

    /// <summary>
    /// 连击计数的画法(证据 E-108):图在 <c>sprite/common/etc/combo.img</c>,每个颜色档十一帧——十个数字(帧号 = 档 × 11 + 数字)和一块 "HitCombo" 标签(帧号 = 档 × 11 + 10)。
    /// 位置(观察,与基线逐像素对过):数字的上边 <see cref="DigitY"/>,标签的上边比它低 <see cref="LabelDy"/>,标签左边缘是 <see cref="ComboCounter.LabelX"/>;
    /// 数字右对齐到标签左边缘:从最后一位往前画,每画一位累计 画布宽 − <see cref="DigitOverlap"/>,这一位画在 x − 累计;先画标签再画数字,左边的数字盖在右边的上面。
    /// 推进量取帧的画布宽(观察,读码):数字字体取的宽度访问器 0x011b3dc0 读帧对象的 +0x24,图像帧头装载函数(0x012072a0 一族)把帧头里的画布宽高(宽、高、大小、x、y 之后的两个数)写进 +0x24 与 +0x26,图块宽高在 +0x1c 与 +0x1e、偏移在 +0x20 与 +0x22。
    /// 数字 1 的画布比图块宽(图块 19、画布 28、偏移 5),所以数字 1 的推进量比它的图块宽;基线里没有带 1 的连击,这一点没被基线逐像素对过(其余数字的画布与图块一样宽,已对过)。
    /// </summary>
    public sealed class ComboArt
    {
        public const string Image = "sprite/common/etc/combo.img";

        /// <summary>数字的上边(像素)。</summary>
        public const int DigitY = 243;

        /// <summary>标签的上边比数字低几个像素。</summary>
        public const int LabelDy = 8;

        /// <summary>相邻两位的重叠(像素,客户端字体对象的间距 −6)。</summary>
        public const int DigitOverlap = 6;

        /// <summary>每个颜色档占的帧数。</summary>
        public const int FramesPerTier = 11;

        /// <summary>显示数的上限(客户端数字绘制函数的夹取值)。</summary>
        public const int MaxShown = 9999999;

        private readonly ISpriteSource _sprites;

        private ComboArt(ISpriteSource sprites)
        {
            _sprites = sprites;
        }

        /// <summary>造画法;图包里没有连击图的第 0 帧就返回 null(调用方退回占位文字)。</summary>
        public static ComboArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            return sprites.TryGetFrame(Image, 0, out SpriteFrame zero) && zero != null ? new ComboArt(sprites) : null;
        }

        /// <summary>要画的数字串:夹在 1 到 <see cref="MaxShown"/> 之间的十进制。</summary>
        public static string Digits(int shown)
        {
            int clamped = shown > MaxShown ? MaxShown : shown < 0 ? 0 : shown;
            return clamped.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把显示数 <paramref name="shown"/>(年龄 <paramref name="ageMs"/>)的标签与数字依画的先后追加到 <paramref name="into"/>,
        /// <paramref name="order"/> 逐个加一。显示数小于 <see cref="ComboCounter.ShowFrom"/> 或图包缺帧时什么也不加并返回 false。
        /// </summary>
        public bool Build(int shown, int ageMs, List<SceneSprite> into, ref int order)
        {
            if (into == null || shown < ComboCounter.ShowFrom)
            {
                return false;
            }
            int first = ComboCounter.TierOf(shown) * FramesPerTier;
            if (!_sprites.TryGetFrame(Image, first + 10, out SpriteFrame label) || label == null)
            {
                return false;
            }
            string digits = Digits(shown);
            var frames = new SpriteFrame[digits.Length];
            for (int i = 0; i < digits.Length; i++)
            {
                if (!_sprites.TryGetFrame(Image, first + (digits[i] - '0'), out SpriteFrame f) || f == null)
                {
                    return false;
                }
                frames[i] = f;
            }
            int x = ComboCounter.LabelXAt(shown, ageMs);
            into.Add(OverlayArt.Place(label, x + label.OffsetX, DigitY + LabelDy + label.OffsetY, order++, "combo"));
            int advance = 0;
            for (int i = frames.Length - 1; i >= 0; i--)
            {
                SpriteFrame f = frames[i];
                advance += f.CanvasWidth - DigitOverlap;
                into.Add(OverlayArt.Place(f, x - advance + f.OffsetX, DigitY + f.OffsetY, order++, "combo"));
            }
            return true;
        }
    }
}
