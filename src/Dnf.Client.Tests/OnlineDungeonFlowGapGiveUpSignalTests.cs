using System.IO.Compression;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// r22-giveup-signal 的宿主层核对:真服放弃之后推来的是 0/9、0/24、0/3、0/36、0/2(没有 0/23),战斗层现在把它判成"放弃回城成功"
/// (<c>GaveUp</c>,但没见过 0/23,所以用户区快照为 null)。宿主这一侧要做的事本来就合理:返回区域的判定拿不到 0/23 就是 unknown,
/// 照样按"放弃回城"走完(计一次放弃、不是失败、离场原因 GaveUp),并且没有超时提示;这里只加测试,不动宿主。
/// 凑不成判据的窗口(缺 0/24 或缺 0/2)仍然是老样子:等满放弃超时、离场原因 Timeout、亮超时提示。
/// 既有的超时用例原样保留在 <see cref="OnlineDungeonFlowGapCombatTests"/> 里。
/// </summary>
public sealed class OnlineDungeonFlowGapGiveUpSignalTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";

    private static string[] Reasons(GameFlow flow)
    {
        return flow.History.Select(h => h.Reason).ToArray();
    }

    private static OnlineDungeonCombatOptions GiveUpOptions(int giveUpTimeoutMs)
    {
        return new OnlineDungeonCombatOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            GiveUpTimeout = TimeSpan.FromMilliseconds(giveUpTimeoutMs),
            SettlementTimeout = TimeSpan.FromSeconds(30),
        };
    }

    private static string[] EnteredLogs()
    {
        // 进副本成功、单房间(2 只怪全对上)的三行起手日志(同 OnlineDungeonFlowGapCombatTests)
        return new[]
        {
            P + "event=select outcome=enter difficulty=0 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=0",
            P + "event=room status=Ready multi=off spawned=2 expected=2 rooms=1",
        };
    }

    /// <summary>能读下来的 0/24:村 3、区 7、0 条记录(宿主层不关心里面的内容)。</summary>
    private static byte[] ParseableAreaUsers()
    {
        byte[] payload = AreaUsersBytes(3, 7);
        Assert.True(TownAreaPayloads.TryParseAreaUsers(payload, out _));
        return payload;
    }

    /// <summary>25 字节、按"4 + 8n"读不下来的 0/24:声明 3 条记录要 24 字节,头之后只剩 21 字节。</summary>
    private static byte[] UnparseableAreaUsers()
    {
        byte[] payload = new NoticePayloadBuilder().U8(3).U8(7).U16(3).Raw(GatewayTestSupport.Pattern(21, 0x71, 1)).ToArray();
        Assert.Equal(25, payload.Length);
        Assert.False(TownAreaPayloads.TryParseAreaUsers(payload, out _));
        return payload;
    }

    /// <summary>包号 2、9 属于压缩包号集合:线上载荷是 zlib 流,假会话解码时会解开,所以这两个帧的载荷要先压一下。</summary>
    private static byte[] Zlib(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain, 0, plain.Length);
        }
        return ms.ToArray();
    }

    /// <summary>真服放弃之后的五个帧,按到达顺序(主机层的假网络会话直接交付明文帧,不需要信封;载荷是可识别的合成字节)。</summary>
    private static CombatKit.ServerFrame[] RealWindow(byte[] payload24, bool without24 = false, bool without2 = false)
    {
        var frames = new List<CombatKit.ServerFrame>
        {
            new CombatKit.ServerFrame(CombatKit.Noti, 9, Zlib(GatewayTestSupport.Pattern(6, 0x31, 3))),
        };
        if (!without24)
        {
            frames.Add(new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, payload24));
        }
        frames.Add(new CombatKit.ServerFrame(CombatKit.Noti, 3, GatewayTestSupport.Pattern(8, 0x41, 5)));
        frames.Add(new CombatKit.ServerFrame(CombatKit.Noti, 36, GatewayTestSupport.Pattern(12, 0x51, 7)));
        if (!without2)
        {
            frames.Add(new CombatKit.ServerFrame(CombatKit.Noti, 2, Zlib(GatewayTestSupport.Pattern(46, 0x61, 1))));
        }
        return frames.ToArray();
    }

    [Theory]
    // 图册版:返回位置 (1,1) 映射成线上 village/area = 1/1,没有 0/23 就没有可比的区域:unknown
    [InlineData(true, 1, 1, false)]
    [InlineData(true, 1, 1, true)]
    // 无图册版:直接比城镇号/区域号;同样没有 0/23 就是 unknown
    [InlineData(false, 1, 1, false)]
    [InlineData(false, 0, 0, true)]
    [InlineData(false, 255, 255, false)]
    public void Giving_up_on_the_real_server_window_without_0_23_returns_to_town_with_an_unknown_area_and_no_timeout_notice(
        bool useAtlas, int town, int area, bool unparseable24)
    {
        // 保护:战斗层判回城成功(GaveUp,UserArea 为 null)时,宿主按"放弃回城"收尾:
        // 离场原因 GaveUp、返回区域 unknown(没有 0/23 可比,不论返回位置配成什么)、计一次放弃(不是失败)、不亮超时提示、只放权一次。
        // 0/24 读不读得下来不影响:判据只看"见到过"。
        byte[] payload24 = unparseable24 ? UnparseableAreaUsers() : ParseableAreaUsers();
        using var rig = new Rig(o =>
        {
            o.ReturnPlace = new TownPlace(town, area);
            o.Atlas = useAtlas ? AtlasWith(1, 1, 2) : null;
            o.CombatOptions = GiveUpOptions(5000);
        });
        ServeRoomDungeon(rig, 2, _ => RealWindow(payload24));
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        rig.Ctrl.RequestGiveUpDungeon();
        Assert.Equal(OnlineDungeonFlowPhase.GivingUp, rig.Ctrl.Phase);
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=GaveUp reported=0 dropped=0 rooms=1",
            P + "event=return area=unknown",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(GameStage.Town, rig.Flow.Stage);
        Assert.Equal(1, rig.Flow.GiveUps);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("online giveup", Reasons(rig.Flow).Last());
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Disconnects);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Fact]
    public void Giving_up_without_a_configured_return_place_on_the_real_window_is_also_unknown_and_quiet()
    {
        using var rig = new Rig(o =>
        {
            o.ReturnPlace = null;
            o.CombatOptions = GiveUpOptions(5000);
        });
        ServeRoomDungeon(rig, 2, _ => RealWindow(ParseableAreaUsers()));
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        rig.Ctrl.RequestGiveUpDungeon();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=GaveUp reported=0 dropped=0 rooms=1",
            P + "event=return area=unknown",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Flow.GiveUps);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Null(rig.Ctrl.ActiveNotice);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_window_missing_the_0_24_or_the_0_2_still_ends_as_a_timeout_with_the_notice(bool without24, bool without2)
    {
        // 保护:凑不成判据((0/23 且 0/24)或(0/24 且 0/2))的窗口没有被误判成回城——照旧等满放弃超时,离场原因 Timeout,返回区域 unknown,亮超时提示
        using var rig = new Rig(o =>
        {
            o.ReturnPlace = new TownPlace(ReturnTownId, ReturnAreaId);
            o.TransientNoticeDurationMs = 1;
            o.CombatOptions = GiveUpOptions(150);
        });
        ServeRoomDungeon(rig, 2, _ => RealWindow(ParseableAreaUsers(), without24, without2));
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);

        rig.Ctrl.RequestGiveUpDungeon();
        rig.PumpUntil(() => rig.Flow.Stage == GameStage.Town && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle);

        string[] expected = EnteredLogs().Concat(new[]
        {
            P + "event=leave reason=Timeout reported=0 dropped=0 rooms=1",
            P + "event=return area=unknown",
        }).ToArray();
        Assert.Equal(expected, rig.Logs());
        Assert.Equal(1, rig.Flow.GiveUps);
        Assert.Equal(0, rig.Flow.Failures);
        Assert.Equal("online giveup", Reasons(rig.Flow).Last());
        DungeonSelectNotice notice = rig.Ctrl.ActiveNotice;
        Assert.NotNull(notice);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, notice.Kind);
    }
}
