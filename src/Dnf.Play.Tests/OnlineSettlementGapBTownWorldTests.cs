using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 回城落位装饰器(<see cref="OnlineSettlementTownWorld"/>)与回城目标(<see cref="OnlineSettlementReturnTarget"/>)的补测(区域 B):
/// 来源令牌的字面量与枚举值互不相同、落点三种场景(没有待消费的目标 / 有目标没坐标 / 有坐标)、内层返回空会话、
/// 落点处主角的动作与动作进度、朝向沿用规则、一次性消费。全部合成数据,期望值手算。
/// </summary>
public sealed class OnlineSettlementGapBTownWorldTests
{
    private static readonly TownPlace Start = new TownPlace(1, 0);
    private static readonly TownPlace ReturnPlace = new TownPlace(1, 1, TownEntry.Return);
    private static readonly TownPlace ServerPlace = new TownPlace(2, 3, TownEntry.Return);

    // ---- 来源令牌 ----

    /// <summary>保护的行为:三种落位来源各给固定的日志令牌;未知的枚举值退回 default。</summary>
    [Theory]
    [InlineData(OnlineSettlementReturnSource.Default, "default")]
    [InlineData(OnlineSettlementReturnSource.PreDungeonArea, "pre_dungeon")]
    [InlineData(OnlineSettlementReturnSource.ServerUserArea, "server_area")]
    public void SourceToken_maps_each_return_source_to_its_literal(OnlineSettlementReturnSource source, string token)
    {
        var target = new OnlineSettlementReturnTarget(source, ServerPlace);
        Assert.Equal(source, target.Source);
        Assert.Equal(token, target.SourceToken);
    }

    /// <summary>保护的行为:三个来源的枚举值互不相同(令牌按值分支,值撞车就会串号);默认构造的目标是 Default 来源;未定义的值走 default 令牌。</summary>
    [Fact]
    public void Return_sources_are_distinct_values_and_unknown_or_default_targets_use_the_default_token()
    {
        int[] values = Enum.GetValues<OnlineSettlementReturnSource>().Select(v => (int)v).ToArray();
        Assert.Equal(3, values.Length);
        Assert.Equal(3, values.Distinct().Count());

        Assert.Equal(OnlineSettlementReturnSource.Default, default(OnlineSettlementReturnTarget).Source);
        Assert.Equal("default", default(OnlineSettlementReturnTarget).SourceToken);
        Assert.Equal("default", new OnlineSettlementReturnTarget((OnlineSettlementReturnSource)42, null).SourceToken);
    }

    /// <summary>保护的行为:目标的各字段原样带出;坐标要 X、Y 都有才算有坐标。</summary>
    [Fact]
    public void Return_target_keeps_every_field_and_needs_both_coordinates_to_count_as_having_them()
    {
        var full = new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.PreDungeonArea, ServerPlace, 412.5, 265.25, true);
        Assert.Equal(OnlineSettlementReturnSource.PreDungeonArea, full.Source);
        Assert.Equal(ServerPlace, full.Place.Value);
        Assert.Equal(412.5, full.X.Value);
        Assert.Equal(265.25, full.Y.Value);
        Assert.True(full.FacingLeft.Value);
        Assert.True(full.HasCoordinates);

        Assert.False(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace).HasCoordinates);
        Assert.False(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, x: 10.0).HasCoordinates);
        Assert.False(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, y: 10.0).HasCoordinates);
    }

    // ---- 落点三场景 ----

    /// <summary>
    /// 保护的行为:没有待消费的回城目标(从没装载、装载的目标指向别的区域、装载后清除)时,
    /// <see cref="OnlineSettlementTownWorld.Create"/> 原样返回内层造的会话,不抛异常、不动主角与镜头、不清出口触发标志。
    /// </summary>
    [Fact]
    public void Create_without_a_consumable_target_returns_the_inner_session_untouched()
    {
        using var fixture = new HostedGatedRoomFixture();
        RoomSession room = fixture.CreateRoomSession(null, Array.Empty<GateSide>());
        TownHero hero = room.Hero;
        var session = new OnlineSettlementGapBHeroSession(hero, room.Camera);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => session);
        var wrapped = new OnlineSettlementTownWorld(inner);
        double x0 = hero.X, y0 = hero.Y, cameraX0 = room.Camera.X;
        bool facing0 = hero.FacingLeft;

        // 1. 从没装载过
        IHeroSession created = wrapped.Create(ReturnPlace);
        Assert.Same(session, created);
        Assert.Equal(new[] { ReturnPlace }, inner.Created);
        AssertUntouched(session, hero, room.Camera, x0, y0, facing0, cameraX0);

        // 2. 装载的目标(带坐标)指向别的区域:这次 Create 不消费、不落点;目标继续挂着
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 700.0, 150.0, true));
        Assert.True(wrapped.HasPendingReturn);
        Assert.Same(session, wrapped.Create(ReturnPlace));
        Assert.True(wrapped.HasPendingReturn);
        AssertUntouched(session, hero, room.Camera, x0, y0, facing0, cameraX0);

        // 3. 装载后又清除:同样原样
        wrapped.ClearReturn();
        Assert.False(wrapped.HasPendingReturn);
        Assert.Same(session, wrapped.Create(ServerPlace));
        AssertUntouched(session, hero, room.Camera, x0, y0, facing0, cameraX0);
        Assert.Equal(new[] { ReturnPlace, ReturnPlace, ServerPlace }, inner.Created);
    }

    /// <summary>
    /// 保护的行为:会话没有主角(Hero 为 null)、也没有待消费的目标时,不会误触发"清出口触发标志"
    /// (只有真的落点了才清),返回内层会话、不抛异常。
    /// </summary>
    [Fact]
    public void Create_without_a_target_never_clears_the_exit_latch_even_when_the_session_has_no_hero()
    {
        var heroless = new OnlineSettlementGapBHeroSession(null, null);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => heroless);
        var wrapped = new OnlineSettlementTownWorld(inner);

        Assert.Same(heroless, wrapped.Create(ReturnPlace));
        Assert.Equal(0, heroless.ExitResets);
        Assert.False(wrapped.HasPendingReturn);
    }

    /// <summary>
    /// 保护的行为:有回城目标但没有坐标(只给了村/区,或只给了 X、Y 之一)时,目标被消费、会话照常返回,
    /// 不抛异常、主角与镜头不被移动、不清出口触发标志(只有真的落点了才清)。
    /// </summary>
    [Fact]
    public void Create_with_a_target_but_no_coordinates_consumes_it_without_moving_the_hero()
    {
        using var fixture = new HostedGatedRoomFixture();
        RoomSession room = fixture.CreateRoomSession(null, Array.Empty<GateSide>());
        TownHero hero = room.Hero;
        var session = new OnlineSettlementGapBHeroSession(hero, room.Camera);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => session);
        var wrapped = new OnlineSettlementTownWorld(inner);
        double x0 = hero.X, y0 = hero.Y, cameraX0 = room.Camera.X;
        bool facing0 = hero.FacingLeft;

        // 只给了村/区
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.PreDungeonArea, ServerPlace));
        Assert.True(wrapped.HasPendingReturn);
        Assert.Equal(ServerPlace, wrapped.Return);
        Assert.Same(session, wrapped.Create(ServerPlace));
        Assert.False(wrapped.HasPendingReturn);
        Assert.Equal(ReturnPlace, wrapped.Return);
        AssertUntouched(session, hero, room.Camera, x0, y0, facing0, cameraX0);

        // 只给了 X(没有 Y):仍不算有坐标
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, x: 640.0, facingLeft: true));
        Assert.Same(session, wrapped.Create(ServerPlace));
        Assert.False(wrapped.HasPendingReturn);
        AssertUntouched(session, hero, room.Camera, x0, y0, facing0, cameraX0);

        // 只给了 Y(没有 X)
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, y: 130.0));
        Assert.Same(session, wrapped.Create(ServerPlace));
        Assert.False(wrapped.HasPendingReturn);
        AssertUntouched(session, hero, room.Camera, x0, y0, facing0, cameraX0);
    }

    /// <summary>保护的行为:同样没有主角的会话,"有目标没坐标"也不会清出口触发标志。</summary>
    [Fact]
    public void Create_with_a_target_but_no_coordinates_does_not_clear_the_exit_latch_of_a_heroless_session()
    {
        var heroless = new OnlineSettlementGapBHeroSession(null, null);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => heroless);
        var wrapped = new OnlineSettlementTownWorld(inner);

        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace));
        Assert.Same(heroless, wrapped.Create(ServerPlace));
        Assert.Equal(0, heroless.ExitResets);
        Assert.False(wrapped.HasPendingReturn);
    }

    /// <summary>
    /// 保护的行为:有坐标且会话非空时,主角落到该坐标、朝向按目标、动作回到站立且动作进度为 0(不是别的起始帧)、
    /// 镜头重来并追上主角、出口触发标志被清一次;目标被消费,第二次 Create 不再生效。
    /// </summary>
    [Fact]
    public void Create_with_coordinates_places_the_hero_at_rest_frame_zero_restarts_the_camera_and_consumes_the_target()
    {
        using var fixture = new HostedGatedRoomFixture();
        RoomSession room = fixture.CreateRoomSession(null, Array.Empty<GateSide>());
        TownHero hero = room.Hero;
        TownCamera camera = room.Camera;
        var session = new OnlineSettlementGapBHeroSession(hero, camera);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => session);
        var wrapped = new OnlineSettlementTownWorld(inner);

        // 让主角先"在路上":向右走几步,动作是行走、动作进度大于 0、朝右、镜头在最左
        for (int i = 0; i < 3; i++)
        {
            hero.Update(100.0, 1, 0);
        }
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Assert.True(hero.MotionMs > 0);
        Assert.False(hero.FacingLeft);
        Assert.Equal(0.0, camera.X);

        // 目标:坐标 (700, 150),朝左。镜头重来:先放最小值 0,再按死区追主角:屏幕位置 700 > 450 → 镜头 x = 700 - 450 = 250(最大值 320 之内)
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 700.0, 150.0, true));
        IHeroSession created = wrapped.Create(ServerPlace);

        Assert.Same(session, created);
        Assert.Equal(700.0, hero.X);
        Assert.Equal(150.0, hero.Y);
        Assert.True(hero.FacingLeft);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0L, hero.MotionMs);
        Assert.Equal(250.0, camera.X);
        Assert.Equal(1, session.ExitResets);
        Assert.False(wrapped.HasPendingReturn);
        Assert.Equal(ReturnPlace, wrapped.Return);

        // 目标已消费:主角被挪走后,同一区域再 Create 一次不会再落点
        hero.Place(333.0, 111.0, false, HeroMotion.Rest, 0);
        Assert.Same(session, wrapped.Create(ServerPlace));
        Assert.Equal(333.0, hero.X);
        Assert.Equal(111.0, hero.Y);
        Assert.False(hero.FacingLeft);
        Assert.Equal(1, session.ExitResets);
        Assert.Equal(new[] { ServerPlace, ServerPlace }, inner.Created);
    }

    /// <summary>保护的行为:目标没给朝向时,主角沿用落点前自己的朝向(左或右都一样);给了朝向才按目标改。</summary>
    [Fact]
    public void Create_keeps_the_hero_facing_when_the_target_gives_none_and_applies_it_when_given()
    {
        using var fixture = new HostedGatedRoomFixture();
        RoomSession room = fixture.CreateRoomSession(null, Array.Empty<GateSide>());
        TownHero hero = room.Hero;
        var session = new OnlineSettlementGapBHeroSession(hero, room.Camera);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => session);
        var wrapped = new OnlineSettlementTownWorld(inner);

        // 先朝左,目标没给朝向 → 仍朝左
        hero.Update(50.0, -1, 0);
        Assert.True(hero.FacingLeft);
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 410.0, 120.0));
        wrapped.Create(ServerPlace);
        Assert.Equal(410.0, hero.X);
        Assert.Equal(120.0, hero.Y);
        Assert.True(hero.FacingLeft);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 再朝右,目标没给朝向 → 仍朝右
        hero.Update(50.0, 1, 0);
        Assert.False(hero.FacingLeft);
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 420.0, 130.0));
        wrapped.Create(ServerPlace);
        Assert.Equal(420.0, hero.X);
        Assert.Equal(130.0, hero.Y);
        Assert.False(hero.FacingLeft);

        // 目标给了朝向 true / false:按目标
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 430.0, 140.0, true));
        wrapped.Create(ServerPlace);
        Assert.True(hero.FacingLeft);
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 440.0, 150.0, false));
        wrapped.Create(ServerPlace);
        Assert.False(hero.FacingLeft);
        Assert.Equal(440.0, hero.X);
        Assert.Equal(150.0, hero.Y);
        Assert.Equal(4, session.ExitResets);
    }

    /// <summary>
    /// 保护的行为:内层没造出会话(返回 null)时,即使目标带坐标也不抛异常、原样返回 null,目标照样被消费。
    /// </summary>
    [Fact]
    public void Create_returns_null_without_throwing_when_the_inner_world_has_no_session_even_with_coordinates()
    {
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => null);
        var wrapped = new OnlineSettlementTownWorld(inner);

        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 400.0, 100.0, false));
        Assert.True(wrapped.HasPendingReturn);
        IHeroSession created = wrapped.Create(ServerPlace);
        Assert.Null(created);
        Assert.False(wrapped.HasPendingReturn);
        Assert.Equal(new[] { ServerPlace }, inner.Created);

        // 没有目标时同样返回 null、不抛异常
        Assert.Null(wrapped.Create(ReturnPlace));
    }

    /// <summary>保护的行为:有坐标但会话里没有主角(或没有镜头)时,不抛异常,仍清出口触发标志(会话认得它)。</summary>
    [Fact]
    public void Create_with_coordinates_tolerates_a_session_without_hero_or_camera_and_still_clears_the_exit_latch()
    {
        var heroless = new OnlineSettlementGapBHeroSession(null, null);
        var inner = new OnlineSettlementGapBTownWorld(Start, ReturnPlace, _ => heroless);
        var wrapped = new OnlineSettlementTownWorld(inner);

        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, ServerPlace, 400.0, 100.0, true));
        Assert.Same(heroless, wrapped.Create(ServerPlace));
        Assert.Equal(1, heroless.ExitResets);
        Assert.False(wrapped.HasPendingReturn);
    }

    private static void AssertUntouched(OnlineSettlementGapBHeroSession session, TownHero hero, TownCamera camera,
        double x, double y, bool facingLeft, double cameraX)
    {
        Assert.Equal(x, hero.X);
        Assert.Equal(y, hero.Y);
        Assert.Equal(facingLeft, hero.FacingLeft);
        Assert.Equal(cameraX, camera.X);
        Assert.Equal(0, session.ExitResets);
    }
}
