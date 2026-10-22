using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 系统菜单(<see cref="SystemMenu"/>)的画法:整屏压暗、居中的深色面板带一圈边、标题、每行一个条(选中的行换亮色)加占位英文。
    /// 本项目自己的占位版式(原版 Esc 菜单的界面没有读到),全部用 <see cref="OverlayArt"/> 的纯色块与像素字,不依赖图包。
    /// </summary>
    public static class SystemMenuArt
    {
        /// <summary>压暗的不透明度(0 到 255)。</summary>
        public const int DimAlpha = 150;

        public const uint FrameColor = 0x8A7A5Au;
        public const uint PanelColor = 0x16161Eu;
        public const uint RowColor = 0x2A2A38u;
        public const uint RowSelectedColor = 0x7A3A10u;

        public const int TitleScale = 4;
        public const int RowScale = 3;

        /// <summary>
        /// 把菜单的精灵追加到 <paramref name="into"/>,<paramref name="order"/> 逐个加一;菜单没开时什么也不加。
        /// 标签:<c>menu-dim</c>、<c>menu-frame</c>、<c>menu-panel</c>、<c>menu-title</c>、<c>menu-row</c>(每行的底条,选中的那条是 <c>menu-row-selected</c>)、<c>menu-label</c>。
        /// </summary>
        public static void Build(List<SceneSprite> into, ref int order, SystemMenu menu, OverlayArt art)
        {
            if (!menu.Open)
            {
                return;
            }
            SpriteFrame black = art.Solid(SystemMenu.ScreenWidth, SystemMenu.ScreenHeight, OverlayArt.Black);
            into.Add(OverlayArt.PlaceDim(black, DimAlpha, order++, "menu-dim"));
            int x = menu.PanelX, y = menu.PanelY, w = SystemMenu.PanelWidth, h = menu.PanelHeight;
            into.Add(OverlayArt.Place(art.Solid(w + 4, h + 4, FrameColor), x - 2, y - 2, order++, "menu-frame"));
            into.Add(OverlayArt.Place(art.Solid(w, h, PanelColor), x, y, order++, "menu-panel"));
            SpriteFrame title = art.Text("SYSTEM MENU", TitleScale, OverlayArt.Yellow);
            into.Add(OverlayArt.Place(title, x + (w - title.Width) / 2, y + (SystemMenu.TitleHeight - title.Height) / 2, order++, "menu-title"));
            for (int i = 0; i < menu.Items.Count; i++)
            {
                (int rx, int ry, int rw, int rh) = menu.RowRect(i);
                bool selected = i == menu.Selected;
                into.Add(OverlayArt.Place(art.Solid(rw, rh, selected ? RowSelectedColor : RowColor), rx, ry, order++, selected ? "menu-row-selected" : "menu-row"));
                SpriteFrame label = art.Text(SystemMenu.LabelOf(menu.Items[i]), RowScale, selected ? OverlayArt.Yellow : OverlayArt.White);
                into.Add(OverlayArt.Place(label, rx + (rw - label.Width) / 2, ry + (rh - label.Height) / 2, order++, "menu-label"));
            }
        }

        /// <summary>同上,返回新建的列表(菜单没开时是空表)。</summary>
        public static List<SceneSprite> Build(SystemMenu menu, OverlayArt art)
        {
            var list = new List<SceneSprite>();
            int order = 0;
            Build(list, ref order, menu, art);
            return list;
        }
    }
}
