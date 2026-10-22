using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

// 证据:E-115(用币窗口的界面图:大金数字、币图标与存量小数字;窗口的摆放位置没有读到,下面的坐标是按观感摆的推断)。
namespace Dnf.Play
{
    /// <summary>
    /// 主角死后的复活倒计时画面(证据 E-115):屏幕上方居中一排原版的金色大数字(<c>coin_countdown.img</c>,0 到 9 各一帧),
    /// 数字下面一行提示("按 X 复活"这类;原版的中文文字走字符串表,我们还没有中文字库,所以用自带的像素字写英文占位)与复活币的存量
    /// (<c>CountDown.img</c> 第 0 帧是币图标,<c>coin_stock.img</c> 的第 10 帧是乘号、0 到 9 是小数字)。
    /// 图包里缺数字图时画法为 null(<see cref="For"/>),调用方退回 <see cref="BuildPlaceholder"/> 的纯像素字版本。
    /// 位置全是推断:原版用币窗口(<c>usecoinwindow.ui</c>)的摆放没有读到。
    /// </summary>
    public sealed class ReviveArt
    {
        public const string DigitImage = "sprite/interface2/common/digit/coin_countdown.img";
        public const string StockImage = "sprite/interface2/common/digit/coin_stock.img";
        public const string IconImage = "sprite/interface2/countdown/countdown.img";

        /// <summary><see cref="IconImage"/> 里的币图标帧。</summary>
        public const int IconFrame = 0;

        /// <summary><see cref="StockImage"/> 里的乘号帧(0 到 9 是数字)。</summary>
        public const int TimesFrame = 10;

        /// <summary>倒计时数字的水平中心(画面 800 宽的正中,推断)。</summary>
        public const int CenterX = 400;

        /// <summary>倒计时数字的上边(推断)。</summary>
        public const int DigitTop = 96;

        /// <summary>相邻两位数字的重叠(像素;数字带外描边,贴紧一点好看,推断)。</summary>
        public const int DigitOverlap = 4;

        /// <summary>数字下沿到提示行上沿的间隔。</summary>
        public const int PromptGap = 12;

        /// <summary>提示行与存量行之间的间隔。</summary>
        public const int LineGap = 10;

        /// <summary>提示文字的字号(占位像素字的放大倍数)。</summary>
        public const int PromptScale = 3;

        /// <summary>提示文字不能用币时的颜色(红)。</summary>
        public const uint BlockedColor = 0xFF5050u;

        private readonly ISpriteSource _sprites;

        private ReviveArt(ISpriteSource sprites)
        {
            _sprites = sprites;
        }

        /// <summary>造画法;图包里没有倒计时数字图的第 0 帧就返回 null(调用方退回占位版)。</summary>
        public static ReviveArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            return sprites.TryGetFrame(DigitImage, 0, out SpriteFrame zero) && zero != null ? new ReviveArt(sprites) : null;
        }

        /// <summary>倒计时显示的数字串:秒数夹在 0 到 99 之间的十进制。</summary>
        public static string Digits(int seconds)
        {
            int clamped = seconds < 0 ? 0 : (seconds > 99 ? 99 : seconds);
            return clamped.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>提示行的占位英文(原版的是字符串表里的 <c>use_coin_00</c> 一类,中文):能复活是"按 X 复活",没有币、到上限各有各的话。</summary>
        public static string PromptOf(CoinVerdict verdict)
        {
            switch (verdict)
            {
                case CoinVerdict.NoCoin:
                    return "NO REVIVE COIN";
                case CoinVerdict.LimitReached:
                    return "COIN LIMIT REACHED";
                default:
                    return "PRESS X TO REVIVE";
            }
        }

        /// <summary>
        /// 把倒计时(<paramref name="seconds"/>)、提示行与币存量依画的先后追加到 <paramref name="into"/>,<paramref name="order"/> 逐个加一。
        /// 图包缺帧时什么也不加并返回 false(调用方退回占位版)。
        /// </summary>
        public bool Build(List<SceneSprite> into, ref int order, OverlayArt text, int seconds, CoinVerdict verdict, int stock)
        {
            if (into == null)
            {
                return false;
            }
            string digits = Digits(seconds);
            var frames = new SpriteFrame[digits.Length];
            int total = 0;
            int height = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                if (!_sprites.TryGetFrame(DigitImage, digits[i] - '0', out SpriteFrame f) || f == null)
                {
                    return false;
                }
                frames[i] = f;
                total += f.CanvasWidth - (i > 0 ? DigitOverlap : 0);
                height = Math.Max(height, f.CanvasHeight);
            }
            int x = CenterX - total / 2;
            foreach (SpriteFrame f in frames)
            {
                into.Add(OverlayArt.Place(f, x + f.OffsetX, DigitTop + f.OffsetY, order++, "revive-digit"));
                x += f.CanvasWidth - DigitOverlap;
            }
            AddPrompt(into, ref order, text, verdict, stock, DigitTop + height + PromptGap, _sprites);
            return true;
        }

        /// <summary>没有原版数字图时的版本:大号像素字的倒计时,下面同样的提示行与存量。</summary>
        public static void BuildPlaceholder(List<SceneSprite> into, ref int order, OverlayArt text, int seconds, CoinVerdict verdict, int stock)
        {
            SpriteFrame big = text.Text(Digits(seconds), 14, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(big, CenterX - big.Width / 2, DigitTop, order++, "revive-digit"));
            AddPrompt(into, ref order, text, verdict, stock, DigitTop + big.Height + PromptGap, null);
        }

        /// <summary>提示行(居中)与它下面的存量行:免费复活写"FREE",否则是币图标、乘号与存量(没有图时写 COINS n)。</summary>
        private static void AddPrompt(List<SceneSprite> into, ref int order, OverlayArt text, CoinVerdict verdict, int stock, int top, ISpriteSource sprites)
        {
            bool blocked = verdict == CoinVerdict.NoCoin || verdict == CoinVerdict.LimitReached;
            SpriteFrame prompt = text.Text(PromptOf(verdict), PromptScale, blocked ? BlockedColor : OverlayArt.Yellow);
            into.Add(OverlayArt.Place(prompt, CenterX - prompt.Width / 2, top, order++, "revive-prompt"));
            int y = top + prompt.Height + LineGap;
            if (verdict == CoinVerdict.Free)
            {
                SpriteFrame free = text.Text("FREE", 2, OverlayArt.White);
                into.Add(OverlayArt.Place(free, CenterX - free.Width / 2, y, order++, "revive-stock"));
                return;
            }
            if (!AddStock(into, ref order, stock, y, sprites))
            {
                SpriteFrame label = text.Text("COINS " + Math.Max(0, stock).ToString(CultureInfo.InvariantCulture), 2, OverlayArt.White);
                into.Add(OverlayArt.Place(label, CenterX - label.Width / 2, y, order++, "revive-stock"));
            }
        }

        /// <summary>币图标 + 乘号 + 存量小数字,整体居中;缺任何一帧就什么也不加并返回 false。</summary>
        private static bool AddStock(List<SceneSprite> into, ref int order, int stock, int top, ISpriteSource sprites)
        {
            if (sprites == null || !sprites.TryGetFrame(IconImage, IconFrame, out SpriteFrame icon) || icon == null
                || !sprites.TryGetFrame(StockImage, TimesFrame, out SpriteFrame times) || times == null)
            {
                return false;
            }
            string digits = Math.Max(0, stock).ToString(CultureInfo.InvariantCulture);
            var frames = new SpriteFrame[digits.Length];
            for (int i = 0; i < digits.Length; i++)
            {
                if (!sprites.TryGetFrame(StockImage, digits[i] - '0', out SpriteFrame f) || f == null)
                {
                    return false;
                }
                frames[i] = f;
            }
            const int gap = 3;
            int total = icon.CanvasWidth + gap + times.CanvasWidth + gap;
            foreach (SpriteFrame f in frames)
            {
                total += f.CanvasWidth;
            }
            int x = CenterX - total / 2;
            into.Add(OverlayArt.Place(icon, x + icon.OffsetX, top + icon.OffsetY, order++, "revive-stock"));
            x += icon.CanvasWidth + gap;
            // 乘号与数字的画布比币图标矮:垂直居中到币图标上
            into.Add(OverlayArt.Place(times, x + times.OffsetX, top + (icon.CanvasHeight - times.CanvasHeight) / 2 + times.OffsetY, order++, "revive-stock"));
            x += times.CanvasWidth + gap;
            foreach (SpriteFrame f in frames)
            {
                into.Add(OverlayArt.Place(f, x + f.OffsetX, top + (icon.CanvasHeight - f.CanvasHeight) / 2 + f.OffsetY, order++, "revive-stock"));
                x += f.CanvasWidth;
            }
            return true;
        }
    }
}
