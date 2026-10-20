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

        /// <summary>占位字库(只有大写字母、数字与少数符号)画得出来的名字;原版的副本名画在卡片图里(见 <see cref="Card"/>),名字字符串是中文系统字体。</summary>
        public string Name { get; }

        /// <summary>世界地图上的入口与卡片数据;没有(占位界面、合成数据)时为 null。</summary>
        public DungeonCard Card { get; }

        public DungeonEntry(string path, string name, DungeonCard card = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Name = name ?? FromPath(path);
            Card = card;
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
    /// 副本选择界面(基线 S05:世界地图上摆着副本卡片,底栏写着"↑↓ 选择地下城、←→ 选择难度、空格 进入地下城",右下角有"返回城镇")。
    /// 有 <see cref="WorldMapScene"/> 时画原版的世界地图界面(<see cref="WorldMapArt"/>,证据 E-094):底图、入口标记、卡片、横幅与按钮都来自用户自备的图包,
    /// 开场有横幅与卡片淡入(<see cref="AgeMs"/> 从界面打开算起,由宿主用 <see cref="Advance"/> 推进);没有时是一张占位画面(深色底、像素字列表、选中行高亮、底栏提示)。
    /// 逻辑两种模式一样:上下键换选中的副本(到头不循环),确认进入、返回城镇。地图模式下上下键按入口在地图上的位置(从上到下)走,
    /// 左右键(选难度)没有做;鼠标点击卡片选中(再点一下进入),点"返回城镇"按钮返回。快速组队要组队系统,点了没反应。
    /// 占位画面是自带的像素字,不依赖任何原版素材,所以没有数据目录也能画。
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
        private readonly WorldMapScene _world;
        private readonly WorldMapArt _worldArt;
        private readonly int[] _navigation;
        private double _ageMs;

        public IReadOnlyList<DungeonEntry> Entries => _entries;

        /// <summary>选中的副本在 <see cref="Entries"/> 里的序号;没有副本时是 -1。</summary>
        public int Index { get; private set; }

        public SelectOutcome Outcome { get; private set; }

        /// <summary>选中的副本;没有副本时为 null。</summary>
        public DungeonEntry Selected => Index >= 0 && Index < _entries.Count ? _entries[Index] : null;

        /// <summary>画原版世界地图界面时的画面数据;占位画面是 null。</summary>
        public WorldMapScene World => _world;

        /// <summary>界面打开以来的毫秒数(<see cref="Advance"/> 累加),开场动画与横幅轮换按它走。</summary>
        public long AgeMs => (long)_ageMs;

        public DungeonSelectScreen(IEnumerable<DungeonEntry> entries, int index = 0, WorldMapScene world = null)
        {
            _entries = new List<DungeonEntry>(entries ?? throw new ArgumentNullException(nameof(entries)));
            Index = _entries.Count == 0 ? -1 : Math.Max(0, Math.Min(index, _entries.Count - 1));
            _world = world;
            _worldArt = world == null ? null : WorldMapArt.For(world.Sprites);
            _navigation = BuildNavigation(_entries, world != null);
        }

        /// <summary>
        /// 上下键的走法:地图模式下,有卡片的条目按入口的位置从上到下(同一高度从左到右)排,没有卡片的条目接在后面;
        /// 占位模式就是列表顺序。(原版的走法没读到客户端代码,这是按地图上的位置推的。)
        /// </summary>
        private static int[] BuildNavigation(List<DungeonEntry> entries, bool world)
        {
            var order = new List<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                order.Add(i);
            }
            if (world)
            {
                // 稳定的插入排序:有卡片的在前,按 (y, x);没有卡片的保持原来的先后
                for (int i = 1; i < order.Count; i++)
                {
                    int item = order[i];
                    int j = i - 1;
                    while (j >= 0 && Before(entries[item], entries[order[j]]))
                    {
                        order[j + 1] = order[j];
                        j--;
                    }
                    order[j + 1] = item;
                }
            }
            return order.ToArray();
        }

        private static bool Before(DungeonEntry a, DungeonEntry b)
        {
            if (a.Card == null)
            {
                return false;
            }
            if (b.Card == null)
            {
                return true;
            }
            if (a.Card.MarkerY != b.Card.MarkerY)
            {
                return a.Card.MarkerY < b.Card.MarkerY;
            }
            return a.Card.MarkerX < b.Card.MarkerX;
        }

        /// <summary>界面的时间往前走 <paramref name="deltaMs"/> 毫秒(负数当 0)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                _ageMs += deltaMs;
            }
        }

        /// <summary>选中项上移(<paramref name="delta"/> 为负)或下移(为正);夹在列表范围里,不循环。已经有结果之后不再响应。</summary>
        public void Move(int delta)
        {
            if (Outcome != SelectOutcome.None || _entries.Count == 0)
            {
                return;
            }
            int position = Array.IndexOf(_navigation, Index);
            position = Math.Max(0, Math.Min(position + delta, _navigation.Length - 1));
            Index = _navigation[position];
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

        /// <summary>
        /// 鼠标(触摸)点在画布坐标 (<paramref name="x"/>, <paramref name="y"/>) 上:点"返回城镇"按钮返回;点没选中的卡片选中它,
        /// 点选中的卡片进入;点快速组队与深渊派对按钮没有反应(单机没有组队)。返回这一下有没有落在界面元素上。占位画面不响应点击。
        /// </summary>
        public bool Click(int x, int y)
        {
            if (_world == null || Outcome != SelectOutcome.None)
            {
                return false;
            }
            WorldMapButton button = WorldMapArt.HitButton(x, y);
            if (button == WorldMapButton.Town)
            {
                Back();
                return true;
            }
            if (button != WorldMapButton.None)
            {
                return true;
            }
            int hit = WorldMapArt.HitCard(_entries, Index, x, y);
            if (hit < 0)
            {
                return false;
            }
            if (hit == Index)
            {
                Confirm();
            }
            else
            {
                Index = hit;
            }
            return true;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            if (_worldArt != null)
            {
                _worldArt.Build(sprites, ref order, _art, _world, _entries, Index, AgeMs);
                return sprites;
            }
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
