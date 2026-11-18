using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 换区命令 1/38 的 x、y 必须是"目标区落点"(主角到了新区之后站的地方),而不是主角在源区里的脚下位置:
/// 服务端只把 x、y 交给 set_position,来源村/区两个字段才用于核对。落点与宿主随后建会话放主角用的是同一个图册落点。
/// 合成图册里让源区主角位置、目标区落点互不相同(X≠Y、源≠目标、都不是 0 或整百);期望值是矩形中心手算的字面量。
/// </summary>
public sealed class OnlineTownDestinationTests
{
    // 合成图册(城镇 1):
    //   区域 0 网关:通往区域 2 的出口矩形 (402,118,38,74) → 中心 (402+38/2, 118+74/2) = (421, 155)
    //   区域 1 普通:通往区域 2 的出口矩形 (610,215,70,36)
    //   区域 2 普通:通往区域 1 的出口矩形 (31,142,46,52) → 中心 (31+46/2, 142+52/2) = (54, 168);通往区域 0 的出口矩形 (702,171,52,98)
    // 可走区域都是 (0,100,800,300),上面的中心都在里面,不会被挪动。
    private static TownAtlas LandingAtlas() => TownKit.Atlas(
        (1, new[]
        {
            new KitArea(0, "[gate]", new[] { 233, 177 }, new[] { 0, 100, 800, 300 }, new[] { new KitExit(402, 118, 38, 74, 1, 2) }),
            new KitArea(1, "[normal]", null, new[] { 0, 100, 800, 300 }, new[] { new KitExit(610, 215, 70, 36, 1, 2) }),
            new KitArea(2, "[normal]", null, new[] { 0, 100, 800, 300 },
                new[] { new KitExit(31, 142, 46, 52, 1, 1), new KitExit(702, 171, 52, 98, 1, 0) }),
        }));

    private static GameFlow CreateFlow(OnlineTownWorld world)
    {
        return new GameFlow(world, (p, _) => null, new[] { new DungeonEntry("dungeon/t.dgn", "T") });
    }

    [Fact]
    public void Normal_to_normal_sends_the_destination_landing_point_and_the_new_session_stands_exactly_there()
    {
        TownAtlas atlas = LandingAtlas();
        using var rig = new TownWorldRig(atlas, new TownPlace(1, 1));
        GameFlow flow = CreateFlow(rig.World);
        flow.EnterTown("start");
        var source = (TownKitSession)flow.Session;
        source.HeroX = 661;   // 主角站在区域 1 的出口矩形里:脚下 (661, 247),与目标落点 (54, 168) 互不相同
        source.HeroY = 247;
        rig.World.Poll(1000);
        rig.World.RecordSafePosition();
        source.PendingExit = TownKit.Exit(610, 215, 70, 36, 1, 2);

        flow.Poll();

        KitSend sent = Assert.Single(rig.Transport.Sent);
        Assert.Equal((byte)1, sent.DstV);
        Assert.Equal((byte)2, sent.DstA);
        Assert.Equal((ushort)54, sent.X);     // 区域 2 通往区域 1 的出口矩形中心 x = 31 + 46/2
        Assert.Equal((ushort)168, sent.Y);    // 中心 y = 142 + 52/2
        Assert.Equal((ushort)1, sent.SrcV);
        Assert.Equal((ushort)1, sent.SrcA);
        Assert.Equal((byte)5, sent.Flags);

        // 服务端确认(普通区域要 0/23 + 0/24)后,宿主放行:新会话建在同一个落点上
        Assert.Equal(AreaChangeStatus.Confirmed, rig.ConfirmNormal(1, 2, 1016).Status);
        flow.Poll();
        Assert.Equal(new TownPlace(1, 2), flow.Place);
        var arrived = (TownKitSession)flow.Session;
        Assert.NotSame(source, arrived);
        Assert.Equal(54.0, arrived.HeroX);
        Assert.Equal(168.0, arrived.HeroY);
        Assert.Equal(sent.X, (ushort)arrived.HeroX);
        Assert.Equal(sent.Y, (ushort)arrived.HeroY);
        Assert.Single(rig.Transport.Sent);
    }

    [Fact]
    public void Normal_to_gate_sends_the_gate_side_landing_point_and_the_new_session_stands_exactly_there()
    {
        TownAtlas atlas = LandingAtlas();
        using var rig = new TownWorldRig(atlas, new TownPlace(1, 2));
        GameFlow flow = CreateFlow(rig.World);
        flow.EnterTown("start");
        var source = (TownKitSession)flow.Session;
        source.HeroX = 718;   // 区域 2 通往网关的出口矩形里:脚下 (718, 262),与目标落点 (421, 155) 互不相同
        source.HeroY = 262;
        rig.World.Poll(2000);
        rig.World.RecordSafePosition();
        source.PendingExit = TownKit.Exit(702, 171, 52, 98, 1, 0);

        flow.Poll();

        KitSend sent = Assert.Single(rig.Transport.Sent);
        Assert.Equal((byte)1, sent.DstV);
        Assert.Equal((byte)0, sent.DstA);
        Assert.Equal((ushort)421, sent.X);    // 网关区域通往区域 2 的出口矩形中心 x = 402 + 38/2
        Assert.Equal((ushort)155, sent.Y);    // 中心 y = 118 + 74/2(不取网关登录点 (233,177))
        Assert.Equal((ushort)1, sent.SrcV);
        Assert.Equal((ushort)2, sent.SrcA);
        Assert.Equal((byte)5, sent.Flags);

        // 网关只等 0/24
        rig.Transport.Push(KitEvents.AreaUsers(1, 0, TownWorldRig.SelfUid));
        Assert.Equal(AreaChangeStatus.Confirmed, rig.World.Poll(2016).Status);
        flow.Poll();
        Assert.Equal(new TownPlace(1, 0), flow.Place);
        var arrived = (TownKitSession)flow.Session;
        Assert.Equal(421.0, arrived.HeroX);
        Assert.Equal(155.0, arrived.HeroY);
        Assert.Equal(sent.X, (ushort)arrived.HeroX);
        Assert.Equal(sent.Y, (ushort)arrived.HeroY);
    }

    [Fact]
    public void Landing_point_does_not_depend_on_where_the_hero_stood_in_the_source_area()
    {
        TownAtlas atlas = LandingAtlas();
        using var rig = new TownWorldRig(atlas, new TownPlace(1, 1));
        TownKitSession session = (TownKitSession)rig.World.Create(new TownPlace(1, 1));
        TownExit exit = TownKit.Exit(610, 215, 70, 36, 1, 2);
        rig.World.Poll(500);

        session.HeroX = 612;
        session.HeroY = 217;
        Assert.Null(rig.Through(new TownPlace(1, 1), exit));
        KitSend first = Assert.Single(rig.Transport.Sent);
        Assert.Equal(((ushort)54, (ushort)168), (first.X, first.Y));
        rig.Transport.Push(KitEvents.Error(0x11));
        Assert.Equal(AreaChangeStatus.RolledBack, rig.World.Poll(516).Status);

        session.HeroX = 678;   // 换一个完全不同的脚下位置、等反弹窗口过去再走一次:发出去的落点不变
        session.HeroY = 249;
        rig.World.Poll(516 + 400);
        Assert.Null(rig.Through(new TownPlace(1, 1), exit));
        Assert.Equal(2, rig.Transport.Sent.Count);
        Assert.Equal(((ushort)54, (ushort)168), (rig.Transport.Sent[1].X, rig.Transport.Sent[1].Y));
    }

    [Theory]
    [InlineData(410.6, 233.4, 411, 233)]
    [InlineData(100.4, 250.6, 100, 251)]
    [InlineData(0.4, 0.6, 0, 1)]
    [InlineData(-3.2, 233.0, 0, 233)]
    [InlineData(233.0, -0.4, 233, 0)]
    [InlineData(70000.4, 305.0, 65535, 305)]
    [InlineData(233.0, 70000.6, 233, 65535)]
    [InlineData(65534.6, 65535.4, 65535, 65535)]
    [InlineData(double.NaN, 305.0, 0, 305)]
    [InlineData(233.0, double.PositiveInfinity, 233, 0)]
    public void Landing_coordinates_are_rounded_clamped_and_kept_in_order_on_the_way_into_the_command(double landingX, double landingY, int expectedX, int expectedY)
    {
        TownAtlas atlas = LandingAtlas();
        using var rig = new TownWorldRig(atlas, new TownPlace(1, 1));
        rig.World.LandingSource = _ => new TownArrival(landingX, landingY, false);
        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(300);

        Assert.Null(rig.Through(new TownPlace(1, 1), TownKit.Exit(610, 215, 70, 36, 1, 2)));

        KitSend sent = Assert.Single(rig.Transport.Sent);
        Assert.Equal(expectedX, (int)sent.X);
        Assert.Equal(expectedY, (int)sent.Y);
    }

    [Fact]
    public void Landing_from_a_real_atlas_arrival_is_clamped_to_the_unsigned_16_bit_range()
    {
        // 这两块区域没有写可走区域,落点不会被挪进可走区域,所以负数与超大数原样走到发送前的夹取
        //   区域 1 通往区域 0 的出口矩形 (-60,233,30,20) → 中心 (-60+30/2, 233+20/2) = (-45, 243) → (0, 243)
        //   区域 2 通往区域 0 的出口矩形 (69990,141,21,12) → 中心 (69990+21/2, 141+12/2) = (70000.5, 147) → (65535, 147)
        TownAtlas atlas = TownKit.Atlas(
            (1, new[]
            {
                new KitArea(0),
                new KitArea(1, "[normal]", null, null, new[] { new KitExit(-60, 233, 30, 20, 1, 0) }),
                new KitArea(2, "[normal]", null, null, new[] { new KitExit(69990, 141, 21, 12, 1, 0) }),
            }));
        using var rig = new TownWorldRig(atlas, new TownPlace(1, 0));
        rig.World.Create(new TownPlace(1, 0));
        rig.World.Poll(300);

        Assert.Null(rig.Through(new TownPlace(1, 0), TownKit.Exit(5, 5, 5, 5, 1, 1)));
        Assert.Equal(((ushort)0, (ushort)243), (rig.Transport.Sent[0].X, rig.Transport.Sent[0].Y));
        rig.Transport.Push(KitEvents.Error(0x11));
        rig.World.Poll(316);
        rig.World.Poll(316 + 400);
        Assert.Null(rig.Through(new TownPlace(1, 0), TownKit.Exit(5, 5, 5, 5, 1, 2)));
        Assert.Equal(2, rig.Transport.Sent.Count);
        Assert.Equal(((ushort)65535, (ushort)147), (rig.Transport.Sent[1].X, rig.Transport.Sent[1].Y));
    }
}
