using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网选角界面的逻辑(<see cref="CharacterSelectScreen.Online"/>):方向键在名册里移动并带着窗口滚、滚动(行数夹紧、选中项不滚出窗口)、点格子(窗口里的格号 → 名册序号)、
/// 点滚动条、点按钮(创立与删除是灰的、点了提示"暂未开放")、选中不能进入的角色点开始只提示不给结果、开始之后进入等待状态不再响应、提示按时间消失。
/// 离线界面(公开的构造函数)的这几项行为不变。
/// </summary>
public class CharacterSelectOnlineScreenTests
{
    private static CharacterSelectScreen Online(int count, int index = 0) => CharSelectKit.Online(count, index);

    private static CharacterRoster RosterWith(params OnlineCharacterEntry[] entries) => OnlineCharacterRoster.From(RosterKit.List(entries));

    // ------------------------------------------------------------------ 起点

    [Fact]
    public void TheOnlineScreenStartsOnTheFirstCharacterWithItsWindowAtTheTop()
    {
        CharacterSelectScreen screen = Online(7);
        Assert.True(screen.IsOnline);
        Assert.Equal(0, screen.Index);
        Assert.Equal(0, screen.FirstRow);
        Assert.Equal("角色1", screen.Selected.Name);
        Assert.NotNull(screen.Selected.Online);
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.False(screen.Busy);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
    }

    [Fact]
    public void AnEmptyRosterSelectsNothingAndCannotStart()
    {
        CharacterSelectScreen screen = Online(0);
        Assert.Equal(-1, screen.Index);
        Assert.Null(screen.Selected);
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Start));      // 命中按钮,但没有角色可开始
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        screen.Move(1, 1);
        Assert.Equal(-1, screen.Index);
        Assert.False(screen.Scroll(1));
    }

    // ------------------------------------------------------------------ 缺省的起点(Online 不传 index)

    [Fact]
    public void WithoutAnIndexTheOnlineScreenSelectsTheFirstCharacter()
    {
        CharacterRoster roster = RosterKit.Roster(3);
        CharacterSelectScreen plain = CharacterSelectScreen.Online(roster, CharSelectKit.Scene());
        Assert.Equal(0, plain.Index);
        Assert.Same(roster[0], plain.Selected);
        Assert.Equal("角色1", plain.Selected.Name);
        Assert.Equal(0, plain.FirstRow);

        CharacterSelectScreen withFonts = CharacterSelectScreen.Online(roster, CharSelectKit.Scene(), fonts: new SizedFonts());
        Assert.Equal(0, withFonts.Index);
        Assert.Same(roster[0], withFonts.Selected);
        Assert.Equal("角色1", withFonts.Selected.Name);
    }

    [Fact]
    public void WithoutAnIndexAnEmptyOnlineRosterSelectsNothing()
    {
        CharacterSelectScreen plain = CharacterSelectScreen.Online(RosterKit.Roster(0), CharSelectKit.Scene());
        Assert.Equal(-1, plain.Index);
        Assert.Null(plain.Selected);

        CharacterSelectScreen withFonts = CharacterSelectScreen.Online(RosterKit.Roster(0), CharSelectKit.Scene(), fonts: new SizedFonts());
        Assert.Equal(-1, withFonts.Index);
        Assert.Null(withFonts.Selected);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 5, 0)]
    [InlineData(11, 11, 0)]
    [InlineData(12, 12, 1)]               // 第三行:窗口往下挪到刚好看见它
    [InlineData(17, 17, 1)]
    [InlineData(18, 18, 2)]
    [InlineData(24, 24, 3)]               // 最后一行(第 5 行):窗口停在最大的第一行
    [InlineData(99, 24, 3)]               // 越界夹进名册
    [InlineData(-4, 0, 0)]
    public void TheStartingIndexIsClampedAndTheWindowScrollsToShowIt(int index, int expectedIndex, int expectedFirstRow)
    {
        CharacterSelectScreen screen = Online(25, index);
        Assert.Equal(expectedIndex, screen.Index);
        Assert.Equal(expectedFirstRow, screen.FirstRow);
    }

    // ------------------------------------------------------------------ 移动

    [Fact]
    public void ArrowsMoveWithinTheRosterWithoutWrappingAndTheWindowFollows()
    {
        CharacterSelectScreen screen = Online(13);       // 三行:6 + 6 + 1

        screen.Move(1, 0);
        Assert.Equal(1, screen.Index);
        screen.Move(-1, 0);
        screen.Move(-1, 0);
        Assert.Equal(0, screen.Index);                   // 不循环
        screen.Move(0, -1);
        Assert.Equal(0, screen.Index);

        screen.Move(0, 1);
        Assert.Equal((6, 0), (screen.Index, screen.FirstRow));
        screen.Move(0, 1);
        Assert.Equal((12, 1), (screen.Index, screen.FirstRow));   // 第三行出现,窗口往下挪一行
        screen.Move(0, 1);
        Assert.Equal((12, 1), (screen.Index, screen.FirstRow));   // 没有第四行
        screen.Move(1, 0);
        Assert.Equal(12, screen.Index);                           // 最后一个右边没有角色
        screen.Move(0, -1);
        screen.Move(0, -1);
        Assert.Equal((0, 0), (screen.Index, screen.FirstRow));    // 回到最上,窗口跟着回去
    }

    [Fact]
    public void MovingDownIntoACellWithoutACharacterDoesNothing()
    {
        CharacterSelectScreen screen = Online(13, 7);    // 第二行第二格;下面一行只有第一格有角色
        screen.Move(0, 1);
        Assert.Equal(7, screen.Index);
        screen.Move(-1, 0);
        screen.Move(0, 1);
        Assert.Equal(12, screen.Index);
    }

    [Fact]
    public void MovingSidewaysPastTheLastCharacterStopsOnIt()
    {
        CharacterSelectScreen screen = Online(8, 6);     // 第二行有两个角色
        screen.Move(5, 0);
        Assert.Equal(7, screen.Index);
    }

    [Fact]
    public void WithAtMostTwelveCharactersTheOnlineWindowNeverMoves()
    {
        CharacterSelectScreen screen = Online(12);
        screen.Move(0, 1);
        screen.Move(0, 1);
        Assert.Equal((6, 0), (screen.Index, screen.FirstRow));
        Assert.False(screen.Scroll(1));
        Assert.False(screen.Scroll(-1));
        Assert.Equal(0, screen.FirstRow);
    }

    // ------------------------------------------------------------------ 滚动

    [Fact]
    public void ScrollingClampsBetweenTheFirstRowAndTheLastWindow()
    {
        CharacterSelectScreen screen = Online(25);       // 五行:窗口第一行 0 到 3
        Assert.True(screen.Scroll(1));
        Assert.Equal(1, screen.FirstRow);
        Assert.True(screen.Scroll(10));
        Assert.Equal(3, screen.FirstRow);
        Assert.False(screen.Scroll(1));
        Assert.Equal(3, screen.FirstRow);
        Assert.True(screen.Scroll(-2));
        Assert.Equal(1, screen.FirstRow);
        Assert.True(screen.Scroll(-99));
        Assert.Equal(0, screen.FirstRow);
        Assert.False(screen.Scroll(-1));
        Assert.False(screen.Scroll(0));
    }

    [Fact]
    public void ScrollingPullsASelectionThatLeavesTheWindowToTheNearestRowOfTheSameColumn()
    {
        CharacterSelectScreen screen = Online(25, 2);    // 第 0 行第 2 列
        screen.Scroll(2);                                // 窗口 2、3:选中项在上面看不见了
        Assert.Equal(2, screen.FirstRow);
        Assert.Equal(14, screen.Index);                  // 窗口里最上一行(第 2 行)的同一列

        screen = Online(25, 22);                         // 第 3 行第 4 列;窗口 2、3
        Assert.Equal(2, screen.FirstRow);
        screen.Scroll(-2);                               // 窗口 0、1:选中项在下面看不见了
        Assert.Equal(0, screen.FirstRow);
        Assert.Equal(10, screen.Index);                  // 窗口里最下一行(第 1 行)的同一列
    }

    [Fact]
    public void ScrollingKeepsASelectionThatIsStillVisible()
    {
        CharacterSelectScreen screen = Online(25, 8);    // 第 1 行
        Assert.True(screen.Scroll(1));                   // 窗口 1、2:第 1 行还看得见
        Assert.Equal(8, screen.Index);
    }

    // 14 个角色:三行 6、6、2 个;窗口 2 行,窗口第一行只能是 0 或 1。选中 0 号是进界面默认的状态,最常见的路径
    private static CharacterSelectScreen Fourteen(int index = 0)
    {
        return CharacterSelectScreen.Online(RosterKit.Roster(14), RosterKit.EmptyScene(), index, new SizedFonts());
    }

    [Fact]
    public void ScrollingDownFromTheFirstCharacterPullsTheSelectionToTheNearestRowOfItsColumn()
    {
        CharacterSelectScreen screen = Fourteen();                  // 进界面默认选中的就是 0 号
        Assert.Equal((0, 0), (screen.Index, screen.FirstRow));

        Assert.True(screen.Scroll(1));                              // 窗口第 1、2 行:0 号滚出了窗口上沿
        Assert.Equal(1, screen.FirstRow);
        Assert.Equal(6, screen.Index);                              // 同一列(第 0 列)、窗口里最近的一行(第 1 行)
        Assert.Equal("角色7", screen.Selected.Name);

        Assert.True(screen.Scroll(-1));                             // 窗口回到第 0、1 行:6 号在第 1 行,还看得见,不动
        Assert.Equal(0, screen.FirstRow);
        Assert.Equal(6, screen.Index);
    }

    [Fact]
    public void ScrollingDownKeepsTheColumnOfTheSelectionWhateverItIs()
    {
        CharacterSelectScreen screen = Fourteen(5);                 // 第 0 行最右一列
        Assert.True(screen.Scroll(1));
        Assert.Equal(1, screen.FirstRow);
        Assert.Equal(11, screen.Index);                             // 第 1 行最右一列
    }

    [Fact]
    public void ScrollingUpPullsASelectionBelowTheWindowToTheNearestRowOfItsColumn()
    {
        CharacterSelectScreen screen = Fourteen(12);                // 第 2 行第 0 列:窗口自动滚到第 1、2 行
        Assert.Equal((12, 1), (screen.Index, screen.FirstRow));

        Assert.True(screen.Scroll(-1));                             // 窗口第 0、1 行:12 号滚出了窗口下沿
        Assert.Equal(0, screen.FirstRow);
        Assert.Equal(6, screen.Index);                              // 同一列、窗口里最近的一行(第 1 行)

        screen = Fourteen(13);                                      // 第 2 行第 1 列(名册最后一个)
        Assert.True(screen.Scroll(-1));
        Assert.Equal(0, screen.FirstRow);
        Assert.Equal(7, screen.Index);
    }

    [Fact]
    public void ScrollingLeavesASelectionThatIsStillInTheWindowWhereItIs()
    {
        CharacterSelectScreen screen = Fourteen(7);                 // 第 1 行第 1 列:窗口在第 0、1 行与第 1、2 行时都看得见
        Assert.True(screen.Scroll(1));
        Assert.Equal((1, 7), (screen.FirstRow, screen.Index));
        Assert.True(screen.Scroll(-1));
        Assert.Equal((0, 7), (screen.FirstRow, screen.Index));
    }

    [Fact]
    public void StartingAfterScrollingAwayFromTheFirstCharacterStartsTheOneOnScreenNotTheHiddenFirst()
    {
        CharacterSelectScreen screen = Fourteen();
        Assert.True(screen.Scroll(1));
        screen.Confirm();

        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.Equal(6, screen.Index);
        Assert.Same(screen.Roster[6], screen.Selected);             // 名册里下标 6 的角色,不是滚出窗口的 0 号
        Assert.Equal("角色7", screen.Selected.Name);
        Assert.Equal(6, screen.Selected.Online.Slot);               // 选角命令的槽号也是 6
    }

    [Fact]
    public void ClickingTheScrollbarArrowThenStartStartsTheCharacterOnScreen()
    {
        CharacterSelectScreen screen = Fourteen();
        Assert.True(screen.Click(CharacterSelectArt.ScrollX + 4, CharacterSelectArt.ScrollBottom + 3));    // 下箭头:窗口往下一行
        Assert.Equal(1, screen.FirstRow);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Start));
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.Equal(6, screen.Index);
        Assert.Equal("角色7", screen.Selected.Name);
    }

    [Fact]
    public void ThePartialLastRowIsReachableByScrollingAndClicking()
    {
        CharacterSelectScreen screen = Online(26);       // 五行,最后一行只有两个角色(24、25)
        Assert.True(screen.Scroll(10));
        Assert.Equal(3, screen.FirstRow);                // 窗口第 3、4 行
        Assert.True(CharSelectKit.ClickCell(screen, 6));
        Assert.Equal(24, screen.Index);
        Assert.True(CharSelectKit.ClickCell(screen, 7));
        Assert.Equal(25, screen.Index);
        Assert.True(CharSelectKit.ClickCell(screen, 8));  // 名册第 27 个:没有
        Assert.Equal(25, screen.Index);
        screen.Move(1, 0);
        Assert.Equal(25, screen.Index);                  // 右边没有角色了
        screen.Move(0, -1);
        Assert.Equal(19, screen.Index);
        Assert.Equal(3, screen.FirstRow);
    }

    // ------------------------------------------------------------------ 点格子

    [Fact]
    public void ClickingACellSelectsTheCharacterInThatWindowPosition()
    {
        CharacterSelectScreen screen = Online(13);
        Assert.True(CharSelectKit.ClickCell(screen, 4));
        Assert.Equal(4, screen.Index);
        Assert.True(CharSelectKit.ClickCell(screen, 9));
        Assert.Equal(9, screen.Index);

        screen.Scroll(1);                                // 窗口第 1、2 行:第 0 格是名册第 6 个
        Assert.True(CharSelectKit.ClickCell(screen, 0));
        Assert.Equal(6, screen.Index);
        Assert.True(CharSelectKit.ClickCell(screen, 5));
        Assert.Equal(11, screen.Index);
        Assert.True(CharSelectKit.ClickCell(screen, 6));
        Assert.Equal(12, screen.Index);                  // 窗口第二行第一格是名册第 13 个
    }

    [Fact]
    public void ClickingACellWithoutACharacterHitsTheFrameButChangesNothing()
    {
        CharacterSelectScreen screen = Online(13);
        screen.Scroll(1);
        CharSelectKit.ClickCell(screen, 0);
        Assert.Equal(6, screen.Index);
        Assert.True(CharSelectKit.ClickCell(screen, 7));    // 窗口第二行第二格:名册第 14 个,没有
        Assert.Equal(6, screen.Index);
    }

    [Fact]
    public void ClickingOutsideEverythingHitsNothing()
    {
        CharacterSelectScreen screen = Online(3);
        Assert.False(screen.Click(5, 5));
        Assert.False(screen.Click(400, 520));                 // 按钮之间
        Assert.False(screen.Click(799, 599));
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
    }

    [Fact]
    public void ThePlaceholderScreenWithoutArtIgnoresClicks()
    {
        CharacterSelectScreen screen = CharacterSelectScreen.Online(RosterKit.Roster(3), null, 0, new SizedFonts());
        Assert.False(CharSelectKit.ClickCell(screen, 1));
        Assert.Equal(0, screen.Index);
    }

    // ------------------------------------------------------------------ 点滚动条

    [Fact]
    public void ScrollbarArrowsScrollOneRowAndTheTrackScrollsOnePage()
    {
        CharacterSelectScreen screen = Online(36);          // 六行:窗口第一行 0 到 4
        int x = CharacterSelectArt.ScrollX + 4;

        Assert.True(screen.Click(x, CharacterSelectArt.ScrollBottom + 3));     // 下箭头
        Assert.Equal(1, screen.FirstRow);
        Assert.True(screen.Click(x, CharacterSelectArt.ScrollBottom + 3));
        Assert.Equal(2, screen.FirstRow);
        Assert.True(screen.Click(x, CharacterSelectArt.ScrollTop + 3));        // 上箭头
        Assert.Equal(1, screen.FirstRow);

        int top = CharacterSelectArt.ThumbTop(screen.FirstRow, screen.Roster.TotalRows);
        int height = CharacterSelectArt.ThumbHeightFor(screen.Roster.TotalRows);
        Assert.True(screen.Click(x, top + height + 5));                        // 滑块下方的轨道:往下一屏(两行)
        Assert.Equal(3, screen.FirstRow);
        top = CharacterSelectArt.ThumbTop(screen.FirstRow, screen.Roster.TotalRows);
        Assert.True(screen.Click(x, top - 5));                                 // 滑块上方的轨道:往上一屏
        Assert.Equal(1, screen.FirstRow);

        top = CharacterSelectArt.ThumbTop(screen.FirstRow, screen.Roster.TotalRows);
        Assert.True(screen.Click(x, top + 3));                                 // 点滑块本身:命中但不动
        Assert.Equal(1, screen.FirstRow);
    }

    [Fact]
    public void ScrollbarClicksOutsideTheBarAreNotScrollbarClicks()
    {
        CharacterSelectScreen screen = Online(25);
        int y = CharacterSelectArt.ScrollBottom + 3;
        Assert.False(screen.Click(CharacterSelectArt.ScrollX - 1, y));
        Assert.False(screen.Click(CharacterSelectArt.ScrollX + CharacterSelectArt.ScrollBarWidth, y));
        Assert.False(screen.Click(CharacterSelectArt.ScrollX + 2, CharacterSelectArt.ScrollTop - 1));
        Assert.False(screen.Click(CharacterSelectArt.ScrollX + 2, CharacterSelectArt.ScrollBottom + CharacterSelectArt.ScrollArrowHeight));
        Assert.Equal(0, screen.FirstRow);
    }

    [Fact]
    public void ScrollbarClicksDoNothingWhenThereIsNothingToScroll()
    {
        CharacterSelectScreen screen = Online(5);
        Assert.True(screen.Click(CharacterSelectArt.ScrollX + 4, CharacterSelectArt.ScrollBottom + 3));
        Assert.Equal(0, screen.FirstRow);
    }

    // ------------------------------------------------------------------ 按钮

    [Fact]
    public void TheStartButtonStartsTheSelectedEnterableCharacter()
    {
        CharacterSelectScreen screen = Online(3, 1);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Start));
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.Equal(1, screen.Index);
        Assert.True(screen.Busy);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
    }

    [Fact]
    public void TheQuitButtonQuits()
    {
        CharacterSelectScreen screen = Online(3);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Quit));
        Assert.Equal(CharacterSelectOutcome.Quit, screen.Outcome);
        Assert.False(screen.Busy);
    }

    [Theory]
    [InlineData(CharacterSelectButton.Create)]
    [InlineData(CharacterSelectButton.Delete)]
    public void CreateAndDeleteAreGreyAndOnlyPromptThatTheyAreNotAvailable(CharacterSelectButton button)
    {
        CharacterSelectScreen screen = Online(3);
        Assert.True(CharSelectKit.ClickButton(screen, button));
        Assert.Equal(CharacterSelectPrompt.NotAvailable, screen.Prompt);
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.Equal(0, screen.Index);
    }

    [Fact]
    public void TheChannelButtonIsHitButTheScreenDoesNothingWithIt()
    {
        // 选择频道由阶段机在屏幕之前截下(OnlineShellFlow.Click);屏幕自己点它不改变任何东西
        CharacterSelectScreen screen = Online(3, 2);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Channel));
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
        Assert.Equal(2, screen.Index);
    }

    [Fact]
    public void TheOfflineScreenStillIgnoresCreateAndDelete()
    {
        var screen = new CharacterSelectScreen(CharacterRoster.Local(), CharSelectKit.Scene());
        Assert.False(screen.IsOnline);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Create));
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Delete));
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.False(screen.Scroll(1));
        Assert.False(screen.Busy);
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.False(screen.Busy);                            // 离线没有等待状态
    }

    // ------------------------------------------------------------------ 不可进入

    [Theory]
    [InlineData(1, 0, 0u, CharacterSelectPrompt.CannotEnter)]      // X1 为 1
    [InlineData(0, 8, 0u, CharacterSelectPrompt.CannotEnter)]      // 状态字节为 8
    [InlineData(0, 0, 5u, CharacterSelectPrompt.PendingDelete)]    // 待删除
    [InlineData(1, 8, 5u, CharacterSelectPrompt.PendingDelete)]    // 都有:待删除在前
    public void StartingABlockedCharacterOnlyShowsAPromptAndGivesNoOutcome(int x1, int status, uint d2, CharacterSelectPrompt prompt)
    {
        CharacterSelectScreen screen = CharSelectKit.OnlineOf(RosterWith(
            RosterKit.Entry("可以", uid: 900),
            RosterKit.Entry("不行", x1: (byte)x1, status: (byte)status, d2: d2, uid: 901)), index: 1);

        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.False(screen.Busy);
        Assert.Equal(prompt, screen.Prompt);

        screen = CharSelectKit.OnlineOf(RosterWith(RosterKit.Entry("可以"), RosterKit.Entry("不行", x1: (byte)x1, status: (byte)status, d2: d2)), index: 1);
        Assert.True(CharSelectKit.ClickButton(screen, CharacterSelectButton.Start));              // 点开始也一样
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
        Assert.Equal(prompt, screen.Prompt);
    }

    [Fact]
    public void ABlockedCharacterCanStillBeSelectedAndAnotherOneStarted()
    {
        CharacterSelectScreen screen = CharSelectKit.OnlineOf(RosterWith(RosterKit.Entry("甲"), RosterKit.Entry("乙", x1: 1), RosterKit.Entry("丙")));
        screen.Move(1, 0);
        Assert.Equal(1, screen.Index);                         // 能选中,只是不能开始
        screen.Confirm();
        Assert.Equal(CharacterSelectPrompt.CannotEnter, screen.Prompt);
        screen.Move(1, 0);
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.Equal(2, screen.Index);
    }

    [Fact]
    public void ThePromptLastsExactlyPromptMsAndAdvancingBeyondItClearsIt()
    {
        CharacterSelectScreen screen = Online(2);
        CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        Assert.Equal(2400, CharacterSelectScreen.PromptMs);
        screen.Advance(CharacterSelectScreen.PromptMs - 1);
        Assert.Equal(CharacterSelectPrompt.NotAvailable, screen.Prompt);
        screen.Advance(1);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
        screen.Advance(5000);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
    }

    [Fact]
    public void ANewPromptReplacesTheOldOneAndRestartsTheClock()
    {
        CharacterSelectScreen screen = CharSelectKit.OnlineOf(RosterWith(RosterKit.Entry("甲", x1: 1), RosterKit.Entry("乙", d2: 3)));
        screen.Confirm();
        Assert.Equal(CharacterSelectPrompt.CannotEnter, screen.Prompt);
        screen.Advance(2000);
        screen.Move(1, 0);
        screen.Confirm();
        Assert.Equal(CharacterSelectPrompt.PendingDelete, screen.Prompt);
        screen.Advance(2000);                                   // 距第二次只过了 2000 毫秒:还在
        Assert.Equal(CharacterSelectPrompt.PendingDelete, screen.Prompt);
        screen.Advance(500);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
    }

    [Fact]
    public void NegativeAndZeroAdvancesDoNothing()
    {
        CharacterSelectScreen screen = Online(2);
        CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        screen.Advance(-5000);
        screen.Advance(0);
        Assert.Equal(CharacterSelectPrompt.NotAvailable, screen.Prompt);
        Assert.Equal(0, screen.AgeMs);
    }

    // ------------------------------------------------------------------ 开始之后

    [Fact]
    public void AfterStartingTheScreenIsBusyAndIgnoresEveryInput()
    {
        CharacterSelectScreen screen = Online(13, 1);
        screen.Confirm();
        Assert.True(screen.Busy);

        screen.Move(1, 0);
        screen.Move(0, 1);
        screen.Back();
        screen.Confirm();
        Assert.False(screen.Scroll(1));
        Assert.False(CharSelectKit.ClickCell(screen, 4));
        Assert.False(CharSelectKit.ClickButton(screen, CharacterSelectButton.Quit));
        Assert.False(CharSelectKit.ClickButton(screen, CharacterSelectButton.Create));

        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.Equal(1, screen.Index);
        Assert.Equal(0, screen.FirstRow);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
    }

    [Fact]
    public void AfterQuittingNothingStartsAnymore()
    {
        CharacterSelectScreen screen = Online(3);
        screen.Back();
        Assert.Equal(CharacterSelectOutcome.Quit, screen.Outcome);
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.Quit, screen.Outcome);
        Assert.False(screen.Busy);
    }

    [Fact]
    public void TheBusyStateStaysThroughTimePassing()
    {
        CharacterSelectScreen screen = Online(2);
        screen.Confirm();
        screen.Advance(60000);
        Assert.True(screen.Busy);
        Assert.Equal(60000, screen.AgeMs);
    }
}
