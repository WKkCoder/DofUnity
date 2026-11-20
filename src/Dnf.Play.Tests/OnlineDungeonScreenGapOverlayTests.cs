using Dnf.Render;
using static Dnf.Play.Tests.OnlineDungeonScreenGapKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 测试补强(区域 C):<see cref="DungeonSelectOverlay"/>(世界地图的文字系统替换、灰显遮罩与角标、提示条)
/// 与 <see cref="OnlineDungeonOverlay"/>(进出副本的提示条)。数据全是合成的:
/// 点阵字(无字体时)帧宽 = (字数*6-1)+2、帧高 = 9(1 倍),字体用 <see cref="OnlineDungeonScreenGapKit.ProbeFonts"/>(帧宽 = 各字前进之和 + 2、帧高 = 字号 + 2);
/// 卡片矩形:气泡 = 标记 - (36, 77),选中卡 = 气泡 + (-27, -20) 的 168×73,未选中卡 = 气泡处的 118×52。期望值都是手算的字面量,注释写推导。
/// </summary>
public class OnlineDungeonScreenGapOverlayTests
{
    private static DungeonEntryStatus Rejected(byte code) => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, code);

    /// <summary>三张占位卡片(取不到卡片图 → 纯色块 + 点阵字名字):标记 (160,160)、(420,220)、(620,300),等级 4、8、6。</summary>
    private static DungeonEntry[] ThreeCards()
    {
        return new[]
        {
            FallbackCardEntry("lorien", 9101, 160, 160, level: 4),
            FallbackCardEntry("vilmark", 9102, 420, 220, level: 8),
            FallbackCardEntry("arad", 9103, 620, 300, level: 6),
        };
    }

    private static DungeonSelectScreen FontScreen(ProbeFonts fonts, int index, long ageMs, Action<DungeonSelectScreen> configure = null)
    {
        var screen = new DungeonSelectScreen(ThreeCards(), index, SampleWorldScene(), _ => 3, 2) { Fonts = fonts };
        configure?.Invoke(screen);
        screen.Advance(ageMs);
        return screen;
    }

    private static (int X, int Y, int W, int H) Box(SceneSprite s) => (s.ScreenX, s.ScreenY, s.Frame.Width, s.Frame.Height);

    // ------------------------------------------------------------------ 文字系统替换(有字体)

    [Fact]
    public void WithFontsTheMapNamesLevelAndRegionAreRedrawnAsFontTextOnTheirOwnCards()
    {
        // 保护:有字体时世界地图上的文字换成字体字:每张卡片的名字落在"自己的"卡片里(选中卡字号 14,未选中卡 12,
        // 贴卡片左边 +6、在卡片里垂直居中),地区名右对齐到 797,选中卡的等级文字(字号 12)留在原来的位置。
        // 三张卡片的显示名宽度各不相同(AB / ABCD / 洛兰X),任何一张取错了卡片(别人的名字、别人的矩形)都会露馅。
        var fonts = new ProbeFonts();
        DungeonSelectScreen screen = FontScreen(fonts, 0, 2000, s =>
        {
            s.RegionDisplayName = "洛兰";
            s.EntryDisplayName = e => e.Card.DungeonId == 9101 ? "AB" : (e.Card.DungeonId == 9102 ? "ABCD" : "洛兰X");
        });
        List<SceneSprite> built = screen.Build(0);

        // 卡片名:按横坐标排序后依次是 0、1、2 号卡
        //   0 号(选中,标记 (160,160) → 气泡 (124,83) → 卡 (97,63,168,73)):"AB" = 3+3 = 6 → 宽 8,字号 14 → 高 16;
        //     x = 97+6 = 103,y = 63 + (73-16)/2 = 63+28 = 91
        //   1 号(标记 (420,220) → 气泡 (384,143) → 卡 (384,143,118,52)):"ABCD" = 12 → 宽 14,字号 12 → 高 14;x = 390,y = 143 + (52-14)/2 = 162
        //   2 号(标记 (620,300) → 气泡 (584,223) → 卡 (584,223,118,52)):"洛兰X" = 4+4+3 = 11 → 宽 13、高 14;x = 590,y = 223+19 = 242
        List<(int X, int Y, int W, int H)> names = Tagged(built, "map-card-name").Select(Box).OrderBy(b => b.X).ToList();
        Assert.Equal(new[] { (103, 91, 8, 16), (390, 162, 14, 14), (590, 242, 13, 14) }, names);
        Assert.Contains(14, fonts.Requested);
        Assert.Contains(12, fonts.Requested);
        Assert.All(Tagged(built, "map-card-name"), s => Assert.Equal(255, s.TintAlpha));

        // 选中卡的等级文字:"Lv. 004~005" 共 11 个字符,空格前进 2、其余 3 → 10*3+2 = 32 → 宽 34、高 12+2 = 14;
        // 位置不变:卡画左上角 (97,63) + (6,26) = (103,89)
        Assert.Equal((103, 89, 34, 14), Box(Assert.Single(Tagged(built, "map-level"))));

        // 地区名 "洛兰" = 4+4 = 8 → 宽 10、高 14+2 = 16;右端 797 → x = 787(>= 762,不挪到图标左边),y = 482
        Assert.Equal((787, 482, 10, 16), Box(Assert.Single(Tagged(built, "map-region-name"))));

        // 没有字体时这几处都是点阵字(字号不同、位置也不同),说明上面确实是字体字:
        // LORIEN(选中,2 倍)宽 (6*6-1)*2+2 = 72、高 16;VILMARK(1 倍)宽 7*6-1+2 = 43、高 9、y = 143+(52-9)/2 = 164;ARAD 宽 4*6-1+2 = 25、y = 223+21 = 244
        DungeonSelectScreen plain = new DungeonSelectScreen(ThreeCards(), 0, SampleWorldScene(), _ => 3, 2);
        plain.Advance(2000);
        List<SceneSprite> bitmap = plain.Build(0);
        Assert.Equal(new[] { (103, 91, 72, 16), (390, 164, 43, 9), (590, 244, 25, 9) }, Tagged(bitmap, "map-card-name").Select(Box).OrderBy(b => b.X).ToList());
    }

    [Fact]
    public void WithFontsADisabledCardGetsAFontBadgeSizedBySelectionAndCenteredInItsCard()
    {
        // 保护:有字体时被禁卡片的角标是字体字"不可进入"(不是点阵 LOCKED),红色,选中卡字号 12、未选中卡 10,在各自卡片里水平垂直居中;
        // 灰显遮罩 150 的不透明度、角标全不透明;遮罩与角标按卡片先后成对插在 map-edges 之前。
        // "不可进入" = 4 个全角字 = 16 → 宽 18;选中卡高 12+2 = 14,未选中卡高 10+2 = 12。
        var fonts = new ProbeFonts();
        DungeonEntryStatus rejected = Rejected(9);
        List<SceneSprite> built = FontScreen(fonts, 0, 2000, s => s.EntryStatus = e => e.Card.DungeonId == 9103 ? DungeonEntryStatus.Allowed : rejected).Build(0);

        int edges = built.FindIndex(s => s.AnimationPath == "map-edges");
        Assert.Equal(
            new[] { "map-card-disabled-selected", "map-card-disabled-tag", "map-card-disabled", "map-card-disabled-tag" },
            Tags(built.Skip(edges - 4).Take(4)));

        // 0 号卡(选中,卡 (97,63,168,73)):遮罩整张卡;角标 x = 97 + (168-18)/2 = 172,y = 63 + (73-14)/2 = 63+29 = 92
        SceneSprite shadeSelected = built[edges - 4];
        SceneSprite badgeSelected = built[edges - 3];
        Assert.Equal((97, 63, 168, 73, 150), (shadeSelected.ScreenX, shadeSelected.ScreenY, shadeSelected.Frame.Width, shadeSelected.Frame.Height, shadeSelected.TintAlpha));
        Assert.Equal((172, 92, 18, 14, 255), (badgeSelected.ScreenX, badgeSelected.ScreenY, badgeSelected.Frame.Width, badgeSelected.Frame.Height, badgeSelected.TintAlpha));
        Assert.Contains(0xFF7A45u, OpaqueColors(badgeSelected.Frame));

        // 1 号卡(未选中,卡 (384,143,118,52)):角标 x = 384 + (118-18)/2 = 434,y = 143 + (52-12)/2 = 163
        SceneSprite shade = built[edges - 2];
        SceneSprite badge = built[edges - 1];
        Assert.Equal((384, 143, 118, 52, 150), (shade.ScreenX, shade.ScreenY, shade.Frame.Width, shade.Frame.Height, shade.TintAlpha));
        Assert.Equal((434, 163, 18, 12, 255), (badge.ScreenX, badge.ScreenY, badge.Frame.Width, badge.Frame.Height, badge.TintAlpha));

        // 2 号卡可进入:没有遮罩;选中项被禁 → 末尾追加原因提示条(Error 语气的图标)
        Assert.Equal(2, Tagged(built, "map-card-disabled-tag").Count);
        Assert.Single(Tagged(built, "select-online-notice-icon"));
        Assert.Equal(Enumerable.Range(0, built.Count), built.Select(s => s.SortKey));
    }

    [Fact]
    public void TheDisabledCardShadeFollowsTheBalloonFadeAndIsAbsentBeforeTheCardsAppear()
    {
        // 保护:遮罩与角标的不透明度跟着卡片淡入走:开场 1300 毫秒之后 300 毫秒淡入。
        //   1450 毫秒 → 淡入 150/300 → 255*0.5 = 127.5 → 128;遮罩 = 150 * 128/255 = 75.29 → 75,角标 = 128;
        //   2000 毫秒 → 255、遮罩 150;1300 毫秒(卡片还看不见)→ 没有遮罩与角标,但选中项的原因提示条照常在。
        DungeonEntryStatus rejected = Rejected(9);
        Func<DungeonEntry, DungeonEntryStatus> secondOnly = e => e.Card.DungeonId == 9102 ? rejected : DungeonEntryStatus.Allowed;

        List<SceneSprite> half = FontScreen(new ProbeFonts(), 0, 1450, s => s.EntryStatus = secondOnly).Build(0);
        Assert.Equal(75, Assert.Single(Tagged(half, "map-card-disabled")).TintAlpha);
        Assert.Equal(128, Assert.Single(Tagged(half, "map-card-disabled-tag")).TintAlpha);

        List<SceneSprite> full = FontScreen(new ProbeFonts(), 0, 2000, s => s.EntryStatus = secondOnly).Build(0);
        Assert.Equal(150, Assert.Single(Tagged(full, "map-card-disabled")).TintAlpha);
        Assert.Equal(255, Assert.Single(Tagged(full, "map-card-disabled-tag")).TintAlpha);

        List<SceneSprite> hidden = FontScreen(new ProbeFonts(), 1, 1300, s => s.EntryStatus = secondOnly).Build(0);
        Assert.Empty(Tagged(hidden, "map-card-disabled-selected"));
        Assert.Empty(Tagged(hidden, "map-card-disabled-tag"));
        Assert.Single(Tagged(hidden, "select-online-notice-icon"));
    }

    // ------------------------------------------------------------------ 提示条:选哪个状态、何时画

    private static (List<SceneSprite> Sprites, int Order) Enhance(
        IReadOnlyList<DungeonEntry> entries,
        int selected,
        Func<DungeonEntry, DungeonEntryStatus> status,
        DungeonSelectNotice notice,
        List<SceneSprite> sprites = null,
        long ageMs = 2000)
    {
        sprites ??= new List<SceneSprite> { OverlayArt.Place(Coded(10, 10, 1), 0, 0, 0, "base") };
        int order = 40;     // 故意给一个过期的序号:函数必须按精灵数自己重置
        DungeonSelectOverlay.EnhanceWorldMap(sprites, ref order, new OverlayArt(), entries, selected, ageMs, 0, status, null, null, notice);
        return (sprites, order);
    }

    [Fact]
    public void TheReasonBarShowsTheSelectedEntrysStatusIncludingTheFirstEntryAndNeverForAnInvalidSelection()
    {
        // 保护:选中项被禁时画原因条(含选中第 0 项);没挂 EntryStatus 时什么都不画也不抛异常;
        // 选中序号越界(-1、等于数量、更大)时即使委托对 null 会回答"被禁"也不画条(根本不该拿 null 去问)。
        DungeonEntry[] entries = { PlainEntry("AAA"), PlainEntry("BBB") };

        // 选中第 0 项被服务端拒绝(码 7):原因文案 "SERVER REJECTED (7)" 共 19 个字符 → 文字帧宽 (19*6-1)+2 = 115 → 条宽 56+115 = 171,
        // x = (800-171)/2 = 314,y = 48;色条 (315,49) 4×30,图标 (330,54) 20×20,文字 (358,59);序号接在原有精灵之后 1..4,out 的序号 = 5
        (List<SceneSprite> sprites, int order) = Enhance(entries, 0, e => ReferenceEquals(e, entries[0]) ? Rejected(7) : DungeonEntryStatus.Allowed, null);
        Assert.Equal(new[] { "base", "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-icon", "select-online-notice" }, Tags(sprites));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, sprites.Select(s => s.SortKey));
        Assert.Equal(5, order);
        Assert.Equal((314, 48, 171, 32), Box(sprites[1]));
        Assert.Equal((315, 49, 4, 30), Box(sprites[2]));
        Assert.Equal((330, 54, 20, 20), Box(sprites[3]));
        Assert.Equal((358, 59, 115, 9), Box(sprites[4]));
        Assert.Equal(new ShellPaint(new OverlayArt()).Icon(ShellTone.Error, 20).Rgba, sprites[3].Frame.Rgba);

        // 选中第 1 项(可进入)而第 0 项被禁:不画条
        (List<SceneSprite> other, int otherOrder) = Enhance(entries, 1, e => ReferenceEquals(e, entries[0]) ? Rejected(7) : DungeonEntryStatus.Allowed, null);
        Assert.Equal(new[] { "base" }, Tags(other));
        Assert.Equal(1, otherOrder);

        // 没挂 EntryStatus:选中序号有效也不画、不抛异常
        (List<SceneSprite> noStatus, int noStatusOrder) = Enhance(entries, 0, null, null);
        Assert.Equal(new[] { "base" }, Tags(noStatus));
        Assert.Equal(1, noStatusOrder);

        // 选中序号越界:委托对 null 回答"被禁"也不能画条
        foreach (int selected in new[] { -1, 2, 5 })
        {
            (List<SceneSprite> bad, int badOrder) = Enhance(entries, selected, e => e == null ? Rejected(1) : DungeonEntryStatus.Allowed, null);
            Assert.Equal(new[] { "base" }, Tags(bad));
            Assert.Equal(1, badOrder);
        }

        // 条目列表为空(null)与 EntryStatus 为空委托时同样安全
        (List<SceneSprite> noEntries, int noEntriesOrder) = Enhance(null, 0, e => Rejected(1), null);
        Assert.Equal(new[] { "base" }, Tags(noEntries));
        Assert.Equal(1, noEntriesOrder);
    }

    [Fact]
    public void ANoticeWinsOverTheSelectedEntrysReasonAndColorsTheBarByItsOwnTone()
    {
        // 保护:有提示(Kind 不是 None)时条上画提示自己的文案与语气,而不是选中项的不可进入原因;
        // Kind=None 的提示视同没有 → 退回显示原因;等待语气画转圈而不是图标。
        DungeonEntry[] entries = { PlainEntry("AAA"), PlainEntry("BBB") };
        Func<DungeonEntry, DungeonEntryStatus> status = e => ReferenceEquals(e, entries[0]) ? Rejected(7) : DungeonEntryStatus.Allowed;

        // 进入中(等待):"ENTERING ONLINE DUNGEON..." 26 个字符 → 宽 (26*6-1)+2 = 157 → 条宽 213,x = 293;转圈 (309,54),文字 (337,59)
        (List<SceneSprite> entering, _) = Enhance(entries, 0, status, new DungeonSelectNotice(DungeonSelectNoticeKind.Entering));
        Assert.Equal(new[] { "base", "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-spinner", "select-online-notice" }, Tags(entering));
        Assert.Equal((293, 48, 213, 32), Box(entering[1]));
        Assert.Equal((309, 54, 20, 20), Box(entering[3]));
        Assert.Equal((337, 59, 157, 9), Box(entering[4]));

        // 超时(错误语气):"SERVER RESPONSE TIMED OUT" 25 个字符 → 宽 (25*6-1)+2 = 151 → 条宽 207,x = (800-207)/2 = 296;图标
        (List<SceneSprite> timeout, _) = Enhance(entries, 0, status, new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout));
        Assert.Equal(new[] { "base", "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-icon", "select-online-notice" }, Tags(timeout));
        Assert.Equal((296, 48, 207, 32), Box(timeout[1]));
        Assert.Equal((340, 59, 151, 9), Box(timeout[4]));

        // 信息语气的自定义文案 "OK":宽 (2*6-1)+2 = 13 → 条宽 69,x = (800-69)/2 = 365;图标是信息图标,不是错误图标
        (List<SceneSprite> info, _) = Enhance(entries, 1, status, new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "OK"));
        Assert.Equal((365, 48, 69, 32), Box(info[1]));
        var paint = new ShellPaint(new OverlayArt());
        Assert.Equal(paint.Icon(ShellTone.Info, 20).Rgba, info[3].Frame.Rgba);
        Assert.NotEqual(paint.Icon(ShellTone.Error, 20).Rgba, info[3].Frame.Rgba);

        // Kind=None 的提示:选中项被禁 → 退回显示原因(条宽 171)
        (List<SceneSprite> none, _) = Enhance(entries, 0, status, new DungeonSelectNotice(DungeonSelectNoticeKind.None));
        Assert.Equal((314, 48, 171, 32), Box(none[1]));
        Assert.Equal(paint.Icon(ShellTone.Error, 20).Rgba, none[3].Frame.Rgba);
    }

    // ------------------------------------------------------------------ 遮罩插入位置与序号

    [Fact]
    public void DisabledCardOverlaysAreInsertedBeforeTheEdgesLayerWhereverItIsAndAppendedWhenThereIsNone()
    {
        // 保护:被禁卡片的遮罩与角标插在 "map-edges" 之前(哪怕它在最前面,即下标 0),没有 "map-edges" 时追加在末尾;
        // 插入之后所有精灵的 SortKey 重排成序号;函数返回时的 order = 精灵总数。
        // 0 号卡(标记 (160,160) → 气泡 (124,83),未选中 118×52)被禁,选中 1 号:遮罩 (124,83) 118×52、不透明度 150;
        // 角标 "LOCKED"(点阵 (6*6-1)+2 = 37 宽、9 高)x = 124 + (118-37)/2 = 164,y = 83 + (52-9)/2 = 104。
        DungeonEntry[] entries = { CardEntry("a", 9101, 160, 160), CardEntry("b", 9102, 420, 220, slot: 1) };
        Func<DungeonEntry, DungeonEntryStatus> firstDisabled = e => e.Card.DungeonId == 9101 ? Rejected(3) : DungeonEntryStatus.Allowed;

        // 1) map-edges 在最前面(下标 0)
        var edgesFirst = new List<SceneSprite> { OverlayArt.Place(Coded(5, 5, 2), 0, 0, 3, "map-edges") };
        (List<SceneSprite> a, int aOrder) = Enhance(entries, 1, firstDisabled, null, edgesFirst);
        Assert.Equal(new[] { "map-card-disabled", "map-card-disabled-tag", "map-edges" }, Tags(a));
        Assert.Equal(new[] { 0, 1, 2 }, a.Select(s => s.SortKey));
        Assert.Equal(3, aOrder);
        Assert.Equal((124, 83, 118, 52, 150), (a[0].ScreenX, a[0].ScreenY, a[0].Frame.Width, a[0].Frame.Height, a[0].TintAlpha));
        Assert.Equal((164, 104, 37, 9), Box(a[1]));

        // 2) map-edges 在中间
        var middle = new List<SceneSprite>
        {
            OverlayArt.Place(Coded(5, 5, 3), 0, 0, 0, "map-first"),
            OverlayArt.Place(Coded(5, 5, 4), 0, 0, 1, "map-edges"),
            OverlayArt.Place(Coded(5, 5, 5), 0, 0, 2, "map-last"),
        };
        (List<SceneSprite> b, int bOrder) = Enhance(entries, 1, firstDisabled, null, middle);
        Assert.Equal(new[] { "map-first", "map-card-disabled", "map-card-disabled-tag", "map-edges", "map-last" }, Tags(b));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, b.Select(s => s.SortKey));
        Assert.Equal(5, bOrder);

        // 3) 没有 map-edges:追加在末尾
        var noEdges = new List<SceneSprite> { OverlayArt.Place(Coded(5, 5, 6), 0, 0, 0, "map-first"), OverlayArt.Place(Coded(5, 5, 7), 0, 0, 1, "map-last") };
        (List<SceneSprite> c, int cOrder) = Enhance(entries, 1, firstDisabled, null, noEdges);
        Assert.Equal(new[] { "map-first", "map-last", "map-card-disabled", "map-card-disabled-tag" }, Tags(c));
        Assert.Equal(new[] { 0, 1, 2, 3 }, c.Select(s => s.SortKey));
        Assert.Equal(4, cOrder);

        // 4) 同时有提示条:条的序号接在插入之后的末尾(不与前面重号)
        var withBar = new List<SceneSprite> { OverlayArt.Place(Coded(5, 5, 8), 0, 0, 0, "map-edges") };
        (List<SceneSprite> d, int dOrder) = Enhance(entries, 1, firstDisabled, new DungeonSelectNotice(DungeonSelectNoticeKind.Entering), withBar);
        Assert.Equal(
            new[] { "map-card-disabled", "map-card-disabled-tag", "map-edges", "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-spinner", "select-online-notice" },
            Tags(d));
        Assert.Equal(Enumerable.Range(0, 7), d.Select(s => s.SortKey));
        Assert.Equal(7, dOrder);
    }

    // ------------------------------------------------------------------ 进出副本的提示条

    [Fact]
    public void TheOnlineDungeonNoticeBarIsCenteredAtTheDefaultOrTownHeightWithItsOwnTags()
    {
        // 保护:进出联网副本的提示条:没有字体时点阵字;按提示语气画转圈或图标,居中 x = (800-条宽)/2,
        // 默认 y = 48、在城镇里 y = 88;标签前缀 onlinedungeon-notice;序号从 0 起;没有可显示的提示返回 null。
        var art = new OverlayArt();
        var enteringNotice = new DungeonSelectNotice(DungeonSelectNoticeKind.Entering);

        Assert.Null(OnlineDungeonOverlay.Build(null, enteringNotice, 0));
        Assert.Null(OnlineDungeonOverlay.Build(art, null, 0));
        Assert.Null(OnlineDungeonOverlay.Build(art, new DungeonSelectNotice(DungeonSelectNoticeKind.None), 0));
        Assert.Null(OnlineDungeonOverlay.Build(art, new DungeonSelectNotice(DungeonSelectNoticeKind.Info), 0));     // Info 没有自带文案,又没给自定义文案 → 空

        // 进入中(等待语气):文字帧 157×9 → 条宽 56+157 = 213,x = (800-213)/2 = 293
        List<SceneSprite> entering = OnlineDungeonOverlay.Build(art, enteringNotice, 0);
        Assert.Equal(
            new[] { "onlinedungeon-notice-bar", "onlinedungeon-notice-accent", "onlinedungeon-notice-spinner", "onlinedungeon-notice" },
            Tags(entering));
        Assert.Equal(new[] { 0, 1, 2, 3 }, entering.Select(s => s.SortKey));
        Assert.Equal((293, 48, 213, 32), Box(entering[0]));
        Assert.Equal((294, 49, 4, 30), Box(entering[1]));
        Assert.Equal((309, 54, 20, 20), Box(entering[2]));
        Assert.Equal((337, 59, 157, 9), Box(entering[3]));

        // 在城镇里下移到 y = 88:色条 89、转圈 88+6 = 94、文字 88 + (32-9)/2 = 99
        List<SceneSprite> inTown = OnlineDungeonOverlay.Build(art, enteringNotice, 0, inTown: true);
        Assert.Equal((293, 88, 213, 32), Box(inTown[0]));
        Assert.Equal((294, 89, 4, 30), Box(inTown[1]));
        Assert.Equal((309, 94, 20, 20), Box(inTown[2]));
        Assert.Equal((337, 99, 157, 9), Box(inTown[3]));

        // 被拒绝(错误语气):"ENTRY REJECTED BY SERVER" 24 个字符 → 宽 (24*6-1)+2 = 145 → 条宽 201,x = (800-201)/2 = 299;图标代替转圈
        List<SceneSprite> rejected = OnlineDungeonOverlay.Build(art, new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected), 0);
        Assert.Equal(
            new[] { "onlinedungeon-notice-bar", "onlinedungeon-notice-accent", "onlinedungeon-notice-icon", "onlinedungeon-notice" },
            Tags(rejected));
        Assert.Equal((299, 48, 201, 32), Box(rejected[0]));
        Assert.Equal((315, 54, 20, 20), Box(rejected[2]));
        Assert.Equal((343, 59, 145, 9), Box(rejected[3]));
        Assert.Equal(new ShellPaint(art).Icon(ShellTone.Error, 20).Rgba, rejected[2].Frame.Rgba);

        // 自定义文案 "OK"(信息语气):宽 13 → 条宽 69,x = 365
        List<SceneSprite> info = OnlineDungeonOverlay.Build(art, new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "OK"), 0);
        Assert.Equal((365, 48, 69, 32), Box(info[0]));
        Assert.Equal((409, 59, 13, 9), Box(info[3]));
    }
}
