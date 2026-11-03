using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 条件批一的接线(M68,证据 E-166):[IS FIRE/WATER/DARK/LIGHT/NONE DAMAGE] 看到的元素来自命中它的那一击
/// (<see cref="MonsterActor.ApplyHit(int, bool, double, bool, HitStunInput, KnockdownInput, bool, int)"/> 的 elementMask),出招运行器与过程动作都收到;
/// [IS END MAP]、[IS END MAP VERTICALLY] 用舞台的宽与高(<see cref="RoomMap.Width"/>、<see cref="RoomMap.HeightPixels"/>)与拥有者此刻的脚本速度;
/// [IS TARGETACTIVESTATUS] 看主角的异常状态集合。规则本身(真值表、取整、边距)在 Game 测试里;这里只看宿主与实体把数据接对了。脚本内容全是合成的。
/// </summary>
public partial class PassiveObjectTests
{
    // ------------------------------------------------------------------ 元素

    [Theory]
    [InlineData("f_procfire", 1, 1)]
    [InlineData("f_procfire", 3, 1)]          // 火加水:带火就算
    [InlineData("f_procfire", 2, 0)]
    [InlineData("f_procfire", 4, 0)]
    [InlineData("f_procfire", 8, 0)]
    [InlineData("f_procfire", 0, 0)]
    [InlineData("f_procwater", 2, 1)]
    [InlineData("f_procwater", 3, 1)]
    [InlineData("f_procwater", 1, 0)]
    [InlineData("f_procwater", 0, 0)]
    [InlineData("f_procnone", 0, 1)]
    [InlineData("f_procnone", 1, 0)]
    [InlineData("f_procnone", 8, 0)]
    public void TheProcScriptsElementConditionSeesTheElementsOfTheHitThatWokeIt(string mob, int elementMask, int spawned)
    {
        using var arena = new ActArena(mob, useVitals: false);
        arena.Steps(3);
        arena.Monster.ApplyHit(1, false, 0, false, default, default, elementMask: elementMask);
        Assert.Equal(spawned, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void AHitThatCarriesNoElementArgumentCountsAsElementNone()
    {
        using var none = new ActArena("f_procnone", useVitals: false);
        none.Steps(3);
        none.Monster.ApplyHit(1, false, 0, false);                  // 老入口:不带元素
        Assert.Equal(1, none.Session.PassivesSpawned);

        using var fire = new ActArena("f_procfire", useVitals: false);
        fire.Steps(3);
        fire.Monster.ApplyHit(1, false, 0, false);
        Assert.Equal(0, fire.Session.PassivesSpawned);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    public void TheCurrentActionsElementConditionSeesTheElementsOfTheHitToo(int elementMask, int spawned)
    {
        using var arena = new ActArena("f_stayfire", useVitals: false);         // 站立动作里的 [ON DAMAGE] 带火元素条件
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.ActiveFamily == MonsterActFamily.Stay) > 0);      // 先出一招,回到站立
        m.ApplyHit(1, false, 0, false, default, default, elementMask: elementMask);
        Assert.Equal(spawned, arena.Session.PassivesSpawned);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    public void AnAttackScriptsDamageEventCarriesTheElementsOfTheHitToo(int elementMask, int spawned)
    {
        using var arena = new ActArena("xfire", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack, 600) > 0);
        Assert.Equal(0, arena.Session.PassivesSpawned);
        m.ApplyHit(1, false, 0, false, default, default, elementMask: elementMask);        // 出招时挨一下
        Assert.Equal(spawned, arena.Session.PassivesSpawned);
    }

    /// <summary>主角按着攻击键打这只怪物(命中信息带这些元素),打中第一下就停;返回被唤醒放出的对象数。</summary>
    private static int SpawnsAfterTheHeroStrikes(string mob, int elementMask)
    {
        var input = new HitInput(100f, false, AttackKind.Physic, 100f, elementMask: elementMask);
        var strike = new HeroStrikeInfo(0, 0, false, source: input);
        using var arena = new ActArena(mob, frozenHero: true, strikes: new[] { strike, strike, strike });
        RoomCombatSession s = arena.Session;
        for (int i = 0; i < 200 && s.PassivesSpawned == 0 && arena.Monster.Hp == arena.Monster.HpMax; i++)
        {
            s.Update(10, 0, 0, i % 20 == 0, false);                       // 隔一阵按一下攻击键
        }
        return s.PassivesSpawned;
    }

    [Theory]
    [InlineData(1)]                           // 火
    [InlineData(2)]                           // 水
    [InlineData(0)]                           // 没有元素
    public void AHeroStrikeCarriesItsHitInputsElementsIntoTheMonstersDamageEvent(int elementMask)
    {
        Assert.Equal(1, SpawnsAfterTheHeroStrikes("f_procdmg", elementMask));      // 对照:挨打就放的怪物,证明这一击打得到
        Assert.Equal(elementMask == 1 ? 1 : 0, SpawnsAfterTheHeroStrikes("f_procfire", elementMask));
        Assert.Equal(elementMask == 2 ? 1 : 0, SpawnsAfterTheHeroStrikes("f_procwater", elementMask));
        Assert.Equal(elementMask == 0 ? 1 : 0, SpawnsAfterTheHeroStrikes("f_procnone", elementMask));
    }

    // ------------------------------------------------------------------ 舞台端头

    [Theory]
    [InlineData(100, 60)]            // 左端:怪物 X ≤ 150;主角站在它外侧,怪物朝外出招
    [InlineData(1020, 1060)]         // 右端:X > 舞台宽(5 块 × 224)− 150
    public void IsEndMapPassesAtEitherStageEndWhenTheScriptsSpeedHeadsOutAndNotWhenItHeadsBackToTheMiddle(int monsterX, int heroX)
    {
        using var outward = new ActArena("xendout", monsterX: monsterX, heroX: heroX, useVitals: false);
        Assert.True(outward.Until(() => outward.Session.PassivesSpawned > 0, 800) > 0);

        using var inward = new ActArena("xendin", monsterX: monsterX, heroX: heroX, useVitals: false);
        MonsterActor m = inward.Monster;
        Assert.True(inward.Until(() => m.SwingNumber > 0, 800) > 0);
        inward.Steps(60);                                           // 帧 1 早过了
        Assert.Equal(0, inward.Session.PassivesSpawned);            // 同一个位置,速度朝向舞台中间:不算到头
    }

    [Fact]
    public void IsEndMapDoesNotPassInTheMiddleOfTheStage()
    {
        using var arena = new ActArena("xendout", useVitals: false);        // 怪物 340,主角 300:离两端都远
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.SwingNumber > 0, 800) > 0);
        arena.Steps(60);
        Assert.Equal(0, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void IsEndMapVerticallyUsesTheStageHeightAndThePositionDepth()
    {
        using var arena = new ActArena("xendv", useVitals: false);          // 怪物的深度 100,离靠上的一端不到 200:舞台高取得到就通过
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0, 800) > 0);
    }

    [Fact]
    public void TheMonstersScriptSpeedOnTheHeightAxisReachesTheSpeedCondition()
    {
        using var rising = new ActArena("xzspeed", useVitals: false);
        Assert.True(rising.Until(() => rising.Session.PassivesSpawned > 0, 800) > 0);        // 出招起 Z 速度 300,帧 1 时还在往上

        using var still = new ActArena("xzspeedn", useVitals: false);                           // 对照:没起速度,高度速度是 0
        MonsterActor m = still.Monster;
        Assert.True(still.Until(() => m.SwingNumber > 0, 800) > 0);
        still.Steps(60);
        Assert.Equal(0, still.Session.PassivesSpawned);
    }

    [Fact]
    public void AMonstersActEntityReportsItsScriptSpeedOnAllThreeAxes()
    {
        using var arena = new ActArena("xdrift", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.ActSpeedY != 0, 800) > 0);          // 出招一开始就起了三个轴的速度
        Assert.Equal(50.0, m.ActSpeedY);
        Assert.NotEqual(0.0, m.ActSpeedX);
        Assert.NotEqual(0.0, m.ActSpeedZ);
        IActEntity self = m.ActSelf;
        Assert.Equal((float)m.ActSpeedX, self.SpeedX);                       // X 速度随朝向取反,实体报的是同一个数
        Assert.Equal((float)m.ActSpeedY, self.SpeedY);
        Assert.Equal((float)m.ActSpeedZ, self.SpeedZ);
    }

    [Fact]
    public void IsEndMapVerticallyUsesTheStageHeightNotItsWidthAtTheFarEnd()
    {
        // 舞台只有 72 像素高(6 行)、1120 宽;怪物深度 100 已在远端之外,Y 速度向前(正)——远端那一支靠舞台高才成立;
        // 要是误取舞台宽(远端阈值 1070)就只剩近端那一支,而近端要求速度不为正,条件就不通过了
        using var arena = new ActArena("xdrift", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0, 800) > 0);
    }

    // ------------------------------------------------------------------ 被动对象当拥有者

    [Fact]
    public void APassiveObjectOwnerFindsTheHeroWithCheckDistanceFromItsOwnPosition()
    {
        // 客户端把拥有者转成碰撞对象类,被动对象也转得过去(E-166 更正 E-156):怪物出招放出两个被动对象,
        // 范围 900×900 的看得到主角、放一个常驻对象;范围 0×0 的看不到,什么也不放
        using var arena = new ActArena("xnear", useVitals: false);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned >= 3, 800) > 0);
        arena.Steps(30);
        Assert.Equal(3, arena.Session.PassivesSpawned);
    }

    // ------------------------------------------------------------------ 被动对象的速度

    [Fact]
    public void APassiveObjectReportsItsScriptSpeedOnAllThreeAxesWithXFollowingItsFacing()
    {
        using var rig = new Rig();
        PassiveObject left = rig.Spawn(AvatarFixture.ActDriftId, facingLeft: true);
        PassiveObject right = rig.Spawn(AvatarFixture.ActDriftId, facingLeft: false);
        Advance(left, 10);
        Advance(right, 10);
        IActEntity l = left;
        IActEntity r = right;
        Assert.Equal(-200f, l.SpeedX);                   // 出生起 X 速度 200,朝左取反
        Assert.Equal(200f, r.SpeedX);
        Assert.Equal(50f, l.SpeedY);
        Assert.Equal(50f, r.SpeedY);
        Assert.InRange(l.SpeedZ, 100f, 300f);            // 高度速度出生时 300,受重力一路减小
        Assert.InRange(r.SpeedZ, 100f, 300f);
    }

    // ------------------------------------------------------------------ 异常状态

    [Fact]
    public void TheTargetStatusConditionSeesTheStatusTheScriptAppliedFramesEarlier()
    {
        using var arena = new ActArena("xstatus", frozenHero: true);
        RoomCombatSession s = arena.Session;
        s.UseStatuses(ActStatusTable());
        Assert.Equal(0, s.PassivesSpawned);

        Assert.True(arena.Until(() => s.PassivesSpawned > 0, 600) > 0);     // 帧 1 挂上眩晕,帧 3 条件才看到
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Stun));
        Assert.True(s.StatusApplied >= 1);
    }

    [Fact]
    public void TheTargetStatusConditionStaysShutWhenTheStatusMissedOrThereIsNoStatusSystem()
    {
        using var missed = new ActArena("xstatusmiss", frozenHero: true);
        RoomCombatSession ms = missed.Session;
        ms.UseStatuses(ActStatusTable());
        Assert.True(missed.Until(() => ms.ActStatusRequests > 0) > 0);
        missed.Steps(30);                                                   // 帧 3 已过
        Assert.Equal((0, 1), (ms.StatusApplied, ms.StatusMissed));          // 概率 0:掷了没中,主角没状态
        Assert.Equal(0, ms.PassivesSpawned);

        using var none = new ActArena("xstatus", frozenHero: true);         // 没装状态表:主角没有状态系统,请求不接,条件看不到状态
        none.Steps(80);
        Assert.Null(none.Session.HeroStatus);
        Assert.Equal(0, none.Session.PassivesSpawned);
    }

    [Fact]
    public void TheHeroEntityAnswersHasActiveStatusFromItsOwnStatusSet()
    {
        using var arena = new ActArena("xstatus", frozenHero: true);
        RoomCombatSession s = arena.Session;
        IActEntity hero = s.HeroEntity;
        Assert.False(hero.HasActiveStatus((int)ActiveStatusKind.Stun));     // 还没有状态系统
        s.UseStatuses(ActStatusTable());
        Assert.False(hero.HasActiveStatus((int)ActiveStatusKind.Stun));

        Assert.True(arena.Until(() => s.HeroStatus.Has(ActiveStatusKind.Stun)) > 0);
        Assert.True(hero.HasActiveStatus((int)ActiveStatusKind.Stun));
        Assert.False(hero.HasActiveStatus((int)ActiveStatusKind.Slow));
        Assert.False(hero.HasActiveStatus(-1));                             // 越界的号不当成别的状态
        Assert.False(hero.HasActiveStatus(300));
        Assert.False(hero.HasActiveStatus((int)ActiveStatusKind.Stun + 256));       // 截断成一个字节会撞上眩晕:不能当成眩晕
        Assert.False(hero.HasActiveStatus((int)ActiveStatusKind.Stun - 256));
    }

    [Fact]
    public void MonstersAndPassiveObjectsHaveNoStatusSystemYet()
    {
        using var arena = new ActArena("xstatus", useVitals: false, frozenHero: true);
        Assert.False(arena.Monster.ActSelf.HasActiveStatus((int)ActiveStatusKind.Stun));
    }
}
