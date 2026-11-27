using Dnf.Net.Frame;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// r21-e2e-fix F4:无人值守驱动 <see cref="OnlineAutoDriver.StepGame"/> 入口守卫 <c>if (Finished || flow == null) return;</c> 的补测。
/// 上一线的变异验收里,把 <c>||</c> 改成 <c>&amp;&amp;</c> 的变异体活了下来:守卫的两个条件各自"单独成立"的情形,现有用例一个也没走到——
/// 收尾之后再来的帧,和流程是 null 的帧。这里把两侧各自钉住:
/// <list type="bullet">
/// <item>已经成功收尾、已经失败收尾的驱动,之后再来任意多帧,帧里带着非 null 的流程与控制器,而且流程正停在驱动"本来会动手"的阶段
/// (选图页会确认、结算菜单会选"回城"、城镇会请求选图、没有结算的副本会改阶段名)。驱动对外可见的一切(阶段名、结果行、退出码、
/// 完成与重试计数、房间自动机、日志)一动不动,传进来的流程与结算控制器也一动不动。</item>
/// <item>还没收尾的驱动收到 null 流程(带或不带结算控制器):不抛异常,状态不变,之后的正常帧照常推进。</item>
/// </list>
/// 全部是合成数据;只经公开入口推进(<see cref="GameFlow"/> + 结算控制器 + 假传输),期望值都是手写字面量。
/// </summary>
public sealed class OnlineAutoDriverFinishedGuardGapTests
{
    // ------------------------------------------------------------------ 公共夹具

    /// <summary>流程停在副本里(不是房间自动机的会话,没有结算),结算控制器空闲。</summary>
    private static OnlineSettlementGapAKit.DirectRig NewDungeonRig()
    {
        var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("test");
        direct.Flow.Select.Confirm();
        direct.Flow.Poll();
        Assert.Equal(GameStage.Dungeon, direct.Flow.Stage);
        return direct;
    }

    /// <summary>流程停在副本里,结算推进到菜单可选(标准成功的 72、73 两步回复之后)。</summary>
    private static OnlineSettlementGapAKit.DirectRig NewSettlementMenuRig()
    {
        OnlineSettlementGapAKit.DirectRig direct = NewDungeonRig();
        direct.StartSettlement(timedOut: false);
        direct.PollAt(100, 16);
        direct.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
        direct.PollAt(120, 16);
        direct.Transport.Push(PacketClass.Cmd, 73, new byte[] { 1, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
        direct.PollAt(140, 16);
        direct.PollAt(160, 16);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);
        Assert.False(direct.Ctrl.Summary.IsTimedOut);
        Assert.Null(direct.Ctrl.CurrentNotice);
        Assert.DoesNotContain(direct.Transport.Sent, s => s.PacketId == 75);
        return direct;
    }

    /// <summary>流程停在副本选择页(只有一个可进的副本、不忙)。驱动若动手,第一帧就会确认进入。</summary>
    private static OnlineSettlementGapAKit.DirectRig NewSelectPageRig()
    {
        var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("bait");
        Assert.Equal(GameStage.DungeonSelect, direct.Flow.Stage);
        Assert.Equal(SelectOutcome.None, direct.Flow.Select.Outcome);
        return direct;
    }

    /// <summary>走完一整局的驱动:一次结算选"回城"、回到城镇、成功收尾(结果行里的毫秒是最后一帧的 350)。</summary>
    private static OnlineAutoDriver NewSucceededDriver(List<string> logs)
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementMenuRig();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 200);       // 选"回城"
        Assert.Equal(1, driver.CompletedSettlements);
        Assert.False(driver.Finished);
        direct.Flow.EnterTown("returned");
        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 350);       // 城镇里等到最终回城:成功收尾

        AssertSucceeded(driver, logs);
        return driver;
    }

    /// <summary>选图页上每个副本都进不去,驱动在选图阶段失败收尾(结果行里的毫秒是 321)。</summary>
    private static OnlineAutoDriver NewFailedDriver(List<string> logs)
    {
        using var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("test");
        direct.Flow.Select.EntryStatus = _ => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);

        driver.StepGame(direct.Flow, null, null, 16.0, 321);

        AssertFailed(driver, logs);
        return driver;
    }

    /// <summary>成功收尾的驱动对外可见的一切(每一帧之后都要原样成立)。</summary>
    private static void AssertSucceeded(OnlineAutoDriver driver, List<string> logs)
    {
        Assert.True(driver.Finished);
        Assert.True(driver.Succeeded);
        Assert.Equal(0, driver.ExitCode);
        Assert.Equal("done", driver.Stage);
        Assert.Equal("DNF_ONLINE result=ok stage=done elapsedMs=350", driver.ResultLine);
        Assert.Equal(1, driver.CompletedSettlements);
        Assert.Equal(0, driver.RetryCycles);
        Assert.Null(driver.ActiveRoomBot);
        Assert.Equal(new[] { "DNF_ONLINE result=ok stage=done elapsedMs=350" }, logs);
    }

    /// <summary>失败收尾的驱动对外可见的一切(每一帧之后都要原样成立)。</summary>
    private static void AssertFailed(OnlineAutoDriver driver, List<string> logs)
    {
        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(1, driver.ExitCode);
        Assert.Equal("select", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=select elapsedMs=321", driver.ResultLine);
        Assert.Equal(0, driver.CompletedSettlements);
        Assert.Equal(0, driver.RetryCycles);
        Assert.Null(driver.ActiveRoomBot);
        Assert.Equal(new[] { "DNF_ONLINE result=fail stage=select elapsedMs=321" }, logs);
    }

    /// <summary>收尾之后再推 5 帧(毫秒从 400 起每帧加 16),每一帧都带着非 null 的流程与结算控制器。</summary>
    private static void StepManyFramesAfterTheEnd(OnlineAutoDriver driver, OnlineSettlementGapAKit.DirectRig bait)
    {
        for (int i = 0; i < 5; i++)
        {
            driver.StepGame(bait.Flow, null, bait.Ctrl, 16.0, 400 + i * 16);
        }
    }

    private static void AssertSelectPageUntouched(OnlineSettlementGapAKit.DirectRig bait, int serialBefore)
    {
        Assert.Equal(GameStage.DungeonSelect, bait.Flow.Stage);
        Assert.Equal(serialBefore, bait.Flow.StageSerial);
        Assert.Equal(SelectOutcome.None, bait.Flow.Select.Outcome);
        Assert.Equal(0, bait.Flow.Select.Index);
        Assert.Equal(0, bait.Flow.Select.Difficulty);
        Assert.False(bait.Flow.Select.Busy);
        Assert.Null(bait.Flow.Select.Notice);
    }

    private static void AssertSettlementMenuUntouched(OnlineSettlementGapAKit.DirectRig bait, int serialBefore)
    {
        Assert.Equal(GameStage.Dungeon, bait.Flow.Stage);
        Assert.Equal(serialBefore, bait.Flow.StageSerial);
        Assert.Equal(OnlineSettlementPhase.MenuReady, bait.Ctrl.Phase);
        Assert.True(bait.Ctrl.IsChoiceEnabled(SettlementChoice.ReturnToTown));
        Assert.DoesNotContain(bait.Transport.Sent, s => s.PacketId == 75);
        Assert.Empty(bait.Completed);
    }

    private static void AssertDungeonWithoutSettlementUntouched(OnlineSettlementGapAKit.DirectRig bait, int serialBefore)
    {
        Assert.Equal(GameStage.Dungeon, bait.Flow.Stage);
        Assert.Equal(serialBefore, bait.Flow.StageSerial);
        Assert.Equal(OnlineSettlementPhase.Idle, bait.Ctrl.Phase);
        Assert.Empty(bait.Transport.Sent);
    }

    private static void AssertTownUntouched(OnlineSettlementGapAKit.DirectRig bait, int serialBefore)
    {
        Assert.Equal(GameStage.Town, bait.Flow.Stage);
        Assert.Equal(serialBefore, bait.Flow.StageSerial);
        Assert.Null(bait.Flow.Select);
    }

    // ------------------------------------------------------------------ 已成功收尾的驱动:之后的帧一概不理

    [Fact]
    public void A_driver_that_finished_with_success_leaves_a_select_page_alone()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewSucceededDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewSelectPageRig();
        int serial = bait.Flow.StageSerial;

        StepManyFramesAfterTheEnd(driver, bait);

        AssertSucceeded(driver, logs);
        AssertSelectPageUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_finished_with_success_leaves_an_open_settlement_menu_alone()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewSucceededDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewSettlementMenuRig();
        int serial = bait.Flow.StageSerial;

        StepManyFramesAfterTheEnd(driver, bait);

        AssertSucceeded(driver, logs);
        AssertSettlementMenuUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_finished_with_success_keeps_its_stage_name_while_the_flow_sits_in_a_dungeon_without_settlement()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewSucceededDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewDungeonRig();
        int serial = bait.Flow.StageSerial;

        StepManyFramesAfterTheEnd(driver, bait);

        AssertSucceeded(driver, logs);
        AssertDungeonWithoutSettlementUntouched(bait, serial);
    }

    // 成功收尾之后遇到城镇页没有可观察的差别(驱动还挂着"等最终回城"的旗,城镇分支只会把已经收尾的结果再写一遍),所以这里没有城镇页的用例;
    // 失败收尾的驱动没有那面旗,城镇分支会请求选图页,下面有对应用例。

    // ------------------------------------------------------------------ 已失败收尾的驱动:之后的帧一概不理

    [Fact]
    public void A_driver_that_finished_with_failure_leaves_a_select_page_it_could_now_confirm_alone()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewFailedDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewSelectPageRig();
        int serial = bait.Flow.StageSerial;

        StepManyFramesAfterTheEnd(driver, bait);

        AssertFailed(driver, logs);
        AssertSelectPageUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_finished_with_failure_leaves_an_open_settlement_menu_alone()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewFailedDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewSettlementMenuRig();
        int serial = bait.Flow.StageSerial;

        StepManyFramesAfterTheEnd(driver, bait);

        AssertFailed(driver, logs);
        AssertSettlementMenuUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_finished_with_failure_keeps_its_stage_name_while_the_flow_sits_in_a_dungeon_without_settlement()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewFailedDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewDungeonRig();
        int serial = bait.Flow.StageSerial;

        StepManyFramesAfterTheEnd(driver, bait);

        AssertFailed(driver, logs);
        AssertDungeonWithoutSettlementUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_finished_with_failure_does_not_ask_a_town_flow_for_the_select_page()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewFailedDriver(logs);
        using var bait = new OnlineSettlementGapAKit.DirectRig();       // 流程停在城镇
        int serial = bait.Flow.StageSerial;
        Assert.Equal(GameStage.Town, bait.Flow.Stage);

        StepManyFramesAfterTheEnd(driver, bait);

        AssertFailed(driver, logs);
        AssertTownUntouched(bait, serial);
    }

    // ------------------------------------------------------------------ 流程是 null 的帧:收尾与否都不抛、不改状态

    [Fact]
    public void A_driver_that_finished_with_success_ignores_a_null_flow_even_when_a_settlement_menu_is_open()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewSucceededDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewSettlementMenuRig();
        int serial = bait.Flow.StageSerial;

        Exception thrown = Record.Exception(() => driver.StepGame(null, null, bait.Ctrl, 16.0, 500));

        Assert.Null(thrown);
        AssertSucceeded(driver, logs);
        AssertSettlementMenuUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_finished_with_failure_ignores_a_null_flow_even_when_a_settlement_menu_is_open()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewFailedDriver(logs);
        using OnlineSettlementGapAKit.DirectRig bait = NewSettlementMenuRig();
        int serial = bait.Flow.StageSerial;

        Exception thrown = Record.Exception(() => driver.StepGame(null, null, bait.Ctrl, 16.0, 500));

        Assert.Null(thrown);
        AssertFailed(driver, logs);
        AssertSettlementMenuUntouched(bait, serial);
    }

    [Fact]
    public void A_driver_that_has_not_started_ignores_a_null_flow_without_throwing_and_stays_unchanged()
    {
        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);

        Exception thrown = Record.Exception(() => driver.StepGame(null, null, null, 16.0, 100));

        Assert.Null(thrown);
        Assert.False(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(0, driver.ExitCode);
        Assert.Equal("init", driver.Stage);
        Assert.Null(driver.ResultLine);
        Assert.Equal(0, driver.CompletedSettlements);
        Assert.Equal(0, driver.RetryCycles);
        Assert.Null(driver.ActiveRoomBot);
        Assert.Empty(logs);
    }

    [Fact]
    public void A_driver_that_has_not_finished_ignores_a_null_flow_even_when_a_settlement_menu_is_open()
    {
        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        using OnlineSettlementGapAKit.DirectRig bait = NewSettlementMenuRig();
        int serial = bait.Flow.StageSerial;

        Exception thrown = Record.Exception(() => driver.StepGame(null, null, bait.Ctrl, 16.0, 100));

        Assert.Null(thrown);
        Assert.False(driver.Finished);
        Assert.Equal("init", driver.Stage);
        Assert.Null(driver.ResultLine);
        Assert.Equal(0, driver.CompletedSettlements);
        Assert.Empty(logs);
        AssertSettlementMenuUntouched(bait, serial);
    }

    [Fact]
    public void Null_flow_frames_neither_end_the_run_nor_spoil_the_frames_around_them()
    {
        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        using OnlineSettlementGapAKit.DirectRig page = NewSelectPageRig();

        for (int i = 0; i < 3; i++)
        {
            driver.StepGame(null, null, null, 16.0, 100 + i * 16);
        }
        Assert.Equal("init", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Equal(SelectOutcome.None, page.Flow.Select.Outcome);

        driver.StepGame(page.Flow, null, null, 16.0, 200);                 // 第一个真帧:选图页上确认
        Assert.Equal("select", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Equal(SelectOutcome.Enter, page.Flow.Select.Outcome);

        for (int i = 0; i < 3; i++)
        {
            driver.StepGame(null, null, null, 16.0, 300 + i * 16);
        }
        Assert.Equal("select", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Null(driver.ResultLine);
        Assert.Empty(logs);
        Assert.Equal(GameStage.DungeonSelect, page.Flow.Stage);
        Assert.Equal(SelectOutcome.Enter, page.Flow.Select.Outcome);
    }
}
