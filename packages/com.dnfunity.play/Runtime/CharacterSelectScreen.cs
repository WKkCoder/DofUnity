using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选角界面的结果。</summary>
    public enum CharacterSelectOutcome
    {
        /// <summary>还没选。</summary>
        None = 0,

        /// <summary>用选中的角色开始游戏(进城)。</summary>
        Start = 1,

        /// <summary>结束游戏。</summary>
        Quit = 2,
    }

    /// <summary>
    /// 选角界面(基线 S02:一排排底座上站着账号里的角色,选中的亮着发光框,底下"游戏开始"按钮,两侧有创立、删除、选择频道、结束游戏的小按钮)。
    /// 有 <see cref="CharacterSelectScene"/> 时画原版的界面(<see cref="CharacterSelectArt"/>,证据 E-096),开场之后光束与头像的动画按
    /// <see cref="AgeMs"/> 走(由宿主用 <see cref="Advance"/> 推进);没有时是一张占位画面(深色底、像素字名单)。
    /// 逻辑两种模式一样:方向键在有角色的格子之间移动选中项(到头不循环),确认(回车、空格、点"游戏开始")用选中的角色开始游戏,
    /// 返回(Esc、点"结束游戏")是结束游戏;鼠标点角色的格子选中它;创立、删除、选择频道三个按钮单机里没有对应的功能,点了没有反应。
    /// 原版键盘怎么操作选角没有读到客户端代码,这里的键位是按界面形态推的。
    /// </summary>
    public sealed class CharacterSelectScreen : ISceneSource
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        private const uint BackgroundColor = 0x0A1220u;
        private const uint SelectionColor = 0x23406Au;

        private readonly OverlayArt _art = new OverlayArt();
        private readonly CharacterRoster _roster;
        private readonly CharacterSelectScene _scene;
        private readonly CharacterSelectArt _sceneArt;
        private double _ageMs;

        public CharacterRoster Roster => _roster;

        /// <summary>选中的角色在名册里的序号;名册是空的时候是 -1。</summary>
        public int Index { get; private set; }

        public CharacterSelectOutcome Outcome { get; private set; }

        /// <summary>选中的角色;名册是空的时候为 null。</summary>
        public CharacterSlot Selected => Index >= 0 && Index < _roster.Count ? _roster[Index] : null;

        /// <summary>画原版界面时的画面数据;占位画面是 null。</summary>
        public CharacterSelectScene Scene => _scene;

        /// <summary>界面打开以来的毫秒数(<see cref="Advance"/> 累加),光束与头像的动画按它走。</summary>
        public long AgeMs => (long)_ageMs;

        public CharacterSelectScreen(CharacterRoster roster, CharacterSelectScene scene = null, int index = 0)
        {
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _scene = scene;
            _sceneArt = scene == null ? null : CharacterSelectArt.For(scene.Sprites);
            Index = _roster.Count == 0 ? -1 : Math.Max(0, Math.Min(index, _roster.Count - 1));
        }

        /// <summary>界面的时间往前走 <paramref name="deltaMs"/> 毫秒(负数当 0)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                _ageMs += deltaMs;
            }
        }

        /// <summary>
        /// 选中项在网格里移动:<paramref name="dx"/> 列、<paramref name="dy"/> 行(各夹在 6 列 × 2 行的网格里,不循环);
        /// 目标格子没有角色时停在名册里最后一个角色(竖着移时同列没有角色就不动)。已经有结果之后不再响应。
        /// </summary>
        public void Move(int dx, int dy = 0)
        {
            if (Outcome != CharacterSelectOutcome.None || _roster.Count == 0)
            {
                return;
            }
            CharacterRoster.Cell(Index, out int column, out int row);
            int newColumn = Math.Max(0, Math.Min(column + dx, CharacterRoster.Columns - 1));
            int newRow = Math.Max(0, Math.Min(row + dy, CharacterRoster.Rows - 1));
            int target = newRow * CharacterRoster.Columns + newColumn;
            if (target >= _roster.Count)
            {
                if (dy != 0)
                {
                    return;                  // 竖着移到了没有角色的格子:不动
                }
                target = _roster.Count - 1;  // 横着移出了名册尾:停在最后一个角色
            }
            Index = target;
        }

        /// <summary>用选中的角色开始游戏;名册是空的、或已经有结果时不响应。</summary>
        public void Confirm()
        {
            if (Outcome == CharacterSelectOutcome.None && Selected != null)
            {
                Outcome = CharacterSelectOutcome.Start;
            }
        }

        /// <summary>结束游戏;已经有结果时不响应。</summary>
        public void Back()
        {
            if (Outcome == CharacterSelectOutcome.None)
            {
                Outcome = CharacterSelectOutcome.Quit;
            }
        }

        /// <summary>
        /// 鼠标(触摸)点在画布坐标 (<paramref name="x"/>, <paramref name="y"/>) 上:点"游戏开始"开始游戏、点"结束游戏"结束;点有角色的格子选中它;
        /// 点没有角色的格子、创立、删除、选择频道没有反应(单机里没有创角、删角与频道)。返回这一下有没有落在界面元素上。占位画面不响应点击。
        /// </summary>
        public bool Click(int x, int y)
        {
            if (_scene == null || Outcome != CharacterSelectOutcome.None)
            {
                return false;
            }
            switch (CharacterSelectArt.HitButton(x, y))
            {
                case CharacterSelectButton.Start:
                    Confirm();
                    return true;
                case CharacterSelectButton.Quit:
                    Back();
                    return true;
                case CharacterSelectButton.None:
                    break;
                default:
                    return true;
            }
            int slot = CharacterSelectArt.HitSlot(x, y);
            if (slot < 0)
            {
                return false;
            }
            if (slot < _roster.Count)
            {
                Index = slot;
            }
            return true;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            if (_sceneArt != null)
            {
                _sceneArt.Build(sprites, ref order, _art, _scene, _roster, Index, AgeMs);
                return sprites;
            }
            sprites.Add(OverlayArt.Place(_art.Solid(ScreenWidth, ScreenHeight, BackgroundColor), 0, 0, order++, "select-char-backdrop"));
            sprites.Add(OverlayArt.Place(_art.Text("SELECT CHARACTER", 3, OverlayArt.Yellow), 40, 30, order++, "select-char-title"));

            const int rowTop = 110;
            const int rowStep = 40;
            for (int i = 0; i < _roster.Count; i++)
            {
                int y = rowTop + rowStep * i;
                bool selected = i == Index;
                if (selected)
                {
                    sprites.Add(OverlayArt.Place(_art.Solid(400, rowStep - 6, SelectionColor), 36, y - 6, order++, "select-char-highlight"));
                }
                string line = _roster[i].Caption + "  " + _roster[i].JobName.ToUpperInvariant();
                sprites.Add(OverlayArt.Place(_art.Text(line, 3, selected ? OverlayArt.Yellow : OverlayArt.White), 48, y, order++, "select-char-name"));
            }
            if (_roster.Count == 0)
            {
                sprites.Add(OverlayArt.Place(_art.Text("NO CHARACTER", 3, OverlayArt.Orange), 48, rowTop, order++, "select-char-empty"));
            }
            sprites.Add(OverlayArt.Place(_art.Text("ARROWS SELECT   ENTER START   ESC QUIT", 2, OverlayArt.White), 40, ScreenHeight - 40, order++, "select-char-hint"));
            return sprites;
        }
    }
}
