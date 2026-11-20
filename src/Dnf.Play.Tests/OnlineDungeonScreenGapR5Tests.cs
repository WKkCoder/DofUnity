using Dnf.Render;
using static Dnf.Play.Tests.OnlineDungeonScreenGapKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 测试补强(区域 C,第五轮):<see cref="DungeonSelectScreen"/> 里第一轮没有钉住的两处——
/// 鼠标点击换选中项时过期提示被清掉(<see cref="DungeonSelectScreen.Click"/> 的换选分支),
/// 以及选择行名字刚好 380 宽时既不缩字号也不截断(字体字限宽的边界);
/// 另外钉住世界地图增强入口在缺精灵列表或缺素材时的空操作(额外探索时发现没人钉)。
/// 复用 <see cref="OnlineDungeonScreenGapKit"/>(internal,同一个测试程序集):合成世界地图、行号编码色块、随字号变化的探针字体 ProbeFonts。
/// 期望值都是手算的字面量,注释写推导;不改全进程共用字体集。
/// </summary>
public class OnlineDungeonScreenGapR5Tests
{
    private static readonly DungeonEntry A = PlainEntry("AAA");
    private static readonly DungeonEntry B = PlainEntry("BBB");
    private static readonly DungeonEntry C = PlainEntry("CCC");

    private static DungeonEntryStatus Rejected(byte code) => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, code);

    private static (int X, int Y, int W, int H) Box(SceneSprite s) => (s.ScreenX, s.ScreenY, s.Frame.Width, s.Frame.Height);

    // ------------------------------------------------------------------ 鼠标点击换选中项

    /// <summary>每张卡的难度上限:0 号(9101)到 3,1 号(9102)只到 1,用来看点击换选时难度有没有被夹。</summary>
    private static int LimitOf(DungeonEntry entry) => entry.Card.DungeonId == 9101 ? 3 : 1;

    /// <summary>
    /// 世界地图界面:两张卡片(有卡片图),起始选中 0 号、难度 3。卡片矩形:气泡 = 标记 - (36, 77);选中卡 = 气泡 + (-27, -20) 的 168×73;未选中卡 = 气泡处的 118×52。
    ///   0 号 标记 (160,160) → 气泡 (124,83) → 选中卡 (97,63,168,73)、未选中卡 (124,83,118,52);
    ///   1 号 标记 (420,220) → 气泡 (384,143) → 选中卡 (357,123,168,73)、未选中卡 (384,143,118,52)。
    /// </summary>
    private static DungeonSelectScreen ClickScreen(Action<DungeonSelectScreen> configure = null)
    {
        DungeonEntry first = CardEntry("lorien", 9101, 160, 160, slot: 0, level: 4);
        DungeonEntry second = CardEntry("vilmark", 9102, 420, 220, slot: 1, level: 8);
        var screen = new DungeonSelectScreen(new[] { first, second }, 0, SampleWorldScene(), LimitOf, 3);
        configure?.Invoke(screen);
        return screen;
    }

    [Fact]
    public void ClickingAnotherCardMovesTheSelectionClampsTheDifficultyAndClearsTheStaleNotice()
    {
        // 保护:鼠标点没选中的卡片 = 换选中项:选中项换成被点的那张、难度夹到新选中项的上限(这里 3 → 1)、过期提示清掉(与键盘 Move 一致);
        // 点在空处、点快速组队/深渊派对按钮(没有反应但算点中)不动选中项也不碰提示;点已选中的卡 = 确认,同样不碰提示;
        // 有了结果之后一律不再响应。
        var notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);
        DungeonSelectScreen screen = ClickScreen(s => s.Notice = notice);
        Assert.Equal((0, 3), (screen.Index, screen.Difficulty));
        Assert.Equal(3, screen.MaxDifficulty);

        // 没有换选中项的点击:提示原样留着
        Assert.False(screen.Click(5, 5));                           // 空处
        Assert.Same(notice, screen.Notice);
        Assert.True(screen.Click(750, 500));                        // 快速组队(单机没有)
        Assert.True(screen.Click(650, 550));                        // 深渊派对(灰的)
        Assert.Equal((0, 3, SelectOutcome.None), (screen.Index, screen.Difficulty, screen.Outcome));
        Assert.Same(notice, screen.Notice);

        // 点 1 号的未选中小卡 (384,143,118,52) 里的 (400,160):选中换到 1 号,难度被夹到它的上限 1,提示清掉
        Assert.True(screen.Click(400, 160));
        Assert.Equal((1, 1, SelectOutcome.None), (screen.Index, screen.Difficulty, screen.Outcome));
        Assert.Equal(1, screen.MaxDifficulty);
        Assert.Null(screen.Notice);

        // 再换回 0 号(此时 0 号是小卡 (124,83,118,52),点 (150,100)):难度不会自动升回 3,提示照样清掉
        screen.Notice = notice;
        Assert.True(screen.Click(150, 100));
        Assert.Equal((0, 1), (screen.Index, screen.Difficulty));
        Assert.Null(screen.Notice);

        // 点已选中的 0 号(大卡 (97,63,168,73) 里的 (150,100)):等于确认进入,提示不动
        screen.Notice = notice;
        Assert.True(screen.Click(150, 100));
        Assert.Equal((0, SelectOutcome.Enter), (screen.Index, screen.Outcome));
        Assert.Same(notice, screen.Notice);

        // 有了结果:点到 1 号小卡也不再响应,选中项与提示都不动
        Assert.False(screen.Click(400, 160));
        Assert.Equal(0, screen.Index);
        Assert.Same(notice, screen.Notice);
    }

    [Fact]
    public void ClickingADisabledCardStillSelectsItAndShowsItsReasonInsteadOfTheStaleNoticeWhileABusyScreenIgnoresTheClick()
    {
        // 保护:点击换选中不看可进入状态——被禁的卡片也能选中,随后顶部条从"过期提示"换成该卡自己的不可进入原因;
        // 忙等(正在请求进入)时点击被整个忽略,选中项、难度、提示都不动。
        // 条宽推导(无字体、点阵 1 倍):"ENTRY REJECTED BY SERVER" 24 个字 → 文字帧宽 (24*6-1)+2 = 145 → 条宽 56+145 = 201,x = (800-201)/2 = 299;
        //                              "SERVER REJECTED (4)" 19 个字 → 宽 (19*6-1)+2 = 115 → 条宽 171,x = (800-171)/2 = 314;y 都是 48、高 32。
        var stale = new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected);
        DungeonEntryStatus reason = Rejected(4);
        DungeonSelectScreen screen = ClickScreen(s =>
        {
            s.EntryStatus = e => e.Card.DungeonId == 9102 ? reason : DungeonEntryStatus.Allowed;
            s.Notice = stale;
        });
        Assert.False(screen.IsSelectedDisabled);
        Assert.Equal((299, 48, 201, 32), Box(Assert.Single(Tagged(screen.Build(0), "select-online-notice-bar"))));

        screen.Busy = true;
        Assert.False(screen.Click(400, 160));
        Assert.Equal((0, 3), (screen.Index, screen.Difficulty));
        Assert.Same(stale, screen.Notice);
        Assert.Equal((299, 48, 201, 32), Box(Assert.Single(Tagged(screen.Build(0), "select-online-notice-bar"))));

        screen.Busy = false;
        Assert.True(screen.Click(400, 160));
        Assert.Equal((1, 1, SelectOutcome.None), (screen.Index, screen.Difficulty, screen.Outcome));
        Assert.True(screen.IsSelectedDisabled);
        Assert.Null(screen.Notice);
        Assert.Equal((314, 48, 171, 32), Box(Assert.Single(Tagged(screen.Build(0), "select-online-notice-bar"))));

        // 已选中的被禁卡片再点一下 = 确认:因为被禁所以不进入(Outcome 仍是 None),但这一下算点中了界面
        Assert.True(screen.Click(400, 160));
        Assert.Equal((1, SelectOutcome.None), (screen.Index, screen.Outcome));
    }

    // ------------------------------------------------------------------ 字体字行名的限宽边界

    [Fact]
    public void ARowNameExactlyAsWideAsTheThreeHundredEightyLimitFitsWhileOnePixelMoreIsShrunkAndClipped()
    {
        // 保护:字体字行名的限宽是"超过 380 才处理":刚好 380 宽原样排出(字号 22、不缩、不截断,帧 382×24),
        // 379 同样放得下,381 才缩到字号下限 18 并从头截断补省略号(帧 381×20);限宽若被改小一像素,刚好 380 的名字就会被缩小并截断。
        // ProbeFonts 的前进:窄字 3、全角字 4(省略号也是全角,前进 4);帧宽 = 前进之和 + 2、帧高 = 字号 + 2。
        //   第 0 行 124 个 'A' + "洛兰" = 124*3 + 4 + 4 = 380 → 帧 (380+2)×(22+2) = 382×24;
        //   第 1 行 123 个 'A' + "洛兰洛" = 369 + 12 = 381 > 380:字号 22→18 仍超,尾部保留:"…" + 后面的字,
        //           start=1 → 378+4 = 382 > 380,start=2 → 375+4 = 379 ≤ 380,所以 "…" + 121 个 'A' + "洛兰洛" = 4 + 363 + 12 = 379,帧 (379+2)×(18+2) = 381×20;
        //   第 2 行 125 个 'A' + "洛" = 375 + 4 = 379 ≤ 380 → 帧 381×24。
        var fonts = new ProbeFonts();
        string exact = new string('A', 124) + "洛兰";
        string over = new string('A', 123) + "洛兰洛";
        string under = new string('A', 125) + "洛";
        var screen = new DungeonSelectScreen(new[] { A, B, C }, 0)
        {
            Fonts = fonts,
            EntryDisplayName = e => ReferenceEquals(e, A) ? exact : (ReferenceEquals(e, B) ? over : under),
        };
        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(
            new[] { "select-backdrop", "select-title", "select-difficulty", "select-highlight", "select-name", "select-name", "select-name", "select-hint" },
            Tags(sprites));
        List<SceneSprite> names = Tagged(sprites, "select-name");
        Assert.Equal((48, 110, 382, 24), Box(names[0]));
        Assert.Equal((48, 150, 381, 20), Box(names[1]));
        Assert.Equal((48, 190, 381, 24), Box(names[2]));

        // 选中行黄字、其余白字(三行都是同一个字体字路径出来的)
        Assert.Contains(0xFFE23Cu, OpaqueColors(names[0].Frame));
        Assert.DoesNotContain(0xF4F4F4u, OpaqueColors(names[0].Frame));
        Assert.Contains(0xF4F4F4u, OpaqueColors(names[1].Frame));
        Assert.Contains(0xF4F4F4u, OpaqueColors(names[2].Frame));

        // 选中第 1 行:仍是同一批尺寸(宽度判定与是否选中无关),高亮条移到 (36, 150-6)
        screen.Move(1);
        List<SceneSprite> moved = screen.Build(0);
        List<SceneSprite> movedNames = Tagged(moved, "select-name");
        Assert.Equal((48, 110, 382, 24), Box(movedNames[0]));
        Assert.Equal((48, 150, 381, 20), Box(movedNames[1]));
        Assert.Equal((48, 190, 381, 24), Box(movedNames[2]));
        Assert.Equal((36, 144, 400, 34), Box(Assert.Single(Tagged(moved, "select-highlight"))));
    }

    // ------------------------------------------------------------------ 世界地图增强入口:缺精灵列表或缺素材

    [Fact]
    public void EnhancingTheWorldMapWithoutASpriteListOrWithoutArtDoesNothingAndKeepsTheGivenOrder()
    {
        // 保护:世界地图增强入口缺少精灵列表,或者缺少素材时,直接空操作:不抛异常、不改调用方传入的序号、不动已有精灵;
        // 两个条件"任缺一个"都要空操作(只缺其中一个也不能继续往下画)。最后用两者齐全的对照说明同样的其它参数在齐全时是会画东西的。
        DungeonEntry[] entries = { A, B };
        var notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "NOTICE");
        Func<DungeonEntry, DungeonEntryStatus> rejected = _ => Rejected(4);

        int order = 77;
        DungeonSelectOverlay.EnhanceWorldMap(null, ref order, new OverlayArt(), entries, 0, 2000, 0, rejected, null, null, notice);
        Assert.Equal(77, order);                                                    // 没有精灵列表:序号原样

        var sprites = new List<SceneSprite> { OverlayArt.Place(Coded(10, 10, 1), 0, 0, 0, "base") };
        DungeonSelectOverlay.EnhanceWorldMap(sprites, ref order, null, entries, 0, 2000, 0, rejected, null, null, notice);
        Assert.Equal(77, order);                                                    // 没有素材:序号原样
        Assert.Equal(new[] { "base" }, Tags(sprites));                              // 精灵列表一个都没多

        DungeonSelectOverlay.EnhanceWorldMap(sprites, ref order, new OverlayArt(), entries, 0, 2000, 0, rejected, null, null, notice);
        Assert.True(sprites.Count > 1);                                             // 对照:两者齐全才会画
        Assert.Single(Tagged(sprites, "select-online-notice-icon"));                // 提示条的图标
    }
}
