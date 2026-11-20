using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器第四轮补测(OnlineDungeonFlowGapR4*)共用的小工具:直接复用上一轮的 <see cref="OnlineDungeonFlowGapKit"/>(场景台、假端口、载荷构造),
/// 这里只补三样——"杀光就结算"的假服务端装配、杀光本局全部本地怪、几处日志与历史的取值。全部是合成数据,不含任何真实数值。
/// </summary>
internal static class OnlineDungeonFlowGapR4Kit
{
    public const string P = "DNF_ONLINE stage=dungeon ";

    /// <summary>流程历史里每条记录的原因,按先后。</summary>
    public static string[] Reasons(GameFlow flow)
    {
        return flow.History.Select(h => h.Reason).ToArray();
    }

    /// <summary>
    /// 进副本成功、单房间(<paramref name="monsters"/> 只怪全对上)的三行起手日志;难度取选择屏缺省的 0,副本 A 的档位上限是 3。
    /// </summary>
    public static string[] EnteredLogs(int monsters = 2)
    {
        string n = monsters.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new[]
        {
            P + "event=select outcome=enter difficulty=0 max=3",
            P + "event=enter areakind=unknown entrance=true difficulty=0",
            P + "event=room status=Ready multi=off spawned=" + n + " expected=" + n + " rooms=1",
        };
    }

    /// <summary>
    /// 把场景台摆成"能杀光、能结算的单房间副本":入口两步命令按标准回包,结算命令(1/49)回标准的结算帧(0x22 + 0x23),
    /// 其余命令交给战斗假服务端(<paramref name="monsters"/> 只怪;清图后它自己回通关通知);建房工厂造一间单房间(合成房间夹具,怪数同)。
    /// </summary>
    public static void ServeSettlementDungeon(Rig rig, int monsters = 2, double returnDelayMs = 0.0)
    {
        var combatServer = new FakeCombatServer(CombatKit.Sequential(monsters));
        rig.Port.SetResponder((cls, id, payload) =>
        {
            CombatKit.ServerFrame[] entry = EntryReply(cls, id, DungeonAId, monsters);
            if (entry != null)
            {
                return entry;
            }
            if (cls == PacketClass.Cmd && id == OnlineDungeonCombat.SettlementPacketId)
            {
                return SettlementFrames();
            }
            return combatServer.Handle(cls, id, payload);
        });
        rig.RoomBuilder = ctx =>
        {
            var room = new OnlineDungeonRoom(ctx.CombatRunner);
            HostedRoomRun run = room.CreateRun(plan => rig.Fixture.Create(Templates(monsters), plan, Array.Empty<GateSide>()), returnDelayMs);
            return OnlineDungeonRoomBuildResult.Success(room, run);
        };
    }

    /// <summary>杀光本局当前房间里的全部本地怪(之后房间每步会把击杀上报给战斗跑器)。</summary>
    public static void KillAll(Rig rig)
    {
        HostedRoomRun run = Assert.IsType<HostedRoomRun>(rig.Flow.Run);
        foreach (MonsterActor monster in run.Room.Monsters.ToArray())
        {
            HostedRoomFixture.Kill(monster);
        }
    }

    /// <summary>服务端对放弃回城(1/45)的回包:0/23 用户所在区域 + 0/24 区域用户(都落在 1 号城镇 1 号区域)。</summary>
    public static IEnumerable<CombatKit.ServerFrame> TownFrames(byte village = 1, byte area = 1)
    {
        return new[]
        {
            new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(village, area)),
            new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(village, area)),
        };
    }

    /// <summary>进副本:选择屏确认 → 轮询到进了副本(<see cref="OnlineDungeonFlowPhase.InDungeon"/>)。</summary>
    public static void EnterDungeon(Rig rig)
    {
        rig.Start();
        rig.ConfirmSelected();
        rig.PumpUntil(() => rig.Ctrl.Phase == OnlineDungeonFlowPhase.InDungeon);
    }
}
