using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 城镇宠物场:状态层的出战宠物 → 城镇里的跟随实体。验收口径:装备、收起、换宠物之后实体加减的断言;跟随目标序列(直接读状态机的输出);
/// 饱食随时间的流逝取自注入的时间源(手动钟),用状态层读回。宠物编号、物品编号与 .cre 里的数都是合成的(<see cref="PetTownKit"/>)。
/// </summary>
public class TownPetFieldTests
{
    private static readonly PetMaster Hero = new PetMaster(500, 300, false);
    private static readonly TownProjection Projection = new TownProjection(24, 200);

    private static ItemInfo Lookup(int code) => PetTownKit.Items.TryGetValue(code, out ItemInfo item) ? item : null;

    // ------------------------------------------------------------ 实体加减

    [Fact]
    public void NothingIsInTownWhileNoPetIsEquippedEvenWithPetsInTheRoster()
    {
        var rig = new PetTownKit.Rig();
        rig.Adopt(PetTownKit.ItemFull);
        rig.Adopt(PetTownKit.ItemPlain);

        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);
        Assert.Empty(rig.Field.BuildSprites(Projection, Hero));
        Assert.Empty(rig.Sprites.Calls);
        Assert.Equal((0, 0), (rig.Field.Spawned, rig.Field.Despawned));
    }

    [Fact]
    public void EquippingAddsExactlyOneEntityAtTheHomePointAndUnequippingRemovesIt()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.Adopt(PetTownKit.ItemFull);
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);

        Assert.Equal(PetEquipResult.Ok, rig.Session.Equip(pet.Uid, 0, false));
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Same(pet, entity.Pet);
        Assert.Same(entity, rig.Field.Find(pet.Uid));
        Assert.Equal(PetTownKit.PetFull, entity.Look.PetId);
        Assert.Equal((500.0 + PetTownKit.FullSpan, 300.0, PetTownKit.FullHeight), (entity.X, entity.Y, entity.Z));   // 主角背后 41,离地 37
        Assert.Equal((pet.Uid, TownPetField.ObjectIdBase + pet.Uid), (entity.Uid, entity.ObjectId));
        Assert.Equal((1, 0), (rig.Field.Spawned, rig.Field.Despawned));

        Assert.Equal(PetEquipResult.Ok, rig.Session.Unequip(0));
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);
        Assert.Null(rig.Field.Find(pet.Uid));
        Assert.Equal((1, 1), (rig.Field.Spawned, rig.Field.Despawned));

        // 再装备:是一个新实体
        Assert.Equal(PetEquipResult.Ok, rig.Session.Equip(pet.Uid, 0, false));
        rig.Field.Advance(16, Hero);
        TownPetEntity again = Assert.Single(rig.Field.Entities);
        Assert.NotSame(entity, again);
        Assert.Equal((2, 1), (rig.Field.Spawned, rig.Field.Despawned));
    }

    [Fact]
    public void EquippingTheSamePetAgainKeepsTheSameEntityAndItsAnimation()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull);
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);

        Assert.Equal(PetEquipResult.AlreadyEquipped, rig.Session.Equip(pet.Uid, 0, false));
        for (int i = 0; i < 5; i++)
        {
            rig.Field.Advance(16, Hero);
        }
        Assert.Same(entity, Assert.Single(rig.Field.Entities));
        Assert.Equal((1, 0), (rig.Field.Spawned, rig.Field.Despawned));
    }

    [Fact]
    public void SwappingThePetReplacesTheEntityWithOneThatHasTheNewPetsOwnLook()
    {
        var rig = new PetTownKit.Rig();
        PetInstance a = rig.AdoptAndEquip(PetTownKit.ItemFull);
        PetInstance b = rig.Adopt(PetTownKit.ItemPlain);
        rig.Field.Advance(16, Hero);
        Assert.Same(a, Assert.Single(rig.Field.Entities).Pet);

        Assert.Equal(PetEquipResult.Ok, rig.Session.Equip(b.Uid, 0, false));      // 状态层:旧的先卸下
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Same(b, entity.Pet);
        Assert.Null(rig.Field.Find(a.Uid));
        Assert.Equal(PetTownKit.PetPlain, entity.Look.PetId);
        Assert.Equal((500.0 - 64, 300.0, 0), (entity.X, entity.Y, entity.Z));     // 903 没写停靠距离与离地高度:缺省背后 64、高度 0
        Assert.Equal((2, 1), (rig.Field.Spawned, rig.Field.Despawned));
    }

    [Fact]
    public void WithSeveralPetsInTheRosterOnlyTheEquippedOneIsInTownAndAPetWithoutAnyArtStillFollows()
    {
        var rig = new PetTownKit.Rig();
        PetInstance full = rig.Adopt(PetTownKit.ItemFull);
        PetInstance plain = rig.Adopt(PetTownKit.ItemPlain);
        PetInstance grow = rig.Adopt(PetTownKit.ItemGrow);

        foreach (PetInstance pet in new[] { full, plain, grow, full })
        {
            rig.Session.Equip(pet.Uid, 0, false);
            rig.Field.Advance(16, Hero);
            Assert.Same(pet, Assert.Single(rig.Field.Entities).Pet);
        }

        rig.Session.Equip(grow.Uid, 0, false);
        rig.Field.Advance(16, Hero);
        Assert.False(rig.Field.Entities[0].Look.HasAppearance);   // 902 没写任何动作:画不出来,但状态层说它出战了,实体就在

        rig.Session.Unequip(0);
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);
    }

    [Fact]
    public void TheFieldReflectsSlotChangesMadeBehindItsBackOnTheVeryNextFrame()
    {
        var rig = new PetTownKit.Rig();
        PetInstance a = rig.Adopt(PetTownKit.ItemFull);
        PetInstance b = rig.Adopt(PetTownKit.ItemPlain);

        rig.Session.Equip(a.Uid, 0, false);                       // 不经过城镇场,直接改状态层
        rig.Field.Advance(16, Hero);
        Assert.Same(a, Assert.Single(rig.Field.Entities).Pet);

        rig.Session.Equip(b.Uid, 0, false);
        rig.Field.Advance(16, Hero);
        Assert.Same(b, Assert.Single(rig.Field.Entities).Pet);

        Assert.True(rig.Session.Release(b.Uid, 0));               // 装备着的被放走:先卸下再移出名册
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);
        Assert.Equal((2, 2), (rig.Field.Spawned, rig.Field.Despawned));
    }

    [Fact]
    public void ASavedGameRestoredIntoTheSessionReplacesTheEntityObjectWithTheSameUid()
    {
        var rig = new PetTownKit.Rig();
        PetInstance before = rig.AdoptAndEquip(PetTownKit.ItemFull);
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);

        var save = new HeroSave();
        rig.Session.Capture(save, 0);
        PetRestoreReport report = rig.Session.Restore(save, Lookup, 0, false);
        Assert.True(report.PetEquipped);

        rig.Field.Advance(16, Hero);
        TownPetEntity after = Assert.Single(rig.Field.Entities);
        Assert.NotSame(entity, after);
        Assert.NotSame(before, after.Pet);                        // 状态层换了实例对象,实体跟着重建
        Assert.Same(rig.Session.Equipped, after.Pet);
        Assert.Equal(before.Uid, after.Uid);
        Assert.Equal((2, 1), (rig.Field.Spawned, rig.Field.Despawned));
    }

    // ------------------------------------------------------------ 饱食与饿死(时间源注入)

    [Fact]
    public void AStarvedPetDisappearsFromTheFirstSecondItStarvesAndNotBefore()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull, fighting: true);   // 消耗中:每 60 秒掉 1 点,从 100 起
        rig.Field.Advance(16, Hero);
        Assert.Single(rig.Field.Entities);

        rig.Clock.Set(5940);                                                       // 100 − 99 = 1:严格小于 1 才算饿死,恰好 1 不算
        rig.Field.Advance(16, Hero);
        Assert.Single(rig.Field.Entities);
        Assert.Equal(1, pet.Stomach(rig.Clock.NowSeconds));

        rig.Clock.Set(5941);                                                       // 差一秒就掉到 1 以下
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);
        Assert.True(pet.IsStarved(rig.Clock.NowSeconds));
        Assert.Equal((1, 1), (rig.Field.Spawned, rig.Field.Despawned));
        Assert.Same(pet, rig.Session.Equipped);                                    // 状态层里它还装备着,只是城镇里不露面
        Assert.Empty(rig.Field.BuildSprites(Projection, Hero));
        Assert.Empty(rig.Sprites.Calls);
    }

    [Fact]
    public void FeedingAStarvedPetBringsItBackAtTheHomePointOfWhereTheMasterIsNow()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull, fighting: true);
        rig.Clock.Set(5941);
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);

        Assert.Equal(PetFeedResult.Revived, rig.Session.Feed(rig.Clock.NowSeconds));      // 状态层:饿死状态下喂食 = 复活
        Assert.True(pet.Stomach(rig.Clock.NowSeconds) >= 30);

        var elsewhere = new PetMaster(800, 340, false);
        rig.Field.Advance(16, elsewhere);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Equal((800.0 + PetTownKit.FullSpan, 340.0), (entity.X, entity.Y));        // 出现在主角现在这里的停靠点,不是原来的地方
        Assert.Equal((1, 0), (rig.Field.Spawned, rig.Field.Despawned));                  // 饿死期间一直没出现过,喂食之后才第一次出现
    }

    [Fact]
    public void EnteringTownRevivesAStarvedPetAndItReappears()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull, fighting: true);
        rig.Clock.Set(6000);
        rig.Field.Advance(16, Hero);
        Assert.Empty(rig.Field.Entities);
        Assert.Equal(PetState.Draining, pet.State);

        PetStateChange change = rig.Field.EnterTown();                                   // 状态层:装备且回补 + 饿死 → 复活成饱食 1
        Assert.True(change.Revived);
        Assert.Equal((PetState.Charging, 1), (pet.State, pet.Stomach(6000)));

        rig.Field.Advance(16, Hero);
        Assert.Same(pet, Assert.Single(rig.Field.Entities).Pet);
    }

    [Fact]
    public void TheSatietyOfTheEquippedPetOnlyMovesWithTheInjectedClock()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull, fighting: true);
        Assert.Same(rig.Clock, rig.Field.Clock);
        Assert.Same(rig.Session, rig.Field.Session);

        // 真实时间过去多久都没关系:钟没走饱食就不动
        Thread.Sleep(30);
        rig.Field.Advance(16, Hero);
        Assert.Equal(100, pet.Stomach(0));

        rig.Clock.Advance(600);                                                          // 消耗中 600 秒 −10
        rig.Field.Advance(16, Hero);
        Assert.Equal(90, pet.Stomach(rig.Clock.NowSeconds));
        rig.Clock.Advance(600);
        rig.Field.Advance(16, Hero);
        Assert.Equal(80, pet.Stomach(rig.Clock.NowSeconds));

        // 回补(不在战斗里):每 360 秒 +1,同样用这个钟
        rig.Field.EnterTown();
        rig.Clock.Advance(3600);
        rig.Field.Advance(16, Hero);
        Assert.Equal(90, pet.Stomach(rig.Clock.NowSeconds));
    }

    [Fact]
    public void AGrowTypePetNeverStarvesNoMatterHowLongTheClockRuns()
    {
        var rig = new PetTownKit.Rig();
        PetInstance grow = rig.AdoptAndEquip(PetTownKit.ItemGrow, fighting: true);
        rig.Clock.Set(1_000_000);
        rig.Field.Advance(16, Hero);
        Assert.Same(grow, Assert.Single(rig.Field.Entities).Pet);
    }

    // ------------------------------------------------------------ 跟随(读状态机输出)

    [Fact]
    public void TheEntityFollowsTheMasterThroughTheFollowRulesAndExposesEachStep()
    {
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Equal((PetFollowPhase.Idle, PetMotion.Stand), (entity.Last.Phase, entity.Motion));

        // 主角站着不动:宠物也不动
        for (int i = 0; i < 3; i++)
        {
            rig.Field.Advance(100, Hero);
        }
        Assert.Equal(459.0, entity.X);

        // 主角走到 600:宠物按 901 自己的速度上限(.cre 的 [move speed] 第 0 个数乘主角的横向换算系数)追
        var moved = new PetMaster(600, 300, false);
        rig.Field.Advance(100, moved);
        double cap = PetTownKit.FullSpeed[0] * TownHero.DefaultSpeedFactorX;
        double step = Math.Min(100 * 5.0, cap) * 0.1;
        Assert.Equal((PetFollowPhase.Chasing, PetMotion.Walk, 559.0), (entity.Last.Phase, entity.Motion, entity.Last.TargetX));
        Assert.Equal(459.0 + step, entity.X, 9);
        Assert.Equal(entity.Follower.X, entity.X);
        Assert.Equal(entity.Last.X, entity.X);
        Assert.False(entity.FacingLeft);
    }

    [Fact]
    public void TheAnimationClockRestartsWhenTheMotionChangesAndOtherwiseAccumulates()
    {
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemPlain);                  // 903:缺省停靠距离 −64、缺省速度 400/100(规则里写的)
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Equal(436.0, entity.X);

        // 主角到 600:停靠点 536,剩 100 → 走 40、30、15、7.5、3.75、1.875,第七步落在停靠点
        var moved = new PetMaster(600, 300, false);
        double[] x = { 476.0, 506.0, 521.0, 528.5, 532.25, 534.125 };
        long[] anim = { 0, 100, 200, 300, 400, 500 };             // 第一步动作从站立换成行走:从 0 起;之后同一个动作累加
        for (int i = 0; i < x.Length; i++)
        {
            rig.Field.Advance(100, moved);
            Assert.Equal((x[i], anim[i], PetMotion.Walk), (entity.X, entity.AnimMs, entity.Motion));
        }
        rig.Field.Advance(100, moved);
        Assert.Equal((536.0, 0L, PetMotion.Stand), (entity.X, entity.AnimMs, entity.Motion));   // 到了:动作换回站立,重新计时
        rig.Field.Advance(250, moved);
        Assert.Equal(250L, entity.AnimMs);
        rig.Field.Advance(0, moved);
        Assert.Equal(250L, entity.AnimMs);                        // 0 毫秒不涨
        rig.Field.Advance(-40, moved);
        Assert.Equal(250L, entity.AnimMs);                        // 负数也不倒退
    }

    [Fact]
    public void ArrivingInANewSceneSnapsEveryEntityToTheNewHomePointWithoutChasingAndRestartsItsAnimation()
    {
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemPlain);
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        rig.Field.Advance(100, new PetMaster(600, 300, false));
        rig.Field.Advance(100, new PetMaster(600, 300, false));
        Assert.Equal(PetMotion.Walk, entity.Motion);
        Assert.True(entity.AnimMs > 0);

        rig.Field.Arrive(new PetMaster(10, 20, true));
        Assert.Equal((74.0, 20.0, true), (entity.X, entity.Y, entity.FacingLeft));      // 朝左:停靠点在主角右边 64
        Assert.Equal((PetFollowPhase.Idle, PetMotion.Stand, 0L), (entity.Last.Phase, entity.Motion, entity.AnimMs));
        Assert.Equal(0, entity.Follower.Teleports);                                     // 落点不算传送
        Assert.Equal(1, rig.Field.Spawned);                                             // 也不换实体

        new TownPetField(rig.Session, rig.Clock).Arrive(new PetMaster(1, 2, false));    // 没有实体时什么也不做
    }

    // ------------------------------------------------------------ 出图

    [Fact]
    public void TheFirstFrameAfterEquippingShowsThePetEvenWithoutAnyAdvance()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.Adopt(PetTownKit.ItemFull);
        Assert.Empty(rig.Field.BuildSprites(Projection, Hero));

        rig.Session.Equip(pet.Uid, 0, false);                                           // 游戏暂停、窗口里换了宠物:没有 Advance 也要如实
        List<SceneSprite> sprites = rig.Field.BuildSprites(Projection, Hero);
        Assert.Single(sprites);
        Assert.Equal(1, rig.Field.Spawned);
    }

    [Fact]
    public void BuildSpritesAsksTheSourceForEachEntityWithItsLookMotionPositionAndFacing()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull);
        var master = new PetMaster(500.7, 300.9, true);                                  // 朝左:停靠点在主角右边 41
        List<SceneSprite> sprites = rig.Field.BuildSprites(Projection, master);

        RecordingSprites.Call call = Assert.Single(rig.Sprites.Calls);
        Assert.Equal((PetTownKit.PetFull, PetMotion.Stand, 0L), (call.Look.PetId, call.Motion, call.AnimMs));
        Assert.Equal((541, 300, PetTownKit.FullHeight, true), (call.X, call.Y, call.Z, call.FacingLeft));   // 541.7、300.9 向下取整
        Assert.Equal(Projection, call.Projection);
        Assert.Equal(TownPetField.ObjectIdBase + pet.Uid, call.ObjectId);

        SceneSprite sprite = Assert.Single(sprites);
        Assert.Equal((541 - 24, 300 - PetTownKit.FullHeight + 200, 300), (sprite.ScreenX, sprite.ScreenY, sprite.SortKey));
        Assert.Equal((TownPetField.ObjectIdBase + pet.Uid, "creature/p901/motion/stay.ani", true), (sprite.NpcId, sprite.AnimationPath, sprite.Mirrored));
    }

    [Fact]
    public void NegativeCoordinatesAreFlooredNotTruncated()
    {
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        rig.Field.BuildSprites(Projection, new PetMaster(-10.5, -3.5, false));          // 停靠点 −51.5、−3.5
        RecordingSprites.Call call = Assert.Single(rig.Sprites.Calls);
        Assert.Equal((-52, -4), (call.X, call.Y));
    }

    [Fact]
    public void ThePassedMotionAndAnimationTimeAreTheEntitysCurrentOnes()
    {
        var rig = new PetTownKit.Rig();
        rig.AdoptAndEquip(PetTownKit.ItemPlain);
        rig.Field.Advance(16, Hero);
        var moved = new PetMaster(600, 300, false);
        rig.Field.Advance(100, moved);
        rig.Field.Advance(100, moved);
        rig.Field.Advance(100, moved);

        rig.Field.BuildSprites(Projection, moved);
        RecordingSprites.Call call = Assert.Single(rig.Sprites.Calls);
        Assert.Equal((PetMotion.Walk, 200L, false), (call.Motion, call.AnimMs, call.FacingLeft));
        Assert.Equal((521, 300), (call.X, call.Y));                                     // 476、506、521:三步之后 521
    }

    [Fact]
    public void WithoutASpriteSourceOrWithoutAPictureTheEntityStaysButNothingIsDrawn()
    {
        var noSource = new PetTownKit.Rig(withSprites: false);
        noSource.AdoptAndEquip(PetTownKit.ItemFull);
        Assert.Empty(noSource.Field.BuildSprites(Projection, Hero));
        Assert.Single(noSource.Field.Entities);

        var noPicture = new PetTownKit.Rig();
        noPicture.AdoptAndEquip(PetTownKit.ItemFull);
        noPicture.Sprites.ReturnNothing = true;
        Assert.Empty(noPicture.Field.BuildSprites(Projection, Hero));
        Assert.Single(noPicture.Sprites.Calls);                                         // 问过了,只是这一帧没有图
        Assert.Single(noPicture.Field.Entities);
    }

    [Fact]
    public void WithoutALookCatalogThePetStillFollowsWithTheDefaultSpanAndHeight()
    {
        var rig = new PetTownKit.Rig(withLooks: false);
        rig.AdoptAndEquip(PetTownKit.ItemFull);
        rig.Field.Advance(16, Hero);
        TownPetEntity entity = Assert.Single(rig.Field.Entities);
        Assert.Equal((PetTownKit.PetFull, false), (entity.Look.PetId, entity.Look.HasAppearance));
        Assert.Equal((500.0 - 64, 300.0, 0), (entity.X, entity.Y, entity.Z));
    }

    // ------------------------------------------------------------ 阶段切换

    [Fact]
    public void TownAndDungeonStagesSwitchThePetBetweenChargingAndDraining()
    {
        var rig = new PetTownKit.Rig();
        Assert.False(rig.Field.EnterTown().Revived);                                    // 没有宠物:什么也不变
        Assert.Equal(0, rig.Field.EnterDungeon().DeathCheckDelay);

        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull);
        Assert.Equal(PetState.Charging, pet.State);

        PetStateChange toDungeon = rig.Field.EnterDungeon();
        Assert.Equal(PetState.Draining, pet.State);
        Assert.Equal(6000, toDungeon.DeathCheckDelay);                                  // 饱食 100 × 60:状态层排的死亡检查延迟

        rig.Field.EnterTown();
        Assert.Equal(PetState.Charging, pet.State);

        rig.Field.OnStage(GameStage.Dungeon);
        Assert.Equal(PetState.Draining, pet.State);
        foreach (GameStage stage in Enum.GetValues(typeof(GameStage)))
        {
            if (stage == GameStage.Dungeon)
            {
                continue;
            }
            rig.Field.OnStage(GameStage.Dungeon);
            Assert.Equal(PetState.Draining, pet.State);
            rig.Field.OnStage(stage);                                                   // 城镇、副本选择、加载、选角:都不在战斗
            Assert.Equal(PetState.Charging, pet.State);
        }

        // 没装备的宠物不受阶段影响(仍是收起状态)
        PetInstance stored = rig.Adopt(PetTownKit.ItemPlain);
        rig.Field.OnStage(GameStage.Dungeon);
        Assert.Equal(PetState.Stored, stored.State);
    }

    [Fact]
    public void TheDungeonDrainsSatietyAtTheInjectedClockRateAndTownGivesItBack()
    {
        var rig = new PetTownKit.Rig();
        PetInstance pet = rig.AdoptAndEquip(PetTownKit.ItemFull);
        rig.Field.OnStage(GameStage.Dungeon);
        rig.Clock.Advance(1200);                                                        // 在副本里 20 分钟:−20
        rig.Field.OnStage(GameStage.Town);
        Assert.Equal(80, pet.Stomach(rig.Clock.NowSeconds));
        rig.Clock.Advance(3600);                                                        // 回城 1 小时:+10
        Assert.Equal(90, pet.Stomach(rig.Clock.NowSeconds));
    }

    [Fact]
    public void EnteringTheDungeonSettlesTheTownChargeAtTheClockTimeBeforeTheDrainStarts()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, false, (1, PetTownKit.ItemFull, 0, 40));      // 出战、回补中、饱食 40
        PetInstance pet = rig.Session.Equipped;
        rig.Clock.Advance(3600);                                     // 城镇里一小时:+10

        rig.Field.OnStage(GameStage.Dungeon);                        // 进副本:先把回补结算到此刻,再改成消耗中
        Assert.Equal((PetState.Draining, 50), (pet.State, pet.Stomach(rig.Clock.NowSeconds)));
        rig.Clock.Advance(600);                                      // 副本里十分钟:−10
        Assert.Equal(40, pet.Stomach(rig.Clock.NowSeconds));

        rig.Field.OnStage(GameStage.Town);                           // 回城:同样先结算再改回回补
        Assert.Equal((PetState.Charging, 40), (pet.State, pet.Stomach(rig.Clock.NowSeconds)));
        rig.Clock.Advance(3600);
        Assert.Equal(50, pet.Stomach(rig.Clock.NowSeconds));
    }

    [Fact]
    public void TheConstructorNeedsASessionAndAClock()
    {
        var rig = new PetTownKit.Rig();
        Assert.Throws<ArgumentNullException>(() => new TownPetField(null, rig.Clock));
        Assert.Throws<ArgumentNullException>(() => new TownPetField(rig.Session, null));
        Assert.NotNull(new TownPetField(rig.Session, rig.Clock));   // 外观目录、精灵来源与规则都可以不给
    }
}
