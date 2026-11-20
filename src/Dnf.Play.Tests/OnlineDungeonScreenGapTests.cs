using Dnf.Render;
using static Dnf.Play.Tests.OnlineDungeonScreenGapKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 测试补强(区域 C):<see cref="DungeonSelectScreen"/> 的占位画面几何与分支条件、选择逻辑(忙等、提示清除)、世界地图模式下何时走联网叠层。
/// 数据全是合成的:点阵字是自带的 5×7(3 倍宽 = (字数*6-1)*3+2、高 = 7*3+2 = 23),字体用 <see cref="OnlineDungeonScreenGapKit.ProbeFonts"/>
/// (帧宽 = 各字前进之和 + 2、帧高 = 字号 + 2);期望值都是手算的字面量,注释写推导。不改全进程共用字体集。
/// </summary>
public class OnlineDungeonScreenGapTests
{
    private static readonly DungeonEntry A = PlainEntry("AAA");
    private static readonly DungeonEntry B = PlainEntry("BBB");
    private static readonly DungeonEntry C = PlainEntry("CCC");

    private static DungeonEntryStatus Rejected(byte code) => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, code);

    // ------------------------------------------------------------------ 占位画面:几何

    [Fact]
    public void PlaceholderScreenLaysOutEveryRowAtItsOwnOffsetWithTheHighlightBehindTheSelectedRow()
    {
        // 保护:占位画面(无字体、无联网状态)的行布局:行 y = 110 + 40 * 序号;高亮条 400×(40-6=34) 放在 (36, y-6)、底色 0x4A3A1A,
        // 排在选中行名字的前面;名字放在 (48, y),点阵字 3 倍(宽 (3*6-1)*3+2 = 53、高 7*3+2 = 23),选中行黄字 0xFFE23C、其余白字 0xF4F4F4;
        // 画的次序与 SortKey 一一对应;没有联网提示条。
        var screen = new DungeonSelectScreen(new[] { A, B, C }, 1);
        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(
            new[] { "select-backdrop", "select-title", "select-difficulty", "select-name", "select-highlight", "select-name", "select-name", "select-hint" },
            Tags(sprites));
        Assert.Equal(Enumerable.Range(0, 8), sprites.Select(s => s.SortKey));
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));

        // 固定部分:底 (0,0) 800×600、标题 (40,30)(14 个字:(14*6-1)*3+2 = 251 宽)、难度 (40,70)、提示 (40, 600-40 = 560)
        Assert.Equal((0, 0, 800, 600), (sprites[0].ScreenX, sprites[0].ScreenY, sprites[0].Frame.Width, sprites[0].Frame.Height));
        Assert.Equal((40, 30, 251, 23), (sprites[1].ScreenX, sprites[1].ScreenY, sprites[1].Frame.Width, sprites[1].Frame.Height));
        Assert.Equal((40, 70), (sprites[2].ScreenX, sprites[2].ScreenY));
        Assert.Equal((40, 560), (sprites[7].ScreenX, sprites[7].ScreenY));

        // 三行名字:y = 110 / 150 / 190,x = 48,都是 53×23
        SceneSprite name0 = sprites[3], highlight = sprites[4], name1 = sprites[5], name2 = sprites[6];
        Assert.Equal((48, 110, 53, 23), (name0.ScreenX, name0.ScreenY, name0.Frame.Width, name0.Frame.Height));
        Assert.Equal((48, 150, 53, 23), (name1.ScreenX, name1.ScreenY, name1.Frame.Width, name1.Frame.Height));
        Assert.Equal((48, 190, 53, 23), (name2.ScreenX, name2.ScreenY, name2.Frame.Width, name2.Frame.Height));

        // 高亮条:选中第 1 行(y = 150)→ 左上角 (36, 144)、尺寸 400×34、单一底色 0x4A3A1A
        Assert.Equal((36, 144, 400, 34), (highlight.ScreenX, highlight.ScreenY, highlight.Frame.Width, highlight.Frame.Height));
        Assert.Equal(0x4A3A1Au, Assert.Single(OpaqueColors(highlight.Frame)));

        // 填色:选中行黄、其余白(与独立点阵字逐字节一致,并且只含自己那种填色)
        var art = new OverlayArt();
        Assert.Equal(art.Text("AAA", 3, OverlayArt.White).Rgba, name0.Frame.Rgba);
        Assert.Equal(art.Text("BBB", 3, OverlayArt.Yellow).Rgba, name1.Frame.Rgba);
        Assert.Equal(art.Text("CCC", 3, OverlayArt.White).Rgba, name2.Frame.Rgba);
        Assert.Contains(0xFFE23Cu, OpaqueColors(name1.Frame));
        Assert.DoesNotContain(0xF4F4F4u, OpaqueColors(name1.Frame));
        Assert.Contains(0xF4F4F4u, OpaqueColors(name0.Frame));
        Assert.DoesNotContain(0xFFE23Cu, OpaqueColors(name0.Frame));
        Assert.DoesNotContain(0xFFE23Cu, OpaqueColors(name2.Frame));

        // 选中项换到第 0 行:高亮条到 (36, 110-6 = 104),并排到第 0 行名字前面
        screen.Move(-1);
        List<SceneSprite> first = screen.Build(0);
        Assert.Equal(
            new[] { "select-backdrop", "select-title", "select-difficulty", "select-highlight", "select-name", "select-name", "select-name", "select-hint" },
            Tags(first));
        Assert.Equal((36, 104, 400, 34), (first[3].ScreenX, first[3].ScreenY, first[3].Frame.Width, first[3].Frame.Height));
        Assert.Equal((48, 110), (first[4].ScreenX, first[4].ScreenY));
        Assert.Contains(0xFFE23Cu, OpaqueColors(first[4].Frame));

        // 换到第 2 行:高亮条在 (36, 190-6 = 184),排在第 1 行名字之后、第 2 行名字之前
        screen.Move(2);
        List<SceneSprite> last = screen.Build(0);
        Assert.Equal(
            new[] { "select-backdrop", "select-title", "select-difficulty", "select-name", "select-name", "select-highlight", "select-name", "select-hint" },
            Tags(last));
        Assert.Equal((36, 184, 400, 34), (last[5].ScreenX, last[5].ScreenY, last[5].Frame.Width, last[5].Frame.Height));
        Assert.Equal((48, 190), (last[6].ScreenX, last[6].ScreenY));
    }

    [Fact]
    public void DisabledRowsAreGrayWithADisabledLabelAndTheSelectedOnesReasonBarWhenAnEntryStatusIsSet()
    {
        // 保护:只要挂了 EntryStatus 就走联网分支(即使没有字体、没有提示):被禁的行灰字 0x7A7E86、标签是"名字 (DISABLED)"、
        // 被禁的选中行高亮条换成 0x2E2E34;可进入的行仍是原样标签;选中项被禁时底部多一条 Error 语气的原因提示条,居中在 y = 48。
        DungeonEntryStatus rejected = Rejected(7);
        var screen = new DungeonSelectScreen(new[] { A, B, C }, 1)
        {
            EntryStatus = e => ReferenceEquals(e, B) ? rejected : DungeonEntryStatus.Allowed,
        };
        List<SceneSprite> sprites = screen.Build(1000);

        Assert.Equal(
            new[]
            {
                "select-backdrop", "select-title", "select-difficulty", "select-name", "select-highlight", "select-name", "select-name", "select-hint",
                "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-icon", "select-online-notice",
            },
            Tags(sprites));
        Assert.Equal(Enumerable.Range(0, 12), sprites.Select(s => s.SortKey));

        var art = new OverlayArt();
        SceneSprite name0 = sprites[3], highlight = sprites[4], name1 = sprites[5], name2 = sprites[6];
        // 高亮:被禁的选中行 → 0x2E2E34,位置尺寸不变
        Assert.Equal((36, 144, 400, 34), (highlight.ScreenX, highlight.ScreenY, highlight.Frame.Width, highlight.Frame.Height));
        Assert.Equal(0x2E2E34u, Assert.Single(OpaqueColors(highlight.Frame)));
        // 被禁行:"BBB (DISABLED)" 14 个字 → 宽 (14*6-1)*3+2 = 251、灰字
        Assert.Equal((48, 150, 251, 23), (name1.ScreenX, name1.ScreenY, name1.Frame.Width, name1.Frame.Height));
        Assert.Equal(art.Text("BBB (DISABLED)", 3, 0x7A7E86u).Rgba, name1.Frame.Rgba);
        // 可进入行:原样标签(宽 53),白字
        Assert.Equal((53, 23), (name0.Frame.Width, name0.Frame.Height));
        Assert.Equal(art.Text("AAA", 3, OverlayArt.White).Rgba, name0.Frame.Rgba);
        Assert.Equal(art.Text("CCC", 3, OverlayArt.White).Rgba, name2.Frame.Rgba);

        // 原因提示条:文字 "SERVER REJECTED (7)" 19 个字,点阵 1 倍 → 宽 (19*6-1)+2 = 115;条宽 56+115 = 171;x = (800-171)/2 = 314
        var paint = new ShellPaint(art);
        Assert.Equal((314, 48, 171, 32), (sprites[8].ScreenX, sprites[8].ScreenY, sprites[8].Frame.Width, sprites[8].Frame.Height));
        Assert.Equal(paint.Box(171, 32, 8, OnlineShellArt.ToneBox(ShellTone.Error)).Rgba, sprites[8].Frame.Rgba);
        Assert.Equal((315, 49, 4, 30), (sprites[9].ScreenX, sprites[9].ScreenY, sprites[9].Frame.Width, sprites[9].Frame.Height));
        Assert.Equal((330, 54, 20, 20), (sprites[10].ScreenX, sprites[10].ScreenY, sprites[10].Frame.Width, sprites[10].Frame.Height));
        Assert.Equal(paint.Icon(ShellTone.Error, 20).Rgba, sprites[10].Frame.Rgba);
        // 文字:x = 314 + 44 = 358,y = 48 + (32-9)/2 = 59
        Assert.Equal((358, 59, 115, 9), (sprites[11].ScreenX, sprites[11].ScreenY, sprites[11].Frame.Width, sprites[11].Frame.Height));

        // 选中项换到可进入的 A:没有提示条;B 仍然是灰字的被禁行,A 的高亮回到 0x4A3A1A
        screen.Move(-1);
        List<SceneSprite> onA = screen.Build(1000);
        Assert.Equal(
            new[] { "select-backdrop", "select-title", "select-difficulty", "select-highlight", "select-name", "select-name", "select-name", "select-hint" },
            Tags(onA));
        Assert.Equal(0x4A3A1Au, Assert.Single(OpaqueColors(onA[3].Frame)));
        Assert.Equal(art.Text("AAA", 3, OverlayArt.Yellow).Rgba, onA[4].Frame.Rgba);
        Assert.Equal(art.Text("BBB (DISABLED)", 3, 0x7A7E86u).Rgba, onA[5].Frame.Rgba);
    }

    [Fact]
    public void AnEntryStatusThatAllowsEverythingDrawsTheSameRowsAsTheOfflineScreen()
    {
        // 保护:挂了 EntryStatus 但全部可进入、也没有提示时,画面与离线占位画面逐精灵(标签、位置、像素)一致,不多出提示条。
        var offline = new DungeonSelectScreen(new[] { A, B, C }, 2);
        var online = new DungeonSelectScreen(new[] { A, B, C }, 2) { EntryStatus = _ => DungeonEntryStatus.Allowed };
        List<SceneSprite> expected = offline.Build(0);
        List<SceneSprite> actual = online.Build(0);

        Assert.Equal(Tags(expected), Tags(actual));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal((expected[i].ScreenX, expected[i].ScreenY, expected[i].SortKey), (actual[i].ScreenX, actual[i].ScreenY, actual[i].SortKey));
            Assert.Equal(expected[i].Frame.Rgba, actual[i].Frame.Rgba);
        }
    }

    [Fact]
    public void ANoticeAloneTurnsOnTheNoticeBarInThePlaceholderWithoutChangingTheRows()
    {
        // 保护:没有字体、没有 EntryStatus,只要有 Kind 不是 None 的提示,占位画面也画提示条(进入联网分支);
        // Kind=None 与没有提示都不画;提示条居中:文字 "ENTERING ONLINE DUNGEON..." 26 个字 → 宽 (26*6-1)+2 = 157,
        // 条宽 56+157 = 213,x = (800-213)/2 = 293;等待语气画转圈(相位 = 时间/80 % 10)而不是图标。
        var screen = new DungeonSelectScreen(new[] { A, B }, 0);
        List<SceneSprite> plain = screen.Build(160);
        string[] offlineTags = { "select-backdrop", "select-title", "select-difficulty", "select-highlight", "select-name", "select-name", "select-hint" };
        Assert.Equal(offlineTags, Tags(plain));

        screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.None);
        Assert.Equal(offlineTags, Tags(screen.Build(160)));

        screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Entering);
        List<SceneSprite> sprites = screen.Build(160);
        Assert.Equal(
            offlineTags.Concat(new[] { "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-spinner", "select-online-notice" }),
            Tags(sprites));
        Assert.Equal(Enumerable.Range(0, 11), sprites.Select(s => s.SortKey));
        for (int i = 0; i < plain.Count; i++)
        {
            // 行本身与没有提示时逐字节相同:只有提示、没有 EntryStatus,行仍按"全部可进入"画
            Assert.Equal((plain[i].ScreenX, plain[i].ScreenY), (sprites[i].ScreenX, sprites[i].ScreenY));
            Assert.Equal(plain[i].Frame.Rgba, sprites[i].Frame.Rgba);
        }

        var paint = new ShellPaint(new OverlayArt());
        Assert.Equal((293, 48, 213, 32), (sprites[7].ScreenX, sprites[7].ScreenY, sprites[7].Frame.Width, sprites[7].Frame.Height));
        Assert.Equal(paint.Box(213, 32, 8, OnlineShellArt.ToneBox(ShellTone.Wait)).Rgba, sprites[7].Frame.Rgba);
        Assert.Equal((294, 49, 4, 30), (sprites[8].ScreenX, sprites[8].ScreenY, sprites[8].Frame.Width, sprites[8].Frame.Height));
        Assert.Equal((309, 54, 20, 20), (sprites[9].ScreenX, sprites[9].ScreenY, sprites[9].Frame.Width, sprites[9].Frame.Height));
        Assert.Equal(paint.Spinner(20, 2, ShellPaint.Blue).Rgba, sprites[9].Frame.Rgba);       // 160 / 80 % 10 = 2
        Assert.Equal((337, 59, 157, 9), (sprites[10].ScreenX, sprites[10].ScreenY, sprites[10].Frame.Width, sprites[10].Frame.Height));

        // 时间推进:480 / 80 % 10 = 6,转圈换了一格
        Assert.Equal(paint.Spinner(20, 6, ShellPaint.Blue).Rgba, screen.Build(480)[9].Frame.Rgba);

        // 提示被摘掉:回到离线画面
        screen.Notice = null;
        Assert.Equal(offlineTags, Tags(screen.Build(160)));
    }

    [Fact]
    public void WithFontsAndADisplayNameResolverTheRowsAreDrawnFromFontTextAtTwentyTwoPixelsAndClippedAtThreeHundredEighty()
    {
        // 保护:有字体且挂了 EntryDisplayName 时,名字走字体(字号 22、最宽 380)而不是点阵字:
        // 帧高 = 22+2 = 24;被禁行在名字后追加 " (不可进入)";名字为空回退到条目名;
        // 宽度刚好超出 380 一像素(127 个窄字 = 127*3 = 381)就缩字号到下限 18 再从头截断补省略号(帧 381×20),
        // 而不是原样排出(那是 383×24)。没有解析器时仍是点阵字。
        var fonts = new ProbeFonts();
        string longName = new string('A', 127);
        DungeonEntryStatus disabled = new DungeonEntryStatus(DungeonEntryBlockKind.MissingLocalData);
        var screen = new DungeonSelectScreen(new[] { A, B }, 0)
        {
            Fonts = fonts,
            EntryDisplayName = e => ReferenceEquals(e, A) ? longName : "洛兰",
            EntryStatus = e => ReferenceEquals(e, B) ? disabled : DungeonEntryStatus.Allowed,
        };
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Equal(
            new[] { "select-backdrop", "select-title", "select-difficulty", "select-highlight", "select-name", "select-name", "select-hint" },
            Tags(sprites));

        SceneSprite nameA = sprites[4], nameB = sprites[5];
        // A:127 个窄字,Measure = 381 > 380 → 字号 22→18(下限 22-4),仍超 → 尾部保留:"…" + 后 125 字 = 4 + 375 = 379,帧宽 381、高 18+2 = 20
        Assert.Equal((48, 110, 381, 20), (nameA.ScreenX, nameA.ScreenY, nameA.Frame.Width, nameA.Frame.Height));
        Assert.Contains(0xFFE23Cu, OpaqueColors(nameA.Frame));
        // B:"洛兰 (不可进入)" 各字前进 4+4+2+3+4+4+4+4+3 = 32,帧宽 34、高 22+2 = 24,灰字
        Assert.Equal((48, 150, 34, 24), (nameB.ScreenX, nameB.ScreenY, nameB.Frame.Width, nameB.Frame.Height));
        Assert.Contains(0x7A7E86u, OpaqueColors(nameB.Frame));
        Assert.DoesNotContain(0xFFE23Cu, OpaqueColors(nameB.Frame));
        Assert.Contains(22, fonts.Requested);

        // 放得下的长名字:125 个窄字 + 1 个全角字 = 125*3+4 = 379 ≤ 380,不缩字号、不截断,帧 (379+2)×(22+2) = 381×24
        screen.EntryDisplayName = e => ReferenceEquals(e, A) ? new string('A', 125) + "洛" : "洛兰";
        SceneSprite fits = screen.Build(0)[4];
        Assert.Equal((379 + 2, 22 + 2), (fits.Frame.Width, fits.Frame.Height));

        // 解析器返回空串:回退到条目名 "AAA"(3 个窄字 = 9,帧宽 11);被禁行 "BBB (不可进入)" = 3*3+2+3+16+3 = 33,帧宽 35
        screen.EntryDisplayName = _ => string.Empty;
        List<SceneSprite> fallback = screen.Build(0);
        Assert.Equal((11, 24), (fallback[4].Frame.Width, fallback[4].Frame.Height));
        Assert.Equal((35, 24), (fallback[5].Frame.Width, fallback[5].Frame.Height));

        // 没有解析器(null)但有字体:走点阵字 3 倍,"AAA" = 53×23,被禁行 "BBB (DISABLED)" = 251×23
        screen.EntryDisplayName = null;
        List<SceneSprite> bitmap = screen.Build(0);
        Assert.Equal((53, 23), (bitmap[4].Frame.Width, bitmap[4].Frame.Height));
        Assert.Equal((251, 23), (bitmap[5].Frame.Width, bitmap[5].Frame.Height));
    }

    // ------------------------------------------------------------------ 选择逻辑

    [Fact]
    public void DifficultyChangesAreIgnoredWhileBusyOrAfterAnOutcomeAndStayWithinTheLimit()
    {
        // 保护:忙等(正在请求进入联网副本)期间左右键不能改难度;有结果之后同样不响应;没有副本时也不响应;难度夹在 0 到上限 3 之间。
        var screen = new DungeonSelectScreen(new[] { A, B }, 0, null, _ => 3, 1);
        Assert.Equal(1, screen.Difficulty);

        screen.Busy = true;
        screen.MoveDifficulty(1);
        Assert.Equal(1, screen.Difficulty);
        screen.MoveDifficulty(-1);
        Assert.Equal(1, screen.Difficulty);

        screen.Busy = false;
        screen.MoveDifficulty(1);
        Assert.Equal(2, screen.Difficulty);
        screen.MoveDifficulty(5);
        Assert.Equal(3, screen.Difficulty);
        screen.MoveDifficulty(-9);
        Assert.Equal(0, screen.Difficulty);
        screen.MoveDifficulty(2);
        Assert.Equal(2, screen.Difficulty);

        screen.Confirm();
        Assert.Equal(SelectOutcome.Enter, screen.Outcome);
        screen.MoveDifficulty(1);
        Assert.Equal(2, screen.Difficulty);

        var empty = new DungeonSelectScreen(new DungeonEntry[0], 0, null, _ => 3, 2);
        empty.MoveDifficulty(1);
        Assert.Equal(0, empty.Difficulty);
    }

    [Fact]
    public void MovingKeepsTheNoticeWhenTheSelectionStaysPutAndClearsItWhenTheSelectionChanges()
    {
        // 保护:提示只在选中项真的换了的时候清掉;到头夹住不动(上、下两头,以及 delta 为 0)时提示原样保留。
        var screen = new DungeonSelectScreen(new[] { A, B }, 0);
        var notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);

        screen.Notice = notice;
        screen.Move(-1);
        Assert.Equal(0, screen.Index);
        Assert.Same(notice, screen.Notice);
        screen.Move(0);
        Assert.Same(notice, screen.Notice);

        screen.Move(1);
        Assert.Equal(1, screen.Index);
        Assert.Null(screen.Notice);

        screen.Notice = notice;
        screen.Move(1);
        Assert.Equal(1, screen.Index);
        Assert.Same(notice, screen.Notice);
        screen.Move(-1);
        Assert.Equal(0, screen.Index);
        Assert.Null(screen.Notice);
    }

    [Fact]
    public void ConfirmingADisabledEntryClearsTheStaleNoticeButDoesNotEnter()
    {
        // 保护:选中项被禁时确认既不进入,也会把上一条过期提示清掉(界面随后只显示该项自己的不可进入原因);
        // 确认成功(可进入)不碰提示。
        var screen = new DungeonSelectScreen(new[] { A, B }, 1)
        {
            EntryStatus = e => ReferenceEquals(e, B) ? Rejected(5) : DungeonEntryStatus.Allowed,
        };
        screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);
        Assert.True(screen.IsSelectedDisabled);

        screen.Confirm();

        Assert.Equal(SelectOutcome.None, screen.Outcome);
        Assert.Null(screen.Notice);

        screen.Move(-1);
        screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "HELLO");
        screen.Confirm();
        Assert.Equal(SelectOutcome.Enter, screen.Outcome);
        Assert.Equal("HELLO", screen.Notice.CustomMessage);
    }

    // ------------------------------------------------------------------ 世界地图模式:何时走联网叠层

    private static DungeonSelectScreen WorldScreen(int index, Action<DungeonSelectScreen> configure = null)
    {
        DungeonEntry e1 = CardEntry("lorien", 9101, 160, 160, slot: 0, level: 4);
        DungeonEntry e2 = CardEntry("vilmark", 9102, 420, 220, slot: 1, level: 8);
        var screen = new DungeonSelectScreen(new[] { e1, e2 }, index, SampleWorldScene(), _ => 3, 2);
        configure?.Invoke(screen);
        screen.Advance(2000);       // 横幅退场之后卡片完全淡入(BalloonAlpha(2000) = 255)
        return screen;
    }

    [Fact]
    public void WorldMapWithoutFontsOverlaysDisabledCardsWhenAnEntryStatusIsSetAndNotOtherwise()
    {
        // 保护:世界地图模式下,没有字体、没有提示,只要挂了 EntryStatus 就走联网叠层:被禁的未选中卡片多出灰显遮罩(118×52、不透明度 150)
        // 与"LOCKED"角标(点阵 37×9,居中:x = 384+(118-37)/2 = 424,y = 143+(52-9)/2 = 164),都插在 map-edges 之前,SortKey 重排成序号;
        // 全部可进入时与离线画面逐标签一致。
        List<SceneSprite> baseline = WorldScreen(0).Build(500);
        Assert.DoesNotContain(baseline, s => s.AnimationPath != null && s.AnimationPath.StartsWith("map-card-disabled", StringComparison.Ordinal));

        // 全部可进入:不多不少
        List<SceneSprite> allowed = WorldScreen(0, s => s.EntryStatus = _ => DungeonEntryStatus.Allowed).Build(500);
        Assert.Equal(Tags(baseline), Tags(allowed));

        // 第二张卡片(未选中,标记 (420, 220) → 气泡 (384, 143))被禁
        DungeonEntryStatus rejected = Rejected(9);
        List<SceneSprite> built = WorldScreen(0, s => s.EntryStatus = e => e.Card.DungeonId == 9102 ? rejected : DungeonEntryStatus.Allowed).Build(500);
        Assert.Equal(baseline.Count + 2, built.Count);
        int edges = built.FindIndex(s => s.AnimationPath == "map-edges");
        SceneSprite shade = built[edges - 2];
        SceneSprite badge = built[edges - 1];
        Assert.Equal("map-card-disabled", shade.AnimationPath);
        Assert.Equal((384, 143, 118, 52, 150), (shade.ScreenX, shade.ScreenY, shade.Frame.Width, shade.Frame.Height, shade.TintAlpha));
        Assert.Equal(0x000000u, Assert.Single(OpaqueColors(shade.Frame)));
        Assert.Equal("map-card-disabled-tag", badge.AnimationPath);
        Assert.Equal((424, 164, 37, 9, 255), (badge.ScreenX, badge.ScreenY, badge.Frame.Width, badge.Frame.Height, badge.TintAlpha));
        Assert.Contains(0xFF7A45u, OpaqueColors(badge.Frame));
        Assert.Equal(Enumerable.Range(0, built.Count), built.Select(s => s.SortKey));
        // 去掉两个叠层精灵后与离线画面同一批标签、同一先后;选中项(可进入)没有原因提示条
        Assert.Equal(Tags(baseline), Tags(built.Where(s => s != shade && s != badge)));
        Assert.DoesNotContain(built, s => s.AnimationPath != null && s.AnimationPath.StartsWith("select-online-notice", StringComparison.Ordinal));
    }

    [Fact]
    public void WorldMapWithoutFontsDrawsTheNoticeBarForANoticeAloneAndNotForNoneOrNull()
    {
        // 保护:世界地图模式下,没有字体、没有 EntryStatus,只有 Kind 不是 None 的提示时也走联网叠层并画提示条
        // (宽 213、x = 293、y = 48;等待语气画转圈);Kind=None 与没有提示都与离线画面一致。
        List<SceneSprite> baseline = WorldScreen(0).Build(160);

        var none = new DungeonSelectNotice(DungeonSelectNoticeKind.None);
        List<SceneSprite> noneBuilt = WorldScreen(0, s => s.Notice = none).Build(160);
        Assert.Equal(Tags(baseline), Tags(noneBuilt));

        var entering = new DungeonSelectNotice(DungeonSelectNoticeKind.Entering);
        List<SceneSprite> built = WorldScreen(0, s => s.Notice = entering).Build(160);
        Assert.Equal(baseline.Count + 4, built.Count);
        Assert.Equal(Tags(baseline), Tags(built.Take(baseline.Count)));
        List<SceneSprite> bar = built.Skip(baseline.Count).ToList();
        Assert.Equal(new[] { "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-spinner", "select-online-notice" }, Tags(bar));
        Assert.Equal(Enumerable.Range(baseline.Count, 4), bar.Select(s => s.SortKey));
        Assert.Equal((293, 48, 213, 32), (bar[0].ScreenX, bar[0].ScreenY, bar[0].Frame.Width, bar[0].Frame.Height));
        Assert.Equal((309, 54, 20, 20), (bar[2].ScreenX, bar[2].ScreenY, bar[2].Frame.Width, bar[2].Frame.Height));
        Assert.Equal((337, 59, 157, 9), (bar[3].ScreenX, bar[3].ScreenY, bar[3].Frame.Width, bar[3].Frame.Height));
    }

    [Fact]
    public void WorldMapShadesTheSelectedDisabledCardAndAppendsTheReasonBarWithContiguousSortKeys()
    {
        // 保护:选中的卡片被禁时:灰显遮罩用选中卡片的矩形(标记 (420,220) → 气泡 (384,143) → 画 (357,123)、168×73),
        // 角标 "LOCKED" 居中(x = 357+(168-37)/2 = 422,y = 123+(73-9)/2 = 155),再追加原因提示条(Error 语气,"SERVER REJECTED (9)" 条宽 171、x = 314);
        // 叠层插入后全部 SortKey 都等于它的序号(提示条的编号接在插入之后的末尾,不与前面重号)。
        DungeonEntryStatus rejected = Rejected(9);
        DungeonSelectScreen screen = WorldScreen(1, s => s.EntryStatus = e => e.Card.DungeonId == 9102 ? rejected : DungeonEntryStatus.Allowed);
        List<SceneSprite> built = screen.Build(0);

        List<SceneSprite> baseline = WorldScreen(1).Build(0);
        Assert.Equal(baseline.Count + 2 + 4, built.Count);
        Assert.Equal(Enumerable.Range(0, built.Count), built.Select(s => s.SortKey));

        int edges = built.FindIndex(s => s.AnimationPath == "map-edges");
        SceneSprite shade = built[edges - 2];
        SceneSprite badge = built[edges - 1];
        Assert.Equal("map-card-disabled-selected", shade.AnimationPath);
        Assert.Equal((357, 123, 168, 73, 150), (shade.ScreenX, shade.ScreenY, shade.Frame.Width, shade.Frame.Height, shade.TintAlpha));
        Assert.Equal("map-card-disabled-tag", badge.AnimationPath);
        Assert.Equal((422, 155, 37, 9, 255), (badge.ScreenX, badge.ScreenY, badge.Frame.Width, badge.Frame.Height, badge.TintAlpha));

        List<SceneSprite> bar = built.Skip(built.Count - 4).ToList();
        Assert.Equal(new[] { "select-online-notice-bar", "select-online-notice-accent", "select-online-notice-icon", "select-online-notice" }, Tags(bar));
        Assert.Equal((314, 48, 171, 32), (bar[0].ScreenX, bar[0].ScreenY, bar[0].Frame.Width, bar[0].Frame.Height));
        var paint = new ShellPaint(new OverlayArt());
        Assert.Equal(paint.Icon(ShellTone.Error, 20).Rgba, bar[2].Frame.Rgba);

        // 选中第 0 张(可进入)而第 1 张被禁:遮罩换成未选中的 "map-card-disabled",没有原因提示条
        List<SceneSprite> other = WorldScreen(0, s => s.EntryStatus = e => e.Card.DungeonId == 9102 ? rejected : DungeonEntryStatus.Allowed).Build(0);
        Assert.Single(Tagged(other, "map-card-disabled"));
        Assert.Empty(Tagged(other, "map-card-disabled-selected"));
        Assert.Empty(Tagged(other, "select-online-notice"));
    }
}
