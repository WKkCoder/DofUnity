using System.Collections;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网房间托管 <see cref="HostedRoomRun.Build"/> 里"外部覆盖层钩子返回空列表"这一分支的补测(区域 B 第五轮):
/// 空覆盖层与没有覆盖层的精灵序列逐项相同(既有用例已钉),但两条路径对覆盖层容器本身的访问不同——
/// 空列表走无覆盖层的重载,宿主只读一次 <c>Count</c>、不枚举、不取下标;若误把空列表也交给带覆盖层的重载,
/// 房间会话会再读一次 <c>Count</c> 并把它枚举一遍(<c>AddRange</c>)。这里用只实现 <see cref="IReadOnlyList{T}"/> 的探针列表把这个差异读出来。
/// 房间是合成的迷你房间(<see cref="RoomCombatTests.Arena"/>),数据全是合成的。
/// </summary>
public sealed class OnlineSettlementGapB5HostedRoomHudTests
{
    /// <summary>
    /// 只实现 <see cref="IReadOnlyList{T}"/>(不实现 <see cref="ICollection{T}"/>)的探针列表:
    /// 记录 <c>Count</c> 被读了几次、被枚举(<c>GetEnumerator</c>)了几次、下标被读了几次;
    /// 不实现 <see cref="ICollection{T}"/> 是为了让 <see cref="List{T}.AddRange(IEnumerable{T})"/> 走"逐个枚举"的分支(空列表也会先要一个枚举器)。
    /// </summary>
    private sealed class SpyHud : IReadOnlyList<SceneSprite>
    {
        private readonly SceneSprite[] _items;

        public SpyHud(params SceneSprite[] items)
        {
            _items = items;
        }

        public int CountReads { get; private set; }

        public int Enumerations { get; private set; }

        public int IndexReads { get; private set; }

        public int Count
        {
            get
            {
                CountReads++;
                return _items.Length;
            }
        }

        public SceneSprite this[int index]
        {
            get
            {
                IndexReads++;
                return _items[index];
            }
        }

        public IEnumerator<SceneSprite> GetEnumerator()
        {
            Enumerations++;
            return ((IEnumerable<SceneSprite>)_items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static HostedRoomRun NewRun(RoomCombatSession room)
    {
        return new HostedRoomRun(room, _ => true, () => false);
    }

    private static List<(string Path, int X, int Y, int Sort, int Rank, int AniFrame, bool Mirrored, int Alpha, int NpcId)> Project(IEnumerable<SceneSprite> sprites)
    {
        return sprites.Select(s => (s.AnimationPath, s.ScreenX, s.ScreenY, s.SortKey, s.LayerRank, s.AniFrame, s.Mirrored, s.TintAlpha, s.NpcId)).ToList();
    }

    // 保护的行为:覆盖层钩子返回"空列表"时,宿主只做"有没有内容"的一次判断(读一次 Count),
    // 随后走不带覆盖层的构建——不把空列表交给房间会话(没有第二次 Count、没有枚举、没有下标读取),输出与没有覆盖层逐项相同;
    // 返回只有一个精灵的列表时才交给房间会话(被枚举恰好一次,精灵并入输出)。
    [Fact]
    public void EmptyOverlayListIsNotHandedToTheRoomSessionWhileANonEmptyOneIsEnumeratedOnce()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        HostedRoomRun run = NewRun(arena.Session);
        run.Update(16, 0, 0);

        var baseline = Project(run.Build(500));
        Assert.NotEmpty(baseline);

        // 空列表:钩子被叫一次,Count 只被宿主的判断读一次,容器没被枚举也没被按下标读
        var empty = new SpyHud();
        int calls = 0;
        run.HudOverlay = (r, t) =>
        {
            calls++;
            return empty;
        };
        List<SceneSprite> withEmpty = run.Build(500);
        Assert.Equal(1, calls);
        Assert.Equal(1, empty.CountReads);
        Assert.Equal(0, empty.Enumerations);
        Assert.Equal(0, empty.IndexReads);
        Assert.Equal(baseline, Project(withEmpty));

        // 再来一次:每次 Build 的访问次数都是同样的(读一次 Count、不枚举),不累积
        _ = run.Build(500);
        Assert.Equal(2, calls);
        Assert.Equal(2, empty.CountReads);
        Assert.Equal(0, empty.Enumerations);

        // 只有一个精灵的列表:Count 为 1(> 0)才交给房间会话,被枚举恰好一次,并入输出且其余项与基线相同
        SceneSprite marker = new SceneSprite(OnlineSettlementGapBKit.Pixel(), 11, 22, 33, 0, "gapb5-hud-marker", 4, layerRank: OverlayArt.UiRank);
        var single = new SpyHud(marker);
        run.HudOverlay = (r, t) => single;
        List<SceneSprite> withSingle = run.Build(500);
        Assert.Equal(1, single.Enumerations);
        Assert.True(single.CountReads >= 1);
        Assert.Equal(baseline.Count + 1, withSingle.Count);
        Assert.Same(marker, Assert.Single(withSingle, s => s.AnimationPath == "gapb5-hud-marker"));
        Assert.Equal(baseline, Project(withSingle.Where(s => s.AnimationPath != "gapb5-hud-marker")));
    }
}
