using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 合成的小地图图源:窗口图包(<see cref="DungeonMinimapArt.WindowImage"/>)含 #3(人物 6×12)、#27..#35(9 宫格边框与底)、#37(首领骷髅 16×14)、#39(顶栏 18×18);
/// 房间瓦片图包(默认 <see cref="DungeonMinimapArt.DefaultTileImage"/> 与可选的第二套图包)含 #0..#121(18×18)。
/// 每帧的首字节 R 存帧号、G 存图包标识(1=窗口, 2=默认瓦片, 3=自定义瓦片),用于断言画出了哪一帧。不含任何原版像素。
/// </summary>
internal sealed class MinimapSprites : ISpriteSource
{
    public const string CustomTileImage = "sprite/map/minimap/act2.img";

    private readonly Dictionary<(string Image, int Frame), SpriteFrame> _frames = new();

    public MinimapSprites(bool includeWindow = true, bool includeDefaultTiles = true, bool includeCustomTiles = false)
    {
        if (includeWindow)
        {
            // #27 左上角带 (3, 3) 偏移,其余边框按原版尺寸与偏移造合成块
            Add(DungeonMinimapArt.WindowImage, 27, 5, 5, 3, 3, 8, 8, 1);
            Add(DungeonMinimapArt.WindowImage, 28, 18, 5, 0, 3, 18, 8, 1);
            Add(DungeonMinimapArt.WindowImage, 29, 4, 5, 0, 3, 8, 8, 1);
            Add(DungeonMinimapArt.WindowImage, 30, 5, 18, 3, 0, 8, 18, 1);
            Add(DungeonMinimapArt.WindowImage, 31, 18, 18, 0, 0, 18, 18, 1);
            Add(DungeonMinimapArt.WindowImage, 32, 4, 18, 0, 0, 8, 18, 1);
            Add(DungeonMinimapArt.WindowImage, 33, 5, 4, 3, 0, 8, 8, 1);
            Add(DungeonMinimapArt.WindowImage, 34, 18, 4, 0, 0, 18, 8, 1);
            Add(DungeonMinimapArt.WindowImage, 35, 4, 4, 0, 0, 8, 8, 1);
            Add(DungeonMinimapArt.WindowImage, 39, 18, 18, 0, 0, 18, 18, 1);
            Add(DungeonMinimapArt.WindowImage, 3, 6, 12, 0, 0, 6, 12, 1);
            Add(DungeonMinimapArt.WindowImage, 37, 16, 14, 0, 0, 16, 14, 1);
        }
        if (includeDefaultTiles)
        {
            for (int i = 0; i <= 121; i++)
            {
                Add(DungeonMinimapArt.DefaultTileImage, i, 18, 18, 0, 0, 18, 18, 2);
            }
        }
        if (includeCustomTiles)
        {
            for (int i = 0; i <= 121; i++)
            {
                Add(CustomTileImage, i, 18, 18, 0, 0, 18, 18, 3);
            }
        }
    }

    private void Add(string image, int frame, int w, int h, int ox, int oy, int cw, int ch, byte sheetId)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)frame;
            rgba[i * 4 + 1] = sheetId;
            rgba[i * 4 + 2] = 0x55;
            rgba[i * 4 + 3] = 255;
        }
        _frames[(image.ToLowerInvariant(), frame)] = new SpriteFrame(w, h, ox, oy, cw, ch, rgba);
    }

    public static int FrameOf(SceneSprite s) => s.Frame.Rgba[0];
    public static int SheetOf(SceneSprite s) => s.Frame.Rgba[1];

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
    {
        sprite = null;
        return imageName != null && _frames.TryGetValue((imageName.ToLowerInvariant(), frame), out sprite);
    }
}

/// <summary>右上角副本小地图(<see cref="DungeonMinimapArt"/>,证据 E-109)的合成数据单元测试。</summary>
public class DungeonMinimapTests
{
    [Fact]
    public void WindowGridClampsDisplayColsToAtLeastThreeAndShiftsNarrowMazesRightByOneColumn()
    {
        // 1 列与 2 列的迷宫都按 3 列宽显示,内框左上角 (739, 29),迷宫列从第 1 显示列(x=757)起画,第 0 显示列(x=739)留空
        Assert.Equal((3, 739, 29), (DungeonMinimapArt.DisplayCols(1), DungeonMinimapArt.WindowX(1), DungeonMinimapArt.WindowY));
        Assert.Equal((3, 739, 29), (DungeonMinimapArt.DisplayCols(2), DungeonMinimapArt.WindowX(2), DungeonMinimapArt.WindowY));
        Assert.Equal((1, 757), (DungeonMinimapArt.DisplayCol(2, 0), DungeonMinimapArt.CellScreenX(2, 0)));
        Assert.Equal((2, 775), (DungeonMinimapArt.DisplayCol(2, 1), DungeonMinimapArt.CellScreenX(2, 1)));

        // 3 列与 4 列直接从第 0 显示列起画;4 列内框左上角 = 793 - 4*18 = 721
        Assert.Equal((3, 739, 0, 739), (DungeonMinimapArt.DisplayCols(3), DungeonMinimapArt.WindowX(3), DungeonMinimapArt.DisplayCol(3, 0), DungeonMinimapArt.CellScreenX(3, 0)));
        Assert.Equal((4, 721, 0, 721), (DungeonMinimapArt.DisplayCols(4), DungeonMinimapArt.WindowX(4), DungeonMinimapArt.DisplayCol(4, 0), DungeonMinimapArt.CellScreenX(4, 0)));

        // 第 0 行是 18 像素高的标题条(y=29),迷宫第 0..2 行在 y = 47, 65, 83
        Assert.Equal(4, DungeonMinimapArt.DisplayRows(3));
        Assert.Equal((47, 65, 83), (DungeonMinimapArt.CellScreenY(0), DungeonMinimapArt.CellScreenY(1), DungeonMinimapArt.CellScreenY(2)));
    }

    [Fact]
    public void PickTileFrameMapsExitMasksToEightFrameGroupsAndUnknownCellsTo120And121()
    {
        // exitMask = 9 (Right=1 | Down=8):常态 #64..#67,高亮 #68..#71
        Assert.Equal(64, DungeonMinimapArt.PickTileFrame(9, false, 0));
        Assert.Equal(67, DungeonMinimapArt.PickTileFrame(9, false, 3));
        Assert.Equal(68, DungeonMinimapArt.PickTileFrame(9, true, 0));
        Assert.Equal(71, DungeonMinimapArt.PickTileFrame(9, true, 3));

        // exitMask = 4 (Left=4):常态 #24..#27,高亮 #28..#31
        Assert.Equal(24, DungeonMinimapArt.PickTileFrame(4, false, 0));
        Assert.Equal(28, DungeonMinimapArt.PickTileFrame(4, true, 0));

        // exitMask = 12 (Left=4 | Down=8):variant=3 时常态 #91、高亮 #95
        Assert.Equal(91, DungeonMinimapArt.PickTileFrame(12, false, 3));
        Assert.Equal(95, DungeonMinimapArt.PickTileFrame(12, true, 3));

        // exitMask <= 0 或 > 15:未知格 #120(常态)/ #121(高亮)
        Assert.Equal(120, DungeonMinimapArt.PickTileFrame(0, false));
        Assert.Equal(121, DungeonMinimapArt.PickTileFrame(0, true));
        Assert.Equal(120, DungeonMinimapArt.PickTileFrame(16, false));
        Assert.Equal(121, DungeonMinimapArt.PickTileFrame(-1, true));
    }

    [Fact]
    public void PickVariantUsesClientRandomOnMapIdTimesYTimesXAndBlinkTogglesEvery250Ms()
    {
        // x=0 或 y=0 时 seed=0,ClientRandom.Next(0) & 3 == 0
        Assert.Equal(0, DungeonMinimapArt.PickVariant(999, 0, 0));
        Assert.Equal(0, DungeonMinimapArt.PickVariant(999, 1, 0));
        Assert.Equal(0, DungeonMinimapArt.PickVariant(999, 0, 2));

        // 非零坐标与 ClientRandom.Next(ref seed) & 3 完全一致
        uint seed = unchecked((uint)(17 * 2 * 1));
        int expected = ClientRandom.Next(ref seed) & 3;
        Assert.Equal(expected, DungeonMinimapArt.PickVariant(17, 1, 2));

        // 250ms 半周期闪烁:[0, 249] 亮、[250, 499] 灭、[500, 749] 亮、[750, 999] 灭
        Assert.True(DungeonMinimapArt.IsBlinkOn(0));
        Assert.True(DungeonMinimapArt.IsBlinkOn(249));
        Assert.False(DungeonMinimapArt.IsBlinkOn(250));
        Assert.False(DungeonMinimapArt.IsBlinkOn(499));
        Assert.True(DungeonMinimapArt.IsBlinkOn(500));
        Assert.False(DungeonMinimapArt.IsBlinkOn(750));
    }

    [Fact]
    public void TryCreateRequiresWindowAndTileSheetsAndFallsBackToDefaultTileImageWhenCustomIsMissing()
    {
        Assert.Null(DungeonMinimapArt.For(null));
        Assert.Null(DungeonMinimapArt.TryCreate(new MinimapSprites(includeWindow: false, includeDefaultTiles: true)));
        Assert.Null(DungeonMinimapArt.TryCreate(new MinimapSprites(includeWindow: true, includeDefaultTiles: false)));

        // 指定的 act2.img 不在图源里时退回 act1.img
        var defaultOnly = new MinimapSprites(includeWindow: true, includeDefaultTiles: true, includeCustomTiles: false);
        DungeonMinimapArt fallback = DungeonMinimapArt.For(defaultOnly, @"Map\MiniMap\Act2.img")!;
        Assert.NotNull(fallback);
        Assert.Equal(DungeonMinimapArt.DefaultTileImage, fallback.TileImage);
        Assert.Same(fallback, DungeonMinimapArt.For(defaultOnly, "sprite/map/minimap/act2.img"));

        // 指定的 act2.img 存在时直接采用
        var withCustom = new MinimapSprites(includeWindow: true, includeDefaultTiles: true, includeCustomTiles: true);
        DungeonMinimapArt custom = DungeonMinimapArt.For(withCustom, @"Map\MiniMap\Act2.img")!;
        Assert.Equal(MinimapSprites.CustomTileImage, custom.TileImage);
    }

    [Fact]
    public void BuildPlacesNineSliceBorderHeaderCurrentRoomBossSkullAndBlinkingOpenGateNeighbours()
    {
        var sprites = new MinimapSprites();
        DungeonMinimapArt art = DungeonMinimapArt.For(sprites)!;

        // 2 列 × 3 行迷宫:
        // (0,0) 当前格(出口 9 = 右+下)  (1,0) 未去过的右邻格(出口 4 = 左,与当前格开门相连)
        // (0,1) 已清空的下邻格(出口 11,与当前格开门相连)  (1,1) 未去过且不与当前格相连(出口 12)
        // (0,2) 首领格(出口 3,与当前格不直接相连)  (1,2) 已进过但不与当前格相连(出口 6)
        DungeonMinimapCell[] BuildCells(bool openNeighbours, MiniMapState bossState = MiniMapState.Hidden, MiniMapState startState = MiniMapState.Current)
        {
            return new[]
            {
                new DungeonMinimapCell(9, 10, startState, false, false),
                new DungeonMinimapCell(4, 11, MiniMapState.Seen, false, openNeighbours),
                new DungeonMinimapCell(11, 12, MiniMapState.Cleared, false, openNeighbours),
                new DungeonMinimapCell(12, 13, MiniMapState.Hidden, false, false),
                new DungeonMinimapCell(3, 14, bossState, true, openNeighbours),
                new DungeonMinimapCell(6, 15, MiniMapState.Visited, false, false),
            };
        }

        // 1. 当前房间未清空(currentRoomCleared = false):
        //    - 当前格 (0,0) 画在 (757, 47):高亮瓦片 #68 + 人物标 #3 @(763, 51)
        //    - 未去过的右邻格 (1,0) 不画!
        //    - 已清空的下邻格 (0,1) 画在 (757, 65):常态瓦片 #80(不闪烁)
        //    - 首领格 (0,2) 画在 (757, 83):#120 底块 + #37 首领骷髅 @(758, 85)
        var list = new List<SceneSprite>();
        int order = 0;
        art.Build(list, ref order, 2, 3, BuildCells(false), currentRoomCleared: false, currentRoomIsBoss: false, clockMs: 0);

        // 左上角边框 #27:画布原点 (739-8, 29-8) = (731, 21),加帧偏移 (3, 3) 得 (734, 24)
        SceneSprite tl = list.First(s => s.AnimationPath == "minimap-border" && MinimapSprites.FrameOf(s) == 27);
        Assert.Equal((734, 24), (tl.ScreenX, tl.ScreenY));
        // 首格中心底块 #31 标为 "minimap" @(739, 29),共 3 列 × 4 行 = 12 块底
        Assert.Single(list, s => s.AnimationPath == "minimap" && MinimapSprites.FrameOf(s) == 31 && s.ScreenX == 739 && s.ScreenY == 29);
        Assert.Equal(11, list.Count(s => s.AnimationPath == "minimap-bg"));
        // 顶栏 3 块 #39 @(739, 29), (757, 29), (775, 29)
        Assert.Equal(new[] { (739, 29), (757, 29), (775, 29) },
            list.Where(s => s.AnimationPath == "minimap-header").Select(s => (s.ScreenX, s.ScreenY)).ToArray());

        List<SceneSprite> cellSprites = list.Where(s => s.AnimationPath == "minimap-cell").ToList();
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 47 && MinimapSprites.FrameOf(s) == 68);
        Assert.DoesNotContain(cellSprites, s => s.ScreenX == 775 && s.ScreenY == 47); // 未清房时不显示未去过的 (1,0)
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 65 && MinimapSprites.FrameOf(s) == 80);
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 83 && MinimapSprites.FrameOf(s) == 120);

        SceneSprite player = Assert.Single(list, s => s.AnimationPath == "minimap-player");
        Assert.Equal((757 + 6, 47 + 4, 3), (player.ScreenX, player.ScreenY, MinimapSprites.FrameOf(player)));
        SceneSprite boss = Assert.Single(list, s => s.AnimationPath == "minimap-boss");
        Assert.Equal((757 + 1, 83 + 2, 37), (boss.ScreenX, boss.ScreenY, MinimapSprites.FrameOf(boss)));

        // 2. 当前房间已清空(currentRoomCleared = true):
        //    - t = 100ms (blinkOn = true):未去过的右邻格 (1,0) 画 #121 高亮问号格,已清空的下邻格 (0,1) 画 #84 高亮通道格;首领格仍固定 #120 + #37
        list.Clear();
        order = 0;
        art.Build(list, ref order, 2, 3, BuildCells(true), currentRoomCleared: true, currentRoomIsBoss: false, clockMs: 100);
        cellSprites = list.Where(s => s.AnimationPath == "minimap-cell").ToList();
        Assert.Contains(cellSprites, s => s.ScreenX == 775 && s.ScreenY == 47 && MinimapSprites.FrameOf(s) == 121);
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 65 && MinimapSprites.FrameOf(s) == 84);
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 83 && MinimapSprites.FrameOf(s) == 120);

        //    - t = 300ms (blinkOn = false):未去过的右邻格 (1,0) 画 #120 常态问号格,已清空的下邻格 (0,1) 画 #80 常态通道格
        list.Clear();
        order = 0;
        art.Build(list, ref order, 2, 3, BuildCells(true), currentRoomCleared: true, currentRoomIsBoss: false, clockMs: 300);
        cellSprites = list.Where(s => s.AnimationPath == "minimap-cell").ToList();
        Assert.Contains(cellSprites, s => s.ScreenX == 775 && s.ScreenY == 47 && MinimapSprites.FrameOf(s) == 120);
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 65 && MinimapSprites.FrameOf(s) == 80);

        // 3. 主角进入首领格(bossState = Current, currentRoomIsBoss = true):
        //    - 首领格 (0,2) 画高亮通道格 #20 (exit=3 -> (3-1)*8 + 4 = 20) + 人物标 #3,不再画 #37 首领骷髅标;邻格也不再闪烁
        list.Clear();
        order = 0;
        art.Build(list, ref order, 2, 3, BuildCells(true, bossState: MiniMapState.Current, startState: MiniMapState.Cleared),
            currentRoomCleared: true, currentRoomIsBoss: true, clockMs: 100);
        cellSprites = list.Where(s => s.AnimationPath == "minimap-cell").ToList();
        Assert.Contains(cellSprites, s => s.ScreenX == 757 && s.ScreenY == 83 && MinimapSprites.FrameOf(s) == 20);
        Assert.DoesNotContain(list, s => s.AnimationPath == "minimap-boss");
        player = Assert.Single(list, s => s.AnimationPath == "minimap-player");
        Assert.Equal((757 + 6, 83 + 4), (player.ScreenX, player.ScreenY));

        Assert.Throws<ArgumentNullException>(() => art.Build(null!, ref order, 2, 3, BuildCells(false), false, false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => art.Build(list, ref order, 0, 3, Array.Empty<DungeonMinimapCell>(), false, false, 0));
        Assert.Throws<ArgumentException>(() => art.Build(list, ref order, 2, 3, new DungeonMinimapCell[2], false, false, 0));
    }

    [Fact]
    public void DungeonSessionUsesDungeonMinimapArtWhenSpritesArePresentAndTracksConnectedNeighbours()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.TwoRoomBossLayout(), (cx, cy) => DungeonSessionTests.None());
        var sprites = new MinimapSprites();
        var session = new DungeonSession(d.Layout, (cx, cy) => d.Rooms.TryGetValue((cx, cy), out RoomCombatSession r) ? r : d.Session.Current,
            sprites: sprites);

        Assert.True(session.IsConnectedNeighbour(1, 0));
        Assert.False(session.IsConnectedNeighbour(0, 0));

        List<SceneSprite> hud = session.Build(0);
        Assert.Contains(hud, s => s.AnimationPath == "minimap");
        Assert.Contains(hud, s => s.AnimationPath == "minimap-header");
        Assert.Contains(hud, s => s.AnimationPath == "minimap-player");
        Assert.Contains(hud, s => s.AnimationPath == "minimap-boss");
        Assert.DoesNotContain(hud, s => s.AnimationPath == "minimap-count");
    }
}
