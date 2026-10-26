using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 副本会话给任务系统的事件(<see cref="IQuestEvents"/>,M31):杀怪、清房(只算打空的、不算进门就空的)、通关(带用时);
/// 事件的副本编号与难度随会话走,后进的房间进门时接上,可以取消。迷宫与房间是合成的(同 <see cref="DungeonSessionTests"/>),数据全是合成的。
/// </summary>
public class QuestHookTests
{
    private sealed class Recorder : IQuestEvents
    {
        public List<(int Dungeon, int Difficulty, int Monster, bool Boss)> Kills { get; } = new();

        public List<int> Maps { get; } = new();

        public List<(int Dungeon, int Difficulty, int Milliseconds)> Dungeons { get; } = new();

        public void MonsterKilled(int dungeonId, int difficulty, int monsterCode, bool boss) => Kills.Add((dungeonId, difficulty, monsterCode, boss));

        public void MapCleared(int mapId) => Maps.Add(mapId);

        public void DungeonCleared(int dungeonId, int difficulty, int clearMilliseconds) => Dungeons.Add((dungeonId, difficulty, clearMilliseconds));
    }

    private static (int X, int Y)[] OneWeakAtStart(int cx, int cy) => cx == 0 && cy == 0 ? new[] { (340, 100) } : Array.Empty<(int, int)>();

    private static void RunUntil(DungeonSession s, Func<bool> done, int dirX, int dirY, int maxMs = 20000)
    {
        for (int t = 0; t < maxMs && !done(); t += 10)
        {
            s.Update(10, dirX, dirY, false);
        }
        Assert.True(done(), "条件在 " + maxMs + " 毫秒内没有满足");
    }

    [Fact]
    public void KillingTheLastMonsterReportsTheKillAndTheMapClear()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), OneWeakAtStart);
        var recorder = new Recorder();
        d.Session.UseQuests(recorder, 33, 2);
        d.Session.Update(10, 0, 0, true);
        for (int t = 0; t < 1200; t += 10)
        {
            d.Session.Update(10, 0, 0, false);
        }
        Assert.Equal((33, 2, 1, false), Assert.Single(recorder.Kills));
        Assert.Equal(d.Layout.RoomAt(0, 0).MapId, Assert.Single(recorder.Maps));
        Assert.Empty(recorder.Dungeons);                                   // 起点格不是首领格
        Assert.Equal(33, d.Session.QuestDungeonId);
        Assert.Equal(2, d.Session.QuestDifficulty);
    }

    [Fact]
    public void RoomsThatAreEmptyOnEntryDoNotReportAMapClear()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), OneWeakAtStart);
        var recorder = new Recorder();
        d.Session.UseQuests(recorder, 5);
        d.Session.Update(10, 0, 0, true);
        for (int t = 0; t < 1200; t += 10)
        {
            d.Session.Update(10, 0, 0, false);
        }
        RunUntil(d.Session, () => d.Session.CellX == 1, 1, 0);             // 走进右边的空房间
        Assert.True(d.Session.IsCleared(1, 0));
        Assert.Equal(d.Layout.RoomAt(0, 0).MapId, Assert.Single(recorder.Maps));
    }

    [Fact]
    public void ClearingTheBossRoomReportsTheDungeonClearWithTheElapsedTime()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.TwoRoomBossLayout(), (cx, cy) => Array.Empty<(int, int)>());
        var recorder = new Recorder();
        d.Session.UseQuests(recorder, 12, 1);
        Assert.Empty(recorder.Dungeons);
        RunUntil(d.Session, () => d.Session.BossCleared, 1, 0);
        (int dungeon, int difficulty, int ms) = Assert.Single(recorder.Dungeons);
        Assert.Equal((12, 1), (dungeon, difficulty));
        Assert.True(ms > 0);
        Assert.Equal((int)d.Session.BossClearedAtMs, ms);
        // 通关只报一次,之后接着走也不重复
        for (int t = 0; t < 500; t += 10)
        {
            d.Session.Update(10, 0, 0, false);
        }
        Assert.Single(recorder.Dungeons);
    }

    [Fact]
    public void TheContextReachesRoomsCreatedLaterAndCanBeRemoved()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), OneWeakAtStart);
        var recorder = new Recorder();
        d.Session.UseQuests(recorder, 9, 3);
        Assert.Same(recorder, d.Rooms[(0, 0)].QuestEvents);
        Assert.Equal((9, 3), (d.Rooms[(0, 0)].QuestDungeonId, d.Rooms[(0, 0)].QuestDifficulty));

        d.Session.Update(10, 0, 0, true);
        for (int t = 0; t < 1200; t += 10)
        {
            d.Session.Update(10, 0, 0, false);
        }
        RunUntil(d.Session, () => d.Session.CellX == 1, 1, 0);
        Assert.Same(recorder, d.Rooms[(1, 0)].QuestEvents);                // 后造的房间进门时接上
        Assert.Equal(9, d.Rooms[(1, 0)].QuestDungeonId);

        d.Session.UseQuests(null, -1);
        Assert.All(d.Rooms.Values, room => Assert.Null(room.QuestEvents));
        Assert.Equal(-1, d.Session.QuestDungeonId);
    }

    [Fact]
    public void SessionsWithoutQuestsDefaultToNoContext()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), OneWeakAtStart);
        Assert.Equal(-1, d.Session.QuestDungeonId);
        Assert.Equal(0, d.Session.QuestDifficulty);
        Assert.Null(d.Rooms[(0, 0)].QuestEvents);
        Assert.Equal(-1, d.Rooms[(0, 0)].QuestDungeonId);
    }
}
