using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 升空出招(M65,证据 E-163,客户端状态 0x40 的第二个模式):姿势播完自动接挥击段;挥击段动画第 0 帧(带攻击框)停住,
/// 主角按技能 [static data] 的三个数(升空高度、升空毫秒、后滑像素)脚本升空,升空时间一到动画接着播完剩下的帧,技能结束主角从当时的高度落下来。
/// 命中只在挥击段开头判一次(默认武器类别)。数据全是合成的(见 <see cref="AvatarFixture.RiseSkill"/>):姿势借 <see cref="AvatarFixture.ChainAAni"/>(400 毫秒),
/// 挥击是 <see cref="AvatarFixture.RiseAni"/>(第 0 帧占位停 10000 毫秒,第 1、2 帧 30 与 40 毫秒);升空参数 (高度 40、升空 300 毫秒、后滑 30)。
/// </summary>
public class SkillRiseStrikeTests
{
    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.RiseSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: AvatarFixture.RiseEtcIndex, attackInfoIndex: 2, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        rise: new SkillRiseSpec(heightStaticIndex: 0, durationStaticIndex: 1, backStaticIndex: 2)),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                autoChain: true),
        });
    }

    private static HeroSkills Load(GameData data)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.RiseSkill, 1));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.RiseSkill) }, 0, 0);       // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, Recipes());
    }

    /// <summary>
    /// <paramref name="monsterNear"/> 为真:怪物在主角前方、主角可走区域只有脚下一小块(怪物与主角都原地不动);
    /// 为假:主角有一大片可走区域(后滑与落下才走得动),怪物远在几千像素以外(测试时间内走不到)。
    /// </summary>
    private static RoomCombatTests.Arena NewArena(bool monsterNear = true)
    {
        var arena = new RoomCombatTests.Arena(new[] { monsterNear ? (340, 100) : (3000, 100) }, hp: 100000, frozen: monsterNear, chr: AvatarFixture.ActionChrPath, vitals: null,
            walkArea: monsterNear ? null : new List<WorldRect> { new WorldRect(0, 0, 4000, 400) }, skillsFactory: Load);
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

    // ------------------------------------------------------------------ 配方

    [Fact]
    public void TheDefaultTableMarksOnlyTheStrikeStageOfThePoseThenStrikeSkillAsARise()
    {
        SkillRecipe found = null;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.Stages.Count == 2 && r.Stages[0].MotionIndex == 112 && r.Stages[1].MotionIndex == 113)
            {
                Assert.Null(found);
                found = r;
            }
        }
        Assert.NotNull(found);
        Assert.Null(found.Stages[0].Rise);
        SkillRiseSpec rise = found.Stages[1].Rise;
        Assert.NotNull(rise);
        Assert.Equal((0, 1, 2), (rise.HeightStaticIndex, rise.DurationStaticIndex, rise.BackStaticIndex));       // 升空高度、升空毫秒、后滑像素在 [static data] 里的位置
        foreach (SkillStageSpec st in found.Stages)
        {
            Assert.Empty(st.FlagEvents);
        }
    }

    [Fact]
    public void TheDefaultTableGivesTheTwoBodyHitSkillsTheirFlagEventsAndNothingElseHasAny()
    {
        int withEvents = 0;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            foreach (SkillStageSpec st in r.Stages)
            {
                if (st.FlagEvents.Count == 0)
                {
                    continue;
                }
                withEvents++;
                Assert.Single(r.Stages);                                            // 都是单段技能
                Assert.True(st.HasOwnStrike);
                Assert.Equal(st.FlagEvents.Count, st.FlagEvents.Select(e => e.Flag).Distinct().Count());   // 一个标志一个事件
                Assert.True(st.FlagEvents.Count == 1 || st.FlagEvents.Count == 2);
                Assert.Contains(st.FlagEvents, e => e.Flag == 1);
                foreach (SkillFlagEvent e in st.FlagEvents)
                {
                    Assert.True(e.Flag > 0);
                    Assert.True(!e.ChangesStrike || e.DamageRateColumn >= 0);       // 换命中的事件都换了伤害列
                    Assert.Equal(-1, e.DamageFixedColumn);                          // 这两个技能没有固定值列
                }
            }
        }
        Assert.Equal(2, withEvents);                                                // 默认表里只有这两个技能带关键帧标志事件

        // 两个技能各自的事件表(按动作项找,不写技能号):动作 120 只有标志 1(清名单并改取伤害第 1 列),动作 121 的标志 1 只清名单、标志 2 改取攻击信息第 86 项与伤害第 1 列
        SkillFlagEvent[] first = EventsOfMotion(120);
        Assert.Equal(new[] { 1 }, first.Select(e => e.Flag).ToArray());
        Assert.True(first[0].ChangesStrike);
        Assert.Equal((-1, 1), (first[0].AttackInfoIndex, first[0].DamageRateColumn));
        SkillFlagEvent[] second = EventsOfMotion(121);
        Assert.Equal(new[] { 1, 2 }, second.Select(e => e.Flag).ToArray());
        Assert.False(second[0].ChangesStrike);
        Assert.True(second[1].ChangesStrike);
        Assert.Equal((86, 1), (second[1].AttackInfoIndex, second[1].DamageRateColumn));
    }

    private static SkillFlagEvent[] EventsOfMotion(int motionIndex)
    {
        SkillStageSpec[] found = SkillRecipes.Default.All.SelectMany(r => r.Stages).Where(st => st.MotionIndex == motionIndex).ToArray();
        Assert.Single(found);
        return found[0].FlagEvents.ToArray();
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void TheStrikeStageHoldsFrameZeroRisesAndSlidesBackThenTheHeroFallsAndLands()
    {
        using var arena = NewArena(monsterNear: false);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double startX = hero.X;

        Run(s, 10, (0, SkillKeys.A));
        Run(s, 380);                                                          // 390 毫秒:姿势段
        Assert.Equal((0, 0.0, startX), (hero.SkillStage, hero.Z, hero.X));    // 姿势段不动
        Run(s, 10);                                                           // 400 毫秒:接上挥击段(升空计划从这一刻起算)
        Assert.Equal((1, 1, true), (hero.SkillStage, s.SkillChains, hero.Rising));

        Run(s, 150);                                                          // 升空 150 毫秒
        Assert.Equal(0, hero.CurrentFrame);                                   // 第 0 帧一直停着
        Assert.Equal(SkillHopPlan.EasedLerp(0, AvatarFixture.RiseHeight, 150, AvatarFixture.RiseMs, true), (int)hero.Z);
        Assert.Equal(startX - SkillHopPlan.IntLerp(0, AvatarFixture.RiseBack, 150, AvatarFixture.RiseMs), hero.X);
        Assert.True(hero.Z > 0 && hero.Z < AvatarFixture.RiseHeight);

        Run(s, 150);                                                          // 升空 300 毫秒:最高点、后滑到位,动画拨到第 1 帧
        Assert.Equal((false, 1, (double)AvatarFixture.RiseHeight), (hero.Rising, hero.CurrentFrame, hero.Z));
        Assert.Equal(startX - AvatarFixture.RiseBack, hero.X);

        Run(s, 70);                                                           // 第 1、2 帧(30 + 40 毫秒)播完:技能结束,从最高点落下来
        Assert.Equal((HeroMotion.Jump, true), (hero.Motion, hero.IsAirborne));
        Assert.InRange(hero.Z, 1.0, AvatarFixture.RiseHeight);

        Run(s, 1500);                                                         // 落地、落地段播完:站立
        Assert.Equal((HeroMotion.Rest, 0.0, startX - AvatarFixture.RiseBack), (hero.Motion, hero.Z, hero.X));
        Assert.Equal((1, 1), (s.SkillCasts, s.SkillChains));                  // 接段不是新的施放
    }

    [Fact]
    public void TheStrikeHitsOnceAtTheStartOfTheStrikeStageAndNotWhileRising()
    {
        using var arena = NewArena();
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 380);                                                          // 姿势段:动画第 2 帧有框,但姿势段不打人
        Assert.Equal(0, s.Hits);
        Run(s, 20);                                                           // 接上挥击段:第 0 帧的框一开始就判,打中一次
        Assert.Equal(1, s.Hits);
        Run(s, 600);                                                          // 升空、播完、落下:再没有命中
        Assert.Equal(1, s.Hits);
        Assert.Equal(0, s.KeyFlagEvents);                                     // 升空段的动画里没有标志
    }

    [Fact]
    public void AFasterAttackSpeedShortensTheAnimationButNotTheRiseTime()
    {
        using var arena = NewArena(monsterNear: false);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        hero.AttackAnimationRate = 2.0;                                       // 动画快一倍:姿势 400 毫秒播成 200 毫秒
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 200);                                                          // 210 毫秒:姿势播完,已接挥击段
        Assert.Equal((1, true), (hero.SkillStage, hero.Rising));

        Run(s, 280);                                                          // 升空时间是墙钟毫秒,不随动画变速:还没到
        Assert.True(hero.Rising);
        Run(s, 40);                                                           // 升空 300 毫秒过了(第 0 帧停到升空时间结束)
        Assert.False(hero.Rising);
        Assert.Equal((double)AvatarFixture.RiseHeight, hero.Z);
    }

    [Fact]
    public void TheCooldownIsTheSkillCoolTimeFromTheCastAndTheHeroCanCastAgainAfterLanding()
    {
        using var arena = NewArena(monsterNear: false);
        RoomCombatSession s = arena.Session;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);
        Run(s, 10, (0, SkillKeys.A));
        Assert.InRange(slot.CooldownLeftMs, 2900.0, 3000.0);

        Run(s, 2000);                                                         // 整个技能(含落地)早已结束,冷却还没过
        Assert.Equal((HeroMotion.Rest, 1), (s.Hero.Motion, s.SkillCasts));
        Run(s, 20, (0, SkillKeys.A));
        Assert.Equal(1, s.SkillCasts);

        Run(s, 1000);
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((2, HeroMotion.Skill), (s.SkillCasts, s.Hero.Motion));
    }
}
