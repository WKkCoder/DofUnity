using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 默认配方表里"单段身体命中、伤害取百分比第 1 列"的那一项(客户端状态 0x15,与 E-162、E-163 的单段身体命中同一族;读码见 <c>SkillRecipes.Default</c> 里该项的注释):
/// 进入块取 [etc motion] 第 2 项当动作、[etc attack info] 第 1 项当攻击信息、[level info] 的百分比第 1 列当伤害(没有固定值列),不滑行、没有特效与被动对象,
/// 逐帧只看动画结束标志——一亮就回站立,所以只有一段、没有接段帧。
/// 配方里的三个下标是客户端状态码里写死的常量;合成数据(<see cref="AvatarFixture.ChainSkill"/>)正好把这三处做成能分辨的值:
/// 动作第 2 项是 400 毫秒的 A 段(第 3 项是 300 毫秒的 B 段)、攻击信息第 1 项是不击倒、击退 120 的 A 段(第 2 项是击倒、击退 200 的 B 段)、
/// [level info] 四列 (第 1 级 150、250、30、50,第 2 级 180、300、40、60) 里第 0 列与第 1 列不同;[static data] 第 0 个数是 200(要是配方滑行就会滑出去);
/// 预载特效两个(要是配方挂特效就会出现)。
/// 本文件把默认表里那一项按"形状"找出来(每种形状表里只有一份,不写死技能编号),换到合成技能的编号上跑;断言的值全是按合成数据手算的。
/// </summary>
public class PlainBodyStrikeRecipeTests
{
    private const int HeroX = 300;
    private const int Level1 = 1;
    private const int Level2 = 2;

    // ------------------------------------------------------------------ 找表里的那一项、造对照配方

    /// <summary>单段、身体打人、不带别的机制(对象、蓄力跳跃、俯冲、空中攻击强化、自动接续、多段命中)的配方;滑行与特效故意不查,留给行为断言。</summary>
    private static bool IsPlainBodyStrike(SkillRecipe r)
    {
        return r.Stages.Count == 1 && r.Stages[0].HasOwnStrike && r.Objects.Count == 0 && r.Hop == null && r.Dive == null && r.JumpAttack == null
            && !r.AutoChain && r.MultiHitPassiveSkillId == 0;
    }

    private static SkillRecipe FindOnly(Func<SkillStageSpec, bool> shape)
    {
        SkillRecipe found = null;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (IsPlainBodyStrike(r) && shape(r.Stages[0]))
            {
                Assert.Null(found);                                      // 每种形状在表里只有一份
                found = r;
            }
        }
        Assert.NotNull(found);
        return found;
    }

    /// <summary>按攻击信息项找(行为断言里再查动作与伤害列)。</summary>
    private static SkillRecipe ByAttackInfo()
    {
        return FindOnly(st => st.AttackInfoIndex == 1);
    }

    /// <summary>按动作项找(行为断言里再查攻击信息与伤害列)。</summary>
    private static SkillRecipe ByMotion()
    {
        return FindOnly(st => st.MotionIndex == 2);
    }

    /// <summary>按伤害百分比列找(行为断言里再查动作与攻击信息)。</summary>
    private static SkillRecipe ByRateColumn()
    {
        return FindOnly(st => st.DamageRateColumn == 1);
    }

    /// <summary>把配方原样搬到合成技能的编号上(其余字段照抄);<paramref name="stage"/> 给了就换掉那一段(对照实验用)。</summary>
    private static SkillRecipe OntoChainSkill(SkillRecipe r, SkillStageSpec stage = null, int? slideSpeedIndex = null)
    {
        return new SkillRecipe(AvatarFixture.ChainSkill, new[] { stage ?? r.Stages[0] }, slideSpeedIndex ?? r.SlideSpeedIndex, r.SlideAccelIndex,
            r.ForwardSpeedIndex, r.ForwardAccelIndex, r.ChainFromFrame, r.SlideFollowsHeldDirection, r.Objects, r.Hop, r.ForwardReplacesBase,
            r.MultiHitPassiveSkillId, r.MultiHitCountIndex, r.Dive, r.JumpAttack, r.AutoChain, r.PassivePowerLinks);
    }

    private static SkillRecipes Table(SkillRecipe recipe)
    {
        return new SkillRecipes(new[] { recipe });
    }

    // ------------------------------------------------------------------ 合成世界

    private static HeroSkills Load(GameData data, int level, SkillRecipes recipes)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    /// <summary>怪物放在主角身前 40 像素(第 2 帧的身体攻击框够得着);<paramref name="frozen"/> 为假时主角可以自由走动(测滑行用,怪物放远)。</summary>
    private static RoomCombatTests.Arena NewArena(SkillRecipe recipe, int level = Level2, bool frozen = true, int monsterX = HeroX + 40)
    {
        // 表里的配方带的是它自己的技能编号;合成世界里只有一个技能,所以先搬到合成技能的编号上
        SkillRecipe onto = recipe.SkillId == AvatarFixture.ChainSkill ? recipe : OntoChainSkill(recipe);
        var arena = new RoomCombatTests.Arena(new[] { (monsterX, 100) }, hp: 1000, frozen: frozen, chr: AvatarFixture.ActionChrPath,
            vitals: new HeroVitals(1, 1000, 500, 0, 0, 0), skillsFactory: data => Load(data, level, Table(onto)));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    private static void Run(RoomCombatSession s, int ms, int dx = 0, params (int At, SkillKeys Keys)[] presses)
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
            s.Update(10, dx, 0, false, false, keys);
        }
    }

    /// <summary>按一下 A 键放技能,一路播到动作结束之后,返回这一次放出来打出的总伤害。</summary>
    private static long DamageOfOneCast(SkillRecipe recipe, int level)
    {
        using var arena = NewArena(recipe, level);
        Run(arena.Session, 600, 0, (0, SkillKeys.A));
        Assert.Equal(1, arena.Session.Hits);                          // 只打中一下
        return arena.Session.TotalDamage;
    }

    /// <summary>技能一击的占位伤害区间:31.15 × 百分比 / 100,小数部分按概率进位,所以是 [向下取整, 向下取整 + 1]。</summary>
    private static (long Low, long High) Expected(int ratePercent)
    {
        long low = (long)Math.Floor(PlaceholderDamage.SkillBaseHit * ratePercent / 100.0);
        return (low, low + 1);
    }

    // ------------------------------------------------------------------ 伤害:百分比取第 1 列、没有固定值列

    [Theory]
    [InlineData(Level1, 250)]                     // 第 1 级:第 1 列是 250(第 0 列是 150)
    [InlineData(Level2, 300)]                     // 第 2 级:第 1 列是 300(第 0 列是 180)
    public void TheHitDamageComesFromTheSecondPercentColumnAtEveryLevel(int level, int column1)
    {
        long actual = DamageOfOneCast(ByAttackInfo(), level);
        (long low, long high) = Expected(column1);
        Assert.InRange(actual, low, high);

        // 对照:同一份配方改成取第 0 列,打出来的是另一个区间(断言不是空转)
        SkillRecipe real = ByAttackInfo();
        SkillStageSpec st = real.Stages[0];
        var column0 = new SkillStageSpec(st.MotionIndex, st.AttackInfoIndex, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
        long other = DamageOfOneCast(OntoChainSkill(real, column0), level);
        int column0Value = level == Level1 ? 150 : 180;
        (long otherLow, long otherHigh) = Expected(column0Value);
        Assert.InRange(other, otherLow, otherHigh);
        Assert.True(otherHigh < low, "第 0 列的区间要落在第 1 列的区间下面,否则对照没有分辨力");
    }

    [Fact]
    public void TheStrikeInfoHasTheRateFromColumnOneAndNoFixedPartAndTheFirstEtcAttackInfo()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        foreach (SkillRecipe found in new[] { ByAttackInfo(), ByMotion(), ByRateColumn() })       // 三种找法找到的是同一份
        {
            HeroSkillSlot slot = Load(data, Level2, Table(OntoChainSkill(found))).SlotOf(SkillKeys.A);
            HeroStrikeInfo body = Assert.Single(slot.StageStrikes);
            // 攻击信息第 1 项:不击倒、击退 120(第 2 项是击倒、击退 200);百分比第 1 列 300(第 0 列是 180);没有固定值(第 0 列固定值是 180、第 2 列是 40)
            Assert.Equal((false, AvatarFixture.ChainAPushAside, 300, 0), (body.KnockDown, body.PushAside, body.SkillRatePercent, body.SkillFixed));
            Assert.NotEqual(AvatarFixture.ChainBPushAside, body.PushAside);
            Assert.True(slot.StageHasOwnStrike(0));
            Assert.Empty(slot.ObjectStrikes);
        }
        Assert.Same(ByMotion(), ByAttackInfo());
        Assert.Same(ByMotion(), ByRateColumn());
    }

    // ------------------------------------------------------------------ 动作:取 [etc motion] 第 2 项,播完回站立

    [Fact]
    public void TheCastPlaysTheSecondEtcMotionForFourHundredMillisecondsAndThenStands()
    {
        using var arena = NewArena(ByAttackInfo());
        RoomCombatSession s = arena.Session;
        Run(s, 10, 0, (0, SkillKeys.A));
        Assert.Equal((1, HeroMotion.Skill), (s.SkillCasts, s.Hero.Motion));
        Run(s, 300);                                                  // 310 毫秒:第 3 项动作(300 毫秒)这时已经播完了,第 2 项还在播
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        Run(s, 80);                                                   // 390 毫秒:还差 10 毫秒
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        Run(s, 20);                                                   // 410 毫秒:播完
        Assert.Equal((HeroMotion.Rest, 0, 0), (s.Hero.Motion, s.Hero.SkillStage, s.SkillChains));
    }

    [Fact]
    public void TheBodyBoxOfTheSecondFrameHitsOnlyAfterTwoHundredMillisecondsAndOnlyOnce()
    {
        using var arena = NewArena(ByMotion());
        RoomCombatSession s = arena.Session;
        Run(s, 10, 0, (0, SkillKeys.A));
        Run(s, 180);                                                  // 190 毫秒:攻击框还没到(第 2 帧 200 毫秒起)
        Assert.Equal(0, s.Hits);
        Run(s, 30);                                                   // 220 毫秒:打中一下
        Assert.Equal(1, s.Hits);
        Run(s, 400);                                                  // 一次挥击只打一下
        Assert.Equal(1, s.Hits);
    }

    // ------------------------------------------------------------------ 不滑行、没有特效与对象、没有第二段

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void HeldDirectionKeysNeitherSlideNorSteerTheCastAndTheHeroWalksAgainRightAfter(int dx)
    {
        // 不冻结:主角可以自由走动,怪物放得很远;滑行要是被配方打开,[static data] 第 0 个数 200 就会让主角滑出去
        using var arena = NewArena(ByAttackInfo(), frozen: false, monsterX: 1500);
        RoomCombatSession s = arena.Session;
        double startX = s.Hero.X;
        Run(s, 400, dx, (0, SkillKeys.A));
        Assert.Equal(startX, s.Hero.X);                               // 整段施法里一直按着方向键,位置一点没动
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);

        Run(s, 100, dx);                                              // 边界:动作一播完方向键就生效(不是被锁住了)
        Assert.True(Math.Abs(s.Hero.X - startX) > 5.0, "动作结束后按着方向键应当走起来");
        Assert.Equal(dx < 0, s.Hero.X < startX);

        // 对照:同一份配方把滑行速度改成取 [static data] 第 0 个数,主角就滑出去了
        using var slid = NewArena(OntoChainSkill(ByAttackInfo(), slideSpeedIndex: 0), frozen: false, monsterX: 1500);
        Run(slid.Session, 400, 0, (0, SkillKeys.A));
        Assert.True(Math.Abs(slid.Session.Hero.X - startX) > 5.0, "对照配方应当滑行,否则这条断言分辨不出有没有滑行");
    }

    [Fact]
    public void TheCastShowsNoEffectAndSpawnsNoObjectAndAKeyPressedAgainChainsNothing()
    {
        using var arena = NewArena(ByAttackInfo());
        RoomCombatSession s = arena.Session;
        // 逐 10 毫秒看:特效一出现就会在列表里(播完就清掉),只在 400 毫秒后看一次会漏掉;250 毫秒再按一次:只有一段,没有可接的下一段(也不是新的施放:冷却中)
        Run(s, 10, 0, (0, SkillKeys.A));
        Assert.Empty(s.Effects);
        for (int t = 10; t < 400; t += 10)
        {
            if (t == 250)
            {
                Run(s, 10, 0, (0, SkillKeys.A));
            }
            else
            {
                Run(s, 10);
            }
            Assert.Empty(s.Effects);
        }
        Assert.Equal((0, 0, 1), (s.HeroObjectsSpawned, s.SkillChains, s.SkillCasts));
        Assert.Equal(480, s.Vitals.Mp);                               // 耗蓝 20 只扣一次

        // 对照:配方挂上特效下标 0,第一个预载特效一进状态就出现
        SkillRecipe real = ByAttackInfo();
        SkillStageSpec st = real.Stages[0];
        var withEffect = new SkillStageSpec(st.MotionIndex, st.AttackInfoIndex, st.DamageRateColumn, st.DamageFixedColumn, effectIndex: 0, effectFrame: 0);
        using var shown = NewArena(OntoChainSkill(real, withEffect), monsterX: 900);
        Run(shown.Session, 10, 0, (0, SkillKeys.A));
        Assert.Contains(shown.Session.Effects, e => e.Path == AvatarFixture.ChainEffect1);
    }

    [Fact]
    public void ThePlainBodyStrikeIsCastWithTheDataCostAndCooldownAndTheTableMarksItImplemented()
    {
        SkillRecipe real = ByAttackInfo();
        Assert.True(HeroSkills.IsImplemented(real.SkillId));
        Assert.NotNull(SkillRecipes.Default.Find(real.SkillId));

        using var arena = NewArena(real);
        RoomCombatSession s = arena.Session;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);
        Run(s, 10, 0, (0, SkillKeys.A));
        Assert.Equal(480, s.Vitals.Mp);                               // 耗蓝取技能数据的 20
        Run(s, 600);
        Assert.InRange(slot.CooldownLeftMs, 2300.0, 3000.0);          // 冷却 3000 毫秒从起手算起
        Run(s, 10, 0, (0, SkillKeys.A));
        Assert.Equal(1, s.SkillCasts);                                // 冷却里再按不是新的施放
    }
}
