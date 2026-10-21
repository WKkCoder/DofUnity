using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>合成的图源:按(图名, 帧号)查帧。</summary>
internal sealed class WindowSprites : ISpriteSource
{
    public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);

    /// <summary>像素 (x, y) = (R = y % 256, G = x % 256, B = tag, A = 255):从任何一块裁出来的像素都能反推它在原图里的行与列。</summary>
    public static SpriteFrame Coded(int width, int height, byte tag)
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
        return new SpriteFrame(width, height, 0, 0, width, height, rgba);
    }

    public const string FaceImage = "sprite/test/faces.img";

    /// <summary>
    /// 合成的目标血条窗口全套图:monster_hp.img 的 19 帧(尺寸与真实的一致,B 通道是帧号)、一张头像(B = 60)、
    /// 类别牌(B = 100 + 帧号,宽 32;第 10 帧是宽 0 的空帧)、死亡 X(B = 70)。
    /// </summary>
    public static WindowSprites Window(bool bars = true, bool category = true)
    {
        var f = new WindowSprites();
        string w = MonsterHpWindow.WindowImage;
        f.Frames[(w, 1)] = Coded(626, 32, 1);
        f.Frames[(w, 8)] = Coded(3, 17, 8);
        f.Frames[(w, 9)] = Coded(628, 36, 9);
        f.Frames[(w, 10)] = Coded(4, 19, 10);
        f.Frames[(w, 11)] = Coded(630, 36, 11);
        f.Frames[(w, 12)] = Coded(4, 36, 12);
        if (bars)
        {
            for (int i = 2; i <= 7; i++)
            {
                f.Frames[(w, i)] = Coded(597, 9, (byte)i);
            }
            for (int i = 13; i <= 18; i++)
            {
                f.Frames[(w, i)] = Coded(38, 26, (byte)i);
            }
        }
        f.Frames[(FaceImage, 3)] = Coded(24, 24, 60);
        f.Frames[(MonsterHpWindow.BaseImage, MonsterHpWindow.DeadMarkFrame)] = Coded(24, 24, 70);
        if (category)
        {
            for (int i = 0; i <= 14; i++)
            {
                f.Frames[(MonsterHpWindow.CategoryImage, i)] = i == 10 ? Coded(0, 0, 110) : Coded(32, 15, (byte)(100 + i));
            }
        }
        return f;
    }
}

/// <summary>
/// 目标怪物血条窗口(<see cref="MonsterHpWindow"/>,证据 E-106)。精灵是合成的"行列编码"色块,验证的是规则:
/// 三种窗口的位置与帧、类别牌的排法、血条的裁切与叠层、掉血后的变暗、白闪、缩短与消失、死亡后的 X 与隐藏时机、换目标。
/// </summary>
public class MonsterHpWindowTests
{
    private static MonsterActor Mob(DnfAssets assets, int hp, MonsterPanelInfo panel = null)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 340, 100, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, hp, 222.5, 1500, 0) { Panel = panel };
    }

    private static MonsterPanelInfo Panel(MonsterKind kind, int gauge, params string[] categories)
        => new MonsterPanelInfo(kind, WindowSprites.FaceImage, 3, categories, gauge);

    private static List<SceneSprite> Build(MonsterHpWindow w)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        w.Build(list, ref order);
        return list;
    }

    private static SceneSprite Only(List<SceneSprite> sprites, string tag)
    {
        return Assert.Single(sprites, s => s.AnimationPath == tag);
    }

    private static List<SceneSprite> All(List<SceneSprite> sprites, string tag)
    {
        return sprites.Where(s => s.AnimationPath == tag).ToList();
    }

    /// <summary>编码帧的 B 通道(帧号)。</summary>
    private static int TagOf(SceneSprite s) => s.Frame.Rgba[2];

    /// <summary>编码帧(未变暗)第一个像素的 G 通道 = 这块在原图里的起始列。</summary>
    private static int LeftColumn(SceneSprite s) => s.Frame.Rgba[1];

    private static int Darkened(int channel) => (channel * MonsterHpWindow.DarkMultiplier + 127) / 255;

    [Fact]
    public void WithoutTheTwoBasicFramesThereIsNoWindow()
    {
        var art = new OverlayArt();
        Assert.Null(MonsterHpWindow.For(null, art));
        Assert.Null(MonsterHpWindow.For(new WindowSprites(), art));
        var onlyBody = new WindowSprites();
        onlyBody.Frames[(MonsterHpWindow.WindowImage, 1)] = WindowSprites.Coded(626, 32, 1);
        Assert.Null(MonsterHpWindow.For(onlyBody, art));
        Assert.NotNull(MonsterHpWindow.For(WindowSprites.Window(), art));
    }

    [Fact]
    public void NothingIsDrawnUntilATargetIsChosen()
    {
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        Assert.False(w.Visible);
        Assert.Empty(Build(w));
        w.Update(100);
        Assert.Empty(Build(w));
        w.SetTarget(null, 120);
        Assert.False(w.Visible);
    }

    [Fact]
    public void TheOrdinaryWindowIsLaidOutFromTheFixedAnchors()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 100, Panel(MonsterKind.Normal, 120, "[human]", "[goblin]", "[melee combat]"));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        Assert.True(w.Visible);
        List<SceneSprite> list = Build(w);

        // 底框 #1:左上角 (4, 98),只露出血条总长 + 29 列
        SceneSprite body = Only(list, "mhp-body");
        Assert.Equal((4, 98, 149, 32, 1), (body.ScreenX, body.ScreenY, body.Frame.Width, body.Frame.Height, TagOf(body)));
        Assert.Equal(0, LeftColumn(body));
        // 端帽 #8 紧贴血条末端:x = 4 + 29 + 120,y = 98 + 15
        SceneSprite cap = Only(list, "mhp-cap");
        Assert.Equal((153, 113, 8), (cap.ScreenX, cap.ScreenY, TagOf(cap)));
        // 头像 (6, 100)
        SceneSprite face = Only(list, "mhp-face");
        Assert.Equal((6, 100, 60), (face.ScreenX, face.ScreenY, TagOf(face)));
        // 满血的血条:第 0 层(帧 #2)的 [0, 120) 列,摆在 (33, 115)
        SceneSprite bar = Only(list, "mhp-bar");
        Assert.Equal((33, 115, 120, 9, 2), (bar.ScreenX, bar.ScreenY, bar.Frame.Width, bar.Frame.Height, TagOf(bar)));
        Assert.Empty(All(list, "mhp-lost"));
        Assert.Empty(All(list, "mhp-dead"));
        // 类别牌:[human] 是第 0 帧,画在 (35, 97);没有牌子的类别什么也不占
        SceneSprite tag = Only(list, "mhp-tag");
        Assert.Equal((35, 97, 100), (tag.ScreenX, tag.ScreenY, TagOf(tag)));
        // 文字 "Lv3":原点在 (牌子末端 35 + 33 + 2, 牌子行 + 3);我们的字带 1 像素边,所以摆在 (原点 - 1, 原点 + 1)
        SceneSprite text = Only(list, "mhp-text");
        Assert.Equal((35 + 33 + 2 - 1, 97 + 3 + 2 - 1), (text.ScreenX, text.ScreenY));
        // 全部是叠加层,按画的先后排
        Assert.All(list, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(new[] { "mhp-body", "mhp-cap", "mhp-face", "mhp-bar", "mhp-tag", "mhp-text" }, list.Select(s => s.AnimationPath).ToArray());
    }

    [Fact]
    public void TheChampionAndBossWindowsUseTheirOwnFramesAndAnchors()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        WindowSprites art = WindowSprites.Window();

        foreach (MonsterKind kind in new[] { MonsterKind.CommonChampion, MonsterKind.SuperChampion })
        {
            var w = MonsterHpWindow.For(art, new OverlayArt())!;
            w.SetTarget(Mob(assets, 100, Panel(kind, 200, "[beast]")), 0);
            List<SceneSprite> list = Build(w);
            SceneSprite body = Only(list, "mhp-body");
            Assert.Equal((2, 96, 236, 36, 9), (body.ScreenX, body.ScreenY, body.Frame.Width, body.Frame.Height, TagOf(body)));   // 200 + 36 列
            SceneSprite cap = Only(list, "mhp-cap");
            Assert.Equal((2 + 36 + 200, 96 + 16, 10), (cap.ScreenX, cap.ScreenY, TagOf(cap)));
            SceneSprite bar = Only(list, "mhp-bar");
            Assert.Equal((38, 115, 200, 2), (bar.ScreenX, bar.ScreenY, bar.Frame.Width, TagOf(bar)));
            SceneSprite face = Only(list, "mhp-face");
            Assert.Equal((6, 100), (face.ScreenX, face.ScreenY));
            SceneSprite tag = Only(list, "mhp-tag");
            Assert.Equal((34, 96, 101), (tag.ScreenX, tag.ScreenY, TagOf(tag)));
        }

        var boss = MonsterHpWindow.For(art, new OverlayArt())!;
        boss.SetTarget(Mob(assets, 100, Panel(MonsterKind.Boss, 100, "[dragon]")), 0);
        List<SceneSprite> b = Build(boss);
        SceneSprite bossBody = Only(b, "mhp-body");
        Assert.Equal((2, 96, 133, 36, 11), (bossBody.ScreenX, bossBody.ScreenY, bossBody.Frame.Width, bossBody.Frame.Height, TagOf(bossBody)));
        SceneSprite bossCap = Only(b, "mhp-cap");
        Assert.Equal((2 + 33 + 100, 96, 12), (bossCap.ScreenX, bossCap.ScreenY, TagOf(bossCap)));
        SceneSprite bossTag = Only(b, "mhp-tag");
        Assert.Equal((39, 104, 109), (bossTag.ScreenX, bossTag.ScreenY, TagOf(bossTag)));
        // 首领的血条是 38 像素的图块(第 0 层用帧 #13),从血条起点 (35, 99) 起铺到 100 像素:38 + 38 + 24
        List<SceneSprite> tiles = All(b, "mhp-bar");
        Assert.Equal(new[] { (35, 38), (73, 38), (111, 24) }, tiles.Select(t => (t.ScreenX, t.Frame.Width)).ToArray());
        Assert.All(tiles, t => Assert.Equal((99, 13), (t.ScreenY, TagOf(t))));
    }

    [Fact]
    public void TheBodyIsCappedAtTheLayerWidthForVeryLongBars()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(Mob(assets, 100, Panel(MonsterKind.Normal, 1400)), 0);
        List<SceneSprite> list = Build(w);
        Assert.Equal(540 + 29, Only(list, "mhp-body").Frame.Width);
        Assert.Equal(4 + 29 + 540, Only(list, "mhp-cap").ScreenX);
    }

    [Fact]
    public void CategoryTagsAdvanceByOnePlusTheirWidthAndSkipUnknownNames()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 名字带不带方括号、大小写都认;空帧(宽 0)的 [oneside move] 不画但占 1 像素;organic 没有牌子
        MonsterActor m = Mob(assets, 100, Panel(MonsterKind.Normal, 100, "[Human]", "organic", "[oneside move]", "[dragon]", "angel"));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        List<SceneSprite> list = Build(w);
        List<SceneSprite> tags = All(list, "mhp-tag");
        Assert.Equal(new[] { (35, 100), (35 + 33 + 1, 109), (35 + 33 + 1 + 33, 114) }, tags.Select(t => (t.ScreenX, TagOf(t))).ToArray());
        // 文字紧跟最后一块:35 + 33 + 1 + 33 + 33 之后 + 2
        Assert.Equal(35 + 33 + 1 + 33 + 33 + 2 - 1, Only(list, "mhp-text").ScreenX);
    }

    [Fact]
    public void TheCategoryTableMatchesTheClientMapping()
    {
        var expected = new Dictionary<string, int>
        {
            { "human", 0 }, { "beast", 1 }, { "plant", 2 }, { "insect", 3 }, { "machine", 4 }, { "hybrid", 5 }, { "undead", 6 },
            { "devil", 7 }, { "spirit", 8 }, { "dragon", 9 }, { "oneside move", 10 }, { "human armor", 11 }, { "fort armor", 12 }, { "angel", 14 },
        };
        foreach (KeyValuePair<string, int> e in expected)
        {
            Assert.True(MonsterHpWindow.TryCategoryFrame("[" + e.Key + "]", out int frame), e.Key);
            Assert.Equal(e.Value, frame);
        }
        foreach (string none in new[] { "[organic]", "[goblin]", "[divine armor]", "[melee combat]", "[close-passive]", "", null! })
        {
            Assert.False(MonsterHpWindow.TryCategoryFrame(none, out _), none);
        }
    }

    [Fact]
    public void TheFaceIsOptionalAndWithoutAPanelTheWindowUsesTheOrdinaryDefaults()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        // 没给资料:普通窗口、没有头像与牌子、血条总长用默认的 (HpMax / 100 × 300)
        MonsterActor m = Mob(assets, 40);
        w.SetTarget(m, 0);
        List<SceneSprite> list = Build(w);
        Assert.Empty(All(list, "mhp-face"));
        Assert.Empty(All(list, "mhp-tag"));
        Assert.Equal(HpGauge.Length(40, HpGauge.DefaultLayers, HpGauge.DefaultScale), w.Gauge);
        Assert.Equal(w.Gauge, Only(list, "mhp-bar").Frame.Width);
        Assert.Single(All(list, "mhp-text"));

        // 有资料但没有头像
        var noFace = new MonsterPanelInfo(MonsterKind.Normal, null, -1, new string[0], 50);
        var w2 = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w2.SetTarget(Mob(assets, 40, noFace), 0);
        Assert.Empty(All(Build(w2), "mhp-face"));
    }

    [Fact]
    public void TheBarShowsTheRemainingFractionWithTruncation()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 60, Panel(MonsterKind.Normal, 100));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        Assert.Equal(100, w.Value);
        m.ApplyHit(25, false, 0, true);                                  // 35 / 60 × 100 = 58.33 → 58
        w.Update(10);
        Assert.Equal(58, w.Value);
        Assert.Equal(58, Only(Build(w), "mhp-bar").Frame.Width);
    }

    [Fact]
    public void ALongBarDrawsTheLowerLayerOnlyWhereTheTopLayerDoesNotCover()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(Mob(assets, 100, Panel(MonsterKind.Normal, 1400)), 0);
        // 1400 像素 = 540 + 540 + 320:最上层(第 2 层,帧 #4)画 [0, 320),下一层(第 1 层,帧 #3)只露出 [320, 540)
        List<SceneSprite> bars = All(Build(w), "mhp-bar");
        Assert.Equal(new[] { (33 + 320, 220, 3), (33, 320, 4) }, bars.Select(b => (b.ScreenX, b.Frame.Width, TagOf(b))).ToArray());
        Assert.Equal(64, LeftColumn(bars[0]));                            // 第一块从原图第 320 列起(320 % 256 = 64)
        Assert.Equal(0, LeftColumn(bars[1]));

        // 刚好一层半的整数倍:540 像素满长时只有一层
        var exact = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        exact.SetTarget(Mob(assets, 100, Panel(MonsterKind.Normal, 540)), 0);
        Assert.Equal(new[] { (33, 540, 2) }, All(Build(exact), "mhp-bar").Select(b => (b.ScreenX, b.Frame.Width, TagOf(b))).ToArray());
        // 541 像素:第 1 层只有 1 像素,相邻的下面那层(第 0 层)整层画出来,再盖上第 1 层的那 1 像素
        var one = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        one.SetTarget(Mob(assets, 100, Panel(MonsterKind.Normal, 541)), 0);
        Assert.Equal(new[] { (33, 540, 2), (33, 1, 3) }, All(Build(one), "mhp-bar").Select(b => (b.ScreenX, b.Frame.Width, TagOf(b))).ToArray());
    }

    [Fact]
    public void ALostSegmentFlashesThenShrinksThenDisappears()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 100, Panel(MonsterKind.Normal, 100));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);                                               // 先选中再结算
        w.Update(0);
        m.ApplyHit(30, false, 0, true);
        w.Update(10);
        Assert.Equal((70, 1), (w.Value, w.HistoryCount));

        // 白闪期(不到 500 毫秒):掉下去的 [70, 100) 画成变暗的第 0 层,上面盖白闪(帧 #7),不透明度从 255 线性降到 0
        List<SceneSprite> list = Build(w);
        SceneSprite lost = Only(list, "mhp-lost");
        Assert.Equal((33 + 70, 115, 30), (lost.ScreenX, lost.ScreenY, lost.Frame.Width));
        Assert.Equal(Darkened(2), TagOf(lost));                          // 第 0 层的图(帧 #2)变暗后的 B 通道
        Assert.Equal(Darkened(70), lost.Frame.Rgba[1]);                  // 变暗:每个颜色通道乘 150 / 255
        Assert.Equal(255, lost.Frame.Rgba[3]);                           // 透明度不动
        SceneSprite flash = Only(list, "mhp-flash");
        Assert.Equal((33 + 70, 115, 30, 7, 255), (flash.ScreenX, flash.ScreenY, flash.Frame.Width, TagOf(flash), flash.TintAlpha));
        Assert.Equal(70, LeftColumn(flash));                             // 白闪帧没有变暗,列号原样
        // 变暗的段在白闪的下面
        Assert.True(list.IndexOf(lost) < list.IndexOf(flash));
        Assert.True(list.IndexOf(Only(list, "mhp-bar")) < list.IndexOf(lost));

        w.Update(260);                                                   // 距掉血 250 毫秒
        Assert.Equal(127, Only(Build(w), "mhp-flash").TintAlpha);
        w.Update(509);                                                   // 距掉血 499 毫秒:还在白闪期,几乎透明
        Assert.Equal(0, Only(Build(w), "mhp-flash").TintAlpha);

        // 距掉血 500 毫秒起:不再白闪,上沿每 5 毫秒缩 1 像素(总长 100 → 跨度 500 毫秒)
        w.Update(510);
        List<SceneSprite> shrinking = Build(w);
        Assert.Empty(All(shrinking, "mhp-flash"));
        Assert.Equal(30, Only(shrinking, "mhp-lost").Frame.Width);
        w.Update(585);                                                   // 缩了 75 / 5 = 15 像素
        Assert.Equal(15, Only(Build(w), "mhp-lost").Frame.Width);
        Assert.Equal(1, w.HistoryCount);
        w.Update(659);                                                   // 缩了 29 像素:还剩 1 像素
        Assert.Equal(1, Only(Build(w), "mhp-lost").Frame.Width);
        w.Update(660);                                                   // 缩了 30 像素:没了,历史清掉
        Assert.Empty(All(Build(w), "mhp-lost"));
        Assert.Equal(0, w.HistoryCount);
    }

    [Fact]
    public void SelectingTheTargetAfterTheDamageShowsNoFlash()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 100, Panel(MonsterKind.Normal, 100));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        m.ApplyHit(30, false, 0, true);
        w.SetTarget(m, 0);                                               // 伤害已经结算,窗口取的就是低血量:没有"掉下去的一段"
        w.Update(10);
        Assert.Equal((70, 0), (w.Value, w.HistoryCount));
        Assert.Empty(All(Build(w), "mhp-lost"));
    }

    [Fact]
    public void SecondHitWhileTheFirstStillFlashesChainsTheSegments()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 100, Panel(MonsterKind.Normal, 100));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        m.ApplyHit(20, false, 0, true);
        w.Update(10);                                                    // 100 → 80
        m.ApplyHit(30, false, 0, true);
        w.Update(60);                                                    // 80 → 50
        Assert.Equal((50, 2), (w.Value, w.HistoryCount));
        List<SceneSprite> lost = All(Build(w), "mhp-lost");
        // 最新的一段 [50, 80)(刚掉,不透明度 255),上一段 [80, 100)(掉了 50 毫秒)接在它上面
        Assert.Equal(new[] { (33 + 50, 30), (33 + 80, 20) }, lost.Select(s => (s.ScreenX, s.Frame.Width)).ToArray());
        List<SceneSprite> flash = All(Build(w), "mhp-flash");
        Assert.Equal(new[] { 255, HpGauge.FlashAlpha(50) }, flash.Select(s => s.TintAlpha).ToArray());
    }

    [Fact]
    public void DeathHidesTheWindowOnlyAfterOneSecondAndWhenTheHistoryIsDone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 短血条:50 像素,缩短跨度 250 毫秒;1 秒的等待是瓶颈
        MonsterActor m = Mob(assets, 50, Panel(MonsterKind.Normal, 50));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        w.Update(0);
        m.ApplyHit(50, false, 0, true);                                  // 打死
        w.Update(10);
        Assert.True(w.Dead);
        Assert.True(w.Visible);
        Assert.Equal(0, w.Value);
        // 死后头像上叠 X:第 0、2、4 … 个 250 毫秒周期画,奇数周期不画
        Assert.Single(All(Build(w), "mhp-dead"));
        w.Update(10 + 249);
        Assert.Single(All(Build(w), "mhp-dead"));
        w.Update(10 + 250);
        Assert.Empty(All(Build(w), "mhp-dead"));
        w.Update(10 + 500);
        Assert.Single(All(Build(w), "mhp-dead"));
        // 历史在白闪 500 毫秒 + 缩短 250 毫秒(50 像素 × 5 毫秒)之后播完,但窗口要等到死后满 1000 毫秒
        w.Update(10 + 749);
        Assert.Equal(1, w.HistoryCount);
        w.Update(10 + 750);
        Assert.Equal(0, w.HistoryCount);
        Assert.True(w.Visible);
        w.Update(10 + 999);
        Assert.True(w.Visible);
        w.Update(10 + 1000);
        Assert.False(w.Visible);
        Assert.Empty(Build(w));
    }

    [Fact]
    public void ALongBarKeepsTheWindowUntilTheShrinkingIsDone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 540 像素的血条:缩短跨度 2700 毫秒(每 5 毫秒 1 像素),一击打死 → 要缩 540 像素才没
        MonsterActor m = Mob(assets, 10, Panel(MonsterKind.Normal, 540));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        m.ApplyHit(10, false, 0, true);
        w.Update(0);                                                     // 死亡与掉血都记在 0 毫秒
        w.Update(1500);                                                  // 死后超过 1000 毫秒,但历史还在缩
        Assert.True(w.Visible);
        Assert.Equal(1, w.HistoryCount);
        w.Update(500 + 2699);                                            // 缩 539 像素:还剩 1 像素
        Assert.True(w.Visible);
        w.Update(500 + 2700);                                            // 缩完
        Assert.Equal(0, w.HistoryCount);
        Assert.False(w.Visible);
    }

    [Fact]
    public void SameTargetKeepsItsHistoryAndAnotherTargetResets()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor a = Mob(assets, 100, Panel(MonsterKind.Normal, 100));
        MonsterActor b = Mob(assets, 100, Panel(MonsterKind.Normal, 200));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(a, 0);
        a.ApplyHit(40, false, 0, true);
        w.Update(10);
        w.SetTarget(a, 20);                                              // 同一只:什么都不变
        Assert.Equal((60, 1, 100), (w.Value, w.HistoryCount, w.Gauge));

        b.ApplyHit(10, false, 0, true);
        w.SetTarget(b, 30);                                              // 换一只:历史清空、总长与初始值按新目标
        Assert.Same(b, w.Target);
        Assert.Equal((180, 0, 200), (w.Value, w.HistoryCount, w.Gauge));
        Assert.True(w.Visible);

        w.SetTarget(a, 40);                                              // 换回来也是重新开始:按 a 现在的血量
        Assert.Equal((60, 0), (w.Value, w.HistoryCount));
    }

    [Fact]
    public void ATargetThatAlreadyDiedShowsTheDeadMarkRightAway()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 10, Panel(MonsterKind.Normal, 100));
        m.ApplyHit(10, false, 0, true);
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 5);
        Assert.True(w.Dead);
        Assert.Equal(0, w.Value);
        Assert.Single(All(Build(w), "mhp-dead"));
        w.Update(5 + 999);
        Assert.True(w.Visible);
        w.Update(5 + 1000);                                              // 没有历史:满 1 秒就消失
        Assert.False(w.Visible);
    }

    [Fact]
    public void TheDeadTimerRestartsOnlyWhenTheDeadFlagFlips()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor a = Mob(assets, 10, Panel(MonsterKind.Normal, 50));
        MonsterActor b = Mob(assets, 10, Panel(MonsterKind.Normal, 50));
        MonsterActor c = Mob(assets, 50, Panel(MonsterKind.Normal, 50));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;

        // 第一只在 0 毫秒死;600 毫秒时换到另一只已经死了的:标志没翻转,计时接着走,1000 毫秒时窗口消失
        w.SetTarget(a, 0);
        a.ApplyHit(10, false, 0, true);
        w.Update(0);
        Assert.True(w.Dead);
        b.ApplyHit(10, false, 0, true);
        w.SetTarget(b, 600);
        w.Update(600);
        Assert.True(w.Visible);
        w.Update(999);
        Assert.True(w.Visible);
        w.Update(1000);
        Assert.False(w.Visible);

        // 换到活着的:标志翻回去(死亡叉号消失),再打死时重新计时
        w.SetTarget(c, 2000);
        Assert.False(w.Dead);
        Assert.Empty(All(Build(w), "mhp-dead"));
        c.ApplyHit(50, false, 0, true);
        w.Update(2100);
        Assert.True(w.Dead);
        Assert.Single(All(Build(w), "mhp-dead"));                          // 从 2100 起第 0 个 250 毫秒周期
        w.Update(2100 + 250);
        Assert.Empty(All(Build(w), "mhp-dead"));
        w.Update(2100 + 999);
        Assert.True(w.Visible);
        w.Update(2100 + 1000);
        Assert.False(w.Visible);
    }

    [Fact]
    public void TheBossTilesScrollOnePixelEveryFiftyMilliseconds()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(Mob(assets, 100, Panel(MonsterKind.Boss, 100)), 0);
        w.Update(0);
        Assert.Equal(new[] { (35, 38), (73, 38), (111, 24) }, All(Build(w), "mhp-bar").Select(t => (t.ScreenX, t.Frame.Width)).ToArray());
        w.Update(50);                                                    // 偏 1 像素:第一块只露出第 1 列起
        List<SceneSprite> tiles = All(Build(w), "mhp-bar");
        Assert.Equal(new[] { (35, 37), (72, 38), (110, 25) }, tiles.Select(t => (t.ScreenX, t.Frame.Width)).ToArray());
        Assert.Equal(1, LeftColumn(tiles[0]));
        w.Update(50 * 38);                                               // 偏 38 像素 = 回到起点
        Assert.Equal(35, All(Build(w), "mhp-bar")[0].ScreenX);
    }

    [Fact]
    public void BossLostSegmentsUseTheTilesAndTheTileFlashOverlay()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Mob(assets, 100, Panel(MonsterKind.Boss, 100));
        var w = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        w.SetTarget(m, 0);
        m.ApplyHit(50, false, 0, true);
        w.Update(0);                                                     // 掉了 [50, 100)
        List<SceneSprite> list = Build(w);
        // 当前的 [0, 50):两块图块 (35 起 38 宽, 73 起 12 宽);掉下去的 [50, 100) 从屏幕 x = 85 起,图块被裁成 26 与 24 宽
        Assert.Equal(new[] { (35, 38), (73, 12) }, All(list, "mhp-bar").Select(t => (t.ScreenX, t.Frame.Width)).ToArray());
        List<SceneSprite> lost = All(list, "mhp-lost");
        Assert.Equal(new[] { (85, 26), (111, 24) }, lost.Select(t => (t.ScreenX, t.Frame.Width)).ToArray());
        Assert.All(lost, t => Assert.Equal(Darkened(13), TagOf(t)));
        // 每块掉下去的图块上盖着第 18 帧的白闪
        List<SceneSprite> flash = All(list, "mhp-flash");
        Assert.Equal(lost.Count, flash.Count);
        Assert.All(flash, t => Assert.Equal((18, 255), (TagOf(t), t.TintAlpha)));
    }
}
