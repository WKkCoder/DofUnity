using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 自动机的"打翻物件"选项(<see cref="DungeonBot.SmashObjects"/>,M34,证据 E-126):房间清空之后走到还没打翻也没触发过的物件跟前连续普攻,
/// 打翻或触发就找下一个,够不着的物件靠每个房间的预算兜底,默认关。数据全是合成的(木箱、木桶、石柱,见 <see cref="AvatarFixture.CrateObjectId"/> 一带)。
/// 这里直接按 <see cref="DungeonBot.Smash"/> 一步一步驱动战斗会话(整座副本里它夹在捡金币与走向出口之间,由命令行 --smash 与播放器 -dnfsmash 覆盖)。
/// </summary>
public class SmashBotTests
{
    private const int ObjY = 100;
    private static readonly (int X, int Y)[] NoMonsters = new (int X, int Y)[0];

    private static Func<DnfAssets, IReadOnlyList<RoomObject>> Room(params (int Id, int X)[] placements)
    {
        return RoomAt(ObjY, placements);
    }

    /// <summary>所有物件都摆在同一行 <paramref name="y"/> 上。</summary>
    private static Func<DnfAssets, IReadOnlyList<RoomObject>> RoomAt(int y, params (int Id, int X)[] placements)
    {
        return assets =>
        {
            PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
            var list = new List<RoomObject>();
            foreach ((int id, int x) in placements)
            {
                Dnf.Render.PassiveObjectDef def = Dnf.Render.PassiveObjectDef.Read(assets, catalog, id);
                Assert.NotNull(def);
                RoomObject o = RoomObject.Create(assets, def, new PassivePlacement(id, x, y, 0));
                Assert.NotNull(o);
                list.Add(o);
            }
            return list;
        };
    }

    /// <summary>每步 10 毫秒让自动机决定、会话推进,直到它说没事可做或者走满 <paramref name="maxSteps"/> 步;返回走了几步。</summary>
    private static int Drive(DungeonBot bot, RoomCombatSession s, int maxSteps)
    {
        int steps = 0;
        while (steps < maxSteps && bot.Smash(s, s.Hero, out int dirX, out int dirY, out bool attack))
        {
            s.Update(10, dirX, dirY, attack);
            steps++;
        }
        return steps;
    }

    [Fact]
    public void ItIsOffByDefaultAndLeavesTheHeroAlone()
    {
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.CrateObjectId, 450)));
        var bot = new DungeonBot();
        Assert.False(bot.SmashObjects);
        Assert.False(bot.Smash(arena.Session, arena.Hero, out int dirX, out int dirY, out bool attack));
        Assert.Equal((0, 0, false), (dirX, dirY, attack));
        Assert.Equal(0, arena.Session.ObjectsHit);
    }

    [Fact]
    public void ItWalksToEveryObjectOnBothSidesAndBreaksThem()
    {
        // 主角站在 (300, 100) 朝右:左边一个木箱(打 2 下)、右边一个木箱和一个木桶(打 1 下)
        using var arena = new Arena(NoMonsters, objects: Room(
            (AvatarFixture.CrateObjectId, 150),
            (AvatarFixture.CrateObjectId, 450),
            (AvatarFixture.BarrelObjectId, 600)));
        RoomCombatSession s = arena.Session;
        var bot = new DungeonBot { SmashObjects = true };
        int steps = Drive(bot, s, 4000);
        Assert.True(steps < DungeonBot.SmashSteps, "应该在预算用完之前打完,实际 " + steps + " 步");
        Assert.All(s.Breakables, b => Assert.True(b.Destroyed));
        Assert.Equal(3, s.ObjectsDestroyed);
        Assert.Equal(2 + 2 + 1, s.ObjectsHit);
        // 打完之后自动机不再有事可做,走位也不再动
        Assert.False(bot.Smash(s, s.Hero, out int dirX, out int dirY, out bool attack));
        Assert.Equal((0, 0, false), (dirX, dirY, attack));
    }

    [Fact]
    public void AnObjectThatOnlyTriggersIsNotStruckAgainAfterItWentOff()
    {
        // 石柱打 2 下触发一次、不消失:触发之后自动机不再回头打它
        using var arena = new Arena(NoMonsters, objects: Room((AvatarFixture.PillarObjectId, 450)));
        RoomCombatSession s = arena.Session;
        var bot = new DungeonBot { SmashObjects = true };
        int steps = Drive(bot, s, 4000);
        BreakableObject pillar = Assert.Single(s.Breakables);
        Assert.True(steps < DungeonBot.SmashSteps);
        Assert.True(pillar.Triggered);
        Assert.False(pillar.Destroyed);
        Assert.True(pillar.HitsTaken >= 2);
        Assert.Equal(0, s.ObjectsDestroyed);
        // 触发时手里已经排好的连击会自己打完(挥击不会因为物件触发而中断);打完之后自动机没有再按过攻击键,所以不再有新的命中
        for (int i = 0; i < 100; i++)
        {
            Assert.False(bot.Smash(s, s.Hero, out int dirX, out int dirY, out bool attack));
            Assert.False(attack);
            s.Update(10, dirX, dirY, attack);
        }
        Assert.False(s.Hero.IsAttacking);
        int settled = pillar.HitsTaken;
        for (int i = 0; i < 300; i++)
        {
            bot.Smash(s, s.Hero, out int dirX, out int dirY, out bool attack);
            s.Update(10, dirX, dirY, attack);
        }
        Assert.Equal(settled, pillar.HitsTaken);
    }

    [Fact]
    public void AnObjectWithNoWayToItIsGivenUpOnAfterTheSearchKeepsFailingAndTheSmashEnds()
    {
        // 主角脚下只有一小块可走的地方(frozen),木箱在够不着的地方:寻路连着 PathFailedSteps 步找不到路就放弃它,没有别的物件了就交还给走向出口
        using var arena = new Arena(NoMonsters, frozen: true, objects: Room((AvatarFixture.CrateObjectId, 700)));
        RoomCombatSession s = arena.Session;
        var bot = new DungeonBot { SmashObjects = true };
        int steps = Drive(bot, s, DungeonBot.SmashSteps + 100);
        Assert.Equal(DungeonBot.PathFailedSteps, steps);
        Assert.Equal(0, s.ObjectsHit);
        Assert.False(s.Breakables[0].Destroyed);
        Assert.False(bot.Smash(s, s.Hero, out _, out _, out _));
    }

    [Fact]
    public void ItNeverPushesTowardAnObjectBeyondTheEdgeOfTheWalkableAreaAndGivesUpOnItQuickly()
    {
        // 可走区域是一条窄带(y 从 90 到 130),木箱摆在带子上方的边外(y = 40):直线走过去只会一直往上顶带子的边缘——
        // 边缘上要是正好有出口,一顶就换了房间,换回来又顶(全库扫描里这样来回晃过一辈子)。有寻路就知道没路:原地不动,几步之后放弃
        var band = new List<WorldRect> { new WorldRect(0, 90, 900, 40) };
        using var arena = new Arena(NoMonsters, walkArea: band, objects: RoomAt(40, (AvatarFixture.CrateObjectId, 450)));
        RoomCombatSession s = arena.Session;
        var bot = new DungeonBot { SmashObjects = true };
        int steps = 0, pushedUp = 0;
        while (steps < DungeonBot.SmashSteps && bot.Smash(s, s.Hero, out int dirX, out int dirY, out bool attack))
        {
            if (dirY < 0)
            {
                pushedUp++;
            }
            s.Update(10, dirX, dirY, attack);
            steps++;
        }
        Assert.Equal(0, pushedUp);
        Assert.Equal(DungeonBot.PathFailedSteps, steps);
        Assert.Equal(0, s.ObjectsHit);
    }

    [Fact]
    public void ItReachesAnObjectBehindAWallByWalkingAroundItInsteadOfPushingIntoTheWall()
    {
        // 可走的带子是 y ∈ [60, 110),中间一堵 x ∈ [340, 380) 的竖墙只在上沿 y ∈ [60, 80) 留着缺口;主角站在墙的左边 (300, 100),木箱在墙的另一侧 (500, 100)。
        // 直线走会顶在墙上走不过去;寻路从缺口绕过去走到木箱跟前,打翻它
        var area = new List<WorldRect>
        {
            new WorldRect(0, 60, 340, 50),
            new WorldRect(380, 60, 520, 50),
            new WorldRect(340, 60, 40, 20),
        };
        using var arena = new Arena(NoMonsters, walkArea: area, objects: Room((AvatarFixture.CrateObjectId, 500)));
        RoomCombatSession s = arena.Session;
        var bot = new DungeonBot { SmashObjects = true };
        int steps = Drive(bot, s, DungeonBot.SmashSteps);
        Assert.True(s.Breakables[0].Destroyed, "没打到:走了 " + steps + " 步,主角在 (" + s.Hero.X + "," + s.Hero.Y + ")");
        Assert.True(steps < DungeonBot.SmashTargetSteps, "绕路也不该耗光单个物件的步数,实际 " + steps);
    }

    [Fact]
    public void AnObjectItWasWalkingToWhenTheHeroLeftTheRoomIsNotTargetedAgainOnReturn()
    {
        // 走向木箱的路上主角出了房间(路过了出口),换到别的房间之后再回来:不再回头去走同一个物件,否则两间房之间会来回晃一辈子
        using var first = new Arena(NoMonsters, objects: Room((AvatarFixture.CrateObjectId, 450)));
        using var other = new Arena(NoMonsters);
        var bot = new DungeonBot { SmashObjects = true };
        for (int i = 0; i < 20; i++)
        {
            Assert.True(bot.Smash(first.Session, first.Hero, out int dirX, out int dirY, out bool attack));
            first.Session.Update(10, dirX, dirY, attack);
        }
        Assert.False(bot.Smash(other.Session, other.Hero, out _, out _, out _));     // 别的房间没有物件:这一步已经算"离开了第一间"
        Assert.False(bot.Smash(first.Session, first.Hero, out int bx, out int by, out bool battack));
        Assert.Equal((0, 0, false), (bx, by, battack));
        Assert.False(first.Session.Breakables[0].Destroyed);
    }
}
