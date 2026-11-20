using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 副本许可适配器"进城初始快照"一侧的第三轮缺口测试(<see cref="OnlineDungeonPermissionAdapter.LoadSnapshot"/>):
/// 上一轮与现有流程测试只验证了"实时帧 → 表"与"一帧合法快照里的条目",没有任何断言覆盖快照里
/// "成功通知数"与"坏通知数"这两个相邻 int 字段是否被各归各位、以及空快照/空引用快照对表的重置语义。
/// 快照由联网层真解析器(<see cref="DungeonPermissionParser.Collect"/>)从手拼的合成帧合并得到,通知数与坏帧数取互不相同的值;
/// 没有任何真实编号或抓包内容。
/// </summary>
public sealed class OnlineDungeonAdapterGapR3SnapshotTests
{
    private static byte[] Payload(params (int DungeonId, int Tier)[] entries)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U16(entries.Length);
        foreach ((int dungeonId, int tier) in entries)
        {
            b.U16(dungeonId).U8(tier);
        }
        return b.ToArray();
    }

    private static DecodedFrame Frame(byte packetClass, ushort packetId, byte[] payload) =>
        DungeonEntryWire.Decoded(packetClass, packetId, payload);

    private static DecodedFrame Permission(byte[] payload) => Frame(CombatKit.Noti, DungeonPermissionParser.PacketId, payload);

    /// <summary>读不通的许可载荷:声明 2 条、实际只有 1 个字节。</summary>
    private static byte[] ShortBody() => new byte[] { 2, 0, 0x55 };

    /// <summary>读不通的许可载荷:连 u16 个数都读不满(只有 1 个字节)。</summary>
    private static byte[] OneByte() => new byte[] { 0x01 };

    /// <summary>
    /// 保护:进城快照填表时,条目逐条拷贝(每个副本号对应各自的档位,含显式的 0 档)、"成功通知数"进成功计数、
    /// "坏通知数"进坏帧计数(两个数不能串位)、表被标记为"收到过许可帧",并且旧条目、旧计数与本局拒绝标记被整体重置;装载使版本号恰好加 1。
    /// 推导:三条合法通知(A、B、空表 C)+ 两条读不通的通知 → NoticeCount = 3、MalformedNoticeCount = 2;
    /// 合并:A 给 9701→2、9702→3,B 给 9702→1(后到覆盖)、9703→0,所以表里是 9701→2、9702→1、9703→0 共 3 项。
    /// 另有两帧无关帧(类对包号不对、包号对类不对)不参与合并。
    /// </summary>
    [Fact]
    public void LoadSnapshotCopiesEveryEntryAndKeepsNoticeAndMalformedCountsApart()
    {
        DungeonPermissionSnapshot snapshot = DungeonPermissionParser.Collect(new[]
        {
            Permission(Payload((9701, 2), (9702, 3))),
            Permission(ShortBody()),
            Permission(Payload((9702, 1), (9703, 0))),
            Frame(CombatKit.Noti, DungeonPermissionParser.PacketId + 1, Payload((9798, 3))),
            Permission(OneByte()),
            Frame(CombatKit.Cmd, DungeonPermissionParser.PacketId, Payload((9799, 3))),
            Permission(Payload()),
        });
        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.NoticeCount); // 前提:真解析器给出的计数
        Assert.Equal(2, snapshot.MalformedNoticeCount);
        Assert.Equal(3, snapshot.Count);

        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { new KeyValuePair<int, byte>(9999, 3) }, hasSnapshot: false, noticeCount: 50, malformedCount: 60);
        table.MarkServerRejected(9999, 7);
        int versionBefore = table.Version;

        OnlineDungeonPermissionAdapter.LoadSnapshot(table, snapshot);

        Assert.True(table.HasSnapshot);
        Assert.Equal(3, table.NoticeCount);
        Assert.Equal(2, table.MalformedNoticeCount);
        Assert.Equal(3, table.EntryCount);
        Assert.True(table.TryGetTier(9701, out byte tier9701));
        Assert.Equal((byte)2, tier9701);
        Assert.True(table.TryGetTier(9702, out byte tier9702));
        Assert.Equal((byte)1, tier9702);
        Assert.True(table.TryGetTier(9703, out byte tier9703));
        Assert.Equal((byte)0, tier9703); // 显式 0 档,与"没有条目"可区分
        Assert.False(table.TryGetTier(9999, out _)); // 旧条目被重置掉
        Assert.False(table.IsServerRejected(9999, out _)); // 旧拒绝标记被清掉
        Assert.False(table.TryGetTier(9798, out _)); // 无关帧没有合并进来
        Assert.False(table.TryGetTier(9799, out _));
        Assert.Equal(versionBefore + 1, table.Version);
    }

    /// <summary>
    /// 保护:只有"空表通知"的快照(条目 0、成功通知 1、坏通知 0)与只有"读不通通知"的快照(条目 0、成功 0、坏 2)
    /// 都会把表标记为"收到过许可帧",并把各自的计数装进对应的字段——两种形状里成功计数与坏帧计数一个为 0、一个非 0,
    /// 与上一个用例(3 与 2)合起来,任何把两个计数互换的写法都会在其中至少一处露馅。
    /// </summary>
    [Fact]
    public void LoadSnapshotWithOnlyEmptyOrOnlyMalformedNoticesStillMarksTheTableAndKeepsCounts()
    {
        DungeonPermissionSnapshot emptyOnly = DungeonPermissionParser.Collect(new[] { Permission(Payload()) });
        Assert.NotNull(emptyOnly);
        Assert.Equal(0, emptyOnly.Count);
        Assert.Equal(1, emptyOnly.NoticeCount);
        Assert.Equal(0, emptyOnly.MalformedNoticeCount);

        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonPermissionAdapter.LoadSnapshot(table, emptyOnly);
        Assert.True(table.HasSnapshot);
        Assert.Equal(0, table.EntryCount);
        Assert.Equal(1, table.NoticeCount);
        Assert.Equal(0, table.MalformedNoticeCount);
        Assert.Equal(1, table.Version);

        DungeonPermissionSnapshot malformedOnly = DungeonPermissionParser.Collect(new[] { Permission(ShortBody()), Permission(OneByte()) });
        Assert.NotNull(malformedOnly);
        Assert.Equal(0, malformedOnly.Count);
        Assert.Equal(0, malformedOnly.NoticeCount);
        Assert.Equal(2, malformedOnly.MalformedNoticeCount);

        OnlineDungeonPermissionAdapter.LoadSnapshot(table, malformedOnly);
        Assert.True(table.HasSnapshot);
        Assert.Equal(0, table.EntryCount);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(2, table.MalformedNoticeCount);
        Assert.Equal(2, table.Version);
    }

    /// <summary>
    /// 保护:没有快照(进城时一个许可帧都没收到,快照为空引用)时,装载把表重置成"没收到过":
    /// 旧条目、旧计数与本局拒绝标记全部清掉,不标记为收到过,版本号加 1;之后实时帧照常可以合并进来。
    /// </summary>
    [Fact]
    public void LoadSnapshotWithoutSnapshotResetsTheTableToNothingReceived()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(
            new[] { new KeyValuePair<int, byte>(9701, 2), new KeyValuePair<int, byte>(9702, 3) },
            hasSnapshot: true,
            noticeCount: 5,
            malformedCount: 4);
        table.MarkServerRejected(9701, 9);
        int versionBefore = table.Version;

        OnlineDungeonPermissionAdapter.LoadSnapshot(table, null);

        Assert.False(table.HasSnapshot);
        Assert.Equal(0, table.EntryCount);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(0, table.MalformedNoticeCount);
        Assert.False(table.TryGetTier(9701, out _));
        Assert.False(table.IsServerRejected(9701, out _));
        Assert.Equal(versionBefore + 1, table.Version);

        Assert.True(OnlineDungeonPermissionAdapter.TryApplyNotificationPayload(table, Payload((9703, 1)), out bool parsed));
        Assert.True(parsed);
        Assert.True(table.HasSnapshot);
        Assert.Equal(1, table.NoticeCount);
        Assert.Equal((byte)1, table.GetTier(9703));
    }
}
