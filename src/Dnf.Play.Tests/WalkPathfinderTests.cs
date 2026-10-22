using Dnf.Play;
using Dnf.Render;

namespace Dnf.Play.Tests;

public class WalkPathfinderTests
{
    private const int RoomWidth = 200;
    private const int RoomHeight = 100;

    /// <summary>按矩形挡路的障碍(合成):矩形取半开区间,盖着的点算被挡。</summary>
    private sealed class RectBlocker : IHeroBlocker
    {
        private readonly WorldRect[] _rects;

        public RectBlocker(params WorldRect[] rects)
        {
            _rects = rects;
        }

        public object BlockedBy(double x, double y)
        {
            foreach (WorldRect r in _rects)
            {
                if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
                {
                    return r;
                }
            }
            return null;
        }
    }

    private static WalkPathfinder Finder(params WorldRect[] walls)
    {
        var area = new RectUnionArea(new[] { new WorldRect(0, 0, RoomWidth, RoomHeight) });
        return new WalkPathfinder(area, walls.Length == 0 ? null : new RectBlocker(walls), RoomWidth, RoomHeight);
    }

    /// <summary>从 (x, y) 起一路取路点、走到路点上,直到代价为 0(站在目标上)或没路;返回走过的点。</summary>
    private static List<(double X, double Y)> Walk(WalkPathfinder pf, int[] field, double x, double y)
    {
        var trail = new List<(double X, double Y)> { (x, y) };
        for (int i = 0; i < 1000; i++)
        {
            if (!pf.TryWaypoint(field, x, y, 6, out double wx, out double wy, out int cost) || cost == 0)
            {
                break;
            }
            x = wx;
            y = wy;
            trail.Add((x, y));
        }
        return trail;
    }

    [Fact]
    public void ThePathGoesThroughTheGapInAWallInsteadOfThroughTheWall()
    {
        // 竖着一堵墙 x ∈ [90, 110),从最上面一直到 y = 70;下面留一个 30 像素高的缺口
        WalkPathfinder pf = Finder(new WorldRect(90, 0, 20, 70));
        int[] field = pf.Field(170, 10, 190, 30);
        Assert.True(pf.DistanceAt(field, 20, 20) > 0);

        List<(double X, double Y)> trail = Walk(pf, field, 20, 20);
        Assert.Equal(0, pf.DistanceAt(field, trail[^1].X, trail[^1].Y));
        Assert.InRange(trail[^1].X, 170, 190);
        Assert.All(trail, p => Assert.True(pf.IsOpen(p.X, p.Y)));
        Assert.Contains(trail, p => p.X >= 90 && p.X < 110);
        Assert.All(trail.Where(p => p.X >= 90 && p.X < 110), p => Assert.True(p.Y >= 70, "穿墙了:(" + p.X + "," + p.Y + ")"));
    }

    [Fact]
    public void AGoalCutOffByAWallHasNoRoute()
    {
        WalkPathfinder pf = Finder(new WorldRect(90, 0, 20, RoomHeight + 1));
        int[] field = pf.Field(170, 10, 190, 30);
        Assert.Equal(-1, pf.DistanceAt(field, 20, 20));
        Assert.False(pf.TryWaypoint(field, 20, 20, 6, out double wx, out double wy, out int cost));
        Assert.Equal((20.0, 20.0, -1), (wx, wy, cost));
        // 墙这一边的格子到得了目标
        Assert.True(pf.DistanceAt(field, 150, 20) > 0);
        // 目标矩形里没有一个站得下的格子(落在墙里):所有格子都够不着
        int[] none = pf.Field(95, 20, 105, 30);
        Assert.All(none, v => Assert.Equal(-1, v));
    }

    [Fact]
    public void AWideDetourBeatsAOneCellSqueezeBetweenTwoWallsButTheSqueezeIsUsedWhenItIsTheOnlyWay()
    {
        // 墙上有两个口:y ∈ [40, 44) 只够一格宽的窄缝,y ≥ 70 是 30 像素宽的大缺口;窄缝是直线、大缺口要绕下去再绕回来
        var upper = new WorldRect(90, 0, 20, 40);
        var middle = new WorldRect(90, 44, 20, 26);
        WalkPathfinder both = Finder(upper, middle);
        int[] field = both.Field(170, 30, 190, 50);
        List<(double X, double Y)> trail = Walk(both, field, 20, 42);
        Assert.Equal(0, both.DistanceAt(field, trail[^1].X, trail[^1].Y));
        Assert.All(trail.Where(p => p.X >= 90 && p.X < 110), p => Assert.True(p.Y >= 70, "走了窄缝:(" + p.X + "," + p.Y + ")"));

        // 大缺口也堵上:窄缝是唯一的路,照样走得过去(代价高得多)
        var bottom = new WorldRect(90, 70, 20, 31);
        WalkPathfinder squeezeOnly = Finder(upper, middle, bottom);
        int[] squeezeField = squeezeOnly.Field(170, 30, 190, 50);
        List<(double X, double Y)> squeeze = Walk(squeezeOnly, squeezeField, 20, 42);
        Assert.Equal(0, squeezeOnly.DistanceAt(squeezeField, squeeze[^1].X, squeeze[^1].Y));
        Assert.All(squeeze.Where(p => p.X >= 90 && p.X < 110), p => Assert.InRange(p.Y, 40, 43));
        Assert.True(squeezeOnly.DistanceAt(squeezeField, 20, 42) > both.DistanceAt(field, 20, 42) + WalkPathfinder.NarrowCost);
    }

    [Fact]
    public void AHeroStandingInsideAWallStillGetsAWaypointFromTheNearbyCells()
    {
        WalkPathfinder pf = Finder(new WorldRect(90, 0, 20, 70));
        int[] field = pf.Field(170, 10, 190, 30);
        Assert.False(pf.IsOpen(92, 20));                                   // 格子中心在墙里
        Assert.Equal(-1, pf.DistanceAt(field, 92, 20));
        Assert.True(pf.TryWaypoint(field, 92, 20, 6, out double wx, out double wy, out int cost));
        Assert.True(cost > 0);                                             // 起步格取周围 5 × 5 格里代价最小的
        Assert.True(pf.IsOpen(wx, wy));
        // 范围之外也是站不下
        Assert.False(pf.IsOpen(-10, 20));
        Assert.False(pf.IsOpen(20, RoomHeight + 20));
    }

    [Fact]
    public void AGoalPredicateUsesOnlyTheWideCellsWhenThereAreAnyAndSeedsGetCostZero()
    {
        WalkPathfinder pf = Finder();
        // 左上角 4 × 4 像素:最外面一圈(x 或 y 为 0)的格子贴着格子表边缘,不算"宽"格;里面的格子才是
        int[] field = pf.Field((x, y) => x <= 4 && y <= 4);
        Assert.Equal(0, pf.DistanceAt(field, 2, 2));
        Assert.Equal(0, pf.DistanceAt(field, 4, 4));
        Assert.Equal(WalkPathfinder.NarrowCost, pf.DistanceAt(field, 0, 0));      // 目标里只有宽格当种子,贴边的格子从宽格再走一步,走进窄格的代价是 NarrowCost
        Assert.True(pf.DistanceAt(field, 100, 50) > 0);
        Assert.Throws<ArgumentNullException>(() => pf.Field((Func<double, double, bool>)null));
    }

    [Fact]
    public void AnUnrestrictedAreaNeedsNoPathfinding()
    {
        var pf = new WalkPathfinder(new RectUnionArea(new List<WorldRect>()), null, RoomWidth, RoomHeight);
        Assert.Equal(0, pf.CellCount);
        Assert.True(pf.IsOpen(-500, 9000));
        int[] field = pf.Field(10, 10, 20, 20);
        Assert.All(field, v => Assert.Equal(-1, v));
        Assert.Throws<ArgumentNullException>(() => new WalkPathfinder(null, null, RoomWidth, RoomHeight));
    }
}
