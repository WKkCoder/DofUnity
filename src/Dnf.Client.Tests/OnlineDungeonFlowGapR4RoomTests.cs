using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网多房间接线(<see cref="OnlineDungeonRoom"/>)的逐帧检查在"玩家已经放弃回城"这段时间里的行为:内存里接真战斗跑器与多房间假服务端,
/// 只复制了上一轮 <c>OnlineDungeonFlowGapRoomTests</c> 里造一局所需的最小一份装配(原文件的装配是私有的),改名 <c>GiveUpRig</c>;
/// 服务端不回放弃,放弃等回包超时之后战斗跑器收场(逐帧检查只在多房间的一局里挂着,所以必须是多房间)。
/// </summary>
public sealed class OnlineDungeonFlowGapR4RoomTests
{
    private sealed class GiveUpRig : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private Task<OnlineDungeonCombatResult> _background;

        public HostedGatedRoomFixture Fx { get; } = new HostedGatedRoomFixture();

        public FakeMultiRoomServer Server { get; private set; }

        public OnlineDungeonCombatRunner Runner { get; private set; }

        public OnlineDungeonRoom Room { get; private set; }

        public HostedRoomRun Run { get; private set; }

        public void CancelCombat()
        {
            _cancel.Cancel();
        }

        public static async Task<GiveUpRig> StartAsync(int giveUpTimeoutMs)
        {
            var exits = new int[52];
            for (int i = 0; i < exits.Length; i++)
            {
                exits[i] = (i == 0 ? 0 : ExitBits.Left) | (i == exits.Length - 1 ? 0 : ExitBits.Right);
            }
            var start = new List<CellRect> { new CellRect(49, 0, 49, 0) };
            var boss = new List<CellRect> { new CellRect(51, 0, 51, 0) };
            var wide = new DungeonMaze("dungeon/test/m.dgn", 52, 1, exits, null, start, boss, null, null, 1f, -1, null, null);

            var plans = new[]
            {
                new FakeMultiRoomServer.RoomPlan(49, 0, CombatKit.Sequential(1)),
                new FakeMultiRoomServer.RoomPlan(50, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId)),
                new FakeMultiRoomServer.RoomPlan(51, 0, CombatKit.Sequential(1, RoomKit.ThirdBaseId)),
            };

            var rig = new GiveUpRig();
            rig.Server = new FakeMultiRoomServer(plans) { MazeWidth = 52 };
            var session = new CombatFakeSession { Respond = (cls, id, payload) => rig.Server.Handle(cls, id, payload) };
            OnlineDungeonCombatOptions options = CombatKit.Options(overallSeconds: 60);
            options.GiveUpTimeout = TimeSpan.FromMilliseconds(giveUpTimeoutMs);
            rig.Runner = new OnlineDungeonCombatRunner(session, null, options);
            StartMapNotice map = RoomKit.Map(plans[0].Ids, plans[0].CellX, plans[0].CellY, 905);
            rig._background = Task.Run(() => rig.Runner.RunAsync(map, _ => { }, rig._cancel.Token));
            await CombatKit.WaitUntil(() => rig.Runner.Machine != null, "战斗状态机建好");

            rig.Room = new OnlineDungeonRoom(rig.Runner);
            var setup = new OnlineMultiRoomSetup(new[] { wide }, (entry, plan) => rig.Fx.Create(Templates(6), plan, GatesOf(entry)));
            rig.Run = rig.Room.CreateRun(plan => rig.Fx.Create(Templates(6), plan, new[] { GateSide.Right }), 0.0, setup);
            Assert.NotNull(rig.Run);
            Assert.True(rig.Room.MultiRoom);
            return rig;
        }

        private static List<GateSide> GatesOf(OnlineRoomEntry entry)
        {
            var gates = new List<GateSide>();
            if (entry.Entry.HasValue)
            {
                gates.Add(entry.Entry.Value);
            }
            if (entry.Exit.HasValue)
            {
                gates.Add(entry.Exit.Value);
            }
            return gates;
        }

        public void Dispose()
        {
            _cancel.Cancel();
            try
            {
                _background?.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }
            Fx.Dispose();
            _cancel.Dispose();
        }
    }

    [Fact]
    public async Task A_run_whose_combat_ended_while_the_player_was_giving_up_is_not_aborted_by_the_rooms_frame_check()
    {
        // 保护:放弃回城命令已经发出(状态机在"放弃中")但还没收到回城通知时,战斗跑器因放弃的回包等不到而超时收场;之后房间每帧的检查
        // 不把这当成一次"房间中止"(放弃回城由流程控制器收尾,房间不去抢着中止、不把这一局标成失败)。
        // 对照组:同样的多房间一局、战斗没在放弃时被取消收场,房间每帧检查会以 NetEnded 中止——说明这个场景里逐帧检查确实在跑。
        using (GiveUpRig control = await GiveUpRig.StartAsync(150))
        {
            control.CancelCombat();
            await CombatKit.WaitUntil(() => control.Runner.Ended, "战斗被取消而收场");
            Assert.False(control.Runner.Machine.IsGivingUp);
            control.Run.Update(20, 0, 0);
            Assert.True(control.Run.Aborted);
            Assert.Equal(HostedAbort.NetEnded, control.Run.AbortReason);
        }

        using GiveUpRig rig = await GiveUpRig.StartAsync(150);
        Assert.True(rig.Runner.PostGiveUp());
        await CombatKit.WaitUntil(() => rig.Runner.Machine.IsGivingUp, "放弃回城已发出、状态机在等回包");
        await CombatKit.WaitUntil(() => rig.Runner.Ended, "放弃的回包等不到,放弃超时,跑器收场");
        Assert.True(rig.Runner.Machine.IsGivingUp);
        Assert.False(rig.Runner.Machine.ReturnedToTown);
        Assert.NotEqual(OnlineCombatPhase.Settled, rig.Runner.Machine.Phase);

        for (int i = 0; i < 5; i++)
        {
            rig.Run.Update(20, 0, 0);
        }

        Assert.False(rig.Run.Aborted);
        Assert.Equal(HostedAbort.None, rig.Run.AbortReason);
        Assert.False(rig.Run.Failed);
    }
}
