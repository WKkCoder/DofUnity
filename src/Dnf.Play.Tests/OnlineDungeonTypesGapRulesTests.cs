using Dnf.Play;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本规则层的"缺口"测试(权限表、难度到模式的换算、区域种类词、选择策略的编号解析与边界)。
/// 现有覆盖在 <c>OnlineDungeonSelectTests</c>;这里只补它没有断言的行为:边界两侧各取一值、计数与版本号逐项断言。
/// 全部用合成数据,不碰真实脚本、编号或路径。
/// </summary>
public class OnlineDungeonTypesGapRulesTests
{
    private static KeyValuePair<int, byte> Tier(int dungeonId, byte tier) => new KeyValuePair<int, byte>(dungeonId, tier);

    private static DungeonEntry CardEntry(int dungeonId, string path = null)
    {
        return new DungeonEntry(
            path ?? ("dungeon/test/gap" + dungeonId + ".dgn"),
            "GAP" + dungeonId,
            new DungeonCard(dungeonId, 10, 10, 0, null, 0, 1));
    }

    // ------------------------------------------------------------------------------------------
    // 难度 → 模式字节、区域种类词
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:选择屏难度换算成 CMD 16 模式字节时,负数与 0 归 0,区间内原样,>= 上限(MaxMode)一律夹到上限;
    /// 在上限前一个(3)、正好等于上限(4)、刚超过上限(5)与极端值两侧各取一例。
    /// 推导:MaxMode = DungeonClears.Difficulties(5) - 1 = 4。
    /// </summary>
    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(255, 4)]
    [InlineData(int.MaxValue, 4)]
    public void DifficultyToModeClampsBothEndsAndKeepsInRangeValues(int difficulty, int expectedMode)
    {
        Assert.Equal(4, OnlineDungeonPermissionRules.MaxMode);
        Assert.Equal((byte)expectedMode, OnlineDungeonPermissionRules.DifficultyToMode(difficulty));
    }

    /// <summary>
    /// 保护:日志里的区域种类词来自城镇区域图册条目的种类名(枚举名,不是数字),
    /// 没有图册条目(null)时是固定的 "unknown"。
    /// </summary>
    [Fact]
    public void AreaKindTokenUsesKindNameAndFallsBackToUnknownForMissingInfo()
    {
        Assert.Equal("unknown", OnlineDungeonPermissionRules.AreaKindToken(null));

        TownAreaInfo Info(TownAreaKind kind)
        {
            return new TownAreaInfo(
                1,
                new TownArea(2, "map/test/gap.map", kind, Array.Empty<int>(), null, false),
                "map/test/gap.map",
                Array.Empty<WorldRect>(),
                Array.Empty<TownExit>());
        }

        Assert.Equal("Normal", OnlineDungeonPermissionRules.AreaKindToken(Info(TownAreaKind.Normal)));
        Assert.Equal("Gate", OnlineDungeonPermissionRules.AreaKindToken(Info(TownAreaKind.Gate)));
        Assert.Equal("DungeonGate", OnlineDungeonPermissionRules.AreaKindToken(Info(TownAreaKind.DungeonGate)));
        Assert.Equal("SafeZoneB", OnlineDungeonPermissionRules.AreaKindToken(Info(TownAreaKind.SafeZoneB)));
    }

    // ------------------------------------------------------------------------------------------
    // 权限表:初始快照
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:初始快照里键为 0 的条目合法(边界),负键条目被丢弃(边界另一侧);
    /// 通知/坏帧计数传负数时钳到 0;每次装载 Version 加 1。
    /// </summary>
    [Fact]
    public void LoadInitialKeepsKeyZeroDropsNegativeKeysAndClampsNegativeCounters()
    {
        var table = new OnlineDungeonPermissionTable();
        Assert.Equal(0, table.Version);
        Assert.False(table.HasSnapshot);

        table.LoadInitial(new[] { Tier(0, 2), Tier(-1, 3), Tier(7, 1) }, hasSnapshot: true, noticeCount: -5, malformedCount: -3);

        Assert.True(table.HasSnapshot);
        Assert.Equal(2, table.EntryCount); // 键 0 与 7 留下,键 -1 丢弃
        Assert.Equal((byte)2, table.GetTier(0));
        Assert.True(table.TryGetTier(0, out byte tierOfZero));
        Assert.Equal((byte)2, tierOfZero);
        Assert.Equal((byte)1, table.GetTier(7));
        Assert.False(table.TryGetTier(-1, out _));
        Assert.Equal((byte)0, table.GetTier(-1));
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(0, table.MalformedNoticeCount);
        Assert.Equal(1, table.Version);
    }

    /// <summary>
    /// 保护:计数参数在 -1 / 0 / 1 三处边界的取值(-1 → 0,0 → 0,1 → 1),
    /// 以及 hasSnapshot=false 的装载不会把 HasSnapshot 置真、entries 为 null 时表为空。
    /// </summary>
    [Fact]
    public void LoadInitialCounterBoundariesAndNullEntries()
    {
        var table = new OnlineDungeonPermissionTable();

        table.LoadInitial(null, hasSnapshot: false, noticeCount: 4, malformedCount: 2);
        Assert.False(table.HasSnapshot);
        Assert.Equal(0, table.EntryCount);
        Assert.Equal(4, table.NoticeCount);
        Assert.Equal(2, table.MalformedNoticeCount);
        Assert.Equal(1, table.Version);

        table.LoadInitial(null, hasSnapshot: false, noticeCount: 1, malformedCount: 1);
        Assert.Equal(1, table.NoticeCount);
        Assert.Equal(1, table.MalformedNoticeCount);

        table.LoadInitial(null, hasSnapshot: false, noticeCount: 0, malformedCount: 0);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(0, table.MalformedNoticeCount);

        table.LoadInitial(null, hasSnapshot: false, noticeCount: -1, malformedCount: -1);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(0, table.MalformedNoticeCount);
        Assert.Equal(4, table.Version); // 四次装载各加 1

        table.LoadInitial(new[] { Tier(3, 1) }, hasSnapshot: true);
        Assert.True(table.HasSnapshot);
        Assert.Equal(0, table.NoticeCount); // 不给计数时缺省为 0
        Assert.Equal(0, table.MalformedNoticeCount);
    }

    /// <summary>保护:再次装载初始快照会清掉旧条目与旧的本局拒绝标记(是"重置",不是"合并")。</summary>
    [Fact]
    public void LoadInitialReplacesOldTiersAndClearsRejections()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { Tier(11, 3), Tier(12, 2) }, hasSnapshot: true);
        table.MarkServerRejected(11, 6);
        Assert.True(table.IsServerRejected(11, out _));

        table.LoadInitial(new[] { Tier(13, 1) }, hasSnapshot: true);

        Assert.Equal(1, table.EntryCount);
        Assert.False(table.TryGetTier(11, out _));
        Assert.Equal((byte)1, table.GetTier(13));
        Assert.False(table.IsServerRejected(11, out byte? code));
        Assert.Null(code);
    }

    // ------------------------------------------------------------------------------------------
    // 权限表:增量合并、坏帧、拒绝标记
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:每条 (0,5) 通知都使 NoticeCount 与 Version 加 1 并把 HasSnapshot 置真(即使 entries 为 null 或为空);
    /// 键 0 合法、负键丢弃;同一通知里后到的条目覆盖先到的;只解除被更新副本的拒绝标记;坏帧计数不受影响。
    /// 推导:装载时 noticeCount=4,三次更新后 NoticeCount=4+3=7;
    /// Version:装载 1,三次拒绝标记 2、3、4,三次更新 5、6、7。
    /// </summary>
    [Fact]
    public void ApplyUpdateCountsEveryNoticeAndMergesByKeyWithLaterEntryWinning()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(null, hasSnapshot: false, noticeCount: 4, malformedCount: 2); // Version=1
        table.MarkServerRejected(0, 9); // Version=2
        table.MarkServerRejected(5, 8); // Version=3
        table.MarkServerRejected(6, 7); // Version=4
        Assert.False(table.HasSnapshot);

        table.ApplyUpdate(new[] { Tier(0, 3), Tier(-2, 1), Tier(5, 0), Tier(4, 1), Tier(4, 2) });

        Assert.True(table.HasSnapshot);
        Assert.Equal(5, table.NoticeCount);
        Assert.Equal(2, table.MalformedNoticeCount);
        Assert.Equal(5, table.Version);
        Assert.Equal(3, table.EntryCount); // 键 0、5、4;负键 -2 丢弃
        Assert.Equal((byte)3, table.GetTier(0));
        Assert.True(table.TryGetTier(5, out byte explicitZero));
        Assert.Equal((byte)0, explicitZero); // 显式写入的 0 档与"没有条目"可区分
        Assert.Equal((byte)2, table.GetTier(4)); // 后到覆盖先到
        Assert.False(table.IsServerRejected(0, out _));
        Assert.False(table.IsServerRejected(5, out _));
        Assert.True(table.IsServerRejected(6, out byte? untouched)); // 没被更新的副本保留拒绝标记
        Assert.Equal((byte)7, untouched);

        table.ApplyUpdate(null);
        Assert.Equal(6, table.NoticeCount);
        Assert.Equal(6, table.Version);
        Assert.Equal(3, table.EntryCount);

        table.ApplyUpdate(Array.Empty<KeyValuePair<int, byte>>());
        Assert.Equal(7, table.NoticeCount);
        Assert.Equal(7, table.Version);
    }

    /// <summary>
    /// 保护:新表上收到坏帧就把 HasSnapshot 置真(服务端确实发过 (0,5)),只增加坏帧计数,
    /// 不增加成功通知数、不改条目、不动 Version。
    /// </summary>
    [Fact]
    public void RecordMalformedNoticeRaisesSnapshotFlagWithoutTouchingEntriesOrVersion()
    {
        var table = new OnlineDungeonPermissionTable();
        Assert.False(table.HasSnapshot);

        table.RecordMalformedNotice();

        Assert.True(table.HasSnapshot);
        Assert.Equal(1, table.MalformedNoticeCount);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(0, table.EntryCount);
        Assert.Equal(0, table.Version);

        table.RecordMalformedNotice();
        Assert.Equal(2, table.MalformedNoticeCount);
        Assert.Equal(0, table.Version);
    }

    /// <summary>
    /// 保护:本局拒绝标记的 Version 语义——合法编号(含 0)每标一次加 1,负编号被忽略不加;
    /// 重复标记会覆盖拒绝码;清除只在确有标记时加 1,空清除不加。
    /// </summary>
    [Fact]
    public void ServerRejectionsBumpVersionOnlyWhenStateActuallyChanges()
    {
        var table = new OnlineDungeonPermissionTable();
        Assert.Equal(0, table.Version);

        table.MarkServerRejected(-1, 5);
        Assert.Equal(0, table.Version); // 负编号:忽略
        Assert.False(table.IsServerRejected(-1, out _));

        table.MarkServerRejected(0, 5);
        Assert.Equal(1, table.Version); // 编号 0 合法
        Assert.True(table.IsServerRejected(0, out byte? codeZero));
        Assert.Equal((byte)5, codeZero);

        table.MarkServerRejected(9101);
        Assert.Equal(2, table.Version);
        Assert.True(table.IsServerRejected(9101, out byte? noCode));
        Assert.Null(noCode); // 没给拒绝码

        table.MarkServerRejected(9101, 8);
        Assert.Equal(3, table.Version); // 重复标记照样加 1
        Assert.True(table.IsServerRejected(9101, out byte? overwritten));
        Assert.Equal((byte)8, overwritten);

        table.ClearServerRejections();
        Assert.Equal(4, table.Version);
        Assert.False(table.IsServerRejected(0, out _));
        Assert.False(table.IsServerRejected(9101, out _));

        table.ClearServerRejections();
        Assert.Equal(4, table.Version); // 本来就空:不加
    }

    // ------------------------------------------------------------------------------------------
    // 选择策略:创建、编号解析、编号边界与本地数据检查
    // ------------------------------------------------------------------------------------------

    /// <summary>保护:许可表为空时 Create 抛 ArgumentNullException(参数名 table),而不是返回 null 让后续处处空引用。</summary>
    [Fact]
    public void CreateRejectsNullTable()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => OnlineDungeonSelectPolicy.Create(null));
        Assert.Equal("table", ex.ParamName);

        ex = Assert.Throws<ArgumentNullException>(() => OnlineDungeonSelectPolicy.Create(null, hasLocalData: _ => true, hasUltimate: _ => true));
        Assert.Equal("table", ex.ParamName);
    }

    /// <summary>
    /// 保护:副本编号解析的优先级——卡片编号 >= 0(含 0)直接采用;卡片编号为负才退回外部解析委托;
    /// 空条目为 -1;既无卡片又无委托又无资源时为 -1。
    /// </summary>
    [Fact]
    public void ResolveDungeonIdPrefersCardIdIncludingZeroThenFallsBackToResolver()
    {
        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonSelectPolicy withResolver = OnlineDungeonSelectPolicy.Create(table, idResolver: _ => 777, hasLocalData: _ => true);

        Assert.Equal(0, withResolver.ResolveDungeonId(CardEntry(0))); // 卡片编号 0 不能被当成"没有"
        Assert.Equal(1, withResolver.ResolveDungeonId(CardEntry(1)));
        Assert.Equal(777, withResolver.ResolveDungeonId(CardEntry(-1))); // 卡片编号为负:退回委托
        Assert.Equal(777, withResolver.ResolveDungeonId(new DungeonEntry("dungeon/test/nocard.dgn", "NOCARD")));
        Assert.Equal(-1, withResolver.ResolveDungeonId(null));

        OnlineDungeonSelectPolicy bare = OnlineDungeonSelectPolicy.Create(table, hasLocalData: _ => true);
        Assert.Equal(-1, bare.ResolveDungeonId(new DungeonEntry("dungeon/test/nocard.dgn", "NOCARD")));
        Assert.Equal(-1, bare.ResolveDungeonId(null));
        Assert.Equal(5, bare.ResolveDungeonId(CardEntry(5)));
    }

    /// <summary>
    /// 保护:副本编号必须落在 0..65535(u16)内才算解析到——0 与 65535 有效,-1 与 65536 无效(MissingDungeonId);
    /// 无效编号的可选最高难度恒为 0,哪怕许可表里恰好有同号条目。
    /// </summary>
    [Fact]
    public void DungeonIdBoundariesZeroAndUshortMaxAreValidAndOneBeyondIsMissing()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { Tier(0, 1), Tier(65535, 2), Tier(65536, 3) }, hasSnapshot: true);
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
            table,
            idResolver: _ => -1,
            hasLocalData: _ => true,
            hasUltimate: _ => true);

        DungeonEntryStatus atZero = policy.EntryStatus(CardEntry(0));
        Assert.False(atZero.Disabled);
        Assert.Equal(DungeonEntryBlockKind.None, atZero.Kind);

        DungeonEntryStatus atMax = policy.EntryStatus(CardEntry(65535));
        Assert.False(atMax.Disabled);
        Assert.Equal(DungeonEntryBlockKind.None, atMax.Kind);

        DungeonEntryStatus beyondMax = policy.EntryStatus(CardEntry(65536));
        Assert.True(beyondMax.Disabled);
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, beyondMax.Kind);

        DungeonEntryStatus negative = policy.EntryStatus(CardEntry(-1)); // 卡片 -1 → 委托也给 -1
        Assert.True(negative.Disabled);
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, negative.Kind);

        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, policy.EntryStatus(null).Kind);

        Assert.Equal(1, policy.DifficultyLimit(CardEntry(0)));
        Assert.Equal(2, policy.DifficultyLimit(CardEntry(65535)));
        Assert.Equal(0, policy.DifficultyLimit(CardEntry(65536)));
        Assert.Equal(0, policy.DifficultyLimit(CardEntry(-1)));
        Assert.Equal(0, policy.DifficultyLimit(null));
    }

    /// <summary>
    /// 保护:没有外部"是否有本地数据"委托、也没有资源包时,本地数据检查退化为路径是否为空——
    /// 空路径视为缺本地数据(MissingLocalData),非空路径视为有;检查顺序是编号无效 → 缺本地数据 → 服务端拒绝。
    /// </summary>
    [Fact]
    public void DefaultLocalDataCheckTreatsEmptyPathAsMissingAndNonEmptyAsPresent()
    {
        var table = new OnlineDungeonPermissionTable();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasUltimate: _ => true);

        DungeonEntryStatus emptyPath = policy.EntryStatus(CardEntry(21, path: string.Empty));
        Assert.True(emptyPath.Disabled);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, emptyPath.Kind);
        Assert.Null(emptyPath.Code);

        DungeonEntryStatus withPath = policy.EntryStatus(CardEntry(22));
        Assert.False(withPath.Disabled);
        Assert.Equal(DungeonEntryBlockKind.None, withPath.Kind);

        // 缺本地数据优先于服务端拒绝:空路径条目即便被标记拒绝,原因仍是 MissingLocalData
        table.MarkServerRejected(21, 3);
        table.MarkServerRejected(22, 4);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, policy.EntryStatus(CardEntry(21, path: string.Empty)).Kind);
        DungeonEntryStatus rejected = policy.EntryStatus(CardEntry(22));
        Assert.Equal(DungeonEntryBlockKind.ServerRejected, rejected.Kind);
        Assert.Equal((byte)4, rejected.Code);
    }
}
