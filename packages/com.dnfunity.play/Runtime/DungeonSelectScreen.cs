using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>副本选择界面上的一个副本。</summary>
    public sealed class DungeonEntry
    {
        /// <summary>副本定义在 PVF 里的路径,例如 dungeon/act1/lorien.dgn。</summary>
        public string Path { get; }

        /// <summary>占位字库(只有大写字母、数字与少数符号)画得出来的名字;真实界面用原版的名字图或字符串(没做)。</summary>
        public string Name { get; }

        public DungeonEntry(string path, string name)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Name = name ?? FromPath(path);
        }

        /// <summary>从路径取文件名(去掉扩展名),转成大写:dungeon/act1/lorien.dgn → LORIEN。</summary>
        public static string FromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            if (dot > 0)
            {
                file = file.Substring(0, dot);
            }
            return file.ToUpperInvariant();
        }
    }

    /// <summary>副本选择界面的结果。</summary>
    public enum SelectOutcome
    {
        /// <summary>还没选。</summary>
        None = 0,

        /// <summary>选定了一个副本、要进去。</summary>
        Enter = 1,

        /// <summary>返回城镇。</summary>
        Back = 2,
    }

    /// <summary>
    /// 副本选择界面(基线 S05:大地图上摆着副本卡片,底栏写着"↑↓ 选择地下城、←→ 选择难度、空格 进入地下城",右下角有"返回城镇")。
    /// 这一版只有**逻辑**与一张占位画面(深色底、像素字列表、选中行高亮、底栏提示):上下键换选中的副本(到头不循环),
    /// 确认进入、返回城镇;原版的大地图与副本卡片、难度、快速组队等还没做,界面的画法与位置另行测量。
    /// 画面是自带的像素字,不依赖任何原版素材,所以没有数据目录也能画。
    /// </summary>
    public sealed class DungeonSelectScreen : ISceneSource
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        private const uint BackgroundColor = 0x14100Cu;
        private const uint SelectionColor = 0x4A3A1Au;
        private const uint HintColor = OverlayArt.White;

        private readonly OverlayArt _art = new OverlayArt();
        private readonly List<DungeonEntry> _entries;

        public IReadOnlyList<DungeonEntry> Entries => _entries;

        /// <summary>选中的副本在 <see cref="Entries"/> 里的序号;没有副本时是 -1。</summary>
        public int Index { get; private set; }

        public SelectOutcome Outcome { get; private set; }

        /// <summary>选中的副本;没有副本时为 null。</summary>
        public DungeonEntry Selected => Index >= 0 && Index < _entries.Count ? _entries[Index] : null;

        public DungeonSelectScreen(IEnumerable<DungeonEntry> entries, int index = 0)
        {
            _entries = new List<DungeonEntry>(entries ?? throw new ArgumentNullException(nameof(entries)));
            Index = _entries.Count == 0 ? -1 : Math.Max(0, Math.Min(index, _entries.Count - 1));
        }

        /// <summary>选中项上移(<paramref name="delta"/> 为负)或下移(为正);夹在列表范围里,不循环。已经有结果之后不再响应。</summary>
        public void Move(int delta)
        {
            if (Outcome != SelectOutcome.None || _entries.Count == 0)
            {
                return;
            }
            Index = Math.Max(0, Math.Min(Index + delta, _entries.Count - 1));
        }

        /// <summary>进入选中的副本;没有副本、或已经有结果时不响应。</summary>
        public void Confirm()
        {
            if (Outcome == SelectOutcome.None && Selected != null)
            {
                Outcome = SelectOutcome.Enter;
            }
        }

        /// <summary>返回城镇;已经有结果时不响应。</summary>
        public void Back()
        {
            if (Outcome == SelectOutcome.None)
            {
                Outcome = SelectOutcome.Back;
            }
        }

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            sprites.Add(OverlayArt.Place(_art.Solid(ScreenWidth, ScreenHeight, BackgroundColor), 0, 0, order++, "select-backdrop"));
            SpriteFrame title = _art.Text("SELECT DUNGEON", 3, OverlayArt.Yellow);
            sprites.Add(OverlayArt.Place(title, 40, 30, order++, "select-title"));

            const int rowTop = 110;
            const int rowStep = 40;
            for (int i = 0; i < _entries.Count; i++)
            {
                int y = rowTop + rowStep * i;
                bool selected = i == Index;
                if (selected)
                {
                    sprites.Add(OverlayArt.Place(_art.Solid(400, rowStep - 6, SelectionColor), 36, y - 6, order++, "select-highlight"));
                }
                SpriteFrame name = _art.Text(_entries[i].Name, 3, selected ? OverlayArt.Yellow : OverlayArt.White);
                if (name != null)
                {
                    sprites.Add(OverlayArt.Place(name, 48, y, order++, "select-name"));
                }
            }
            if (_entries.Count == 0)
            {
                sprites.Add(OverlayArt.Place(_art.Text("NO DUNGEON", 3, OverlayArt.Orange), 48, rowTop, order++, "select-empty"));
            }

            SpriteFrame hint = _art.Text("UP DOWN SELECT   ENTER GO   ESC TOWN", 2, HintColor);
            sprites.Add(OverlayArt.Place(hint, 40, ScreenHeight - 40, order++, "select-hint"));
            return sprites;
        }
    }
}
