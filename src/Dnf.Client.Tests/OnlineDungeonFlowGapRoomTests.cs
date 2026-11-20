using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网多房间接线(<see cref="OnlineDungeonRoom"/>)里"主动放弃回城期间"的出口请求:内存里接真战斗跑器与多房间假服务端,
/// 只复制了 <c>OnlineDungeonRoomMultiTests</c> 里造一局所需的最小一份装配(原文件的装配是私有的)。
/// </summary>
public sealed class OnlineDungeonFlowGapRoomTests
{
    /// <summary>
    /// 一条 52 格的横线迷宫上的多房间一局:起点在第 49 格(0x31),下一格是第 50 格,超过换房请求能带的上限(0x31)——
    /// 这时只要出口请求走到"拼换房请求"这一步,就必然以 CellMismatch 中止本局;所以"中止没有发生"能证明请求在更早就被丢弃了。
    /// </summary>
    private sealed class WideRig : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private Task<OnlineDungeonCombatResult> _background;

        public HostedGatedRoomFixture Fx { get; } = new HostedGatedRoomFixture();

        public FakeMultiRoomServer Server { get; private set; }

        public OnlineDungeonCombatRunner Runner { get; private set; }

        public OnlineDungeonRoom Room { get; private set; }

        public HostedRoomRun Run { get; private set; }

        public static async Task<WideRig> StartAsync()
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

            var rig = new WideRig();
            rig.Server = new FakeMultiRoomServer(plans) { MazeWidth = 52 };
            var session = new CombatFakeSession { Respond = (cls, id, payload) => rig.Server.Handle(cls, id, payload) };
            rig.Runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options(overallSeconds: 60));
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
    public async Task A_door_request_made_while_giving_up_is_dropped_without_touching_the_run()
    {
        // 保护:主动放弃回城期间(1/45 已发出、还在等服务端回包)宿主的出口请求直接丢弃——不换房、也不因为"目标格拼不出请求"误中止本局,
        // 放弃的收尾由流程控制器负责。对照组里同样的请求在没放弃时会以 CellMismatch 中止本局,说明这个场景确实能区分两种处理。
        using (WideRig control = await WideRig.StartAsync())
        {
            Assert.False(control.Run.ExitRequest(GateSide.Right, 1090, 100));
            Assert.True(control.Run.Aborted);
            Assert.Equal(HostedAbort.CellMismatch, control.Run.AbortReason);
            Assert.Empty(control.Server.Moves);
        }

        using WideRig rig = await WideRig.StartAsync();
        rig.Runner.PostGiveUp();
        await CombatKit.WaitUntil(() => rig.Runner.Machine.IsGivingUp, "放弃回城已发出、状态机在等回包");
        Assert.False(rig.Runner.Ended);
        Assert.False(rig.Runner.Machine.ReturnedToTown);

        Assert.False(rig.Run.ExitRequest(GateSide.Right, 1090, 100));
        Assert.False(rig.Run.Aborted);
        Assert.Equal(HostedAbort.None, rig.Run.AbortReason);
        Assert.Equal(0, rig.Run.MoveRequests);
        Assert.Empty(rig.Server.Moves);
    }
}
