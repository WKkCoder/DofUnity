using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 判定盒的相交盒与中心(证据 E-058、E-107):相交盒各轴下界取较大的、上界取较小的;
/// 中心按客户端的算法取整:(int)(尺寸 × 0.5(单精度) + 下界),向零取整。数据全是合成的。
/// </summary>
public class AabbTests
{
    private static (int, int, int, int, int, int) Fields(Aabb b)
    {
        return (b.MinX, b.MaxX, b.MinY, b.MaxY, b.MinZ, b.MaxZ);
    }

    [Fact]
    public void TheIntersectionTakesTheLargerLowerBoundAndTheSmallerUpperBoundOnEveryAxis()
    {
        var a = new Aabb(10, 60, 90, 110, 0, 40);
        var b = new Aabb(30, 80, 95, 105, -5, 70);
        Assert.Equal((30, 60, 95, 105, 0, 40), Fields(Aabb.Intersection(a, b)));
        Assert.Equal(Fields(Aabb.Intersection(a, b)), Fields(Aabb.Intersection(b, a)));          // 与先后无关
    }

    [Fact]
    public void ABoxInsideAnotherIsItsOwnIntersection()
    {
        var outer = new Aabb(0, 100, 0, 100, 0, 100);
        var inner = new Aabb(20, 30, 40, 50, 60, 70);
        Assert.Equal(Fields(inner), Fields(Aabb.Intersection(outer, inner)));
    }

    [Fact]
    public void TheCenterIsTheLowerBoundPlusHalfTheSizeTruncatedTowardZero()
    {
        var even = new Aabb(30, 60, 10, 20, 0, 60);
        Assert.Equal((45, 15, 30), (even.CenterX, even.CenterY, even.CenterZ));

        var odd = new Aabb(31, 60, 11, 20, 1, 60);                         // 尺寸 29、9、59:一半是 .5,向零取整
        Assert.Equal((45, 15, 30), (odd.CenterX, odd.CenterY, odd.CenterZ));

        var negative = new Aabb(-5, 0, -9, -4, -3, 0);                      // 中心 -2.5、-6.5、-1.5:向零取整,不是向下
        Assert.Equal((-2, -6, -1), (negative.CenterX, negative.CenterY, negative.CenterZ));
    }

    [Fact]
    public void TheCenterOfAnIntersectionIsWhereTheTwoBoxesMeet()
    {
        var strike = new Aabb(310, 360, 90, 110, 0, 60);                   // 攻击框
        var hurt = new Aabb(310, 350, 94, 106, 0, 60);                     // 受击框
        Assert.True(strike.Overlaps(hurt));
        Aabb hit = Aabb.Intersection(strike, hurt);
        Assert.Equal((330, 100, 30), (hit.CenterX, hit.CenterY, hit.CenterZ));
    }
}
