using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// 宿主给联网房间挑房间地图的纯逻辑(<see cref="OnlineDungeonRoom.PickRoomMap"/>):合成的候选表头与合成的路径、编号(9xx),不读任何脚本。
/// 起始地图通知里的地图编号"是 map/map.lst 的编号"只是推断,所以编号对不上时必须能退而求其次,而不是让宿主退回城镇。
/// </summary>
public class OnlineRoomMapPickTests
{
    private static RoomCandidate Room(string path, int id) => new RoomCandidate(path, id, 15, 15, false, false);

    private static RoomCandidate Boss(string path, int id) => new RoomCandidate(path, id, 15, 15, true, false);

    private static RoomCandidate Dummy(string path, int id) => new RoomCandidate(path, id, RoomType.Dummy, null, false, null);

    /// <summary>按路径查"有几项 [monster]"的假读取,同时记下被读过的路径(读脚本有成本,测试盯着读了几张)。</summary>
    private sealed class Counts
    {
        private readonly Dictionary<string, int> _table;

        public Counts(params (string Path, int Monsters)[] entries)
        {
            _table = entries.ToDictionary(e => e.Path, e => e.Monsters);
        }

        public List<string> Reads { get; } = new List<string>();

        public int Count(string path)
        {
            Reads.Add(path);
            return _table.TryGetValue(path, out int n) ? n : -1;
        }
    }

    [Fact]
    public void The_room_with_the_start_maps_number_is_picked_when_it_has_monsters()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900), Room("map/t/b.map", 901), Room("map/t/c.map", 902) };
        var counts = new Counts(("map/t/a.map", 2), ("map/t/b.map", 3), ("map/t/c.map", 1));

        string picked = OnlineDungeonRoom.PickRoomMap(rooms, 901, counts.Count, out bool exact);

        Assert.Equal("map/t/b.map", picked);
        Assert.True(exact);
        Assert.Equal(new[] { "map/t/b.map" }, counts.Reads);                         // 对上了就不再读别的图
    }

    [Fact]
    public void A_matching_room_without_monsters_falls_back_to_the_first_room_that_has_some_and_is_not_read_twice()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900), Room("map/t/b.map", 901), Room("map/t/c.map", 902) };
        var counts = new Counts(("map/t/a.map", 0), ("map/t/b.map", 0), ("map/t/c.map", 4));

        string picked = OnlineDungeonRoom.PickRoomMap(rooms, 901, counts.Count, out bool exact);

        Assert.Equal("map/t/c.map", picked);
        Assert.False(exact);
        Assert.Equal(new[] { "map/t/b.map", "map/t/a.map", "map/t/c.map" }, counts.Reads);   // 按编号读了 b;退而求其次从头查,跳过读过的 b
    }

    [Fact]
    public void A_number_that_is_not_in_the_pool_or_no_number_at_all_falls_back_to_the_first_room_with_monsters()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900), Room("map/t/b.map", 901) };
        var counts = new Counts(("map/t/a.map", 0), ("map/t/b.map", 2));

        Assert.Equal("map/t/b.map", OnlineDungeonRoom.PickRoomMap(rooms, 999, counts.Count, out bool unknownExact));
        Assert.False(unknownExact);
        Assert.Equal("map/t/b.map", OnlineDungeonRoom.PickRoomMap(rooms, null, counts.Count, out bool noneExact));
        Assert.False(noneExact);
    }

    [Fact]
    public void The_fallback_skips_boss_and_dummy_rooms_but_an_exact_match_may_be_a_boss_room()
    {
        RoomCandidate[] rooms = { Boss("map/t/boss.map", 910), Dummy("map/t/dummy.map", 911), Room("map/t/normal.map", 912) };
        var counts = new Counts(("map/t/boss.map", 1), ("map/t/dummy.map", 5), ("map/t/normal.map", 2));

        Assert.Equal("map/t/normal.map", OnlineDungeonRoom.PickRoomMap(rooms, null, counts.Count, out bool fallbackExact));
        Assert.False(fallbackExact);
        Assert.DoesNotContain("map/t/boss.map", counts.Reads);                       // 退而求其次时首领与假首领房间连读都不读
        Assert.DoesNotContain("map/t/dummy.map", counts.Reads);

        Assert.Equal("map/t/boss.map", OnlineDungeonRoom.PickRoomMap(rooms, 910, counts.Count, out bool bossExact));   // 服务端说的就是首领房间:按编号用它
        Assert.True(bossExact);
    }

    [Fact]
    public void The_fallback_reads_at_most_the_scan_limit_and_gives_up_without_a_guess()
    {
        var rooms = new List<RoomCandidate>();
        var entries = new List<(string, int)>();
        for (int i = 0; i < OnlineDungeonRoom.MaxMapScan + 10; i++)
        {
            string path = "map/t/r" + i + ".map";
            rooms.Add(Room(path, 900 + i));
            entries.Add((path, i == OnlineDungeonRoom.MaxMapScan + 5 ? 3 : 0));      // 唯一有怪的房间排在上限之后
        }
        var counts = new Counts(entries.ToArray());

        string picked = OnlineDungeonRoom.PickRoomMap(rooms, null, counts.Count, out bool exact);

        Assert.Null(picked);
        Assert.False(exact);
        Assert.Equal(OnlineDungeonRoom.MaxMapScan, counts.Reads.Count);
    }

    [Fact]
    public void Nothing_to_pick_gives_null_for_empty_pools_unreadable_maps_and_null_entries()
    {
        var none = new Counts();

        Assert.Null(OnlineDungeonRoom.PickRoomMap(null, 900, none.Count, out bool nullExact));
        Assert.False(nullExact);
        Assert.Null(OnlineDungeonRoom.PickRoomMap(new RoomCandidate[0], 900, none.Count, out _));
        Assert.Empty(none.Reads);

        RoomCandidate[] unreadable = { null!, Room("map/t/gone.map", 900) };      // 候选里夹着 null、地图读不出来(-1)
        Assert.Null(OnlineDungeonRoom.PickRoomMap(unreadable, 900, none.Count, out bool unreadableExact));
        Assert.False(unreadableExact);
    }

    [Fact]
    public void A_missing_reader_is_an_error()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineDungeonRoom.PickRoomMap(new[] { Room("map/t/a.map", 900) }, 900, null!, out _));
    }
}
