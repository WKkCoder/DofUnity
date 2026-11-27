using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// r20fix-e2e C-2:无人值守驱动在选图屏上"一步一步走到目标"的光标导航(<c>StepSelectTowards</c> / <c>StepsTo</c> / <c>RestoreIndex</c>)。
/// 表驱动:条目数 1 至 6、起点与目标的所有组合、其余条目全部灰显(导航穿过灰显条目照常走);无地图(导航按下标)与有地图(导航按入口位置从上到下、同高从左到右、
/// 没有卡片的接在后面)两种构造都覆盖,有地图的排列都是手算好的置换,所以驱动不能靠比较 <c>Index</c> 的大小走。
/// 每次调用 <c>StepGame</c> 之后断言:① 光标在导航数组里恰好朝目标移了一格;② 到位恰好用"距离"次调用;③ 中途不越过目标、不离开目标再回来;
/// ④ 难度没变、通知没变(这条只在"起始通知为空、各条目难度上限一致"的前提下成立:驱动探测方向时真实移动光标,移动会清掉通知,上限不一致时还会夹难度)。
/// 到位之后的下一次调用确认进入。所有数据都是合成的。
/// </summary>
public sealed class OnlineAutoDriverSelectNavGapTests
{
    private sealed class NoSprites : ISpriteSource
    {
        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            sprite = null;
            return false;
        }
    }

    /// <summary>一种排列:每个条目的卡片位置(null 表示没有卡片)与手算出来的上下键走法(导航数组:第 k 位是哪个条目下标)。</summary>
    private sealed class Layout
    {
        public Layout(string name, bool map, (int X, int Y)?[] markers, int[] nav)
        {
            Name = name;
            Map = map;
            Markers = markers;
            Nav = nav;
        }

        public string Name { get; }
        public bool Map { get; }
        public (int X, int Y)?[] Markers { get; }
        public int[] Nav { get; }
        public int Count => Markers.Length;

        public int PositionOf(int entryIndex)
        {
            for (int p = 0; p < Nav.Length; p++)
            {
                if (Nav[p] == entryIndex)
                {
                    return p;
                }
            }
            throw new InvalidOperationException("导航数组里没有条目 " + entryIndex);
        }
    }

    private static readonly Layout[] Layouts = BuildLayouts();

    private static Layout[] BuildLayouts()
    {
        var list = new List<Layout>();

        // 无地图:导航就是下标顺序,卡片位置(故意与下标顺序相反)不起作用
        for (int n = 1; n <= 6; n++)
        {
            var markers = new (int X, int Y)?[n];
            var nav = new int[n];
            for (int i = 0; i < n; i++)
            {
                markers[i] = (100 + 7 * i, 600 - 100 * i);
                nav[i] = i;
            }
            list.Add(new Layout("nomap-" + n, false, markers, nav));
        }

        // 有地图:按 (Y, X) 从上到下、同高从左到右;没有卡片的接在后面并保持原先后(以下置换都是手算的)
        list.Add(new Layout("map-1", true, new (int X, int Y)?[] { (300, 100) }, new[] { 0 }));
        list.Add(new Layout("map-2", true, new (int X, int Y)?[] { (300, 300), (300, 100) }, new[] { 1, 0 }));
        list.Add(new Layout("map-3", true, new (int X, int Y)?[] { (500, 200), (100, 200), (300, 50) }, new[] { 2, 1, 0 }));
        list.Add(new Layout("map-4", true, new (int X, int Y)?[] { (300, 100), null, (200, 100), (400, 50) }, new[] { 3, 2, 0, 1 }));
        list.Add(new Layout("map-5", true, new (int X, int Y)?[] { (100, 400), (100, 300), (100, 200), (100, 100), (100, 500) }, new[] { 3, 2, 1, 0, 4 }));
        list.Add(new Layout("map-6", true, new (int X, int Y)?[] { null, (10, 250), null, (5, 250), (700, 100), (20, 600) }, new[] { 4, 3, 1, 5, 0, 2 }));
        return list.ToArray();
    }

    private static Layout LayoutNamed(string name)
    {
        foreach (Layout layout in Layouts)
        {
            if (layout.Name == name)
            {
                return layout;
            }
        }
        throw new InvalidOperationException("没有这个排列:" + name);
    }

    public static TheoryData<string, int, int> StartTargetPairs()
    {
        var data = new TheoryData<string, int, int>();
        foreach (Layout layout in Layouts)
        {
            for (int start = 0; start < layout.Count; start++)
            {
                for (int target = 0; target < layout.Count; target++)
                {
                    if (start != target)
                    {
                        data.Add(layout.Name, start, target);
                    }
                }
            }
        }
        return data;
    }

    public static TheoryData<string, int> StartsWithEverythingEnabled()
    {
        var data = new TheoryData<string, int>();
        foreach (Layout layout in Layouts)
        {
            for (int start = 0; start < layout.Count; start++)
            {
                data.Add(layout.Name, start);
            }
        }
        return data;
    }

    private static DungeonEntry[] EntriesOf(Layout layout)
    {
        var entries = new DungeonEntry[layout.Count];
        for (int i = 0; i < layout.Count; i++)
        {
            string name = "e" + i;
            DungeonCard card = layout.Markers[i].HasValue
                ? new DungeonCard(9100 + i, layout.Markers[i].Value.X, layout.Markers[i].Value.Y, 0, null, 0, 1)
                : null;
            entries[i] = new DungeonEntry("dungeon/test/" + name + ".dgn", name, card);
        }
        return entries;
    }

    /// <summary>
    /// 选图屏停在 <paramref name="start"/> 号条目上(起点用导航位置跳过去:先夹到第 0 位、再前进手算好的位数);难度起在 1,各条目上限一致(2)。
    /// 难度上限的回调顺带当"光标足迹"用:选图屏每次 <c>Move</c> 都会按移动之后选中的条目问一次上限,<paramref name="visits"/> 里就是条目下标的先后顺序
    /// (建屏与摆起点时的几次已经清掉)。
    /// </summary>
    private static GameFlow SelectAt(Layout layout, int start, List<int> visits)
    {
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        WorldMapScene map = layout.Map ? new WorldMapScene(new NoSprites(), "sprite/test/map.img", 0, "R") : null;
        DungeonEntry[] entries = EntriesOf(layout);
        var flow = new GameFlow(world, (p, _) => new OnlineSettlementGapAKit.OtherRun(), entries, map)
        {
            DifficultyLimit = e =>
            {
                visits.Add(Array.IndexOf(entries, e));
                return 2;
            },
        };
        flow.EnterTown("boot");
        flow.EnterSelect("test");
        flow.Select.Move(-100);
        flow.Select.Move(layout.PositionOf(start));
        flow.Select.MoveDifficulty(1);
        Assert.Equal(start, flow.Select.Index);
        Assert.Equal(1, flow.Select.Difficulty);
        Assert.Equal(2, flow.Select.MaxDifficulty);
        Assert.Null(flow.Select.Notice);
        Assert.DoesNotContain(-1, visits);
        visits.Clear();
        return flow;
    }

    /// <summary>
    /// 逐帧驱动:每次调用 <c>StepGame</c> 光标恰好朝目标移一格;最后一格走完之后的下一次调用确认进入。
    /// 每次调用里光标的足迹(探测与复位也算)在导航数组里一次只挪一格、不跳格——驱动是在替人按方向键。
    /// </summary>
    private static void WalkAndConfirm(Layout layout, GameFlow flow, List<int> visits, int start, int target)
    {
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonMode: 1));
        int targetPosition = layout.PositionOf(target);
        int position = layout.PositionOf(start);
        int distance = Math.Abs(targetPosition - position);
        long now = 100;

        for (int call = 1; call <= distance; call++)
        {
            int before = position;
            visits.Clear();
            driver.StepGame(flow, null, null, 16.0, now += 16);
            position = layout.PositionOf(flow.Select.Index);

            // 足迹:从调用前的位置出发,每一次移动最多挪一格,最后停在调用后的位置上;探测一定动过光标
            Assert.NotEmpty(visits);
            int footprint = before;
            foreach (int visited in visits)
            {
                int visitedPosition = layout.PositionOf(visited);
                Assert.True(Math.Abs(visitedPosition - footprint) <= 1, "第 " + call + " 次调用里光标从导航位置 " + footprint + " 一步跳到了 " + visitedPosition);
                footprint = visitedPosition;
            }
            Assert.Equal(position, footprint);

            // ① 恰好朝目标移一格(也就是 ③ 没越过目标、没离开目标再回来)
            Assert.Equal(before + Math.Sign(targetPosition - before), position);
            Assert.Equal(distance - call, Math.Abs(targetPosition - position));
            // ④ 难度与通知没变;驱动没收尾、屏没出结果
            Assert.Equal(1, flow.Select.Difficulty);
            Assert.Null(flow.Select.Notice);
            Assert.False(driver.Finished);
            Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
            Assert.False(flow.Select.Busy);
        }

        // ② 恰好用了"距离"次调用到位
        Assert.Equal(target, flow.Select.Index);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);

        driver.StepGame(flow, null, null, 16.0, now += 16);
        Assert.Equal(SelectOutcome.Enter, flow.Select.Outcome);
        Assert.Equal(target, flow.Select.Index);
        Assert.Equal(1, flow.Select.Difficulty);
        Assert.False(driver.Finished);
    }

    [Theory]
    [MemberData(nameof(StartTargetPairs))]
    public void Driver_walks_one_step_per_call_straight_to_the_only_enabled_entry(string layoutName, int start, int target)
    {
        Layout layout = LayoutNamed(layoutName);
        var visits = new List<int>();
        GameFlow flow = SelectAt(layout, start, visits);
        DungeonEntry targetEntry = flow.Select.Entries[target];
        flow.Select.EntryStatus = e => ReferenceEquals(e, targetEntry) ? DungeonEntryStatus.Allowed : new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        Assert.Equal(layout.Count, flow.Select.Entries.Count);
        Assert.False(flow.Select.StatusOf(targetEntry).Disabled);
        for (int i = 0; i < layout.Count; i++)
        {
            Assert.Equal(i != target, flow.Select.StatusOf(flow.Select.Entries[i]).Disabled);
        }

        WalkAndConfirm(layout, flow, visits, start, target);
    }

    [Theory]
    [MemberData(nameof(StartsWithEverythingEnabled))]
    public void Driver_heads_for_the_lowest_listed_entry_not_the_first_one_in_walking_order_when_everything_is_enabled(string layoutName, int start)
    {
        Layout layout = LayoutNamed(layoutName);
        var visits = new List<int>();
        GameFlow flow = SelectAt(layout, start, visits);

        WalkAndConfirm(layout, flow, visits, start, 0);
    }

    [Theory]
    [InlineData("nomap-1")]
    [InlineData("map-1")]
    public void A_single_entry_is_confirmed_by_the_first_call_without_any_move(string layoutName)
    {
        Layout layout = LayoutNamed(layoutName);
        GameFlow flow = SelectAt(layout, 0, new List<int>());
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonMode: 1));

        driver.StepGame(flow, null, null, 16.0, 500);

        Assert.Equal(SelectOutcome.Enter, flow.Select.Outcome);
        Assert.Equal(0, flow.Select.Index);
        Assert.Equal(1, flow.Select.Difficulty);
        Assert.False(driver.Finished);
    }
}
