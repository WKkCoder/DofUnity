using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// <see cref="OnlineDungeonRoomBuilder.Build"/> 对"真资源"的胶水:用一份自己造的合成资源包(只有副本目录、地图目录、一张只有表头的房间地图、
/// 一份有三个迷宫段落的副本定义),不依赖任何原版数据。整条链走到"没有可用的房间地图"为止——
/// 能观察到的是宿主日志(<c>DNF_NET stage=room</c> 行)与失败结局里的房间状态;成功路径要真实的主角资源(见各用例的注释),这里造不出来。
/// </summary>
public sealed class OnlineDungeonFlowGapRoomBuilderTests
{
    private const string GlueDungeonPath = "dungeon/test/glue.dgn";
    private const string Host = "DNF_NET stage=room ";

    /// <summary>
    /// 合成资源包:<c>map/map.lst</c> 只登记一张 <c>glue/a.map</c>(只有 [type] 表头:能登记成候选房间,但读不出任何怪、也读不成房间地图),
    /// 副本定义 <c>dungeon/test/glue.dgn</c> 里有三个 2 × 1 的迷宫段落——默认迷宫、任务连接 0、任务连接 1——起点都在 (0,0),第一步都是向右。
    /// </summary>
    private sealed class GlueAssets : IDisposable
    {
        private readonly List<string> _pool = new List<string>();

        public string Root { get; }

        public DnfAssets Assets { get; }

        private sealed class Script
        {
            private readonly GlueAssets _owner;
            private readonly List<(byte Type, int Value)> _tokens = new List<(byte, int)>();

            public Script(GlueAssets owner)
            {
                _owner = owner;
            }

            public Script Tag(string tag)
            {
                _tokens.Add(((byte)TokenType.Tag, _owner.Intern(tag)));
                return this;
            }

            public Script Str(params string[] texts)
            {
                foreach (string t in texts)
                {
                    _tokens.Add(((byte)TokenType.String, _owner.Intern(t)));
                }
                return this;
            }

            public Script Int(params int[] values)
            {
                foreach (int v in values)
                {
                    _tokens.Add(((byte)TokenType.Int, v));
                }
                return this;
            }

            public byte[] Build()
            {
                return TestData.BuildTokenScript(_tokens);
            }
        }

        private int Intern(string text)
        {
            int at = _pool.IndexOf(text);
            if (at < 0)
            {
                _pool.Add(text);
                at = _pool.Count - 1;
            }
            return at;
        }

        private Script NewScript()
        {
            return new Script(this);
        }

        public GlueAssets()
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-glue-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            Script dungeon = NewScript();
            dungeon.Tag("[maze info]").Tag("[size]").Int(2, 1).Tag("[greed]").Str("BBEE").Tag("[start map]").Int(0, 0).Tag("[boss map]").Int(1, 0).Tag("[/maze info]");
            dungeon.Tag("[maze info]").Tag("[quest connection]").Int(0).Tag("[size]").Int(2, 1).Tag("[greed]").Str("BBEE").Tag("[start map]").Int(0, 0).Tag("[boss map]").Int(1, 0).Tag("[/maze info]");
            dungeon.Tag("[maze info]").Tag("[quest connection]").Int(1).Tag("[size]").Int(2, 1).Tag("[greed]").Str("BBEE").Tag("[start map]").Int(0, 0).Tag("[boss map]").Int(1, 0).Tag("[/maze info]");

            var scripts = new List<(string Name, byte[] Data)>
            {
                // 副本目录里没有 glue 副本:房间候选改按"目录与副本同名"的老约定(glue/ 开头的地图)去找
                ("dungeon/dungeon.lst", NewScript().Int(99).Str("Other/Other.dgn").Build()),
                ("map/map.lst", NewScript().Int(1).Str("Glue/a.map").Build()),
                ("map/glue/a.map", NewScript().Tag("[type]").Str("[normal]").Build()),
                (GlueDungeonPath, dungeon.Build()),
            };

            var strings = new List<byte[]>();
            foreach (string s in _pool)
            {
                strings.Add(Encoding.ASCII.GetBytes(s));
            }
            var files = new List<TestData.PvfFileSpec> { new TestData.PvfFileSpec("stringtable.bin", TestData.BuildStringTable(strings), 0) };
            foreach ((string name, byte[] data) in scripts)
            {
                files.Add(new TestData.PvfFileSpec(name, data, 0));
            }
            (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
            File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);
            Assets = DnfAssets.Open(Root);
        }

        public void Dispose()
        {
            Assets.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }

    [Fact]
    public async Task The_room_builder_plans_with_the_default_mazes_only_and_logs_why_it_fell_back_to_the_single_room_path()
    {
        // 保护:联网房间装配只拿"默认迷宫"(没写任务连接的段落)去对起始房间——任务连接 0、1 的迷宫段落不参与,
        // 所以日志里"对上的迷宫数"是 1 而不是 3 / 2;
        // 需要怪的起始房间(这里服务端的怪表有 1 只怪)挑不到"有怪的"房间地图就老实报"没有房间",而不是硬去造一个没有怪的房间;
        // 退回单房间路径后又挑不到房间地图,于是把"没有房间地图"这一行写进宿主日志(而不是吞掉),整个装配以 NoRoom 失败收场,
        // 失败结局里带着房间对象,能看到迷宫对上了、但没有进多房间。
        // 成功路径(造出真房间)要真实的主角资源(角色脚本与起手武器),合成资源包造不出来,见 NOTES。
        using var glue = new GlueAssets();
        var plans = new[]
        {
            new FakeMultiRoomServer.RoomPlan(0, 0, CombatKit.Sequential(1)),
            new FakeMultiRoomServer.RoomPlan(1, 0, CombatKit.Sequential(1, RoomKit.SecondBaseId)),
        };
        var server = new FakeMultiRoomServer(plans) { MazeWidth = 2 };
        var session = new CombatFakeSession { Respond = (cls, id, payload) => server.Handle(cls, id, payload) };
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options(overallSeconds: 60));
        StartMapNotice map = RoomKit.Map(plans[0].Ids, plans[0].CellX, plans[0].CellY, 905);
        using var cancel = new CancellationTokenSource();
        Task<OnlineDungeonCombatResult> background = Task.Run(() => runner.RunAsync(map, _ => { }, cancel.Token));
        try
        {
            await CombatKit.WaitUntil(() => runner.Machine != null, "战斗状态机建好");
            var context = new OnlineDungeonRoomBuildContext(7, GlueDungeonPath, 0, map, runner);
            var lines = new List<string>();

            OnlineDungeonRoomBuildResult result = OnlineDungeonRoomBuilder.Build(context, glue.Assets, null, 1, false, 0.0, lines.Add);

            Assert.Equal(new[]
            {
                Host + "align status=Aligned mazes=1 plan=Step exit=Right",
                Host + "align status=NoRoom fallback=single",
                Host + "status=NoRoomMap rooms=1 fallback=select",
            }, lines);
            Assert.False(result.Succeeded);
            Assert.Equal(OnlineRoomStatus.NoRoom, result.Status);
            Assert.NotNull(result.Room);
            Assert.Null(result.Run);
            Assert.Equal(HostedMazeStatus.Aligned, result.Room.Alignment);
            Assert.False(result.Room.MultiRoom);
        }
        finally
        {
            cancel.Cancel();
            await background;
        }
    }
}
