using System.Text;
using Dnf.Data;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网选角界面(真名册,r18-charui)在宿主一侧的接线:宿主把 <see cref="OnlineCharacterSelectOptions"/> 交给外壳阶段机之后,
/// 服务器给的角色列表原样成了名册(超过 12 个也全在、选中的下标就是发给服务器的槽位、不能进入的角色不发选角命令);
/// 以及 <see cref="OnlineCharacterSelectSetup"/> 在有无字体时给出什么职业称号来源。会话用假的、网关用内存里的假网关:不连任何网络,名字与编号全是合成的。
/// </summary>
public sealed class OnlineCharacterSelectHostTests
{
    private sealed class FixedTitles : ICharacterTitles
    {
        public string DefaultName => "默认名";

        public string ClassName(int job, int growType, int awakening) => "职业" + job;
    }

    /// <summary>合成的角色列表经真解析器。</summary>
    private static UserInfoNotice Roster(int count, Action<int, UserInfoTestData.EntrySpec> tweak = null)
    {
        var entries = new UserInfoTestData.EntrySpec[count];
        for (int i = 0; i < count; i++)
        {
            UserInfoTestData.EntrySpec e = UserInfoTestData.Standard(i);
            e.Name = Encoding.UTF8.GetBytes("角色" + (i + 1));
            e.X1 = 0;
            e.D2 = 0;                                  // 标准条目的 D2 不是 0(=待删除),这里要的是能进入的普通角色
            e.Status = 0;
            e.Job = i % 11;
            e.Level = i + 1;
            tweak?.Invoke(i, e);
            entries[i] = e;
        }
        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(entries), out UserInfoNotice notice));
        return notice;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(UserInfoNotice roster, OnlineCharacterSelectOptions options)
        {
            Backend = new BackendRig(true, port =>
            {
                port.OnConnect = (channel, ct) => Task.FromResult(new OnlineConnectReport(OnlineFailure.None, OnlineCharacterListStatus.Received, roster));
            });
            var setup = new OnlineShellSetup(Backend.Template, OnlineShellConfigSource.ConfigFile, null, 31000, BackendRig.User, null);
            Host = new OnlineShellHost(setup, Backend.Backend, null, _ => { }, null, options);
        }

        public BackendRig Backend { get; }

        public OnlineShellHost Host { get; }

        public long Now = 1000;

        public FakeSessionPort Port => Backend.Port(0);

        public void Tick(OnlineShellFrameInput input = default)
        {
            Host.Tick(input, 16, Now += 16);
        }

        public void ReachCharacterSelect()
        {
            Tick(new OnlineShellFrameInput { Text = BackendRig.Password });
            Tick(new OnlineShellFrameInput { Enter = true });
            HostPump.Until(Host, ref Now, () => Host.Stage == OnlineShellStage.CharacterSelect);
        }

        public void Press(bool right = false, bool down = false, bool enter = false)
        {
            Tick(new OnlineShellFrameInput { Right = right, Down = down, Enter = enter });
        }

        public void Dispose()
        {
            Host.Dispose();
        }
    }

    // ---------------------------------------------------------------- 名册与槽位

    [Fact]
    public void WithTheOptionsTheServersRosterIsShownAndTheSelectedPositionIsTheSlotSentToTheServer()
    {
        using var rig = new Rig(Roster(3), new OnlineCharacterSelectOptions { Titles = new FixedTitles() });
        rig.ReachCharacterSelect();
        Assert.Equal(3, rig.Host.Flow.Characters.Entries.Count);

        rig.Press(right: true);                                    // 第 2 个角色
        rig.Press(enter: true);
        HostPump.Until(rig.Host, ref rig.Now, () => rig.Host.InGame);

        Assert.Equal(1, rig.Port.SelectCalls);
        Assert.Equal(1, rig.Port.LastSlot);                       // 槽位 = 名册里的位置,不是列表条目里的编号(Uid)
    }

    [Fact]
    public void MoreThanTwelveCharactersAreAllReachableAndTheLastOneIsSentWithItsListPosition()
    {
        using var rig = new Rig(Roster(14), new OnlineCharacterSelectOptions { Titles = new FixedTitles() });
        rig.ReachCharacterSelect();
        Assert.Equal(14, rig.Host.Flow.Characters.Entries.Count);

        rig.Press(down: true);                                     // 一行 6 个:下移两行到第 13 个,再右移到第 14 个
        rig.Press(down: true);
        rig.Press(right: true);
        rig.Press(enter: true);
        HostPump.Until(rig.Host, ref rig.Now, () => rig.Host.InGame);

        Assert.Equal(13, rig.Port.LastSlot);
    }

    [Fact]
    public void ACharacterTheClientRefusesToStartIsNeverSentToTheServer()
    {
        // 第 1 个 X1 = 1(不能进入),第 2 个正在删除(D2 ≠ 0),第 3 个状态字节 8,第 4 个正常
        using var rig = new Rig(
            Roster(4, (i, e) =>
            {
                if (i == 0) { e.X1 = 1; }
                if (i == 1) { e.D2 = 3; }
                if (i == 2) { e.Status = 8; }
            }),
            new OnlineCharacterSelectOptions { Titles = new FixedTitles() });
        rig.ReachCharacterSelect();

        for (int i = 0; i < 3; i++)
        {
            rig.Press(enter: true);
            rig.Tick();
            Assert.Equal(0, rig.Port.SelectCalls);
            Assert.Equal(OnlineShellStage.CharacterSelect, rig.Host.Stage);
            rig.Press(right: true);
        }

        rig.Press(enter: true);                                    // 第 4 个正常:这一次才发
        HostPump.Until(rig.Host, ref rig.Now, () => rig.Host.InGame);
        Assert.Equal(1, rig.Port.SelectCalls);
        Assert.Equal(3, rig.Port.LastSlot);
    }

    // ---------------------------------------------------------------- 装配

    [Fact]
    public void WithoutFontsTheSetupHasNoTitleSourceAndNeverReadsTheGameData()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        OnlineCharacterSelectOptions options = OnlineCharacterSelectSetup.Create(assets, hasFonts: false);
        Assert.NotNull(options);
        Assert.Null(options.Titles);
    }

    [Fact]
    public void WithFontsTheSetupReadsTheTitlesFromTheGameDataAndMissingScriptsGiveNoName()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        OnlineCharacterSelectOptions options = OnlineCharacterSelectSetup.Create(assets, hasFonts: true);
        Assert.IsType<DnfCharacterTitles>(options.Titles);
        Assert.Equal(DnfCharacterTitles.DefaultNameText, options.Titles.DefaultName);
        Assert.Null(options.Titles.ClassName(0, 0, 0));          // 合成数据里没有鬼剑士的角色脚本:查不到,界面换英文占位
        Assert.Null(options.Titles.ClassName(99, 0, 0));
    }

    [Fact]
    public void TheSetupNeedsTheGameData()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineCharacterSelectSetup.Create(null, hasFonts: true));
        Assert.Throws<ArgumentNullException>(() => OnlineCharacterSelectSetup.Create(null, hasFonts: false));
    }
}
