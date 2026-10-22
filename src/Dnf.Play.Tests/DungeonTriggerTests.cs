using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角什么时候算"走进了出口"(<see cref="DungeonSession.HeroTriggers"/>)。触发条件是推断,客户端的触发代码没有读到(证据 E-113),
/// 这里只固定我们实现的规则:门体盖着出口那一行的左右出口看"顶进门体",没有门体的左右出口看 y 与离出口的横向距离(或顶着边缘),上下出口看"向上/向下走"加占位盒压到识别矩形。
/// </summary>
public class DungeonTriggerTests
{
    private static TownHero Hero(DnfAssets assets, WorldRect floor, double x, double y)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ChrPath, outfit, new[] { floor }, x, y, false);
    }

    private static RoomGate Gate(DnfAssets assets, GateSide side, int x, int y)
    {
        return new RoomGate(assets, new GateObject(800 + (int)side, "x.obj", "[normal]", new List<GatePart>(), null), side, x, y);
    }

    [Fact]
    public void WithoutADoorBodyALeftGateNeedsTheHeroInItsRowsAndEitherNearTheGateOrPushingAgainstTheEdgeInsideItsColumns()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomGate left = Gate(assets, GateSide.Left, 16, 120);
        Assert.Equal((16, 67, 120, 106), (left.Area.X, left.Area.Y, left.Area.Width, left.Area.Height));   // x ∈ [16, 136),y ∈ [67, 173)
        Assert.False(left.BodyReachesGate(40, 10));

        // 可走区域从 x = 60 开始(出口位置 x = 16 落在可走区域外)
        var floor = new WorldRect(60, 0, 1000, 400);
        TownHero hero = Hero(assets, floor, 100, 120);
        Assert.False(DungeonSession.HeroTriggers(left, hero));                 // 离出口 84 像素,也没顶着边缘

        hero.Place(28, 120, false, HeroMotion.Rest, 0);
        Assert.True(DungeonSession.HeroTriggers(left, hero));                  // 离出口恰好 12 像素、y 在矩形的行里
        hero.Place(29, 120, false, HeroMotion.Rest, 0);
        Assert.False(DungeonSession.HeroTriggers(left, hero));
        hero.Place(20, 67, false, HeroMotion.Rest, 0);
        Assert.True(DungeonSession.HeroTriggers(left, hero));                  // 矩形的上沿含
        hero.Place(20, 66, false, HeroMotion.Rest, 0);
        Assert.False(DungeonSession.HeroTriggers(left, hero));                 // y 不在矩形的行里:再近也不算
        hero.Place(20, 172, false, HeroMotion.Rest, 0);
        Assert.True(DungeonSession.HeroTriggers(left, hero));
        hero.Place(20, 173, false, HeroMotion.Rest, 0);
        Assert.False(DungeonSession.HeroTriggers(left, hero));                 // 下沿不含

        // 顶着可走区域的左边缘走:边缘 x = 60 在矩形的列里,算
        hero.Place(70, 120, false, HeroMotion.Rest, 0);
        hero.Update(1000, -1, 0);
        Assert.True(hero.PushedLeft);
        Assert.Equal(60.0, hero.X, 6);
        Assert.True(DungeonSession.HeroTriggers(left, hero));
        // 同样顶着边缘,但 y 不在矩形的行里:不算
        hero.Place(70, 40, false, HeroMotion.Rest, 0);
        hero.Update(1000, -1, 0);
        Assert.True(hero.PushedLeft);
        Assert.False(DungeonSession.HeroTriggers(left, hero));
        // 可走区域的边缘在矩形的列之外(x = 200 ≥ 136):顶着也不算
        TownHero far = Hero(assets, new WorldRect(200, 0, 1000, 400), 250, 120);
        far.Update(1000, -1, 0);
        Assert.True(far.PushedLeft);
        Assert.False(DungeonSession.HeroTriggers(left, far));
    }

    [Fact]
    public void ARightGateWithoutADoorBodyMirrorsTheLeftOne()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomGate right = Gate(assets, GateSide.Right, 1104, 120);
        Assert.Equal((984, 67, 120, 106), (right.Area.X, right.Area.Y, right.Area.Width, right.Area.Height));

        var floor = new WorldRect(0, 0, 1040, 400);                           // 右边缘 x = 1040 在矩形的列 [984, 1104) 里
        TownHero hero = Hero(assets, floor, 500, 120);
        Assert.False(DungeonSession.HeroTriggers(right, hero));
        hero.Place(1092, 120, false, HeroMotion.Rest, 0);
        Assert.True(DungeonSession.HeroTriggers(right, hero));                // 离出口恰好 12 像素
        hero.Place(1091, 120, false, HeroMotion.Rest, 0);
        Assert.False(DungeonSession.HeroTriggers(right, hero));
        hero.Place(1100, 66, false, HeroMotion.Rest, 0);
        Assert.False(DungeonSession.HeroTriggers(right, hero));               // y 不在行里

        hero.Place(1000, 120, false, HeroMotion.Rest, 0);
        hero.Update(1000, 1, 0);
        Assert.True(hero.PushedRight);
        Assert.Equal(1040.0, hero.X, 6);
        Assert.True(DungeonSession.HeroTriggers(right, hero));                // 顶着右边缘
        TownHero far = Hero(assets, new WorldRect(0, 0, 900, 400), 800, 120); // 边缘 x = 900 在矩形的列之前
        far.Update(1000, 1, 0);
        Assert.True(far.PushedRight);
        Assert.False(DungeonSession.HeroTriggers(right, far));
    }

    [Fact]
    public void ADoorBodyThatCoversTheGateRowTurnsTheTriggerIntoPushingIntoTheBody()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomGate left = Gate(assets, GateSide.Left, 16, 120);
        // 14 列 × 6 行全挡的门体,锚点 (0, 0):左上角那一格是 (1, 10),盖住 x ∈ [16, 240)、y ∈ [120, 192),左出口的表第 0 列的值还延伸到地图左缘
        var wall = new TileFile("wall", new List<KeyValuePair<string, int>>(), 80, Enumerable.Repeat((byte)2, 14 * 6).ToArray());
        left.SetBody(wall, 0, 0);
        Assert.True(left.BodyReachesGate(40, 10));

        var floor = new WorldRect(0, 0, 1000, 400);
        // 在门体的行里向左走:被门体挡住 → 触发(占位盒半宽 20,停在 240 + 20)
        TownHero inside = Hero(assets, floor, 300, 150);
        inside.Blocker = new GateBodies(new[] { left });
        inside.Update(2000, -1, 0);
        Assert.Same(left, inside.PushedInto);
        Assert.Equal(260.0, inside.X, 6);
        Assert.True(DungeonSession.HeroTriggers(left, inside));

        // y 在识别矩形的行里、但在门体的行外(占位盒碰不到门体):走到地图左缘也没顶进门体 → 不触发(没有门体的"离出口 12 像素以内"规则在这里不用)
        TownHero outside = Hero(assets, floor, 100, 90);
        outside.Blocker = new GateBodies(new[] { left });
        outside.Update(2000, -1, 0);
        Assert.Null(outside.PushedInto);
        Assert.True(outside.PushedLeft);
        Assert.Equal(0.0, outside.X, 6);
        Assert.False(DungeonSession.HeroTriggers(left, outside));
    }

    [Fact]
    public void AnUpOrDownGateNeedsTheHeroToTryToGoThatWayWithTheFootBoxTouchingTheRecognizeRectangle()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomGate up = Gate(assets, GateSide.Up, 560, 12);
        Assert.Equal((490, -8, 140, 40), (up.Area.X, up.Area.Y, up.Area.Width, up.Area.Height));         // y ∈ [-8, 32)
        var floor = new WorldRect(0, 5, 1200, 400);

        TownHero hero = Hero(assets, floor, 560, 20);
        Assert.False(DungeonSession.HeroTriggers(up, hero));                  // 站在矩形里但没有往上走
        hero.Update(10, 0, 1);
        Assert.False(DungeonSession.HeroTriggers(up, hero));                  // 往下走不算
        hero.Place(560, 20, false, HeroMotion.Rest, 0);
        hero.Update(10, 0, -1);
        Assert.True(hero.TriedUp);
        Assert.True(DungeonSession.HeroTriggers(up, hero));
        hero.Update(2000, 0, -1);                                             // 一直顶到可走区域的上缘(y = 5)也还在矩形里
        Assert.True(hero.PushedUp);
        Assert.True(DungeonSession.HeroTriggers(up, hero));
        TownHero aside = Hero(assets, floor, 100, 20);
        aside.Update(10, 0, -1);
        Assert.False(DungeonSession.HeroTriggers(up, aside));                 // 横向离出口太远

        // 下出口:矩形 y ∈ [328, 368)。可走区域的下缘在 324:脚点(324)够不着矩形,占位盒的下沿(324 + 5 = 329)压到了
        RoomGate down = Gate(assets, GateSide.Down, 560, 348);
        Assert.Equal((490, 328, 140, 40), (down.Area.X, down.Area.Y, down.Area.Width, down.Area.Height));
        TownHero low = Hero(assets, new WorldRect(0, 0, 1200, 324), 560, 320);
        Assert.False(DungeonSession.HeroTriggers(down, low));
        low.Update(2000, 0, 1);
        Assert.True(low.TriedDown);
        Assert.True(low.PushedDown);
        Assert.Equal(324.0, low.Y, 6);
        Assert.False(GateAreas.Contains(down.Area, 560, 324));
        Assert.True(DungeonSession.HeroTriggers(down, low));
        // 下缘再高 2 像素(322 + 5 = 327 < 328):压不到,不触发
        TownHero lower = Hero(assets, new WorldRect(0, 0, 1200, 322), 560, 320);
        lower.Update(2000, 0, 1);
        Assert.True(lower.PushedDown);
        Assert.False(DungeonSession.HeroTriggers(down, lower));
    }
}
