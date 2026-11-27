using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Play;
using Dnf.Play.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// r23-shell-guard-tests:无人值守驱动 <see cref="OnlineAutoDriver.StepShell"/> 入口守卫 <c>if (Finished || flow == null) return;</c> 的补测,
/// 与 <see cref="OnlineAutoDriverFinishedGuardGapTests"/> 钉住 <see cref="OnlineAutoDriver.StepGame"/> 那一句是对称的。
/// 上一线验收时,把外壳这一句改成 <c>Finished &amp;&amp; flow == null</c>、只留 <c>flow == null</c>、只留 <c>Finished</c>、整句删掉,
/// 整个 Dnf.Client.Tests 里没有一条用例变红:守卫的两个条件各自"单独成立"的情形,现有用例一个也没走到——
/// 收尾之后再来的外壳帧,和流程是 null 的帧。这里把两侧各自钉住:
/// <list type="bullet">
/// <item>已经失败收尾、已经成功收尾的驱动,之后再来任意多帧外壳,帧里带着非 null 的外壳流程,而且流程正停在驱动"本来会动手"的地方
/// (登录页会按回车;选服务器页与选频道页就绪时会按回车、没有真界面时会放行占位页、读失败或没有内容时会改阶段名;选角页会走光标或按回车;
/// 加载页会改阶段名)。驱动对外可见的一切(阶段名、结果行、退出码、成功与否、日志)与它的全部私有字段一动不动,
/// 传进来的外壳流程也一动不动:没有待办的动作、没有攒下的按键、再轮询几次也没有任何阶段变化与对假后端的调用。
/// 进入游戏、带错误提示的外壳、"收尾前进过游戏、收尾后退回登录页"这几种,守卫单独拿掉时在公开状态上看不出差别
/// (进游戏只写一个私有旗;错误提示与掉线都走 <see cref="OnlineAutoDriver.Fail"/>,它自带"已收尾就不动"的守卫),
/// 钉的是两层守卫合起来的结果,以及私有字段整体不变。</item>
/// <item>还没收尾的驱动、已经失败收尾的驱动、已经成功收尾的驱动收到 null 外壳流程:不抛异常,状态不变,之后的正常帧照常推进。</item>
/// <item>对照:没收尾的驱动对同样这些"饵"确实会动手(或收尾),所以上面"一动不动"不是饵摆得不对造成的假象。</item>
/// </list>
/// 全部是合成数据;只经公开入口推进(真的 <see cref="OnlineShellFlow"/> 加假后端与假目录),期望值都是手写字面量。
/// </summary>
public sealed class OnlineAutoDriverShellFinishedGuardGapTests
{
    // ------------------------------------------------------------------ 公共夹具:已收尾的驱动

    /// <summary>流程停在副本里,结算推进到菜单可选(标准成功的 72、73 两步回复之后)。</summary>
    private static OnlineSettlementGapAKit.DirectRig NewSettlementMenuRig()
    {
        var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("test");
        direct.Flow.Select.Confirm();
        direct.Flow.Poll();
        Assert.Equal(GameStage.Dungeon, direct.Flow.Stage);
        direct.StartSettlement(timedOut: false);
        direct.PollAt(100, 16);
        direct.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
        direct.PollAt(120, 16);
        direct.Transport.Push(PacketClass.Cmd, 73, new byte[] { 1, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
        direct.PollAt(140, 16);
        direct.PollAt(160, 16);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);
        return direct;
    }

    /// <summary>
    /// 走完一整局的驱动:一次结算选"回城"、回到城镇、成功收尾(结果行里的毫秒是最后一帧的 350)。
    /// <paramref name="characterSlot"/> 是它要选的角色槽位;<paramref name="beforeTheEnd"/> 在收尾之前对这个驱动做点事(例如先让它看一眼外壳)。
    /// 外壳层面造不出成功收尾(<c>CompleteSuccess</c> 是私有的,只有游戏帧在城镇里等到最终回城才会走到),所以借游戏帧把它走到成功。
    /// </summary>
    private static OnlineAutoDriver NewSucceededDriver(List<string> logs, int characterSlot = 0, Action<OnlineAutoDriver> beforeTheEnd = null)
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementMenuRig();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: characterSlot), logs.Add);
        beforeTheEnd?.Invoke(driver);

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 200);       // 选"回城"
        Assert.Equal(1, driver.CompletedSettlements);
        Assert.False(driver.Finished);
        direct.Flow.EnterTown("returned");
        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 350);       // 城镇里等到最终回城:成功收尾

        AssertSucceeded(driver, logs);
        return driver;
    }

    /// <summary>
    /// 直接调公开的 <see cref="OnlineAutoDriver.Fail"/> 失败收尾(结果行里的毫秒是 321)。阶段名取 <c>select</c>——
    /// 它和外壳五个阶段的名字(login、server、channel、charselect、loading)都不同,收尾之后若有帧把阶段名改写成外壳的名字,一眼就看得出。
    /// </summary>
    private static OnlineAutoDriver NewFailedDriver(List<string> logs, int characterSlot = 0, Action<OnlineAutoDriver> beforeTheEnd = null)
    {
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: characterSlot), logs.Add);
        beforeTheEnd?.Invoke(driver);

        driver.Fail("select", 321);

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

    /// <summary>驱动的全部实例字段(含私有旗与计数)的当前值。</summary>
    private static Dictionary<string, object> FieldsOf(OnlineAutoDriver driver)
    {
        var fields = new Dictionary<string, object>();
        foreach (FieldInfo field in typeof(OnlineAutoDriver).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            fields[field.Name] = field.GetValue(driver);
        }
        return fields;
    }

    /// <summary>
    /// 收尾之后驱动一动不动:不光对外可见的那几项,连"进过游戏""已提交登录""光标位置""最近看到的外壳阶段序号"这些私有旗与计数也不变
    /// (公开状态看不出来的写入,比如进入游戏页只写一个私有旗,靠它抓)。
    /// </summary>
    private static void AssertDriverFrozen(Dictionary<string, object> before, OnlineAutoDriver driver)
    {
        Assert.NotEmpty(before);
        Dictionary<string, object> after = FieldsOf(driver);
        foreach (KeyValuePair<string, object> field in before)
        {
            Assert.True(Equals(field.Value, after[field.Key]), "已收尾的驱动改了字段 " + field.Key + ":" + field.Value + " -> " + after[field.Key]);
        }
    }

    /// <summary>外壳上再推 5 帧(毫秒从 400 起每帧加 16),每一帧都是同一个非 null 的外壳流程;帧与帧之间不轮询流程,驱动留下的意图原样留在流程里等着被看见。</summary>
    private static void StepShellFrames(OnlineAutoDriver driver, OnlineShellFlow flow)
    {
        for (int i = 0; i < 5; i++)
        {
            driver.StepShell(flow, 400 + i * 16);
        }
    }

    // ------------------------------------------------------------------ 公共夹具:外壳流程的"饵"

    /// <summary>外壳流程停在哪里。每一个都是没收尾的驱动本来会动手、或会把阶段名改成外壳阶段名的地方。</summary>
    public enum BaitScene
    {
        /// <summary>登录页:账号口令填好、不忙、没有待办动作、没有提示。</summary>
        LoginPage,

        /// <summary>选服务器真界面,目录读完、选中一个能选的服务器("游戏开始"可点)。</summary>
        ServerPageReady,

        /// <summary>选服务器真界面,读目录失败(提示为空)。</summary>
        ServerPageFailed,

        /// <summary>选服务器真界面,目录里没有服务器。</summary>
        ServerPageEmpty,

        /// <summary>选服务器占位页(没注入目录、也不自动放行)。</summary>
        ServerPagePlaceholder,

        /// <summary>选频道真界面,有频道可进、不忙。</summary>
        ChannelPageReady,

        /// <summary>选频道真界面,读失败(提示为空)。</summary>
        ChannelPageFailed,

        /// <summary>选频道真界面,这个服务器没有频道。</summary>
        ChannelPageEmpty,

        /// <summary>选角页,三个角色都能进,驱动要选的是槽位 0(按一下回车)。</summary>
        CharacterPage,

        /// <summary>选角页,三个角色都能进,驱动要选的是槽位 2(先向右走两格再按回车)。</summary>
        CharacterPageFarSlot,

        /// <summary>加载页:选角请求在途(挂着不返回),外壳忙。</summary>
        LoadingPage,

        /// <summary>进入游戏之后(外壳把控制权交给宿主)。</summary>
        InGame,

        /// <summary>登录页,带一条错误提示(账号口令错),不忙。</summary>
        LoginPageWithErrorNotice,

        /// <summary>选频道占位页,带一条错误提示(连接失败),不忙。</summary>
        ChannelPageWithErrorNotice,
    }

    /// <summary>一个饵:真的外壳流程加它的假后端(与外壳测试台共用的夹具)。</summary>
    private sealed class Bait : IDisposable
    {
        private readonly TaskCompletionSource<OnlineSelectOutcome> _selectGate;

        public Bait(ShellRig rig, int driverCharacterSlot = 0, TaskCompletionSource<OnlineSelectOutcome> selectGate = null)
        {
            Rig = rig;
            DriverCharacterSlot = driverCharacterSlot;
            _selectGate = selectGate;
        }

        public ShellRig Rig { get; }

        /// <summary>驱动要选的角色槽位(只有选角页上驱动动手时才用到)。</summary>
        public int DriverCharacterSlot { get; }

        public OnlineShellFlow Flow => Rig.Flow;

        public FakeOnlineBackend Backend => Rig.Backend;

        /// <summary>外壳此刻对外可见的一行:阶段、忙不忙、提示的类别、假后端被调过的方法(按顺序)。</summary>
        public string Describe()
        {
            string notice = Flow.Notice == null ? "none" : Flow.Notice.Kind.ToString();
            return Flow.Stage + "|busy=" + Flow.Busy + "|notice=" + notice + "|calls=" + string.Join(",", Backend.Calls);
        }

        /// <summary>轮询外壳几次:驱动若在流程里留下了按键、选定这类意图,轮询会把它们落地成阶段变化或对假后端的调用。</summary>
        public void Flush()
        {
            Rig.Pump(3);
        }

        public void Dispose()
        {
            _selectGate?.TrySetResult(OnlineSelectOutcome.Ok());
        }
    }

    private static Bait NewLoginPage()
    {
        var rig = new ShellRig();
        rig.Fill();
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(LoginAction.None, rig.Flow.Login.PendingAction);
        Assert.Null(rig.Flow.Notice);
        return new Bait(rig);
    }

    /// <summary>选服务器真界面。<paramref name="phase"/> 是 Ready(能开始)、Failed(读失败、提示为空)或 Empty(目录里没有服务器)。</summary>
    private static Bait NewServerPage(PickerPhase phase)
    {
        ServerDirectory directory = phase == PickerPhase.Empty
            ? PickerData.Directory()
            : PickerData.Directory(PickerData.Server("srv_a", 3), PickerData.Server("srv_b", 2, 4));
        var picker = new PickerRig(directory);
        picker.ReachServerSelect();
        if (phase == PickerPhase.Failed)
        {
            // 读失败要不带提示:靠假目录给失败会带一条错误提示(那是下面"带错误提示"的饵),所以让已经读好的页面直接进入"读失败"
            picker.Server.SetFailed();
        }
        Assert.Equal(OnlineShellStage.ServerSelect, picker.Flow.Stage);
        Assert.Equal(phase, picker.Server.Phase);
        if (phase == PickerPhase.Ready)
        {
            Assert.True(picker.Server.CanStart);
        }
        else
        {
            Assert.False(picker.Server.CanStart);
        }
        Assert.False(picker.Flow.Busy);
        Assert.Null(picker.Flow.Notice);
        Assert.Null(picker.Server.Notice);
        return new Bait(picker.Rig);
    }

    private static Bait NewServerPlaceholderPage()
    {
        var rig = new ShellRig(options => options.AutoPassPlaceholders = false);
        rig.Fill();
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.ServerSelect, rig.Flow.Stage);
        Assert.Null(rig.Flow.ServerScreen);
        Assert.False(rig.Flow.Busy);
        Assert.Null(rig.Flow.Notice);
        return new Bait(rig);
    }

    /// <summary>选频道真界面。<paramref name="phase"/> 是 Ready(能开始)、Failed(读失败、提示为空)或 Empty(服务器没有频道)。</summary>
    private static Bait NewChannelPage(PickerPhase phase)
    {
        var picker = new PickerRig(PickerData.Directory(PickerData.Server("srv_a", 3), PickerData.Server("srv_b", 2, 4)));
        picker.ReachChannelSelect();
        if (phase == PickerPhase.Failed)
        {
            picker.Channel.SetFailed();
        }
        else if (phase == PickerPhase.Empty)
        {
            picker.Channel.SetServer(new DirectoryServer("srv_empty", null));
        }
        Assert.Equal(OnlineShellStage.ChannelSelect, picker.Flow.Stage);
        Assert.Equal(phase, picker.Channel.Phase);
        if (phase == PickerPhase.Ready)
        {
            Assert.True(picker.Channel.CanStart);
            Assert.Equal(ChannelBusy.None, picker.Channel.Busy);
        }
        else
        {
            Assert.False(picker.Channel.CanStart);
        }
        Assert.False(picker.Flow.Busy);
        Assert.Null(picker.Flow.Notice);
        Assert.Null(picker.Channel.Notice);
        return new Bait(picker.Rig);
    }

    /// <summary>停在选角页的外壳:三个角色都能进(没有待删除、没有名字标记、状态字节正常),选中的是槽位 0。</summary>
    private static ShellRig NewCharacterPageRig()
    {
        var rig = new ShellRig(options => options.CharacterSelect = new OnlineCharacterSelectOptions());
        rig.Backend.SeedManagedRoster(("HeroA", (byte)0, (byte)10), ("HeroB", (byte)0, (byte)11), ("HeroC", (byte)0, (byte)12));
        rig.Fill();
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(3, rig.Flow.Characters.Entries.Count);
        Assert.False(rig.Flow.Busy);
        Assert.Null(rig.Flow.Notice);
        return rig;
    }

    private static Bait NewCharacterPage(int driverCharacterSlot)
    {
        return new Bait(NewCharacterPageRig(), driverCharacterSlot);
    }

    private static Bait NewLoadingPage()
    {
        var gate = new TaskCompletionSource<OnlineSelectOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRig rig = NewCharacterPageRig();
        rig.Backend.OnSelect = (slot, ct) => gate.Task;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.Loading, rig.Flow.Stage);
        Assert.True(rig.Flow.Busy);
        Assert.Equal(1, rig.Backend.SelectCalls);
        return new Bait(rig, selectGate: gate);
    }

    private static Bait NewInGame()
    {
        ShellRig rig = NewCharacterPageRig();
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.SelectCalls);
        return new Bait(rig);
    }

    private static Bait NewLoginPageWithErrorNotice()
    {
        var rig = new ShellRig();
        rig.Backend.OnLogin = (user, password, ct) => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials));
        rig.Fill();
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(OnlineNoticeKind.WrongCredentials, rig.Flow.Notice.Kind);
        Assert.True(rig.Flow.Notice.IsError);
        return new Bait(rig);
    }

    private static Bait NewChannelPageWithErrorNotice()
    {
        var rig = new ShellRig();
        rig.Backend.OnConnect = (target, ct) => Task.FromResult(OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed));
        rig.Fill();
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(OnlineNoticeKind.ConnectFailed, rig.Flow.Notice.Kind);
        Assert.True(rig.Flow.Notice.IsError);
        return new Bait(rig);
    }

    private static Bait Build(BaitScene scene)
    {
        switch (scene)
        {
            case BaitScene.LoginPage: return NewLoginPage();
            case BaitScene.ServerPageReady: return NewServerPage(PickerPhase.Ready);
            case BaitScene.ServerPageFailed: return NewServerPage(PickerPhase.Failed);
            case BaitScene.ServerPageEmpty: return NewServerPage(PickerPhase.Empty);
            case BaitScene.ServerPagePlaceholder: return NewServerPlaceholderPage();
            case BaitScene.ChannelPageReady: return NewChannelPage(PickerPhase.Ready);
            case BaitScene.ChannelPageFailed: return NewChannelPage(PickerPhase.Failed);
            case BaitScene.ChannelPageEmpty: return NewChannelPage(PickerPhase.Empty);
            case BaitScene.CharacterPage: return NewCharacterPage(driverCharacterSlot: 0);
            case BaitScene.CharacterPageFarSlot: return NewCharacterPage(driverCharacterSlot: 2);
            case BaitScene.LoadingPage: return NewLoadingPage();
            case BaitScene.InGame: return NewInGame();
            case BaitScene.LoginPageWithErrorNotice: return NewLoginPageWithErrorNotice();
            case BaitScene.ChannelPageWithErrorNotice: return NewChannelPageWithErrorNotice();
            default: throw new ArgumentOutOfRangeException(nameof(scene), scene, null);
        }
    }

    /// <summary>
    /// 每个饵刚立好时外壳对外可见的那一行(手写字面量):阶段|忙不忙|提示类别|假后端被调过的方法。
    /// 收尾之后的驱动不管推多少帧、再轮询多少次,外壳都得还是这一行。
    /// </summary>
    public static TheoryData<BaitScene, string> Baits => new TheoryData<BaitScene, string>
    {
        { BaitScene.LoginPage, "Login|busy=False|notice=none|calls=" },
        { BaitScene.ServerPageReady, "ServerSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ServerPageFailed, "ServerSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ServerPageEmpty, "ServerSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ServerPagePlaceholder, "ServerSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ChannelPageReady, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ChannelPageFailed, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ChannelPageEmpty, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.CharacterPage, "CharacterSelect|busy=False|notice=none|calls=login,connect" },
        { BaitScene.CharacterPageFarSlot, "CharacterSelect|busy=False|notice=none|calls=login,connect" },
        { BaitScene.LoadingPage, "Loading|busy=True|notice=none|calls=login,connect,select" },
        { BaitScene.InGame, "InGame|busy=False|notice=none|calls=login,connect,select" },
        { BaitScene.LoginPageWithErrorNotice, "Login|busy=False|notice=WrongCredentials|calls=login,disconnect" },
        { BaitScene.ChannelPageWithErrorNotice, "ChannelSelect|busy=False|notice=ConnectFailed|calls=login,connect" },
    };

    /// <summary>
    /// 外壳流程没被碰:驱动若按过键,痕迹先留在登录页的待办动作、选服务器与选频道页攒下的动作里(还没轮询,没人替它办事);
    /// 再轮询几次,留下的意图会落地成阶段变化或对假后端的调用。两次看到的都得和饵刚立好时一模一样。
    /// </summary>
    private static void AssertFlowUntouched(Bait bait, string flowLine)
    {
        Assert.Equal(flowLine, bait.Describe());
        if (bait.Flow.Stage == OnlineShellStage.Login)
        {
            Assert.Equal(LoginAction.None, bait.Flow.Login.PendingAction);
        }
        if (bait.Flow.Stage == OnlineShellStage.ServerSelect && bait.Flow.ServerScreen != null)
        {
            Assert.Equal(ServerSelectAction.None, bait.Flow.ServerScreen.TakeAction());
        }
        if (bait.Flow.Stage == OnlineShellStage.ChannelSelect && bait.Flow.ChannelScreen != null)
        {
            Assert.Equal(ChannelSelectAction.None, bait.Flow.ChannelScreen.TakeAction(out _));
        }

        bait.Flush();

        Assert.Equal(flowLine, bait.Describe());
    }

    // ------------------------------------------------------------------ 已失败收尾的驱动:之后的外壳帧一概不理

    [Theory]
    [MemberData(nameof(Baits))]
    public void A_driver_that_finished_with_failure_leaves_every_shell_page_alone(BaitScene scene, string flowLine)
    {
        var logs = new List<string>();
        using Bait bait = Build(scene);
        Assert.Equal(flowLine, bait.Describe());                       // 饵立在那里
        OnlineAutoDriver driver = NewFailedDriver(logs, bait.DriverCharacterSlot);
        Dictionary<string, object> frozen = FieldsOf(driver);

        StepShellFrames(driver, bait.Flow);

        AssertFailed(driver, logs);
        AssertDriverFrozen(frozen, driver);
        AssertFlowUntouched(bait, flowLine);
    }

    // ------------------------------------------------------------------ 已成功收尾的驱动:之后的外壳帧一概不理

    [Theory]
    [MemberData(nameof(Baits))]
    public void A_driver_that_finished_with_success_leaves_every_shell_page_alone(BaitScene scene, string flowLine)
    {
        var logs = new List<string>();
        using Bait bait = Build(scene);
        Assert.Equal(flowLine, bait.Describe());                       // 饵立在那里
        OnlineAutoDriver driver = NewSucceededDriver(logs, bait.DriverCharacterSlot);
        Dictionary<string, object> frozen = FieldsOf(driver);

        StepShellFrames(driver, bait.Flow);

        AssertSucceeded(driver, logs);
        AssertDriverFrozen(frozen, driver);
        AssertFlowUntouched(bait, flowLine);
    }

    // ------------------------------------------------------------------ 收尾前进过游戏、收尾后外壳退回登录页

    private const string LoginPageAfterAGame = "Login|busy=False|notice=Disconnected|calls=login,connect,select,disconnect";

    /// <summary>进入游戏的外壳掉线:退回登录页,带"已断开"的错误提示。</summary>
    private static void FallBackToLoginPage(Bait bait)
    {
        Assert.Equal(OnlineShellStage.InGame, bait.Flow.Stage);
        bait.Backend.RaiseDisconnected();
        bait.Rig.Pump(1);
        Assert.Equal(LoginPageAfterAGame, bait.Describe());
    }

    [Fact]
    public void A_driver_that_finished_with_failure_after_seeing_the_game_leaves_the_login_page_the_shell_fell_back_to_alone()
    {
        // 收尾之前它看过进了游戏的外壳(记下"进过游戏");拿掉入口守卫时,退回登录页的帧会让它走进 Fail("disconnect"),被 Fail 自带的守卫挡住——钉的是两层守卫合起来的结果
        var logs = new List<string>();
        using Bait bait = NewInGame();
        OnlineAutoDriver driver = NewFailedDriver(logs, beforeTheEnd: d => d.StepShell(bait.Flow, 100));
        FallBackToLoginPage(bait);
        Dictionary<string, object> frozen = FieldsOf(driver);

        StepShellFrames(driver, bait.Flow);

        AssertFailed(driver, logs);
        AssertDriverFrozen(frozen, driver);
        AssertFlowUntouched(bait, LoginPageAfterAGame);
    }

    [Fact]
    public void A_driver_that_finished_with_success_after_seeing_the_game_leaves_the_login_page_the_shell_fell_back_to_alone()
    {
        var logs = new List<string>();
        using Bait bait = NewInGame();
        OnlineAutoDriver driver = NewSucceededDriver(logs, beforeTheEnd: d => d.StepShell(bait.Flow, 100));
        FallBackToLoginPage(bait);
        Dictionary<string, object> frozen = FieldsOf(driver);

        StepShellFrames(driver, bait.Flow);

        AssertSucceeded(driver, logs);
        AssertDriverFrozen(frozen, driver);
        AssertFlowUntouched(bait, LoginPageAfterAGame);
    }

    // ------------------------------------------------------------------ 流程是 null 的帧:收尾与否都不抛、不改状态

    [Fact]
    public void A_driver_that_has_not_finished_ignores_a_null_shell_flow_without_throwing_and_stays_unchanged()
    {
        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        Dictionary<string, object> frozen = FieldsOf(driver);

        Exception thrown = Record.Exception(() => driver.StepShell(null, 100));

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
        AssertDriverFrozen(frozen, driver);
    }

    [Fact]
    public void Null_shell_flow_frames_neither_end_the_run_nor_spoil_the_frames_around_them()
    {
        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        using Bait page = NewLoginPage();

        for (int i = 0; i < 3; i++)
        {
            driver.StepShell(null, 100 + i * 16);
        }
        Assert.Equal("init", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Equal(LoginAction.None, page.Flow.Login.PendingAction);

        driver.StepShell(page.Flow, 200);                                  // 第一个真帧:登录页上按下它的那一次回车
        Assert.Equal("login", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Equal(LoginAction.Login, page.Flow.Login.PendingAction);

        for (int i = 0; i < 3; i++)
        {
            driver.StepShell(null, 300 + i * 16);
        }
        Assert.Equal("login", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Null(driver.ResultLine);
        Assert.Empty(logs);
        Assert.Equal(LoginAction.Login, page.Flow.Login.PendingAction);

        page.Rig.Pump(1);                                                  // 那一次回车这才落地:登录请求只发了一次
        Assert.Equal(1, page.Backend.LoginCalls);
    }

    [Fact]
    public void A_driver_that_finished_with_failure_ignores_a_null_shell_flow_without_throwing()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewFailedDriver(logs);
        Dictionary<string, object> frozen = FieldsOf(driver);

        Exception thrown = Record.Exception(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                driver.StepShell(null, 500 + i * 16);
            }
        });

        Assert.Null(thrown);
        AssertFailed(driver, logs);
        AssertDriverFrozen(frozen, driver);
    }

    [Fact]
    public void A_driver_that_finished_with_success_ignores_a_null_shell_flow_without_throwing()
    {
        var logs = new List<string>();
        OnlineAutoDriver driver = NewSucceededDriver(logs);
        Dictionary<string, object> frozen = FieldsOf(driver);

        Exception thrown = Record.Exception(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                driver.StepShell(null, 500 + i * 16);
            }
        });

        Assert.Null(thrown);
        AssertSucceeded(driver, logs);
        AssertDriverFrozen(frozen, driver);
    }

    // ------------------------------------------------------------------ 对照:没收尾的驱动对同样的饵确实会动手(上面"一动不动"不是饵摆得不对造成的假象)

    /// <summary>
    /// 每个饵上,没收尾的驱动推 5 帧外壳、再轮询外壳 3 次之后:驱动的阶段名、收没收尾、外壳那一行(手写字面量)。
    /// 会动手的饵(登录页、选服务器就绪与占位、选频道就绪、选角页)外壳那一行与饵刚立好时不同;
    /// 读失败、没有内容、带错误提示的饵驱动自己失败收尾;加载页与进入游戏之后驱动只改阶段名(或什么都不改)。
    /// </summary>
    public static TheoryData<BaitScene, string, bool, string> UnfinishedDriverReactions => new TheoryData<BaitScene, string, bool, string>
    {
        { BaitScene.LoginPage, "login", false, "CharacterSelect|busy=False|notice=none|calls=login,connect" },
        { BaitScene.ServerPageReady, "server", false, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ServerPageFailed, "server", true, "ServerSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ServerPageEmpty, "server", true, "ServerSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ServerPagePlaceholder, "server", false, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ChannelPageReady, "channel", false, "CharacterSelect|busy=False|notice=none|calls=login,connect" },
        { BaitScene.ChannelPageFailed, "channel", true, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.ChannelPageEmpty, "channel", true, "ChannelSelect|busy=False|notice=none|calls=login" },
        { BaitScene.CharacterPage, "charselect", false, "InGame|busy=False|notice=none|calls=login,connect,select" },
        { BaitScene.CharacterPageFarSlot, "charselect", false, "InGame|busy=False|notice=none|calls=login,connect,select" },
        { BaitScene.LoadingPage, "loading", false, "Loading|busy=True|notice=none|calls=login,connect,select" },
        { BaitScene.InGame, "init", false, "InGame|busy=False|notice=none|calls=login,connect,select" },
        { BaitScene.LoginPageWithErrorNotice, "login", true, "Login|busy=False|notice=WrongCredentials|calls=login,disconnect" },
        { BaitScene.ChannelPageWithErrorNotice, "channel", true, "ChannelSelect|busy=False|notice=ConnectFailed|calls=login,connect" },
    };

    [Theory]
    [MemberData(nameof(UnfinishedDriverReactions))]
    public void An_unfinished_driver_acts_on_the_same_shell_pages_the_finished_ones_leave_alone(BaitScene scene, string driverStage, bool driverFinished, string flowLineAfter)
    {
        using Bait bait = Build(scene);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: bait.DriverCharacterSlot));

        StepShellFrames(driver, bait.Flow);
        bait.Flush();

        Assert.Equal(driverStage, driver.Stage);
        Assert.Equal(driverFinished, driver.Finished);
        Assert.Equal(flowLineAfter, bait.Describe());
    }

    [Fact]
    public void An_unfinished_driver_walks_the_character_cursor_to_a_far_slot_before_it_presses_enter()
    {
        // 驱动要选槽位 2:前两帧各向右走一格,第三帧才按回车(与槽位 0 时第一帧就按回车不同)
        using Bait bait = NewCharacterPage(driverCharacterSlot: 2);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: bait.DriverCharacterSlot));

        driver.StepShell(bait.Flow, 400);
        driver.StepShell(bait.Flow, 416);
        bait.Flush();
        Assert.Equal(OnlineShellStage.CharacterSelect, bait.Flow.Stage);
        Assert.Equal(0, bait.Backend.SelectCalls);

        driver.StepShell(bait.Flow, 432);
        bait.Flush();

        Assert.False(driver.Finished);
        Assert.Equal(1, bait.Backend.SelectCalls);
        Assert.Equal(2, bait.Backend.LastSlot);
    }

    [Fact]
    public void An_unfinished_driver_that_has_seen_the_game_fails_as_a_disconnect_when_the_shell_falls_back_to_the_login_page()
    {
        var logs = new List<string>();
        using Bait bait = NewInGame();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        driver.StepShell(bait.Flow, 100);
        Assert.False(driver.Finished);
        FallBackToLoginPage(bait);

        driver.StepShell(bait.Flow, 500);

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal("disconnect", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=disconnect elapsedMs=500", driver.ResultLine);
        Assert.Equal(new[] { "DNF_ONLINE result=fail stage=disconnect elapsedMs=500" }, logs);
    }
}
