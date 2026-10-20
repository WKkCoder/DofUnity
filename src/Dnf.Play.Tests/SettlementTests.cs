using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 结算菜单与通关营地(<see cref="SettlementArt"/>、<see cref="ClearCamp"/>,证据 E-091)。精灵是合成的"行号编码"色块与夹具里的合成动画,
/// 验证的是布局规则:四行的位置与点击测试、头脚与文字图的摆放、悬停与禁用、缺图时的占位面板、变暗层的前后次序、营火与 NPC 的摆法。
/// 会话里结算阶段的整体行为(延迟、变暗、选择)在 <see cref="DungeonSessionTests"/> 里。
/// </summary>
public class SettlementTests
{
    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    /// <summary>像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255)。</summary>
    private static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = (byte)(y % 256);
                rgba[o + 1] = (byte)(x % 256);
                rgba[o + 2] = tag;
                rgba[o + 3] = 255;
            }
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    /// <summary>头(268×54,偏移 1)、行(270×30)、脚(268×16,偏移 1)与 16 张文字图(蓝色通道 = 100 + 帧号);选择其它地下城的文字 112 宽(偏移 70),其余 66 宽(偏移 93),都是 19 高、偏移 y = 2。</summary>
    private static FakeSprites Panel(bool header = true, bool row = true, bool footer = true, bool labels = true)
    {
        var f = new FakeSprites();
        if (header)
        {
            f.Frames[(SettlementArt.Image, SettlementArt.HeaderFrame)] = Coded(268, 54, 1, 1, 0);
        }
        if (row)
        {
            f.Frames[(SettlementArt.Image, SettlementArt.RowFrame)] = Coded(270, 30, 2);
        }
        if (footer)
        {
            f.Frames[(SettlementArt.Image, SettlementArt.FooterFrame)] = Coded(268, 16, 3, 1, 0);
        }
        if (labels)
        {
            foreach (int first in new[] { SettlementArt.RetryLabelFrame, SettlementArt.SelectLabelFrame, SettlementArt.TownLabelFrame, SettlementArt.PartyLabelFrame })
            {
                for (int state = 0; state < 4; state++)
                {
                    bool wide = first == SettlementArt.SelectLabelFrame;
                    f.Frames[(SettlementArt.Image, first + state)] = wide
                        ? Coded(112, 19, (byte)(100 + first + state), 70, 2)
                        : Coded(66, 19, (byte)(100 + first + state), 93, 2);
                }
            }
        }
        return f;
    }

    private static SceneSprite Only(List<SceneSprite> sprites, string tag, int nth = 0)
    {
        return sprites.Where(s => s.AnimationPath == tag).ElementAt(nth);
    }

    [Fact]
    public void TheMenuHasFourRowsInTheOriginalOrderAndAClickOrHoverFindsTheRowUnderThePointer()
    {
        Assert.Equal(new[] { SettlementChoice.Retry, SettlementChoice.QuickParty, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown }, SettlementArt.RowChoices);
        Assert.Equal((76, 196), (SettlementArt.RowTop, SettlementArt.FooterY));      // 头 54 高、四行各 30 高
        Assert.Equal((512, 22, 270), (SettlementArt.PanelX, SettlementArt.PanelY, SettlementArt.PanelWidth));

        // 行的边界:顶 76 含、每行 30 高;左 512 含、右 782 不含
        Assert.Equal(-1, SettlementArt.HitRow(600, 75));
        Assert.Equal(0, SettlementArt.HitRow(600, 76));
        Assert.Equal(0, SettlementArt.HitRow(600, 105));
        Assert.Equal(1, SettlementArt.HitRow(600, 106));
        Assert.Equal(2, SettlementArt.HitRow(600, 165));
        Assert.Equal(3, SettlementArt.HitRow(600, 166));
        Assert.Equal(3, SettlementArt.HitRow(600, 195));
        Assert.Equal(-1, SettlementArt.HitRow(600, 196));                              // 脚
        Assert.Equal(-1, SettlementArt.HitRow(511, 100));
        Assert.Equal(0, SettlementArt.HitRow(512, 100));
        Assert.Equal(0, SettlementArt.HitRow(781, 100));
        Assert.Equal(-1, SettlementArt.HitRow(782, 100));

        Assert.Equal(SettlementChoice.Retry, SettlementArt.HitTest(600, 80));
        Assert.Equal(SettlementChoice.QuickParty, SettlementArt.HitTest(600, 110));
        Assert.Equal(SettlementChoice.SelectDungeon, SettlementArt.HitTest(600, 150));
        Assert.Equal(SettlementChoice.ReturnToTown, SettlementArt.HitTest(600, 190));
        Assert.Equal(SettlementChoice.None, SettlementArt.HitTest(10, 10));

        Assert.Equal(SettlementChoice.None, SettlementArt.ChoiceAt(-1));
        Assert.Equal(SettlementChoice.None, SettlementArt.ChoiceAt(4));
        Assert.Equal(new[] { 0, 1, 2, 3, -1 }, new[]
        {
            SettlementArt.RowOf(SettlementChoice.Retry), SettlementArt.RowOf(SettlementChoice.QuickParty),
            SettlementArt.RowOf(SettlementChoice.SelectDungeon), SettlementArt.RowOf(SettlementChoice.ReturnToTown), SettlementArt.RowOf(SettlementChoice.None),
        });
    }

    [Fact]
    public void RetryTownAndSelectAreEnabledWithTheirOriginalHotkeysAndQuickPartyIsNeither()
    {
        Assert.True(SettlementArt.IsEnabled(SettlementChoice.Retry));
        Assert.True(SettlementArt.IsEnabled(SettlementChoice.SelectDungeon));
        Assert.True(SettlementArt.IsEnabled(SettlementChoice.ReturnToTown));
        Assert.False(SettlementArt.IsEnabled(SettlementChoice.QuickParty));
        Assert.False(SettlementArt.IsEnabled(SettlementChoice.None));
        Assert.Equal("F13", SettlementArt.HotkeyName(SettlementChoice.Retry));
        Assert.Equal("F11", SettlementArt.HotkeyName(SettlementChoice.SelectDungeon));
        Assert.Equal("F12", SettlementArt.HotkeyName(SettlementChoice.ReturnToTown));
        Assert.Null(SettlementArt.HotkeyName(SettlementChoice.QuickParty));
        // 每个选项四张文字图:常态、悬停、按下、禁用
        Assert.Equal(new[] { 5, 6, 7, 8 }, Enumerable.Range(0, 4).Select(s => SettlementArt.LabelFrameIndex(SettlementChoice.Retry, s)));
        Assert.Equal(9, SettlementArt.LabelFrameIndex(SettlementChoice.SelectDungeon, SettlementArt.StateNormal));
        Assert.Equal(14, SettlementArt.LabelFrameIndex(SettlementChoice.ReturnToTown, SettlementArt.StateHover));
        Assert.Equal(19, SettlementArt.LabelFrameIndex(SettlementChoice.QuickParty, SettlementArt.StatePressed));
        Assert.Equal(-1, SettlementArt.LabelFrameIndex(SettlementChoice.None, 0));
    }

    [Fact]
    public void ThePanelIsAHeaderFourRowsAFooterAndLabelsWithHotkeyHintsAtTheMeasuredPositions()
    {
        SettlementArt art = SettlementArt.TryCreate(Panel())!;
        var font = new OverlayArt();
        var into = new List<SceneSprite>();
        int order = 0;
        art.Build(into, ref order, font);

        // 头、四行、脚、四张文字图、三条热键提示(快速组队没指定热键)
        Assert.Equal(1 + 4 + 1 + 4 + 3, into.Count);
        Assert.Equal(into.Count, order);
        Assert.Equal(Enumerable.Range(0, into.Count), into.Select(s => s.SortKey));    // 次序连续编号
        Assert.All(into, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));

        SceneSprite header = Only(into, "settle-header");
        Assert.Equal((513, 22), (header.ScreenX, header.ScreenY));                    // 画布原点 512 + 帧偏移 1
        Assert.Equal(new[] { 76, 106, 136, 166 }, into.Where(s => s.AnimationPath == "settle-row").Select(s => s.ScreenY));
        Assert.All(into.Where(s => s.AnimationPath == "settle-row"), s => Assert.Equal(512, s.ScreenX));
        SceneSprite footer = Only(into, "settle-footer");
        Assert.Equal((513, 196), (footer.ScreenX, footer.ScreenY));

        // 文字图:画布原点 522 加各自的帧偏移;行顶加偏移 y = 2
        var labels = into.Where(s => s.AnimationPath == "settle-label").ToList();
        Assert.Equal(new[] { (615, 78), (615, 108), (592, 138), (615, 168) }, labels.Select(s => (s.ScreenX, s.ScreenY)));
        Assert.Equal(new[] { 105, 117, 109, 113 }, labels.Select(s => (int)s.Frame.Rgba[2]));   // 100 + 帧号:再次挑战 5、快速组队 17、选择 9、返回城镇 13

        // 热键提示:文字图右边空 HintGap 像素,在行里垂直居中
        var hints = into.Where(s => s.AnimationPath == "settle-hint").ToList();
        int hintWidth = OverlayArt.TextWidth("(F13)", 1);
        Assert.Equal(3, hints.Count);
        Assert.Equal((615 + 66 + SettlementArt.HintGap, 76 + (30 - OverlayArt.TextHeight(1)) / 2), (hints[0].ScreenX, hints[0].ScreenY));
        Assert.Equal((592 + 112 + SettlementArt.HintGap, 136 + (30 - OverlayArt.TextHeight(1)) / 2), (hints[1].ScreenX, hints[1].ScreenY));
        Assert.Equal((615 + 66 + SettlementArt.HintGap, 166 + (30 - OverlayArt.TextHeight(1)) / 2), (hints[2].ScreenX, hints[2].ScreenY));
        Assert.All(hints, h => Assert.Equal(hintWidth, h.Frame.Width));
    }

    [Fact]
    public void HoveringARowSwapsInItsHoverLabelButADisabledRowNeverLightsUp()
    {
        SettlementArt art = SettlementArt.TryCreate(Panel())!;
        var font = new OverlayArt();

        int LabelTag(int hoverRow, int row)
        {
            var into = new List<SceneSprite>();
            int order = 0;
            art.Build(into, ref order, font, hoverRow);
            return Only(into, "settle-label", row).Frame.Rgba[2];
        }

        Assert.Equal(105, LabelTag(-1, 0));                              // 没有悬停:常态
        Assert.Equal(106, LabelTag(0, 0));                               // 悬停在第 0 行(再次挑战):帧 5 + 1
        Assert.Equal(117, LabelTag(1, 1));                               // 快速组队不可用:悬停也保持常态(帧 17)
        Assert.Equal(110, LabelTag(2, 2));                               // 选择其它地下城:帧 9 + 1
        Assert.Equal(113, LabelTag(2, 3));                               // 悬停在别的行时这一行是常态
        Assert.Equal(114, LabelTag(3, 3));                               // 返回城镇:帧 13 + 1
    }

    [Fact]
    public void TheArtIsSharedPerImagePackAndAMissingHeaderRowOrFooterMeansNoArtButMissingLabelsJustAreNotDrawn()
    {
        FakeSprites sprites = Panel();
        Assert.Same(SettlementArt.For(sprites), SettlementArt.For(sprites));
        Assert.NotNull(SettlementArt.For(sprites));
        Assert.NotSame(SettlementArt.For(sprites), SettlementArt.For(Panel()));       // 不同的图包各有一个
        Assert.Null(SettlementArt.For(null));
        Assert.Null(SettlementArt.TryCreate(Panel(header: false)));
        Assert.Null(SettlementArt.TryCreate(Panel(row: false)));
        Assert.Null(SettlementArt.TryCreate(Panel(footer: false)));
        Assert.Null(SettlementArt.For(new FakeSprites()));

        SettlementArt noLabels = SettlementArt.TryCreate(Panel(labels: false))!;
        var into = new List<SceneSprite>();
        int order = 0;
        noLabels.Build(into, ref order, new OverlayArt());
        Assert.Equal(1, into.Count(s => s.AnimationPath == "settle-header"));
        Assert.Equal(4, into.Count(s => s.AnimationPath == "settle-row"));
        Assert.DoesNotContain(into, s => s.AnimationPath == "settle-label");
    }

    [Fact]
    public void TheFallbackPanelHasTheSameRowsAndHotkeysWithoutAnyImage()
    {
        var font = new OverlayArt();
        var into = new List<SceneSprite>();
        int order = 0;
        SettlementArt.BuildFallback(into, ref order, font);

        Assert.All(into, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        SceneSprite back = Only(into, "settle-fallback");
        Assert.Equal((512, 22, 270, 54 + 4 * 30 + 16), (back.ScreenX, back.ScreenY, back.Frame.Width, back.Frame.Height));
        Assert.Equal(210, back.TintAlpha);
        var labels = into.Where(s => s.AnimationPath == "settle-label").ToList();
        Assert.Equal(4, labels.Count);
        // 文字在各行里垂直居中、左边对齐文字图的画布原点
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(SettlementArt.LabelX, labels[i].ScreenX);
            Assert.Equal(76 + 30 * i + (30 - labels[i].Frame.Height) / 2, labels[i].ScreenY);
        }
        Assert.Equal(3, into.Count(s => s.AnimationPath == "settle-hint"));

        // 悬停的行换成黄字,不可用的行是灰字,其余白字
        uint ColorOf(SceneSprite label)
        {
            for (int i = 0; i < label.Frame.Rgba.Length; i += 4)
            {
                if (label.Frame.Rgba[i + 3] == 255 && label.Frame.Rgba[i] + label.Frame.Rgba[i + 1] + label.Frame.Rgba[i + 2] > 0)
                {
                    return (uint)(label.Frame.Rgba[i] << 16 | label.Frame.Rgba[i + 1] << 8 | label.Frame.Rgba[i + 2]);
                }
            }
            return 0;
        }
        Assert.Equal(OverlayArt.White, ColorOf(labels[0]));
        Assert.Equal(0x808080u, ColorOf(labels[1]));
        into.Clear();
        order = 0;
        SettlementArt.BuildFallback(into, ref order, font, 0);
        Assert.Equal(OverlayArt.Yellow, ColorOf(Only(into, "settle-label", 0)));
        into.Clear();
        order = 0;
        SettlementArt.BuildFallback(into, ref order, font, 1);              // 不可用的行悬停也不亮
        Assert.Equal(0x808080u, ColorOf(Only(into, "settle-label", 1)));
    }

    [Fact]
    public void TheDimLayersSitBetweenTheSceneLayersTheObjectsAndTheInterfaceInDrawOrder()
    {
        Assert.True(OverlayArt.DimRank > 5);                              // 第二层黑幕盖住近景图层(大类 5)
        Assert.True(OverlayArt.DimRank < OverlayArt.UiRank);
        Assert.Equal(3, OverlayArt.BackDimRank);                          // 第一层黑幕在 bottom 大类里,正好在 normal(物件)之下
        Assert.Equal(TownScene.NormalRank - 1, OverlayArt.BackDimRank);

        var art = new OverlayArt();
        SpriteFrame black = art.Solid(800, 600, OverlayArt.Black);
        SceneSprite dim = OverlayArt.PlaceDim(black, 100, 3);
        Assert.Equal((0, 0, 100, OverlayArt.DimRank, "dim", 3), (dim.ScreenX, dim.ScreenY, dim.TintAlpha, dim.LayerRank, dim.AnimationPath, dim.SortKey));
        Assert.Equal(SpriteBlend.Normal, dim.Blend);
        SceneSprite back = OverlayArt.PlaceDim(black, 56, 0, "back", OverlayArt.BackDimRank);
        Assert.Equal((0, 0, 56, OverlayArt.BackDimRank, "back"), (back.ScreenX, back.ScreenY, back.TintAlpha, back.LayerRank, back.AnimationPath));

        SceneSprite ui = OverlayArt.Place(art.Solid(10, 10, OverlayArt.White), 5, 5, 0, "ui");
        SpriteFrame dot = art.Solid(4, 4, OverlayArt.White);
        var map = new SceneSprite(dot, 1, 1, 0, -1, "map", 0, false, SpriteBlend.Normal, 255, 1);
        var bottom = new SceneSprite(dot, 1, 1, 0, -1, "bottom", 0, false, SpriteBlend.Normal, 255, 3);
        var normal = new SceneSprite(dot, 1, 1, 50, 1, "normal", 0);
        var close = new SceneSprite(dot, 1, 1, 0, -1, "close", 0, false, SpriteBlend.Normal, 255, 5);
        var fire = new SceneSprite(dot, 1, 1, 0, -1, "fire", 0, false, SpriteBlend.Normal, 255, ClearCamp.FireRank);
        // 房间场景把外面传进来的精灵(两层黑幕、营火、界面)追加在地图图层之后:同大类按列表顺序,所以第一层黑幕在 bottom 图层之后,营火在第二层黑幕之后
        List<SceneSprite> sorted = TownScene.SortByDepth(new List<SceneSprite> { map, bottom, ui, dim, fire, close, normal, back });
        Assert.Equal(new[] { "map", "bottom", "back", "normal", "close", "dim", "fire", "ui" }, sorted.Select(s => s.AnimationPath));
    }

    [Fact]
    public void TheCampfireIsBuiltFromTheActionScriptsBaseAndSubAnimationsAtTheCampPosition()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(0, 200);
        ClearCamp camp = ClearCamp.Create(assets, null, 400, 120);

        Assert.Equal((400.0, 120.0, 3, 0), (camp.X, camp.Y, camp.FireLayers, camp.NpcCount));    // 木柴 + 两层子动画;没给地图就没有 NPC
        var sprites = new List<SceneSprite>();
        camp.AddSprites(sprites, assets, projection);
        // 脚本里的次序:基础动画先、子动画按写的顺序;图的相对路径(含大小写与 ..)都能解析到
        Assert.Equal(new[] { AvatarFixture.CampBaseAni, AvatarFixture.CampGlowAni, AvatarFixture.CampBaseAni }, sprites.Select(s => s.AnimationPath));
        // 位置 = 世界 − 镜头 + .ani 偏移 (−5, −8) + IMG 头偏移 (−3, −4);排序键 = 世界 y
        Assert.Equal((400 - 5 - 3, 120 + 200 - 8 - 4), (sprites[0].ScreenX, sprites[0].ScreenY));
        Assert.Equal((410 - 5 - 3, 115 + 200 - 8 - 4), (sprites[1].ScreenX, sprites[1].ScreenY));    // 光晕:偏移 (10, −5)
        Assert.Equal(new[] { 120, 115, 120 }, sprites.Select(s => s.SortKey));
        // 营火各层在变暗层的大类里(不被压暗,也盖在主角前面);NPC 才在 normal 大类
        Assert.All(sprites, s => Assert.Equal(ClearCamp.FireRank, s.LayerRank));
        Assert.Equal(OverlayArt.DimRank, ClearCamp.FireRank);
    }

    [Fact]
    public void TheCampPlaysForeverTheWoodHoldsItsLastFrameAndTheGlowLoops()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(0, 200);
        ClearCamp camp = ClearCamp.Create(assets, null, 400, 120);

        int Red(int layer)
        {
            var sprites = new List<SceneSprite>();
            camp.AddSprites(sprites, assets, projection);
            return sprites[layer].Frame.Rgba[0];
        }

        Assert.Equal((200, 200, 200), (Red(0), Red(1), Red(2)));          // 刚生起:各层第 0 帧
        camp.Advance(150);
        Assert.Equal((201, 201, 201), (Red(0), Red(1), Red(2)));          // 150 毫秒:第 1 帧
        camp.Advance(10_000);                                             // 播完很久之后
        Assert.Equal(10_150, camp.AgeMs);
        Assert.Equal(3, camp.FireLayers);
        Assert.Equal(201, Red(0));                                        // 不循环的木柴停在最后一帧,不消失
        Assert.Equal(201, Red(1));                                        // 10150 mod 200 = 150 → 光晕在第 1 帧
        camp.Advance(50);                                                 // 10200 mod 200 = 0 → 光晕回到第 0 帧,木柴还停着
        Assert.Equal((201, 200, 201), (Red(0), Red(1), Red(2)));
        camp.Advance(0);
        camp.Advance(-5);                                                 // 零与负的时间不推进
        Assert.Equal(10_200, camp.AgeMs);
    }

    [Fact]
    public void OnlyNpcsFlaggedVisibleOnDungeonClearStandByTheFireAutoPlacedOnesLeftOfItAndExplicitOnesWhereTheMapSaysAndTheyHoldTheirPose()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(0, 200);
        // 夹具自身的数据链(地图 [NPC] → npc.lst → .npc → [field animation])先核一遍,出错时能分清是夹具还是营地
        Assert.Equal(3, TownScene.ReadMapNpcs(assets, AvatarFixture.CampMapPath).Count);
        Assert.Equal(3, TownScene.ReadNpcCatalog(assets).Count);
        Assert.Equal(AvatarFixture.CampKeeperAni, TownScene.FieldAnimationPath(assets, AvatarFixture.CampKeeperScript));
        ClearCamp camp = ClearCamp.Create(assets, AvatarFixture.CampMapPath, 400, 120);

        // 守卫、客人带 [visible on dungeon clear] 1;普通 NPC 写的是 0:不出现
        Assert.Equal(2, camp.NpcCount);
        Assert.True(ClearCamp.IsVisibleOnDungeonClear(assets, AvatarFixture.CampKeeperScript));
        Assert.False(ClearCamp.IsVisibleOnDungeonClear(assets, AvatarFixture.CampPlainScript));
        Assert.False(ClearCamp.IsVisibleOnDungeonClear(assets, "npc/camp/missing.npc"));

        var sprites = new List<SceneSprite>();
        camp.AddSprites(sprites, assets, projection);
        Assert.Equal(2 + 3, sprites.Count);
        // NPC 先、营火各层后;守卫坐标 (0, 0, 0) = 自动安排:营火左边 NpcOffsetX、y 差 NpcOffsetY,朝 [left] 镜像;客人在地图写的 (500, 60),朝右
        SceneSprite keeper = sprites[0];
        SceneSprite guest = sprites[1];
        Assert.Equal(AvatarFixture.CampKeeperAni, keeper.AnimationPath);
        Assert.Equal(AvatarFixture.CampGuestAni, guest.AnimationPath);
        Assert.True(keeper.Mirrored);
        Assert.False(guest.Mirrored);
        Assert.Equal((-100, 0), (ClearCamp.NpcOffsetX, ClearCamp.NpcOffsetY));    // 基线上粗测的德利拉相对营火的位置
        Assert.Equal(120 + ClearCamp.NpcOffsetY + ClearCamp.NpcSortBias, keeper.SortKey);
        Assert.Equal(60 + ClearCamp.NpcSortBias, guest.SortKey);
        Assert.Equal(500 - 5 - 3, guest.ScreenX);                         // 世界 x − 镜头 + .ani 偏移 + IMG 头偏移
        Assert.Equal(60 + 200 - 8 - 4, guest.ScreenY);
        // 镜像:翻转后的左边缘 = 世界 x − 镜头 − (.ani x + IMG x + 宽) = 300 − (−5 − 3 + 2) = 306
        Assert.Equal((400 + ClearCamp.NpcOffsetX) - (-5 - 3 + 2), keeper.ScreenX);
        Assert.Equal(120 + ClearCamp.NpcOffsetY + 200 - 8 - 4, keeper.ScreenY);
        Assert.Equal(-1, keeper.NpcId);
        // NPC 在 normal 大类(夹在两层黑幕之间,与主角一起按世界 y 排);只有营火各层在变暗层大类
        Assert.All(sprites.Take(2), s => Assert.Equal(TownScene.NormalRank, s.LayerRank));
        Assert.All(sprites.Skip(2), s => Assert.Equal(ClearCamp.FireRank, s.LayerRank));

        // NPC 的待机动画(循环)照常播,常驻不消失
        camp.Advance(10_000);
        sprites.Clear();
        camp.AddSprites(sprites, assets, projection);
        Assert.Equal(5, sprites.Count);
    }

    [Fact]
    public void ACampWithoutAssetsOrWithAMapThatCannotBeReadIsStillAValidCamp()
    {
        ClearCamp none = ClearCamp.Create(null, "map/whatever.map", 10, 20);
        Assert.Equal((10.0, 20.0, 0, 0), (none.X, none.Y, none.FireLayers, none.NpcCount));
        var sprites = new List<SceneSprite>();
        none.AddSprites(sprites, null, new TownProjection(0, 200));
        Assert.Empty(sprites);
        none.Advance(100);                                                // 空营地照样能推进
        Assert.Equal(100, none.AgeMs);

        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        ClearCamp noMap = ClearCamp.Create(assets, "map/test/nothing.map", 400, 120);   // 地图读不到:营火照样生,NPC 没有
        Assert.Equal((3, 0), (noMap.FireLayers, noMap.NpcCount));
        ClearCamp emptyPath = ClearCamp.Create(assets, string.Empty, 400, 120);
        Assert.Equal((3, 0), (emptyPath.FireLayers, emptyPath.NpcCount));
    }
}
