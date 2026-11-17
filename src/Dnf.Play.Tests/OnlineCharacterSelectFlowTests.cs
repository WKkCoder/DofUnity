using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳阶段机里的真名册选角(<see cref="OnlineShellOptions.CharacterSelect"/>):用假后端和合成角色列表走完整的阶段流转。
/// 选角成功(槽号是列表位置、不是 Uid;十三号以后的角色也能选)、不可进入的角色不发选角命令只弹提示、失败留在选角(位置保留)或回登录(不能再试、断线、超时)、
/// 选择频道与结束游戏、创立与删除只提示、空账号、日志不带角色名、没有选角设置时仍是外壳原来的占位名册。
/// </summary>
public class OnlineCharacterSelectFlowTests
{
    private static ShellRig NewRig(IGlyphFonts fonts = null, ICharacterTitles titles = null, bool art = true)
    {
        return new ShellRig(o =>
        {
            if (art)
            {
                o.Art = CharSelectKit.Scene();
            }
            o.Fonts = fonts ?? new SizedFonts();
            o.CharacterSelect = new OnlineCharacterSelectOptions { Titles = titles };
        });
    }

    private static void Reach(ShellRig rig, OnlineCharacterList list)
    {
        rig.Backend.OnConnect = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(list));
        rig.Fill();
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    private static List<SceneSprite> Scene(ShellRig rig) => rig.Flow.Scene.Build(0);

    private static List<SceneSprite> Tagged(ShellRig rig, string tag) => SpriteQuery.Tagged(Scene(rig), tag);

    private static SpriteFrame Line(string text, uint color = CharacterSelectArt.CaptionColor)
    {
        return new ShellPaint(new OverlayArt { Localize = false, Fonts = new SizedFonts() }).Line(text, 12, color, OverlayArt.Black, 115);
    }

    private static void Click(ShellRig rig, CharacterSelectButton button)
    {
        (int x, int y) = CharSelectKit.ButtonCenter(button);
        rig.Flow.Click(x, y);
        rig.Poll();
    }

    private static void Press(ShellRig rig, OnlineShellKey key)
    {
        rig.Flow.Key(key);
        rig.Poll();
    }

    // ------------------------------------------------------------------ 名册进界面

    [Fact]
    public void TheServerListBecomesTheRosterWithEveryCharacterAndNothingTruncated()
    {
        ShellRig rig = NewRig();
        OnlineCharacterList list = RosterKit.Many(14);
        Reach(rig, list);

        Assert.Same(list, rig.Flow.Characters);
        Assert.Equal(14, rig.Flow.Characters.Entries.Count);
        List<SceneSprite> captions = Tagged(rig, "select-char-caption");
        Assert.Equal(24, captions.Count);                                        // 窗口里 12 格
        Assert.True(ShellFrames.Same(Line("Lv1 角色1"), captions[0].Frame));
        Assert.True(ShellFrames.Same(Line("Lv12 角色12"), captions[22].Frame));

        Press(rig, OnlineShellKey.Down);
        Press(rig, OnlineShellKey.Down);                                         // 选中第 13 个:窗口往下滚一行
        captions = Tagged(rig, "select-char-caption");
        Assert.Equal(16, captions.Count);                                        // 名册第 6 到 13 个
        Assert.True(ShellFrames.Same(Line("Lv7 角色7"), captions[0].Frame));
        Assert.True(ShellFrames.Same(Line("Lv14 角色14"), captions[14].Frame));
    }

    [Fact]
    public void WithoutTheCharacterSelectOptionsTheOldPlaceholderRosterIsUnchanged()
    {
        var rig = new ShellRig(o =>
        {
            o.Art = CharSelectKit.Scene();
            o.Fonts = new SizedFonts();
        });
        rig.ReachCharacterSelect(3);
        List<SceneSprite> sprites = Scene(rig);

        Assert.Equal(6, SpriteQuery.Tagged(sprites, "select-char-caption").Count);
        Assert.Empty(SpriteQuery.Tagged(sprites, "select-char-marker"));
        Assert.Empty(SpriteQuery.Tagged(sprites, "select-char-button-disabled"));       // 外壳原来的离线画法:没有灰按钮
        Assert.Equal(4, SpriteQuery.Tagged(sprites, "select-char-button").Count);
        SpriteFrame placeholder = new OverlayArt().Text(OnlineShellRoster.Placeholder(rig.Flow.Characters)[0].Caption, 1, CharacterSelectArt.CaptionColor);
        Assert.True(ShellFrames.Same(placeholder, SpriteQuery.Tagged(sprites, "select-char-caption")[0].Frame));
    }

    [Fact]
    public void ThePlaceholderRosterStillCapsAtTwelveWhenThereAreNoOptions()
    {
        var rig = new ShellRig(o => o.Art = CharSelectKit.Scene());
        rig.ReachCharacterSelect(14);
        Assert.Equal(14, rig.Flow.Characters.Entries.Count);
        Assert.Equal(24, SpriteQuery.Tagged(Scene(rig), "select-char-caption").Count);
        Press(rig, OnlineShellKey.Down);
        Press(rig, OnlineShellKey.Down);                                          // 占位名册只有十二个:第三行没有角色,选中项不动
        Assert.Equal(24, SpriteQuery.Tagged(Scene(rig), "select-char-caption").Count);
    }

    [Fact]
    public void TheTitleSourceReachesTheClassLine()
    {
        ShellRig rig = NewRig(null, new StubTitles());
        Reach(rig, RosterKit.Many(2));
        Assert.True(ShellFrames.Same(Line("职0转0觉0"), Tagged(rig, "select-char-caption")[1].Frame));
        Assert.True(ShellFrames.Same(Line("职1转0觉0"), Tagged(rig, "select-char-caption")[3].Frame));
    }

    [Fact]
    public void WithoutArtTheOnlineRosterStillWorksOnThePlainScreen()
    {
        ShellRig rig = NewRig(null, null, art: false);
        Reach(rig, RosterKit.Many(3));
        Assert.NotEmpty(Scene(rig));                                              // 没有素材图包也画得出(占位画面,中文名画成问号,不抛)
        Press(rig, OnlineShellKey.Right);
        Press(rig, OnlineShellKey.Enter);
        Assert.Equal(1, rig.Backend.LastSlot);
    }

    // ------------------------------------------------------------------ 选角成功

    [Fact]
    public void TheSlotSentToTheServerIsTheListPositionAndNotTheUid()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.List(RosterKit.Entry("甲", uid: 905), RosterKit.Entry("乙", uid: 901), RosterKit.Entry("丙", uid: 903)));

        var pending = new TaskCompletionSource<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => pending.Task;
        Press(rig, OnlineShellKey.Right);
        Press(rig, OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.SelectCalls);
        Assert.Equal(1, rig.Backend.LastSlot);                                   // 不是 901
        Assert.Equal(1, rig.Flow.SelectedSlot);
        Assert.Equal(901, rig.Flow.SelectedCharacter.Uid);

        pending.SetResult(OnlineSelectOutcome.Ok());
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.SelectCalls);
    }

    [Fact]
    public void WhileTheSelectIsPendingTheShellShowsItsLoadingScreenAndNotTheRoster()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        var pending = new TaskCompletionSource<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => pending.Task;
        Press(rig, OnlineShellKey.Enter);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        Assert.Empty(Tagged(rig, "select-char-caption"));                        // 状态画面已经换上,选角屏的字不在了
        Assert.True(rig.Flow.Busy);

        pending.SetResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true));
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(6, Tagged(rig, "select-char-caption").Count);               // 回到选角:名册重新画出来
        Assert.False(rig.Flow.Busy);
    }

    [Fact]
    public void AThirteenthCharacterCanBeSelectedByScrollingWithTheKeys()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(14));
        Press(rig, OnlineShellKey.Down);
        Press(rig, OnlineShellKey.Down);                                         // 第 12 个(0 起)
        Press(rig, OnlineShellKey.Space);
        Assert.Equal(12, rig.Backend.LastSlot);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
    }

    [Fact]
    public void AThirteenthCharacterCanBeSelectedByTheScrollbarAndAClick()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(14));
        rig.Flow.Click(CharacterSelectArt.ScrollX + 4, CharacterSelectArt.ScrollBottom + 3);     // 下箭头:窗口往下一行
        rig.Poll();
        (int x, int y) = CharSelectKit.CellCenter(6);                                           // 窗口第二行第一格 = 名册第 13 个(第 12 号)
        rig.Flow.Click(x, y);
        rig.Poll();
        (int sx, int sy) = CharSelectKit.ButtonCenter(CharacterSelectButton.Start);
        rig.Flow.Click(sx, sy);
        rig.Poll();
        Assert.Equal(12, rig.Backend.LastSlot);
        Assert.Equal(1, rig.Backend.SelectCalls);
    }

    [Fact]
    public void TheStartButtonClickStartsTheSelectedCharacter()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        (int x, int y) = CharSelectKit.CellCenter(2);
        rig.Flow.Click(x, y);
        Click(rig, CharacterSelectButton.Start);
        Assert.Equal(2, rig.Backend.LastSlot);
        Assert.Equal(1, rig.Backend.SelectCalls);
    }

    [Fact]
    public void WhatTheNextLineGetsAfterASuccessfulSelectIsTheSelectedCharacterWithAllItsFields()
    {
        var equips = new[] { new OnlineCharacterEquip(3, 31u, 32u, 4, 33u, 34u), new OnlineCharacterEquip(5, 51u, 52u, 6, 53u, 54u) };
        ShellRig rig = NewRig(null, new StubTitles());
        Reach(rig, RosterKit.List(
            RosterKit.Entry("甲"),
            RosterKit.Entry("乙", job: 3, growTypeByte: 0x21, level: 55, uid: 902, equips: equips)));
        Assert.Null(OnlineCharacterRoster.SelectedSlotOf(rig.Flow));              // 还没选

        Press(rig, OnlineShellKey.Right);
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);

        CharacterSlot picked = OnlineCharacterRoster.SelectedSlotOf(rig.Flow, new StubTitles());
        Assert.Equal("乙", picked.Name);
        Assert.Equal(55, picked.Level);
        Assert.Equal("职3转1觉2", picked.JobName);
        Assert.Equal((1, 3, 1, 2), (picked.Online.Slot, picked.Online.Job, picked.Online.GrowType, picked.Online.Awakening));
        Assert.Equal(902, picked.Online.Entry.Uid);
        Assert.Equal(2, picked.Online.Equips.Count);
        Assert.Same(equips[1], picked.Online.Equips[1]);
        Assert.Same(rig.Flow.SelectedCharacter, picked.Online.Entry);
    }

    // ------------------------------------------------------------------ 不可进入

    [Theory]
    [InlineData(1, 0, 0u)]
    [InlineData(0, 8, 0u)]
    [InlineData(0, 0, 3u)]
    public void ABlockedCharacterIsNeverSentToTheServerAndTheScreenSaysSo(int x1, int status, uint d2)
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.List(RosterKit.Entry("甲"), RosterKit.Entry("乙", x1: (byte)x1, status: (byte)status, d2: d2)));

        Press(rig, OnlineShellKey.Right);
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.DoesNotContain("select", rig.Backend.Calls);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Single(Tagged(rig, "select-char-prompt"));
        Assert.Single(Tagged(rig, "select-char-marker"));

        rig.Flow.Advance(CharacterSelectScreen.PromptMs);
        Assert.Empty(Tagged(rig, "select-char-prompt"));

        Press(rig, OnlineShellKey.Left);                                         // 换一个能进的
        Press(rig, OnlineShellKey.Enter);
        Assert.Equal(1, rig.Backend.SelectCalls);
        Assert.Equal(0, rig.Backend.LastSlot);
    }

    [Fact]
    public void ClickingStartOnABlockedCharacterSendsNothingEither()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.List(RosterKit.Entry("甲", x1: 1)));
        Click(rig, CharacterSelectButton.Start);
        rig.Pump(2);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Single(Tagged(rig, "select-char-prompt"));
    }

    [Fact]
    public void AnAccountWhoseOnlyCharacterIsBlockedStaysOnTheScreenForever()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.List(RosterKit.Entry("甲", status: 8)));
        for (int i = 0; i < 5; i++)
        {
            Press(rig, OnlineShellKey.Enter);
            rig.Flow.Advance(CharacterSelectScreen.PromptMs);
        }
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.QuitRequested);
    }

    // ------------------------------------------------------------------ 失败

    [Fact]
    public void ARejectedSelectStaysOnTheScreenWithTheSameCharacterSelected()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(14));
        Press(rig, OnlineShellKey.Down);
        Press(rig, OnlineShellKey.Down);                                          // 第 12 号
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true));
        int serial = rig.Flow.StageSerial;
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);          // 留在选角(界面重新进入一次)
        Assert.True(rig.Flow.StageSerial > serial);
        Assert.Equal(OnlineNoticeKind.SelectFailed, rig.Flow.Notice.Kind);
        Assert.Equal(12, rig.Flow.SelectedSlot);
        Assert.Equal(14, rig.Flow.Characters.Entries.Count);                     // 名册还在
        Assert.Single(Tagged(rig, "online-select-notice"));
        List<SceneSprite> captions = Tagged(rig, "select-char-caption");
        Assert.True(ShellFrames.Same(Line("Lv7 角色7"), captions[0].Frame));     // 窗口还停在选中项那一屏
        Assert.Equal(0, rig.Backend.DisconnectCalls);                            // 连接没断

        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Ok());
        Press(rig, OnlineShellKey.Enter);                                        // 再点:还是第 12 号
        Assert.Equal(12, rig.Backend.LastSlot);
        Assert.Equal(2, rig.Backend.SelectCalls);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
    }

    [Fact]
    public void ARejectedSelectLetsThePlayerPickAnotherCharacter()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(s == 0
            ? OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true)
            : OnlineSelectOutcome.Ok());
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Press(rig, OnlineShellKey.Right);
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(1, rig.Backend.LastSlot);
        Assert.Equal(2, rig.Backend.SelectCalls);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
    }

    [Fact]
    public void ARejectionThatLeavesNoUsableConnectionGoesBackToLogin()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, false));
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Null(rig.Flow.Characters);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
        Assert.True(rig.Backend.DisconnectCalls >= 1);
    }

    [Fact]
    public void ADisconnectedOutcomeGoesBackToLoginEvenIfAnotherSlotCouldBeTried()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.Disconnected, true));
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void ASelectThatThrowsAConnectionErrorGoesBackToLogin()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        rig.Backend.OnSelect = (s, ct) => Task.FromException<OnlineSelectOutcome>(new IOException("连接被重置"));
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void ASelectThatNeverAnswersTimesOutBackToLoginAndDropsTheSession()
    {
        // 任务书写"超时留在选角界面",但阶段机骨架的既有语义是超时回登录(放弃会话),骨架不在本线可改的范围内:这里把既有语义钉住
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        var never = new TaskCompletionSource<OnlineSelectOutcome>();
        rig.Backend.OnSelect = (s, ct) => never.Task;
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        rig.Poll(59000);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        rig.Poll(1000);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.NetworkError, rig.Flow.Notice.Kind);
        Assert.True(rig.Backend.DisconnectCalls >= 1);
    }

    [Fact]
    public void ADisconnectEventOnTheSelectScreenGoesBackToLogin()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        rig.Backend.RaiseDisconnected();
        rig.Poll();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void EscapeOnTheSelectScreenLeavesTheSessionAndGoesToLogin()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        Press(rig, OnlineShellKey.Escape);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.True(rig.Backend.DisconnectCalls >= 1);
        Assert.Equal(0, rig.Backend.SelectCalls);
    }

    // ------------------------------------------------------------------ 频道、结束游戏、创立删除

    [Fact]
    public void TheChannelButtonStillGoesToChannelSelectAndIsNotGrey()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        int serial = rig.Flow.StageSerial;
        int connects = rig.Backend.ConnectCalls;
        Assert.DoesNotContain(Tagged(rig, "select-char-button-disabled"), s => s.ScreenX == CharacterSelectArt.ChannelButtonX);

        rig.Backend.OnSwitch = (t, ct) => Task.FromResult(OnlineConnectOutcome.Ok(RosterKit.Many(2)));
        Click(rig, CharacterSelectButton.Channel);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(rig.Flow.StageSerial > serial);
        rig.Pump(3);
        Assert.Equal(1, rig.Backend.SwitchCalls);                                // 已经连着:换频道,不是重连
        Assert.Equal(connects, rig.Backend.ConnectCalls);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(2, rig.Flow.Characters.Entries.Count);                      // 新频道的角色列表
        Assert.Equal(0, rig.Backend.SelectCalls);
    }

    [Fact]
    public void TheQuitButtonRequestsQuitAndTheHostDropsTheSession()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        Assert.False(rig.Flow.QuitRequested);
        Click(rig, CharacterSelectButton.Quit);
        Assert.True(rig.Flow.QuitRequested);
        Assert.Equal(0, rig.Backend.SelectCalls);

        rig.Flow.Shutdown();                                                     // 宿主据 QuitRequested 退出时做的事
        Assert.True(rig.Backend.DisconnectCalls >= 1);
    }

    [Theory]
    [InlineData(CharacterSelectButton.Create)]
    [InlineData(CharacterSelectButton.Delete)]
    public void CreateAndDeleteOnlyShowTheNotAvailablePrompt(CharacterSelectButton button)
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.Many(3));
        int callsBefore = rig.Backend.Calls.Count;
        int serial = rig.Flow.StageSerial;
        Click(rig, button);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Equal(callsBefore, rig.Backend.Calls.Count);
        Assert.Single(Tagged(rig, "select-char-prompt"));
    }

    [Fact]
    public void AnEmptyAccountShowsTheNoCharactersNoticeAndCannotStart()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.List());
        Assert.Equal(OnlineNoticeKind.NoCharacters, rig.Flow.Notice.Kind);
        Press(rig, OnlineShellKey.Enter);
        Click(rig, CharacterSelectButton.Start);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.Single(Tagged(rig, "select-char-start-disabled"));

        Click(rig, CharacterSelectButton.Create);                                // 外壳的提示在,屏幕自己的提示条不叠上去
        Assert.Empty(Tagged(rig, "select-char-prompt"));
        Assert.Single(Tagged(rig, "online-select-notice"));
    }

    // ------------------------------------------------------------------ 日志

    [Fact]
    public void TheLogNeverCarriesCharacterNamesOrIds()
    {
        ShellRig rig = NewRig();
        Reach(rig, RosterKit.List(RosterKit.Entry("独一无二的名字", uid: 912), RosterKit.Entry("乙", x1: 1, uid: 913)));
        Press(rig, OnlineShellKey.Right);
        Press(rig, OnlineShellKey.Enter);                                        // 被拦住
        Press(rig, OnlineShellKey.Left);
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true));
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);
        rig.Backend.OnSelect = (s, ct) => Task.FromResult(OnlineSelectOutcome.Ok());
        Press(rig, OnlineShellKey.Enter);
        rig.Pump(2);

        Assert.NotEmpty(rig.Lines);
        foreach (string line in rig.Lines)
        {
            Assert.StartsWith("DNF_ONLINE stage=", line);
            Assert.DoesNotContain("独一无二", line);
            Assert.DoesNotContain("乙", line);
            Assert.DoesNotContain("912", line);
            Assert.DoesNotContain("913", line);
        }
        Assert.Contains(rig.Lines, l => l.Contains("reason=select_failed"));
    }
}
