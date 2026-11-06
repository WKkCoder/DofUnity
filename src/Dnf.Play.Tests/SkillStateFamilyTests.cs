using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 状态族扩展在房间会话里的行为(M104,证据 E-271、E-272,客户端状态 0x21、0x25、0x27):
/// 段首瞬移与"留在出发点的被动对象"、逐击连段的"起手自己接打击、打击过了接段帧再按键接下一击、最后一击与没按键时回站立、倒数第二击可被锁"、
/// 每段"不滑行"。数据全是合成的(技能 <see cref="AvatarFixture.ChainSkill"/>,见 <see cref="SkillAutoChainTests"/> 的说明):
/// [static data] (200, −400, 100, −100, 2),[level info] 2 级 (180, 300, 40, 60),冷却 3000、耗蓝 20;[etc motion] 第 3 项 300 毫秒(第 1 帧 100 到 200 毫秒有攻击框)、
/// 第 2 项 400 毫秒(第 2 帧 200 到 300 毫秒有攻击框)、第 1 项 600 毫秒;[etc attack info] 第 1、2 项是前两项动作各自的攻击信息。主角出生在 (300, 100)。
/// </summary>
public class SkillStateFamilyTests
{
    private const int WarpDistance = 200;      // [static data] 第 0 个数
    private const int PoseMs = 300;            // [etc motion] 第 3 项的时长
    private const int StrikeMs = 400;          // [etc motion] 第 2 项的时长
    private const int HeavyMs = 600;           // [etc motion] 第 1 项的时长

    // ------------------------------------------------------------------ 夹具

    private static SkillRecipes WarpRecipes(bool objectAtOrigin = true, bool warp = true, int forwardX = 0, int warpMotion = 2)
    {
        SkillObjectSpec obj = new SkillObjectSpec(AvatarFixture.WaveObjectId, stage: 1, frame: 0, damageRateColumn: 1, damageFixedColumn: -1, forwardX: forwardX);
        SkillStageSpec warpStage = new SkillStageSpec(motionIndex: warpMotion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    warp ? warpStage.WithWarp(new SkillWarpSpec(0)) : warpStage,
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                objects: new[] { objectAtOrigin ? obj.AtWarpOrigin() : obj },
                autoChain: true),
        });
    }

    /// <summary>四段模板:起手 300 毫秒(自己接、不滑行)、打击 400 毫秒(第 2 帧起能按键接)、最后一击的起手 600 毫秒、最后一击的打击 300 毫秒;起滑速度与加速度取 [static data] 第 2、3 个数。</summary>
    private static SkillRecipes RepeatRecipes(int countIndex = 4, int lockIndex = -1, bool windUpsSlide = false)
    {
        SkillStageSpec windUp = new SkillStageSpec(motionIndex: 3, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0).WithAutoAdvance();
        SkillStageSpec lastWindUp = new SkillStageSpec(motionIndex: 1, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0).WithAutoAdvance();
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    windUpsSlide ? windUp : windUp.WithoutSlide(),
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    windUpsSlide ? lastWindUp : lastWindUp.WithoutSlide(),
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: 2, slideAccelIndex: 3, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 2, slideFollowsHeldDirection: false)
                .WithRepeat(new SkillRepeatSpec(countIndex, lockIndex)),
        });
    }

    private static HeroSkills Load(GameData data, SkillRecipes recipes)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 2));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    /// <summary>可以走的房间(主角带全套动作):怪物不动,可走区域默认不限制,也可以给一个矩形。</summary>
    private static RoomCombatTests.Arena NewArena(SkillRecipes recipes, (int X, int Y)[] monsters = null, bool facingLeft = false, List<WorldRect> walkArea = null,
        HeroVitals vitals = null, bool frozen = false)
    {
        var arena = new RoomCombatTests.Arena(monsters ?? new[] { (1900, 100) }, hp: 1000, frozen: frozen, facingLeft: facingLeft, chr: AvatarFixture.ActionChrPath,
            vitals: vitals, monsterSpeed: 0, walkArea: walkArea, skillsFactory: data => Load(data, recipes));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    private static void Run(RoomCombatSession s, int ms, params (int At, SkillKeys Keys)[] presses)
    {
        for (int t = 0; t < ms; t += 10)
        {
            SkillKeys keys = SkillKeys.None;
            foreach ((int at, SkillKeys k) in presses)
            {
                if (at == t)
                {
                    keys |= k;
                }
            }
            s.Update(10, 0, 0, false, false, keys);
        }
    }

    private static List<WorldRect> Walls(int right) => new List<WorldRect> { new WorldRect(0, 0, right, 300) };

    // ------------------------------------------------------------------ 段首瞬移

    [Fact]
    public void TheWarpStageJumpsTheHeroTheStaticDistanceAheadAndTheObjectStaysWhereTheHeroStood()
    {
        using var arena = NewArena(WarpRecipes());
        RoomCombatSession s = arena.Session;
        Assert.Equal((300.0, 100.0), (s.Hero.X, s.Hero.Y));
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 20);                                               // 290 毫秒:还在姿势段,没动,没有对象
        Assert.Equal((0, 300.0, 0), (s.Hero.SkillStage, s.Hero.X, s.HeroObjectsSpawned));
        Run(s, 10);                                                        // 300 毫秒:接上第二段,同一步里瞬移、放对象
        Assert.Equal((1, 300.0 + WarpDistance, 100.0), (s.Hero.SkillStage, s.Hero.X, s.Hero.Y));
        HeroObject o = Assert.Single(s.HeroObjects);
        Assert.Equal((300.0, 100.0, false), (o.X, o.Y, o.FacingLeft));      // 对象留在出发点
    }

    [Fact]
    public void AnObjectNotMarkedAsOriginIsPlacedAtTheLandingPoint()
    {
        using var arena = NewArena(WarpRecipes(objectAtOrigin: false));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 10);
        Assert.Equal(500.0, s.Hero.X);
        Assert.Equal(500.0, Assert.Single(s.HeroObjects).X);                // 对照:没标"放在出发点"的对象放在放它那一刻主角的位置,即落点
    }

    [Fact]
    public void FacingLeftTheWarpGoesLeftAndTheOriginObjectKeepsItsForwardOffsetOnTheFacingSide()
    {
        using var arena = NewArena(WarpRecipes(forwardX: 20), facingLeft: true);
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 10);
        Assert.Equal((1, 100.0, true), (s.Hero.SkillStage, s.Hero.X, s.Hero.FacingLeft));     // 300 − 200
        HeroObject o = Assert.Single(s.HeroObjects);
        Assert.Equal((280.0, true), (o.X, o.FacingLeft));                    // 出发点 300,朝左的前方偏移 20 → 280

        using var right = NewArena(WarpRecipes(forwardX: 20));
        Run(right.Session, 10, (0, SkillKeys.A));
        Run(right.Session, PoseMs - 10);
        Assert.Equal(320.0, Assert.Single(right.Session.HeroObjects).X);      // 对照:朝右 300 + 20
    }

    [Fact]
    public void AWallShortensTheWarpToTheLastStandablePixelAndABlockedFirstStepKeepsTheHeroInPlace()
    {
        using var wall = NewArena(WarpRecipes(), walkArea: Walls(450));
        Run(wall.Session, 10, (0, SkillKeys.A));
        Run(wall.Session, PoseMs - 10);
        Assert.Equal(450.0, wall.Session.Hero.X);                           // 301 到 450 站得住,451 站不住
        Assert.Equal(300.0, Assert.Single(wall.Session.HeroObjects).X);

        using var edge = NewArena(WarpRecipes(), walkArea: Walls(300));
        Run(edge.Session, 10, (0, SkillKeys.A));
        Run(edge.Session, PoseMs - 10);
        Assert.Equal((1, 300.0), (edge.Session.Hero.SkillStage, edge.Session.Hero.X));      // 第一步就站不住:原地,技能照常走完
        Assert.Equal(300.0, Assert.Single(edge.Session.HeroObjects).X);

        using var exact = NewArena(WarpRecipes(), walkArea: Walls(500));
        Run(exact.Session, 10, (0, SkillKeys.A));
        Run(exact.Session, PoseMs - 10);
        Assert.Equal(500.0, exact.Session.Hero.X);                          // 目标点正好站得住:整段走完
    }

    [Fact]
    public void TheNextCastRemembersItsOwnDepartureNotTheOldOne()
    {
        using var arena = NewArena(WarpRecipes());
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 10);
        Assert.Equal((500.0, 300.0), (s.Hero.X, s.HeroObjects.Last().X));
        Run(s, 3200);                                                      // 冷却 3000 毫秒过去,第一次早已播完
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 10);
        Assert.Equal(2, s.SkillCasts);
        Assert.Equal((700.0, 500.0), (s.Hero.X, s.HeroObjects.Last().X));   // 第二次从 500 出发:落点 700,对象留在 500,不是第一次的 300
    }

    [Fact]
    public void AnOriginObjectInARecipeWithoutAWarpFallsBackToTheHeroPosition()
    {
        using var arena = NewArena(WarpRecipes(warp: false));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 10);
        Assert.Equal(300.0, s.Hero.X);                                      // 没有瞬移,主角没动
        Assert.Equal(300.0, Assert.Single(s.HeroObjects).X);               // 没有记到出发点:放在放对象那一刻主角的位置(恰好也是 300)
    }

    [Fact]
    public void AWarpStageWhoseMotionCannotBeEnteredDoesNotMoveTheHeroNorDropTheObject()
    {
        // 第二段的动作下标读不到(越界):这一段进不去,技能照常在姿势段播完时结束;瞬移与对象都挂在"这一段放成了"上,所以主角没动、也没有对象
        using var broken = NewArena(WarpRecipes(warpMotion: 99));
        RoomCombatSession s = broken.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs + 100);
        Assert.Equal(1, s.SkillCasts);
        Assert.Equal((300.0, 0, HeroMotion.Rest), (s.Hero.X, s.HeroObjectsSpawned, s.Hero.Motion));

        using var fine = NewArena(WarpRecipes());                           // 对照:动作读得到,同一时刻已经瞬移并放了对象
        Run(fine.Session, 10, (0, SkillKeys.A));
        Run(fine.Session, PoseMs - 10);
        Assert.Equal((500.0, 1), (fine.Session.Hero.X, fine.Session.HeroObjectsSpawned));
    }

    [Fact]
    public void TheOriginObjectHitsAMonsterTheHeroWarpedOverAndTheBodyNeverHits()
    {
        // 怪物站在出发点前 40 像素(340, 100);对象的框在 x = 出发点 + 20 起宽 120(320 到 440),所以够得着;落点 500 在怪物身后,对象放在落点的话(520 起)够不着
        using var origin = NewArena(WarpRecipes(), monsters: new[] { (340, 100) });
        Run(origin.Session, 10, (0, SkillKeys.A));
        Run(origin.Session, 500);
        Assert.Equal((1, 1, 1), (origin.Session.Hits, origin.Session.HeroObjectHits, origin.Session.HeroObjectsSpawned));
        double expected = PlaceholderDamage.SkillBaseHit * 300 / 100.0;     // 对象取 [level info] 第 1 列(2 级 300%),没有固定值列
        Assert.InRange(origin.Session.TotalDamage, (long)Math.Floor(expected), (long)Math.Floor(expected) + 1);

        using var landing = NewArena(WarpRecipes(objectAtOrigin: false), monsters: new[] { (340, 100) });
        Run(landing.Session, 10, (0, SkillKeys.A));
        Run(landing.Session, 500);
        Assert.Equal((0, 0, 1), (landing.Session.Hits, landing.Session.HeroObjectHits, landing.Session.HeroObjectsSpawned));
    }

    // ------------------------------------------------------------------ 逐击连段

    /// <summary>每步前先看主角此刻的段与帧号,满足条件的第一步按下技能键;返回按键那一步之前的累计毫秒。</summary>
    private static int RunUntilThenPress(RoomCombatSession s, Func<TownHero, bool> ready, int limitMs = 3000)
    {
        int t = 0;
        while (t < limitMs && !ready(s.Hero))
        {
            s.Update(10, 0, 0, false, false, SkillKeys.None);
            t += 10;
        }
        Assert.True(ready(s.Hero), "条件一直没满足");
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        return t;
    }

    [Fact]
    public void WithoutAnyKeyTheWindUpRunsIntoTheFirstStrikeAndTheSkillEndsAfterThatOneHit()
    {
        using var arena = NewArena(RepeatRecipes(), monsters: new[] { (340, 100) }, frozen: true, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 20);                                                // 290 毫秒:起手段
        Assert.Equal((0, 0, 0), (s.Hero.SkillStage, s.SkillChains, s.Hits));
        Run(s, 10);                                                         // 300 毫秒:起手播完,自己接上打击段(算一次接段,不算新的施放)
        Assert.Equal((1, 1, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
        Run(s, 200);                                                        // 打击段第 2 帧(200 到 300 毫秒)有框:打中一次,伤害取第 0 列(180%)
        Assert.Equal(1, s.Hits);
        double first = PlaceholderDamage.SkillBaseHit * 180 / 100.0;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(first), (long)Math.Floor(first) + 1);
        Run(s, 190);                                                        // 起手 300 + 打击 400 = 700 毫秒差 10:还在打击段
        Assert.Equal((HeroMotion.Skill, 1), (s.Hero.Motion, s.Hero.SkillStage));
        Run(s, 10);                                                         // 700 毫秒:没按键,打击段播完回站立
        Assert.Equal((HeroMotion.Rest, 1, 1, 1), (s.Hero.Motion, s.SkillChains, s.SkillCasts, s.Hits));
        Assert.Equal(480, s.Vitals.Mp);                                     // 魔法值只在起手扣了一次
    }

    [Fact]
    public void AKeyDuringTheWindUpDoesNothingAndAKeyBeforeTheChainFrameDoesNothingEither()
    {
        using var arena = NewArena(RepeatRecipes(), monsters: new[] { (340, 100) }, frozen: true);
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        // 起手段里(第 2 帧,已经过了接段帧 2)按键:起手段不接,仍等播完
        RunUntilThenPress(s, h => h.SkillStage == 0 && h.CurrentFrame == 2);
        Assert.Equal((0, 0, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
        Run(s, 20);
        Assert.Equal((0, 0), (s.Hero.SkillStage, s.SkillChains));
        // 打击段里第 1 帧(没到接段帧 2)按键:不接
        RunUntilThenPress(s, h => h.SkillStage == 1 && h.CurrentFrame == 1);
        Assert.Equal((1, 1, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
        Run(s, 10);
        Assert.Equal(1, s.Hero.SkillStage);
    }

    [Fact]
    public void AKeyInTheStrikeAfterTheChainFrameStartsTheNextHitsWindUpAndTheLastStrikeCannotChainFurther()
    {
        using var arena = NewArena(RepeatRecipes(), monsters: new[] { (340, 100) }, frozen: true, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        RunUntilThenPress(s, h => h.SkillStage == 1 && h.CurrentFrame == 2);          // 打击段第 2 帧(接段帧)按键:接下一击的起手
        Assert.Equal((2, 2, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));      // 第 2 击是最后一击:起手段(展开后的第 2 段)
        Assert.Equal(1, s.Hits);                                                         // 第一击打中了一次(第 2 帧的框)
        // 最后一击的起手 600 毫秒播完自己接最后的打击段;最后的打击段里再按键也没有下一段
        RunUntilThenPress(s, h => h.SkillStage == 3 && h.CurrentFrame == 2);
        Assert.Equal((3, 3, 1), (s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
        Run(s, 400);
        Assert.Equal((HeroMotion.Rest, 3, 1), (s.Hero.Motion, s.SkillChains, s.SkillCasts));
        Assert.Equal(480, s.Vitals.Mp);                                                  // 起手扣过一次,接段与自动接都不再扣
        // 第二击的打击取第 1 列(300%)与另一个攻击信息:最后一击的打击段(300 毫秒、第 1 帧有框)打中同一个怪物
        Assert.Equal(2, s.Hits);
        double expected = PlaceholderDamage.SkillBaseHit * 180 / 100.0 + PlaceholderDamage.SkillBaseHit * 300 / 100.0;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(expected), (long)Math.Floor(expected) + 2);
    }

    [Fact]
    public void TheStageTimelineOfATwoHitSkillIsWindUpStrikeWindUpStrikeWithEachWindUpRunningItsOwnMotionLength()
    {
        using var arena = NewArena(RepeatRecipes(), monsters: new[] { (1900, 100) }, frozen: true);
        RoomCombatSession s = arena.Session;
        // 起手 300 → 打击;打击第 2 帧(200 毫秒)后按键 → 第二击起手 600 → 最后的打击 300 → 回站立
        Run(s, 10, (0, SkillKeys.A));
        int atStrike = 10;
        while (s.Hero.SkillStage == 0)
        {
            Run(s, 10);
            atStrike += 10;
        }
        Assert.Equal(PoseMs, atStrike);                                     // 起手段正好播了 300 毫秒
        int pressed = atStrike + RunUntilThenPress(s, h => h.SkillStage == 1 && h.CurrentFrame == 2);
        Assert.Equal(atStrike + 200, pressed);                              // 打击段播到第 2 帧用了 200 毫秒(帧 100 毫秒一格)
        int lastWindUp = 10;
        while (s.Hero.SkillStage == 2)
        {
            Run(s, 10);
            lastWindUp += 10;
        }
        Assert.Equal(HeavyMs, lastWindUp);                                  // 最后一击的起手段播了 600 毫秒(按键那一步已经算进去了 10 毫秒)
        int lastStrike = 0;
        while (s.Hero.Motion == HeroMotion.Skill)
        {
            Run(s, 10);
            lastStrike += 10;
        }
        Assert.Equal(300, lastStrike);                                      // 最后的打击段 300 毫秒
        Assert.Equal((1, 3), (s.SkillCasts, s.SkillChains));                // 一次施放;接段三回:起手→打击(自己接)、打击→最后一击的起手(按键)、起手→最后的打击(自己接)
    }

    [Fact]
    public void ALockedPenultimateHitCannotChainByKeyButTheSameSkillWithoutTheLockCan()
    {
        // 锁的下标 1 读到 −400(非 0 即开):两击的技能里倒数第二击就是第 1 击,它的打击段不许按键接
        using var locked = NewArena(RepeatRecipes(lockIndex: 1), monsters: new[] { (340, 100) }, frozen: true);
        RoomCombatSession a = locked.Session;
        Run(a, 10, (0, SkillKeys.A));
        RunUntilThenPress(a, h => h.SkillStage == 1 && h.CurrentFrame == 2);
        Assert.Equal((1, 1), (a.Hero.SkillStage, a.SkillChains));            // 没接上,还在打击段
        Run(a, 400);
        Assert.Equal((HeroMotion.Rest, 1, 1), (a.Hero.Motion, a.SkillChains, a.SkillCasts));

        // 对照:锁的下标 9 超出 [static data] 读成 0(关):同样的按键接上了
        using var open = NewArena(RepeatRecipes(lockIndex: 9), monsters: new[] { (340, 100) }, frozen: true);
        RoomCombatSession b = open.Session;
        Run(b, 10, (0, SkillKeys.A));
        RunUntilThenPress(b, h => h.SkillStage == 1 && h.CurrentFrame == 2);
        Assert.Equal((2, 2), (b.Hero.SkillStage, b.SkillChains));
    }

    [Fact]
    public void ASkillWhoseHitCountReadsAsOneIsJustTheLastHitsWindUpAndStrike()
    {
        using var arena = NewArena(RepeatRecipes(countIndex: 9), monsters: new[] { (340, 100) }, frozen: true);       // 下标 9 读成 0:至少一击
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, HeavyMs - 20);                                               // 最后一击的起手 600 毫秒:590 毫秒还在起手
        Assert.Equal((0, 0), (s.Hero.SkillStage, s.SkillChains));
        Run(s, 10);                                                         // 600 毫秒:接上最后的打击段
        Assert.Equal((1, 1), (s.Hero.SkillStage, s.SkillChains));
        RunUntilThenPress(s, h => h.SkillStage == 1 && h.CurrentFrame == 1);   // 只有这一击:按键也没有下一段
        Run(s, 400);
        Assert.Equal((HeroMotion.Rest, 1, 1, 1), (s.Hero.Motion, s.SkillChains, s.SkillCasts, s.Hits));
    }

    // ------------------------------------------------------------------ 逐段不滑行

    [Fact]
    public void AStageMarkedAsNotSlidingStandsStillWhileTheStrikeAfterItSlidesByTheStaticSpeedAndAcceleration()
    {
        using var arena = NewArena(RepeatRecipes());
        RoomCombatSession s = arena.Session;
        double x0 = s.Hero.X;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 20);                                                // 起手段 290 毫秒:一动不动
        Assert.Equal((0, x0), (s.Hero.SkillStage, s.Hero.X));
        Run(s, 10);                                                         // 300 毫秒:进打击段,起滑速度 100 像素/秒、加速度 −100 像素/秒²,此刻还没滑
        Assert.Equal((1, x0), (s.Hero.SkillStage, s.Hero.X));
        Run(s, StrikeMs - 10);                                              // 打击段过了 0.39 秒:100×0.39 − ½×100×0.39² = 31.395 像素
        Assert.Equal(x0 + 31.395, s.Hero.X, 6);
        Run(s, 10);                                                         // 0.4 秒:32 像素,打击段播完回站立,滑行也结束
        Assert.Equal(x0 + 32.0, s.Hero.X, 6);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        Run(s, 100);
        Assert.Equal(x0 + 32.0, s.Hero.X, 6);
    }

    [Fact]
    public void WithoutTheNotSlidingMarkTheWindUpSlidesToo()
    {
        using var arena = NewArena(RepeatRecipes(windUpsSlide: true));
        RoomCombatSession s = arena.Session;
        double x0 = s.Hero.X;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, PoseMs - 10);                                                // 起手段滑满 0.3 秒:100×0.3 − ½×100×0.3² = 25.5 像素,同一步里接上打击段
        Assert.Equal(1, s.Hero.SkillStage);
        Assert.Equal(x0 + 25.5, s.Hero.X, 6);
    }

    [Fact]
    public void AStageMarkedAsNotSlidingCancelsTheSlideTheStageBeforeLeftRunning()
    {
        // 打击段(滑行 0.4 秒)过了接段帧(第 2 帧,0.2 秒起)就按键接下一击的起手:标了不滑行的起手段不继承打击段剩下的滑行
        using var arena = NewArena(RepeatRecipes());
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        RunUntilThenPress(s, h => h.SkillStage == 1 && h.CurrentFrame == 2);
        Assert.Equal(2, s.Hero.SkillStage);
        double atStart = s.Hero.X;                                          // 按键那一步:打击段滑到按键那一刻,起手段从这一步起不动
        Run(s, 200);
        Assert.Equal(atStart, s.Hero.X, 6);

        // 对照:起手段没标不滑行就带着起滑速度继续滑(接上那一步已经过了 0.01 秒,再过 0.2 秒:100×0.2 − ½×100×(0.21² − 0.01²) = 17.8 像素)
        using var slides = NewArena(RepeatRecipes(windUpsSlide: true));
        RoomCombatSession t = slides.Session;
        Run(t, 10, (0, SkillKeys.A));
        RunUntilThenPress(t, h => h.SkillStage == 1 && h.CurrentFrame == 2);
        Assert.Equal(2, t.Hero.SkillStage);
        double before = t.Hero.X;
        Run(t, 200);
        Assert.Equal(before + 17.8, t.Hero.X, 6);
    }

    // ------------------------------------------------------------------ 默认表里的状态族配方

    [Fact]
    public void TheDefaultTablesStateFamilyRecipesAreWellFormed()
    {
        int warps = 0;
        int repeats = 0;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.Repeat != null)
            {
                repeats++;
                // 四段模板:起手(自己接、不滑行、没有自己的命中)与打击(有自己的命中)交替,最后一击的一对用不同的动作
                Assert.Equal(SkillRepeatSpec.TemplateStages, r.Stages.Count);
                for (int i = 0; i < 4; i += 2)
                {
                    Assert.True(r.Stages[i].AutoAdvance && !r.Stages[i].Slides && !r.Stages[i].HasOwnStrike);
                    Assert.True(r.Stages[i + 1].HasOwnStrike && !r.Stages[i + 1].AutoAdvance);
                }
                Assert.NotEqual(r.Stages[0].MotionIndex, r.Stages[2].MotionIndex);
                Assert.True(r.SlideSpeedIndex >= 0 && !r.AutoChain && r.Repeat.CountStaticIndex >= 0);
            }
            for (int i = 0; i < r.Stages.Count; i++)
            {
                SkillStageSpec st = r.Stages[i];
                if (st.Warp != null)
                {
                    warps++;
                    // 瞬移不在第一段(检查要拿到瞬移前的位置),配方是自动接续,放在这一段的对象标成"放在出发点"
                    Assert.True(i >= 1 && r.AutoChain && st.Warp.DistanceStaticIndex >= 0);
                    Assert.Contains(r.Objects, o => o.FromWarpOrigin && o.Stage == i);
                }
            }
        }
        Assert.True(repeats >= 1 && warps >= 1);
    }
}
