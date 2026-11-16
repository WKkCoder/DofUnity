using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 带出口的联网副本房间夹具(r18-hostmulti,宿主层多房间):一间迷你房间(五块 14 × 16 全可走的瓦片,宽 1120、高 192),想要几侧出口就摆几侧——
/// 出口物件是合成的(没有动画、没有门体),位置固定:左 (24, 100)、右 (1096, 100)、上 (560, 40)、下 (560, 170),识别矩形取缺省大小(<see cref="GateAreas"/>)。
/// 每调一次 <see cref="Create"/> 造一间<b>全新</b>的房间(新的地图、新的主角、新的出口),所以一个夹具能造出一整座多房间副本;
/// 房间会话走生产路径 <see cref="RoomCombatSession.Create"/>(怪物由 <see cref="RoomMonsterBuilder"/> 按夹具里的怪物目录造),不是测试自己拼怪物。
/// 只用公开接口(Client.Tests 也编译进这个文件);怪物编号只用 <see cref="HostedRoomFixture"/> 里的合成编号,不含任何原版数据。
/// </summary>
internal sealed class HostedGatedRoomFixture : IDisposable
{
    private readonly AvatarFixture _fx = new AvatarFixture();
    private int _rooms;

    public DnfAssets Assets { get; }

    /// <summary>已经造过几间房。</summary>
    public int RoomsCreated => _rooms;

    public HostedGatedRoomFixture()
    {
        Assets = DnfAssets.Open(_fx.Root);
    }

    /// <summary>某一侧出口在地图里的位置(<c>[pathgate pos]</c> 里的 x、y)。</summary>
    public static int[] GatePosition(GateSide side)
    {
        switch (side)
        {
            case GateSide.Left: return new[] { 24, 100 };
            case GateSide.Right: return new[] { 1096, 100 };
            case GateSide.Up: return new[] { 560, 40 };
            default: return new[] { 560, 170 };
        }
    }

    /// <summary>某一侧出口的识别矩形(服务端校验过门位置、进房间落点、上下出口触发都用它)。</summary>
    public static WorldRect GateRect(GateSide side)
    {
        int[] p = GatePosition(side);
        return GateAreas.For(side, p[0], p[1]);
    }

    /// <summary>
    /// 造一间全新的房间。
    /// </summary>
    /// <param name="templates">房间地图自己的 [monster](出生点模板);null 是没有。</param>
    /// <param name="spawnPlan">出生点计划(联网宿主按怪表只数填的);null 是缺省的"地图自己的 [monster]"。</param>
    /// <param name="gates">摆哪几侧出口。</param>
    /// <param name="heroVitals">主角有没有生命值(没有就是 -dnfnodamage 那样,怪物打不到他)。</param>
    /// <param name="heroX">主角的初始位置(世界坐标)。</param>
    /// <param name="heroY">主角的初始位置(世界坐标)。</param>
    public RoomCombatSession Create(IReadOnlyList<RoomMonsterSpawn> templates, Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> spawnPlan, IEnumerable<GateSide> gates,
        bool heroVitals = false, double heroX = 300, double heroY = 100)
    {
        RoomSession room = CreateRoomSession(templates, gates, heroX, heroY);
        return RoomCombatSession.Create(Assets, room, 1, heroVitals, null, spawnPlan: spawnPlan);
    }

    /// <summary>
    /// 只拼出一间全新的房间会话(地图、出口、主角、镜头),不摆怪物也不接战斗:宿主自己拼房间、再交给 <see cref="HostedRoomCombat"/> 摆怪的做法,
    /// 测试从这里拿房间。<see cref="Create"/> 就是它加上 <see cref="RoomCombatSession.Create"/>。
    /// </summary>
    public RoomSession CreateRoomSession(IReadOnlyList<RoomMonsterSpawn> templates, IEnumerable<GateSide> gates, double heroX = 300, double heroY = 100)
    {
        var tiles = new List<TileFile>();
        for (int i = 0; i < 5; i++)
        {
            tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 16]));
        }
        var sides = new List<GateSide>(gates ?? new GateSide[0]);
        var positions = new List<int[]> { null, null, null, null };
        foreach (GateSide side in sides)
        {
            positions[(int)side] = GatePosition(side);
        }
        var map = new RoomMap("map/test/gated" + _rooms++ + ".map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
            positions, null, monsters: templates);
        var roomGates = new List<RoomGate>();
        foreach (GateSide side in sides)
        {
            int[] p = positions[(int)side];
            roomGates.Add(new RoomGate(Assets, new GateObject(900 + (int)side, "synthetic/gate", "[normal]", new List<GatePart>(), null), side, p[0], p[1],
                map.GateRects[(int)side]));
        }
        var content = new RoomContent(map, null, null, null, roomGates);
        AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        // 可走区域与生产路径一样是房间瓦片的通行表(整间都能走,边缘就是房间的边):上下出口靠"顶着可走带的边缘往外走"触发,没有边缘就走不出去
        TownHero hero = TownHero.Create(Assets, AvatarFixture.ChrPath, outfit, new RoomWalkArea(map, content.Objects), heroX, heroY, false, TownHero.DungeonRestTag);
        return new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));
    }

    public void Dispose()
    {
        Assets.Dispose();
        _fx.Dispose();
    }
}
