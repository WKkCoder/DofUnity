// 证据:E-109(右上角副本小地图:UI/DungeonMinimap/DungeonMinimap.ui 与 0x010383c0/0x010384e0/0x01038690/0x010382b0/0x004ba760/0x007d6e30/0x007d6a50/0x00ef6a50/0x00ef95a0/0x012fe559)。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 副本小地图上一格的数据(证据 E-109):出口位组合(<see cref="ExitBits"/>)、房间地图编号(<c>map.lst</c> 的编号,即客户端房间对象 <c>+0x248</c>)、
    /// 显示状态(<see cref="MiniMapState"/>)、是否首领格、以及是否与当前房间通过已打开的门相连(<c>0x007d6e30</c> + <c>0x007d6a50</c>)。
    /// </summary>
    public readonly struct DungeonMinimapCell
    {
        public int Exits { get; }
        public int MapId { get; }
        public MiniMapState State { get; }
        public bool IsBoss { get; }
        public bool IsOpenGateNeighbour { get; }

        public DungeonMinimapCell(int exits, int mapId, MiniMapState state, bool isBoss, bool isOpenGateNeighbour)
        {
            Exits = exits;
            MapId = mapId;
            State = state;
            IsBoss = isBoss;
            IsOpenGateNeighbour = isOpenGateNeighbour;
        }
    }

    /// <summary>
    /// 右上角副本小地图(证据 E-109,客户端 <c>0x010383c0</c> 构造、<c>0x010384e0</c> 初始化、<c>0x01038690</c> 绘制):
    ///   · 两套图包:<see cref="WindowImage"/>(<c>sprite/interface2/minimap/dungeon/minimap.img</c>,窗口 9 宫格边框与底、顶栏标题条、当前房间人物标、首领骷髅标)
    ///     与副本脚本 <c>[minimap image]</c> 指定的房间格图包(<c>0x012fe559</c>,缺省 <see cref="DefaultTileImage"/> <c>sprite/map/minimap/act1.img</c>,122 帧 18×18)。
    ///   · 窗口网格与右上角吸附位置(<c>0x010381d0</c>、<c>0x01038721</c>..<c>0x0103873f</c>、<c>0x0103886e</c>..<c>0x010388af</c>、<c>0x00ef95a0</c>、<c>0x00ef6a50</c>):
    ///     显示列数 <c>displayCols = max(cols, 3)</c>(不足 3 列时按 3 列宽,且迷宫第 <c>x</c> 列向右偏 1 列画在 <c>displayCol = x + 1</c>,第 0 列留空:<c>0x01038a6b</c>..<c>0x01038a82</c>);
    ///     显示行数 <c>displayRows = rows + 1</c>(第 0 行画顶栏标题条 <c>#39</c>,第 <c>1..rows</c> 行画迷宫各行)。
    ///     右上角吸附时内框左上角固定在 <c>windowX = 793 - displayCols * 18</c>、<c>windowY = 29</c>(3 列时内框左上角 <c>(739, 29)</c>),
    ///     外围一圈 8 像素的 9 宫格边框(<c>#27..#35</c>)包住内框(左上角画布原点 <c>(windowX - 8, 21)</c>),内框每格铺半透明底(<c>#31</c>),第 0 行再铺标题条(<c>#39</c>)。
    ///   · 房间格瓦片选择(<c>0x004ba760</c>、<c>0x01038bc2</c>..<c>0x01038c0d</c>):
    ///     出口位掩码 <c>exitMask ∈ [1, 15]</c>(<c>Right=1, Up=2, Left=4, Down=8</c>)每一种占 8 帧(前 4 帧常态、后 4 帧高亮):
    ///     <c>frame = (exitMask - 1) * 8 + (highlighted ? 4 : 0) + variant</c>,其中 <c>variant = ClientRandom.Next(ref seed) &amp; 3</c>、
    ///     <c>seed = (uint)(mapId * y * x)</c>(<c>x == 0</c> 或 <c>y == 0</c> 时 <c>seed = 0</c> 恒得 <c>variant = 0</c>)。
    ///     <c>exitMask == 0</c>(未知邻格或首领格底块)返回 <c>120 + (highlighted ? 1 : 0)</c>(<c>#120</c> 常态问号格、<c>#121</c> 高亮问号格)。
    ///   · 房间显示、闪烁与图标(<c>0x01038a15</c>..<c>0x010391c8</c>):
    ///     当当前房间已清空且不是首领房时开启闪烁(<c>canBlink = true</c>),按 250 毫秒半周期切换 <c>blinkOn = (~((uint)clockMs / 250) &amp; 1) != 0</c>。
    ///     - 当前房间(<c>State == Current</c>):画高亮通道格(<c>+4</c>)并在 <c>(cellX + 6, cellY + 4)</c> 叠 <c>minimap.img #3</c> 人物小图标(即使在首领房也不叠骷髅标:<c>0x010391a2</c>..<c>0x010391b6</c>)。
    ///     - 非当前房间的首领格(<c>IsBoss &amp;&amp; State != Current</c>):无论是否去过或相邻,始终调 <c>0x010382b0</c> 画 <c>#120</c> 底块并在 <c>(cellX + 1, cellY + 2)</c> 叠 <c>minimap.img #37</c> 首领骷髅标(不随清房闪烁)。
    ///     - 已去过的普通房间(<c>State == Visited || State == Cleared</c>,非首领):若与已清空的当前房间通过打开的门相邻则随 <c>blinkOn</c> 在常态与高亮(<c>+4</c>)之间闪烁,否则画常态。
    ///     - 未去过的普通房间(<c>State == Seen || State == Hidden</c>,非首领):仅当当前房间已清空(且非首领)并通过打开的门与该格相邻时,画问号格并随 <c>blinkOn</c> 在 <c>#120</c> 与 <c>#121</c> 之间闪烁(<c>0x01039040</c>..<c>0x010390d6</c>);当前房间未清空时不显示未去过的邻格。
    /// </summary>
    public sealed class DungeonMinimapArt
    {
        public const string WindowImage = "sprite/interface2/minimap/dungeon/minimap.img";
        public const string DefaultTileImage = "sprite/map/minimap/act1.img";

        public const int TileSize = 18;
        public const int BorderSize = 8;
        public const int MinDisplayCols = 3;
        public const int DockedRightEdge = 793;
        public const int DockedTop = 29;

        public const int BorderTopLeftFrame = 27;
        public const int BorderTopFrame = 28;
        public const int BorderTopRightFrame = 29;
        public const int BorderLeftFrame = 30;
        public const int BorderCenterFrame = 31;
        public const int BorderRightFrame = 32;
        public const int BorderBottomLeftFrame = 33;
        public const int BorderBottomFrame = 34;
        public const int BorderBottomRightFrame = 35;

        public const int HeaderRowFrame = 39;
        public const int PlayerIconFrame = 3;
        public const int PlayerIconOffsetX = 6;
        public const int PlayerIconOffsetY = 4;
        public const int BossIconFrame = 37;
        public const int BossIconOffsetX = 1;
        public const int BossIconOffsetY = 2;

        public const int UnknownTileFrame = 120;
        public const int UnknownHighlightFrame = 121;
        public const int FramesPerExitMask = 8;
        public const int HighlightFrameOffset = 4;
        public const int VariantsPerExitMask = 4;
        public const int BlinkPeriodMs = 250;

        private readonly ISpriteSource _sprites;
        private readonly string _tileImage;

        public string TileImage => _tileImage;

        private DungeonMinimapArt(ISpriteSource sprites, string tileImage)
        {
            _sprites = sprites;
            _tileImage = tileImage;
        }

        private static readonly ConditionalWeakTable<ISpriteSource, Dictionary<string, DungeonMinimapArt>> Shared =
            new ConditionalWeakTable<ISpriteSource, Dictionary<string, DungeonMinimapArt>>();

        /// <summary>按图包源与副本小地图瓦片图路径取缓存的实例;缺图(合成数据测试)返回 null。</summary>
        public static DungeonMinimapArt For(ISpriteSource sprites, string tileImage = null)
        {
            if (sprites == null)
            {
                return null;
            }
            string key = DungeonMaze.NormalizeMinimapImage(tileImage) ?? DefaultTileImage;
            Dictionary<string, DungeonMinimapArt> byTile = Shared.GetOrCreateValue(sprites);
            lock (byTile)
            {
                if (byTile.TryGetValue(key, out DungeonMinimapArt cached))
                {
                    return cached;
                }
                DungeonMinimapArt created = TryCreate(sprites, key);
                if (created != null)
                {
                    byTile[key] = created;
                }
                return created;
            }
        }

        /// <summary>尝试创建小地图绘制器:要求窗口底格(<c>minimap.img #31</c>)与房间瓦片首帧存在;指定的 <paramref name="tileImage"/> 不存在时退回 <see cref="DefaultTileImage"/>。</summary>
        public static DungeonMinimapArt TryCreate(ISpriteSource sprites, string tileImage = null)
        {
            if (sprites == null || !sprites.TryGetFrame(WindowImage, BorderCenterFrame, out _))
            {
                return null;
            }
            string normalized = DungeonMaze.NormalizeMinimapImage(tileImage) ?? DefaultTileImage;
            if (!sprites.TryGetFrame(normalized, 0, out _))
            {
                if (string.Equals(normalized, DefaultTileImage, StringComparison.Ordinal)
                    || !sprites.TryGetFrame(DefaultTileImage, 0, out _))
                {
                    return null;
                }
                normalized = DefaultTileImage;
            }
            return new DungeonMinimapArt(sprites, normalized);
        }

        /// <summary>窗口内框的显示列数:不足 3 列时按 3 列计(<c>0x0103887e</c>..<c>0x0103889b</c>)。</summary>
        public static int DisplayCols(int cols) => Math.Max(cols, MinDisplayCols);

        /// <summary>窗口内框的显示行数:第 0 行是标题条,下面是 <paramref name="rows"/> 行迷宫格(<c>0x010381d0</c>)。</summary>
        public static int DisplayRows(int rows) => Math.Max(rows, 1) + 1;

        /// <summary>右上角吸附时内框左上角 x(<c>793 - DisplayCols(cols) * 18</c>,<c>0x01038726</c>..<c>0x01038736</c>)。</summary>
        public static int WindowX(int cols) => DockedRightEdge - DisplayCols(cols) * TileSize;

        /// <summary>右上角吸附时内框左上角 y(<c>29</c>,<c>0x0103873a</c>)。</summary>
        public static int WindowY => DockedTop;

        /// <summary>迷宫第 <paramref name="x"/> 列在窗口内框里的显示列号:列数不足 3 时整体右移 1 列,第 0 列留空(<c>0x01038a6b</c>..<c>0x01038a82</c>)。</summary>
        public static int DisplayCol(int cols, int x) => cols < MinDisplayCols ? x + 1 : x;

        /// <summary>迷宫格 (<paramref name="x"/>, <paramref name="y"/>) 在屏幕上的左上角 x。</summary>
        public static int CellScreenX(int cols, int x) => WindowX(cols) + DisplayCol(cols, x) * TileSize;

        /// <summary>迷宫格 (<paramref name="x"/>, <paramref name="y"/>) 在屏幕上的左上角 y(第 0 行是顶栏,所以迷宫第 y 行在 <c>y + 1</c> 行)。</summary>
        public static int CellScreenY(int y) => DockedTop + (y + 1) * TileSize;

        /// <summary>250 毫秒半周期的闪烁相位(<c>0x01038a45</c>..<c>0x01038a60</c>:<c>(~(clockMs / 250) &amp; 1) != 0</c>,0..249 毫秒亮、250..499 毫秒灭)。</summary>
        public static bool IsBlinkOn(double clockMs)
        {
            uint ticks = clockMs <= 0 ? 0u : (uint)clockMs;
            return ((~(ticks / (uint)BlinkPeriodMs)) & 1u) != 0u;
        }

        /// <summary>
        /// 已去过房间的瓦片变体号(0..3,证据 E-109 <c>0x01038bc2</c>..<c>0x01038c07</c>):
        /// 种子 <c>seed = (uint)(mapId * y * x)</c>,推进一次 <see cref="ClientRandom.Next"/>(<c>0x00e50e60</c>)后取模 4。
        /// </summary>
        public static int PickVariant(int mapId, int x, int y)
        {
            uint seed = unchecked((uint)(mapId * y * x));
            return ClientRandom.Next(ref seed) & (VariantsPerExitMask - 1);
        }

        /// <summary>
        /// 房间瓦片在 <c>act*.img</c> 里的帧号(<c>0x004ba760</c>):
        /// <paramref name="exitMask"/> ∈ [1, 15] 时为 <c>(exitMask - 1) * 8 + (highlighted ? 4 : 0) + (variant &amp; 3)</c>;
        /// 其余(未知邻格或首领底块传 0)返回 <c>120 + (highlighted ? 1 : 0)</c>。
        /// </summary>
        public static int PickTileFrame(int exitMask, bool highlighted, int variant = 0)
        {
            if (exitMask <= 0 || exitMask > 15)
            {
                return highlighted ? UnknownHighlightFrame : UnknownTileFrame;
            }
            int v = variant & (VariantsPerExitMask - 1);
            return (exitMask - 1) * FramesPerExitMask + (highlighted ? HighlightFrameOffset : 0) + v;
        }

        /// <summary>把整个右上角小地图(9 宫格边框与底、顶栏、可见房间格、当前人物标、首领骷髅标)追加到 <paramref name="into"/>。</summary>
        public void Build(
            List<SceneSprite> into,
            ref int order,
            int cols,
            int rows,
            IReadOnlyList<DungeonMinimapCell> cells,
            bool currentRoomCleared,
            bool currentRoomIsBoss,
            double clockMs)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            if (cols < 1 || rows < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(cols));
            }
            if (cells == null || cells.Count != cols * rows)
            {
                throw new ArgumentException("单元格数量必须等于 cols * rows", nameof(cells));
            }

            int dCols = DisplayCols(cols);
            int dRows = DisplayRows(rows);
            int wx = WindowX(cols);
            int wy = DockedTop;

            // 1. 窗口 9 宫格边框与半透明底(#27..#35,0x00ef6a50)
            int topY = wy - BorderSize;
            PlaceWindow(into, BorderTopLeftFrame, wx - BorderSize, topY, ref order, "minimap-border");
            for (int c = 0; c < dCols; c++)
            {
                PlaceWindow(into, BorderTopFrame, wx + c * TileSize, topY, ref order, "minimap-border");
            }
            PlaceWindow(into, BorderTopRightFrame, wx + dCols * TileSize, topY, ref order, "minimap-border");

            bool firstCenter = true;
            for (int r = 0; r < dRows; r++)
            {
                int ry = wy + r * TileSize;
                PlaceWindow(into, BorderLeftFrame, wx - BorderSize, ry, ref order, "minimap-border");
                for (int c = 0; c < dCols; c++)
                {
                    PlaceWindow(into, BorderCenterFrame, wx + c * TileSize, ry, ref order, firstCenter ? "minimap" : "minimap-bg");
                    firstCenter = false;
                }
                PlaceWindow(into, BorderRightFrame, wx + dCols * TileSize, ry, ref order, "minimap-border");
            }

            int bottomY = wy + dRows * TileSize;
            PlaceWindow(into, BorderBottomLeftFrame, wx - BorderSize, bottomY, ref order, "minimap-border");
            for (int c = 0; c < dCols; c++)
            {
                PlaceWindow(into, BorderBottomFrame, wx + c * TileSize, bottomY, ref order, "minimap-border");
            }
            PlaceWindow(into, BorderBottomRightFrame, wx + dCols * TileSize, bottomY, ref order, "minimap-border");

            // 2. 第 0 行的顶栏标题条(#39,0x00ef7a26..0x00ef7aba)
            for (int c = 0; c < dCols; c++)
            {
                PlaceWindow(into, HeaderRowFrame, wx + c * TileSize, wy, ref order, "minimap-header");
            }

            // 3. 迷宫各格(0x01038a15..0x010391c8)
            bool canBlink = currentRoomCleared && !currentRoomIsBoss;
            bool blinkOn = canBlink && IsBlinkOn(clockMs);

            for (int y = 0; y < rows; y++)
            {
                int cy = CellScreenY(y);
                for (int x = 0; x < cols; x++)
                {
                    DungeonMinimapCell cell = cells[y * cols + x];
                    if (cell.Exits == 0)
                    {
                        continue;
                    }
                    int cx = CellScreenX(cols, x);

                    if (cell.State == MiniMapState.Current)
                    {
                        int variant = PickVariant(cell.MapId, x, y);
                        int frame = PickTileFrame(cell.Exits, true, variant);
                        PlaceTile(into, frame, cx, cy, ref order, "minimap-cell");
                        PlaceWindow(into, PlayerIconFrame, cx + PlayerIconOffsetX, cy + PlayerIconOffsetY, ref order, "minimap-player");
                        continue;
                    }

                    if (cell.IsBoss)
                    {
                        // 非当前房间的首领格始终画 #120 问号底块 + #37 首领骷髅标(0x010382b0)
                        PlaceTile(into, UnknownTileFrame, cx, cy, ref order, "minimap-cell");
                        PlaceWindow(into, BossIconFrame, cx + BossIconOffsetX, cy + BossIconOffsetY, ref order, "minimap-boss");
                        continue;
                    }

                    if (cell.State == MiniMapState.Visited || cell.State == MiniMapState.Cleared)
                    {
                        bool highlight = canBlink && blinkOn && cell.IsOpenGateNeighbour;
                        int variant = PickVariant(cell.MapId, x, y);
                        int frame = PickTileFrame(cell.Exits, highlight, variant);
                        PlaceTile(into, frame, cx, cy, ref order, "minimap-cell");
                    }
                    else if (canBlink && cell.IsOpenGateNeighbour)
                    {
                        // 未去过但与已清空的当前房间通过打开的门相连:画 #120/#121 闪烁问号格(0x01039040..0x010390d6)
                        int frame = PickTileFrame(0, blinkOn);
                        PlaceTile(into, frame, cx, cy, ref order, "minimap-cell");
                    }
                }
            }
        }

        private void PlaceWindow(List<SceneSprite> into, int frameIndex, int x, int y, ref int order, string tag)
        {
            if (_sprites.TryGetFrame(WindowImage, frameIndex, out SpriteFrame frame) && frame.Width > 0 && frame.Height > 0)
            {
                into.Add(OverlayArt.Place(frame, x + frame.OffsetX, y + frame.OffsetY, order++, tag));
            }
        }

        private void PlaceTile(List<SceneSprite> into, int frameIndex, int x, int y, ref int order, string tag)
        {
            if (_sprites.TryGetFrame(_tileImage, frameIndex, out SpriteFrame frame) && frame.Width > 0 && frame.Height > 0)
            {
                into.Add(OverlayArt.Place(frame, x + frame.OffsetX, y + frame.OffsetY, order++, tag));
            }
        }
    }
}
