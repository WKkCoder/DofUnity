using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算画面左侧的战果文字(<see cref="OnlineSettlementOverlay.Build"/> 里标签 <c>online-settle-reward</c> 的精灵)的版式:
/// 起点(没有评分字母时从 <see cref="OnlineSettlementOverlay.ResultTop"/> 起,画出了字母则从 <see cref="ResultArt.TextTop"/> 起)、
/// 每一行都要画(含第一行)、14 号字(有字体时读帧高,无字体时读点阵放大倍数)、行距、末行换色。
/// 有字体用"随字号与字符变化"的合成字体 <see cref="OnlineSettlementGapCKit.ProbeFonts"/>:字号 14 的一行字,帧高 = 14 + 2 = 16;
/// 无字体用点阵字库:放大 2 倍的一行字,帧高 = 7 × 2 + 2 = 16,所以两条路径行距都是 16 + 5 = 21。
/// </summary>
public sealed class OnlineSettlementGapCResultPanelTests
{
    private const string RewardTag = "online-settle-reward";

    private static OnlineSettlementRenderState State(OnlineSettlementSummary summary, ISpriteSource sprites = null)
    {
        return new OnlineSettlementRenderState
        {
            Summary = summary,
            AgeMs = 0,                                    // 没有压暗层:精灵序号从版面本身开始
            IsChoiceEnabled = SettlementArt.IsEnabled,
            Sprites = sprites,
        };
    }

    // 保护的行为:战果行区域的几个版式常量(起点 y、左边距)是固定的数值。
    [Fact]
    public void ResultAreaConstantsAreFixed()
    {
        Assert.Equal(24, OnlineSettlementOverlay.ResultTop);
        Assert.Equal(24, OnlineSettlementOverlay.ResultLeft);
        Assert.Equal(220, ResultArt.TextTop);
    }

    // 保护的行为:有字体、没有评分字母时,战果行一行不落地画出(第一行是通关用时),第一行的 y 恰为 ResultTop,
    // 每行 14 号字(帧高 16)、行距 5,x 恰为 ResultLeft,末行换成浅灰蓝色,其余是白色。
    [Fact]
    public void WithFontsEveryResultLineIsDrawnFromResultTopAtFourteenPixelsAndTheLastLineChangesColor()
    {
        var fonts = new OnlineSettlementGapCKit.ProbeFonts();
        var art = new OverlayArt { Fonts = fonts };
        OnlineSettlementSummary summary = OnlineSettlementGapCKit.SmallSummary();

        List<string> lines = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: true, rankLetterDrawn: false);
        Assert.Equal(new[] { "通关用时 01:05", "通关基础经验 +1800", "掉落物品: 无", "提示: 物品/金币暂未同步至本地" }, lines);

        List<SceneSprite> hud = OnlineSettlementOverlay.Build(art, State(summary), 0);
        List<SceneSprite> rewards = OnlineSettlementGapCKit.Tagged(hud, RewardTag);

        // 4 行,一行不少;第一个精灵就是第一行(通关用时)
        Assert.Equal(4, rewards.Count);
        // y:起点 24,每行高 14 + 2 = 16 再加行距 5 → 24、45、66、87;x = ResultLeft = 24
        Assert.Equal(new[] { 24, 45, 66, 87 }, rewards.Select(s => s.ScreenY));
        Assert.All(rewards, s => Assert.Equal(24, s.ScreenX));
        // 帧高:字号 14 → 14 + 2 = 16(若用了别的字号,这里会变)
        Assert.All(rewards, s => Assert.Equal(16, s.Frame.Height));
        // 帧宽读出文字内容(宽 = 各字前进之和 + 2;全角 4、数字 2 + d、冒号与斜线与加号 3、空格 2):
        // "通关用时 01:05" = 16 + 2 + (2 + 3 + 3 + 2 + 7) + 2 = 37;"通关基础经验 +1800" = 24 + 2 + 3 + (3 + 10 + 2 + 2) + 2 = 48;
        // "掉落物品: 无" = 16 + 3 + 2 + 4 + 2 = 27;"提示: 物品/金币暂未同步至本地" = 8 + 3 + 2 + 8 + 3 + 36 + 2 = 62
        Assert.Equal(new[] { 37, 48, 27, 62 }, rewards.Select(s => s.Frame.Width));
        // 颜色:前三行白色,末行 0xB8C6DA
        for (int i = 0; i < 3; i++)
        {
            Assert.Contains(OverlayArt.White, OnlineSettlementGapCKit.OpaqueColors(rewards[i].Frame));
            Assert.DoesNotContain(0xB8C6DAu, OnlineSettlementGapCKit.OpaqueColors(rewards[i].Frame));
        }
        Assert.Contains(0xB8C6DAu, OnlineSettlementGapCKit.OpaqueColors(rewards[3].Frame));
        Assert.DoesNotContain(OverlayArt.White, OnlineSettlementGapCKit.OpaqueColors(rewards[3].Frame));
        // 战果行排在序号里是连续的(接在灰显神秘商店之后),没有压暗层时序号从 0 起
        Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
    }

    // 保护的行为:无字体(点阵字库)时同样一行不落地,且战果行用 2 倍放大的点阵字(= 14 号字对应的倍数),
    // 每行的帧就是"同样文字、2 倍、对应颜色"的点阵帧(帧按内容缓存,同参数同对象)。
    [Fact]
    public void WithoutFontsEveryResultLineIsDrawnFromResultTopWithTwiceScaledBlockText()
    {
        var art = new OverlayArt();
        Assert.False(art.HasFonts);
        OnlineSettlementSummary summary = OnlineSettlementGapCKit.SmallSummary();

        List<string> lines = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: false, rankLetterDrawn: false);
        Assert.Equal(new[] { "CLEAR TIME 01:05", "CLEAR EXP +1800", "DROPS: NONE", "NOTE: ITEMS/GOLD UNSYNCED" }, lines);

        List<SceneSprite> hud = OnlineSettlementOverlay.Build(art, State(summary), 0);
        List<SceneSprite> rewards = OnlineSettlementGapCKit.Tagged(hud, RewardTag);

        Assert.Equal(4, rewards.Count);
        // 2 倍点阵:帧高 = 7 × 2 + 2 = 16,行距 5 → y 与有字体时一样是 24、45、66、87
        Assert.Equal(new[] { 24, 45, 66, 87 }, rewards.Select(s => s.ScreenY));
        Assert.All(rewards, s => Assert.Equal(24, s.ScreenX));
        Assert.All(rewards, s => Assert.Equal(16, s.Frame.Height));
        // 帧宽 = (字数 × 6 − 1) × 2 + 2:16 个字 → 192;15 个字 → 180;11 个字 → 132;25 个字 → 300
        Assert.Equal(new[] { 192, 180, 132, 300 }, rewards.Select(s => s.Frame.Width));
        for (int i = 0; i < 4; i++)
        {
            uint color = i == 3 ? 0xB8C6DAu : OverlayArt.White;
            Assert.Same(art.Text(lines[i], 2, color), rewards[i].Frame);
        }
        Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
    }

    // 保护的行为:画出了本地评分字母时,字母放在左侧(帧偏移加在 x = 24、y = 20 上),
    // 战果文字改从字母下方的 ResultArt.TextTop(220)起,且不再重复输出文字版的"评分"行;每一行仍都要画。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawnRankLetterPushesTheResultTextDownToTextTopAndDropsTheRankLine(bool hasFonts)
    {
        OverlayArt art = hasFonts ? new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() } : new OverlayArt();
        OnlineSettlementGapCKit.FakeSprites sprites = OnlineSettlementGapCKit.PanelSprites(withPanel: false, withRankLetters: true);
        OnlineSettlementSummary summary = OnlineSettlementGapCKit.SmallSummary(hasRank: true, rankFrame: 2, rankLetter: "A");

        List<string> lines = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts, rankLetterDrawn: true);
        Assert.Equal(4, lines.Count);                                      // 没有 "评分/RANK" 行
        Assert.DoesNotContain(lines, l => l.StartsWith("RANK", StringComparison.Ordinal) || l.StartsWith("评分", StringComparison.Ordinal));

        List<SceneSprite> hud = OnlineSettlementOverlay.Build(art, State(summary, sprites), 0);
        SceneSprite letter = Assert.Single(hud, s => s.AnimationPath == ResultArt.Tag);
        // 评分字母:x = ResultLeft(24) + 偏移 3 = 27;y = LetterTop(20) + 偏移 5 = 25;蓝色通道 = 200 + 帧号 2
        Assert.Equal((27, 25), (letter.ScreenX, letter.ScreenY));
        Assert.Equal((byte)202, letter.Frame.Rgba[2]);

        List<SceneSprite> rewards = OnlineSettlementGapCKit.Tagged(hud, RewardTag);
        Assert.Equal(lines.Count, rewards.Count);
        // 起点 220,行距:帧高 16 + 5 = 21
        Assert.Equal(new[] { 220, 241, 262, 283 }, rewards.Select(s => s.ScreenY));
        Assert.All(rewards, s => Assert.Equal(24, s.ScreenX));
        // 字母精灵的序号紧跟在灰显神秘商店文字之后、战果文字之前;全体序号连续
        int letterIndex = hud.IndexOf(letter);
        Assert.Equal("settle-secretshop-disabled", hud[letterIndex - 1].AnimationPath);
        Assert.Equal(RewardTag, hud[letterIndex + 1].AnimationPath);
        Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
    }

    // 保护的行为:摘要说有评分、但图包里取不到这一帧字母时,字母没画出来,
    // 文字版的评分行补上(排在第一行),战果文字仍从 ResultTop(24)起,所有行都画出。
    [Theory]
    [InlineData(false, "RANK A")]
    [InlineData(true, "评分 A")]
    public void WhenTheRankLetterCannotBePlacedTheTextRankLineComesFirstFromResultTop(bool hasFonts, string firstLine)
    {
        OverlayArt art = hasFonts ? new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() } : new OverlayArt();
        OnlineSettlementSummary summary = OnlineSettlementGapCKit.SmallSummary(hasRank: true, rankFrame: 2, rankLetter: "A");
        OnlineSettlementGapCKit.FakeSprites noLetters = OnlineSettlementGapCKit.PanelSprites(withPanel: false, withRankLetters: false);

        List<string> lines = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts, rankLetterDrawn: false);
        Assert.Equal(5, lines.Count);
        Assert.Equal(firstLine, lines[0]);

        foreach (ISpriteSource sprites in new ISpriteSource[] { null, noLetters })
        {
            List<SceneSprite> hud = OnlineSettlementOverlay.Build(art, State(summary, sprites), 0);
            Assert.DoesNotContain(hud, s => s.AnimationPath == ResultArt.Tag);
            List<SceneSprite> rewards = OnlineSettlementGapCKit.Tagged(hud, RewardTag);
            Assert.Equal(5, rewards.Count);
            Assert.Equal(new[] { 24, 45, 66, 87, 108 }, rewards.Select(s => s.ScreenY));
            Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
        }
    }

    // 保护的行为:摘要缺失时战果行是空表;画笔、状态、摘要任何一个缺失时 Build 整体返回 null,不画任何东西也不抛异常。
    [Fact]
    public void BuildYieldsNothingWhenTheArtTheStateOrTheSummaryIsMissing()
    {
        Assert.Empty(OnlineSettlementOverlay.BuildResultLines(null, hasFonts: false, rankLetterDrawn: false));
        Assert.Null(OnlineSettlementOverlay.Build(new OverlayArt(), new OnlineSettlementRenderState { Summary = null }, 0));
        Assert.Null(OnlineSettlementOverlay.Build(null, State(OnlineSettlementGapCKit.BareSummary()), 0));
        Assert.Null(OnlineSettlementOverlay.Build(new OverlayArt(), null, 0));
    }
}
