using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>结算菜单上的选项(证据 E-091)。</summary>
    public enum SettlementChoice
    {
        /// <summary>还没选。</summary>
        None = 0,

        /// <summary>再次挑战(原版热键 F13):同一个副本重新开始。</summary>
        Retry = 1,

        /// <summary>快速组队(原版没指定热键):要组队系统,单机里没有,点了没反应。</summary>
        QuickParty = 2,

        /// <summary>选择其它地下城(原版热键 F11):回到副本选择界面。</summary>
        SelectDungeon = 3,

        /// <summary>返回城镇(原版热键 F12)。</summary>
        ReturnToTown = 4,
    }

    /// <summary>
    /// 结算菜单(通关后右上角那块面板,证据 E-091):用户自备的图包里的 <c>sprite/interface/eplpnew.img</c> 画出来,和别的精灵走同一条绘制管线。
    /// 上边一个 268×54 的头(标题图案)、中间四行 270×30 的底、下边一个 268×16 的脚;每一行盖一张按钮文字图(菜单文字在图里,PVF 里没有文本)。
    /// 面板位置与大小是拿基线结算帧用 <c>dnf locate</c> 逐像素找出来的(头、脚的不透明像素 100% 一致);行的位置由头脚之间的空隙推出;
    /// 文字图的横向位置与基线对照过(肉眼逐行对,自己渲染的结算画面见 <c>dnf dungeon-check --after-clear</c>),误差在 ±1 像素量级。
    /// 热键提示 "(F13)" 在原版里是系统字体渲染的,这里用内置的像素字(<see cref="OverlayArt"/>)摆在文字右边,位置与颜色是近似。
    /// 取不到图时(合成数据的测试)用 <see cref="BuildFallback"/> 画一块像素字的占位面板。
    /// </summary>
    public sealed class SettlementArt
    {
        public const string Image = "sprite/interface/eplpnew.img";

        // ----- eplpnew.img 的帧号
        public const int HeaderFrame = 0;
        public const int RowFrame = 1;
        public const int FooterFrame = 2;

        /// <summary>每个选项的按钮文字图有 4 张:常态、悬停、按下、禁用,首张的帧号如下。</summary>
        public const int RetryLabelFrame = 5;

        public const int SelectLabelFrame = 9;
        public const int TownLabelFrame = 13;
        public const int PartyLabelFrame = 17;

        public const int StateNormal = 0;
        public const int StateHover = 1;
        public const int StatePressed = 2;
        public const int StateDisabled = 3;

        // ----- 位置(屏幕 800×600)
        /// <summary>面板画布的原点(头与脚的帧自带 1 像素偏移,像素块在 513)。</summary>
        public const int PanelX = 512;

        public const int PanelY = 22;
        public const int PanelWidth = 270;
        public const int HeaderHeight = 54;
        public const int RowHeight = 30;
        public const int RowCount = 4;
        public const int FooterHeight = 16;

        /// <summary>第一行的顶(= 面板顶 + 头高)。</summary>
        public const int RowTop = PanelY + HeaderHeight;

        /// <summary>脚的顶(= 第一行的顶 + 四行)。</summary>
        public const int FooterY = RowTop + RowHeight * RowCount;

        /// <summary>按钮文字图的画布原点 x(各图自带的偏移再加在它上面)。</summary>
        public const int LabelX = 522;

        /// <summary>热键提示与按钮文字之间留的空隙(像素;近似)。</summary>
        public const int HintGap = 6;

        public const uint HintColor = 0xC8B890u;

        /// <summary>屏幕上从上到下的四行。</summary>
        public static readonly SettlementChoice[] RowChoices =
        {
            SettlementChoice.Retry,
            SettlementChoice.QuickParty,
            SettlementChoice.SelectDungeon,
            SettlementChoice.ReturnToTown,
        };

        private readonly ISpriteSource _sprites;
        private readonly SpriteFrame _header;
        private readonly SpriteFrame _row;
        private readonly SpriteFrame _footer;

        private SettlementArt(ISpriteSource sprites, SpriteFrame header, SpriteFrame row, SpriteFrame footer)
        {
            _sprites = sprites;
            _header = header;
            _row = row;
            _footer = footer;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, SettlementArt> Shared =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, SettlementArt>();

        /// <summary>同一份图包共用一个对象(同 <see cref="HudArt.For"/>);头、行、脚缺任何一个就返回 null。</summary>
        public static SettlementArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            if (Shared.TryGetValue(sprites, out SettlementArt art))
            {
                return art;
            }
            art = TryCreate(sprites);
            if (art != null)
            {
                Shared.Add(sprites, art);
            }
            return art;
        }

        /// <summary>从图包里取面板需要的图;头、行、脚缺任何一个返回 null。按钮文字图缺了只是不画。</summary>
        public static SettlementArt TryCreate(ISpriteSource sprites)
        {
            if (sprites == null
                || !sprites.TryGetFrame(Image, HeaderFrame, out SpriteFrame header)
                || !sprites.TryGetFrame(Image, RowFrame, out SpriteFrame row)
                || !sprites.TryGetFrame(Image, FooterFrame, out SpriteFrame footer))
            {
                return null;
            }
            return new SettlementArt(sprites, header, row, footer);
        }

        /// <summary>第 <paramref name="row"/> 行(0 到 3)是哪个选项;越界返回 <see cref="SettlementChoice.None"/>。</summary>
        public static SettlementChoice ChoiceAt(int row)
        {
            return row >= 0 && row < RowCount ? RowChoices[row] : SettlementChoice.None;
        }

        /// <summary>某个选项在第几行;不在菜单里返回 -1。</summary>
        public static int RowOf(SettlementChoice choice)
        {
            for (int i = 0; i < RowCount; i++)
            {
                if (RowChoices[i] == choice)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>选项能不能用(快速组队要组队系统,没有)。</summary>
        public static bool IsEnabled(SettlementChoice choice)
        {
            return choice == SettlementChoice.Retry || choice == SettlementChoice.SelectDungeon || choice == SettlementChoice.ReturnToTown;
        }

        /// <summary>选项的热键名(原版菜单上写的);没指定热键的返回 null。</summary>
        public static string HotkeyName(SettlementChoice choice)
        {
            switch (choice)
            {
                case SettlementChoice.Retry: return "F13";
                case SettlementChoice.SelectDungeon: return "F11";
                case SettlementChoice.ReturnToTown: return "F12";
                default: return null;
            }
        }

        /// <summary>屏幕坐标 (<paramref name="x"/>, <paramref name="y"/>) 落在哪一行上;不在四行的范围内返回 -1。</summary>
        public static int HitRow(int x, int y)
        {
            if (x < PanelX || x >= PanelX + PanelWidth || y < RowTop || y >= RowTop + RowHeight * RowCount)
            {
                return -1;
            }
            return (y - RowTop) / RowHeight;
        }

        /// <summary>屏幕坐标落在哪个选项上;不在菜单行里返回 <see cref="SettlementChoice.None"/>。</summary>
        public static SettlementChoice HitTest(int x, int y)
        {
            return ChoiceAt(HitRow(x, y));
        }

        /// <summary>某个选项的按钮文字图在 eplpnew.img 里的帧号(<paramref name="state"/> 是 <see cref="StateNormal"/> 等);不在菜单里返回 -1。</summary>
        public static int LabelFrameIndex(SettlementChoice choice, int state)
        {
            int baseFrame;
            switch (choice)
            {
                case SettlementChoice.Retry: baseFrame = RetryLabelFrame; break;
                case SettlementChoice.SelectDungeon: baseFrame = SelectLabelFrame; break;
                case SettlementChoice.ReturnToTown: baseFrame = TownLabelFrame; break;
                case SettlementChoice.QuickParty: baseFrame = PartyLabelFrame; break;
                default: return -1;
            }
            return baseFrame + state;
        }

        /// <summary>
        /// 把面板的精灵加进 <paramref name="into"/>(按头、四行、脚、文字、热键提示的顺序,<paramref name="order"/> 接着往下编号)。
        /// <paramref name="hoverRow"/> 是鼠标所在的行(-1 = 没有),那一行的文字用悬停图;禁用的行不会悬停。
        /// </summary>
        public void Build(List<SceneSprite> into, ref int order, OverlayArt font, int hoverRow = -1)
        {
            into.Add(OverlayArt.Place(_header, PanelX + _header.OffsetX, PanelY + _header.OffsetY, order++, "settle-header"));
            for (int i = 0; i < RowCount; i++)
            {
                into.Add(OverlayArt.Place(_row, PanelX + _row.OffsetX, RowTop + RowHeight * i + _row.OffsetY, order++, "settle-row"));
            }
            into.Add(OverlayArt.Place(_footer, PanelX + _footer.OffsetX, FooterY + _footer.OffsetY, order++, "settle-footer"));
            for (int i = 0; i < RowCount; i++)
            {
                SettlementChoice choice = RowChoices[i];
                int state = i == hoverRow && IsEnabled(choice) ? StateHover : StateNormal;
                int top = RowTop + RowHeight * i;
                int right = LabelX;
                if (_sprites.TryGetFrame(Image, LabelFrameIndex(choice, state), out SpriteFrame label) && label.Width > 0)
                {
                    into.Add(OverlayArt.Place(label, LabelX + label.OffsetX, top + label.OffsetY, order++, "settle-label"));
                    right = LabelX + label.OffsetX + label.Width;
                }
                AddHint(into, ref order, font, choice, right, top);
            }
        }

        /// <summary>
        /// 取不到面板图时的占位:半透明的深色底,四行像素字(热键写在后面)。位置、行高与真面板一致,点击测试通用。
        /// </summary>
        public static void BuildFallback(List<SceneSprite> into, ref int order, OverlayArt font, int hoverRow = -1)
        {
            int height = HeaderHeight + RowHeight * RowCount + FooterHeight;
            into.Add(OverlayArt.Place(font.Solid(PanelWidth, height, 0x101820u), PanelX, PanelY, order++, "settle-fallback", 210));
            SpriteFrame title = font.Text("CLEAR!", 3, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, PanelX + (PanelWidth - title.Width) / 2, PanelY + (HeaderHeight - title.Height) / 2, order++, "settle-title"));
            string[] names = { "RETRY", "PARTY", "SELECT", "TOWN" };
            for (int i = 0; i < RowCount; i++)
            {
                SettlementChoice choice = RowChoices[i];
                int top = RowTop + RowHeight * i;
                uint color = !IsEnabled(choice) ? 0x808080u : (i == hoverRow ? OverlayArt.Yellow : OverlayArt.White);
                SpriteFrame text = font.Text(names[i], 2, color);
                into.Add(OverlayArt.Place(text, LabelX, top + (RowHeight - text.Height) / 2, order++, "settle-label"));
                AddHint(into, ref order, font, choice, LabelX + text.Width, top);
            }
        }

        private static void AddHint(List<SceneSprite> into, ref int order, OverlayArt font, SettlementChoice choice, int labelRight, int rowTop)
        {
            string key = HotkeyName(choice);
            if (key == null || font == null)
            {
                return;
            }
            SpriteFrame hint = font.Text("(" + key + ")", 1, HintColor);
            into.Add(OverlayArt.Place(hint, labelRight + HintGap, rowTop + (RowHeight - hint.Height) / 2, order++, "settle-hint"));
        }
    }
}
