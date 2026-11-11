using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网副本房间的合成夹具(r15-dungeonhost):一间迷你房间(五块 14 × 6 全可走的瓦片),房间地图自己的 [monster] 是调用方给的"出生点模板",
/// 主角站在 (300, 100)。<see cref="Create"/> 走的是生产路径 <see cref="RoomCombatSession.Create"/>(怪物由 <see cref="RoomMonsterBuilder"/> 按夹具里的怪物目录造),
/// 不是测试自己拼怪物;怪物编号只用夹具里的合成编号(1 完整、3 脚本不存在),不含任何原版数据。
/// </summary>
internal sealed class HostedRoomFixture : IDisposable
{
    /// <summary>夹具里完整的迷你怪物(monster.lst 编号 1):能造出来。</summary>
    public const int Buildable = 1;

    /// <summary>夹具里目录有、脚本不存在的编号(monster.lst 编号 3):造不出来。</summary>
    public const int Unbuildable = 3;

    private readonly AvatarFixture _fx = new AvatarFixture();

    public DnfAssets Assets { get; }

    public RoomSession Room { get; }

    public RoomMap Map { get; }

    /// <param name="templates">房间地图自己的 [monster](出生点模板);null 是没有。</param>
    public HostedRoomFixture(IReadOnlyList<RoomMonsterSpawn> templates = null)
    {
        Assets = DnfAssets.Open(_fx.Root);
        var tiles = new List<TileFile>();
        for (int i = 0; i < 5; i++)
        {
            tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]));
        }
        Map = new RoomMap("map/test/hosted.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
            new List<int[]> { null, null, null, null }, null, monsters: templates);
        var content = new RoomContent(Map, null, null, null, null);
        AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero hero = TownHero.Create(Assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), 300, 100, false, TownHero.DungeonRestTag);
        Room = new RoomSession(Assets, content, hero, new TownCamera(0, Map.MaxCameraX, hero.X));
    }

    /// <summary>一个出生点模板:地图 [fixed]、普通怪、等级档 1。</summary>
    public static RoomMonsterSpawn Spawn(int code, int x, int y = 100)
    {
        return new RoomMonsterSpawn(code, 0, 0, x, y, 0, 1, 0, "[fixed]", "[normal]");
    }

    /// <summary>用生产路径造房间会话(主角没有生命值,怪物打不到他);<paramref name="spawnPlan"/> 为 null 就是缺省的"地图自己的 [monster]"。</summary>
    public RoomCombatSession Create(Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> spawnPlan)
    {
        return RoomCombatSession.Create(Assets, Room, 1, false, null, spawnPlan: spawnPlan);
    }

    /// <summary>直接扣光一只怪的血(与观察者眼里的"死了"是同一个事实)。</summary>
    public static void Kill(MonsterActor monster)
    {
        Assert.True(monster.ApplyHit(1_000_000, false, 0, false), "这一下应当把它打死");
        Assert.False(monster.IsAlive);
    }

    public void Dispose()
    {
        Assets.Dispose();
        _fx.Dispose();
    }
}
