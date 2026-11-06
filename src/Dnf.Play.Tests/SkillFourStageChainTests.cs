using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 四段自动接续类技能(M105,R7-he-07,客户端状态 0x2c 的模式 2):第 0 段是起手姿势(身体不打人),后三段各有自己的攻击信息与 [level info] 伤害列,
/// 每段动画播完那一步自己接下一段,最后一段播完回到站立;接段不算新的施放、不扣蓝。数据全是合成的(<see cref="AvatarFixture.ChainSkill"/>):
/// 动作 2 是 400 毫秒(第 2 帧,200 毫秒起有攻击框,攻击信息 1 不击倒)、动作 3 是 300 毫秒(第 1 帧,100 到 200 毫秒有攻击框,攻击信息 2 击倒),
/// 四段依次用动作 2、2、2、3(击倒的放在最后,怪物不会倒在前两段的框前);
/// [level info] 四列的 2 级是 (180, 300, 40, 60);第 1 段取第 0 列、第 2 段取第 1 列、第 3 段取第 2 列(百分比)与第 3 列(固定值)。
/// </summary>
public class SkillFourStageChainTests
{
    private static SkillRecipes Recipes(bool auto = true)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 2, damageFixedColumn: 3, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                autoChain: auto),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    private static RoomCombatTests.Arena NewArena(SkillRecipes recipes = null, HeroVitals vitals = null, (int X, int Y)[] monsters = null)
    {
        var arena = new RoomCombatTests.Arena(monsters ?? new[] { (340, 100) }, hp: 100000, frozen: true, chr: AvatarFixture.ActionChrPath, vitals: vitals,
            skillsFactory: data => Load(data, 2, recipes));
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

    private static (long Lo, long Hi) Range(double rate, double fixedDamage = 0)
    {
        double hit = PlaceholderDamage.SkillBaseHit * rate / 100.0 + fixedDamage;
        return ((long)Math.Floor(hit), (long)Math.Floor(hit) + 1);
    }

    [Fact]
    public void EachStageOfASlotReadsItsOwnStrikeAndItsOwnColumnsAndAPoseStageHasNone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkillSlot slot = Load(new GameData(new PvfScriptSource(assets.Pvf))).SlotOf(SkillKeys.A);

        Assert.Equal(4, slot.Recipe.Stages.Count);
        Assert.False(slot.StageHasOwnStrike(0));
        Assert.True(slot.StageHasOwnStrike(1) && slot.StageHasOwnStrike(2) && slot.StageHasOwnStrike(3));
        // 2 级 [level info] (180, 300, 40, 60):第 1 段第 0 列、第 2 段第 1 列、第 3 段第 2 列 + 固定值第 3 列
        Assert.Equal((180, 0), (slot.StageStrikes[1].SkillRatePercent, slot.StageStrikes[1].SkillFixed));
        Assert.Equal((300, 0), (slot.StageStrikes[2].SkillRatePercent, slot.StageStrikes[2].SkillFixed));
        Assert.Equal((40, 60), (slot.StageStrikes[3].SkillRatePercent, slot.StageStrikes[3].SkillFixed));
        // 第 1、2 段用同一个攻击信息文件(击退 120),第 3 段另一个(击退 200)
        Assert.Equal(AvatarFixture.ChainAPushAside, slot.StageStrikes[1].PushAside);
        Assert.Equal(AvatarFixture.ChainAPushAside, slot.StageStrikes[2].PushAside);
        Assert.Equal(AvatarFixture.ChainBPushAside, slot.StageStrikes[3].PushAside);
        // 特效:第 0、1 段各一个(预载表第 0、1 个),后两段没有
        Assert.Equal(new[] { 0 }, slot.Recipe.Stages[0].EffectIndices.ToArray());
        Assert.Equal(new[] { 1 }, slot.Recipe.Stages[1].EffectIndices.ToArray());
        Assert.Empty(slot.Recipe.Stages[2].EffectIndices);
    }

    [Fact]
    public void InTheRoomTheFourStagesFollowOneAnotherByThemselvesAndEachHitUsesItsOwnColumn()
    {
        // 主角 (300, 100) 朝右;怪物在 (340, 100),血很多:四段里三个有命中的段各打一次
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((1, 0, 0), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage));
        int mpAfterCast = s.Vitals.Mp;
        Run(s, 380);                                                 // 390 毫秒:第 0 段(姿势,400 毫秒)的动画第 2 帧有攻击框,但这一段身体不打人
        Assert.Equal((0, 0, 0, 0L), (s.Hits, s.SkillChains, s.Hero.SkillStage, s.TotalDamage));

        Run(s, 10);                                                  // 400 毫秒:接第 1 段(动作 2)
        Assert.Equal((1, 1, 1, "chaina.ani"), (s.SkillChains, s.Hero.SkillStage, s.SkillCasts, s.Hero.SkillMotionName));
        Run(s, 310);                                                 // 710 毫秒:第 1 段第 2 帧(200 到 300 毫秒)的框打中一次,伤害取第 0 列
        Assert.Equal(1, s.Hits);
        (long lo, long hi) = Range(180);
        Assert.InRange(s.TotalDamage, lo, hi);

        long before = s.TotalDamage;
        Run(s, 90);                                                  // 800 毫秒:第 1 段播完,接第 2 段(动作 2)
        Assert.Equal((2, 2, "chaina.ani"), (s.SkillChains, s.Hero.SkillStage, s.Hero.SkillMotionName));
        Run(s, 310);                                                 // 1110 毫秒:第 2 段的框打中一次,伤害取第 1 列
        Assert.Equal(2, s.Hits);
        (lo, hi) = Range(300);
        Assert.InRange(s.TotalDamage - before, lo, hi);

        before = s.TotalDamage;
        Run(s, 90);                                                  // 1200 毫秒:接第 3 段(动作 3)
        Assert.Equal((3, 3, "chainb.ani"), (s.SkillChains, s.Hero.SkillStage, s.Hero.SkillMotionName));
        Run(s, 210);                                                 // 1410 毫秒:第 3 段第 1 帧(100 到 200 毫秒)的框打中一次,伤害取第 2 列百分比加第 3 列固定值
        Assert.Equal(3, s.Hits);
        (lo, hi) = Range(40, 60);
        Assert.InRange(s.TotalDamage - before, lo, hi);

        Run(s, 100);                                                 // 1510 毫秒:第 3 段播完,回到站立;没有第 4 个接段,魔法值只在起手扣过
        Assert.Equal((HeroMotion.Rest, 0, 3, 1, 3), (s.Hero.Motion, s.Hero.SkillStage, s.SkillChains, s.SkillCasts, s.Hits));
        Assert.Equal(mpAfterCast, s.Vitals.Mp);
    }

    [Fact]
    public void WithoutAutoChainTheFirstStageEndsTheSkillAndNothingIsChained()
    {
        using var arena = NewArena(Recipes(auto: false), monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;

        Run(s, 10, (0, SkillKeys.A));
        Run(s, 500);                                                 // 510 毫秒:第 0 段 400 毫秒播完,没有自动接续:技能结束,回到站立
        Assert.Equal((HeroMotion.Rest, 0, 0, 1), (s.Hero.Motion, s.Hero.SkillStage, s.SkillChains, s.SkillCasts));
    }

    [Fact]
    public void AHitDuringTheMiddleOfTheChainStopsTheRestOfTheStages()
    {
        using var arena = NewArena(monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;

        Run(s, 10, (0, SkillKeys.A));
        Run(s, 400);                                                 // 410 毫秒:已在第 1 段
        Assert.Equal(1, s.Hero.SkillStage);
        Assert.True(s.Hero.BeginHurt(100, 0, 0, true));              // 第 1 段播到一半被打:技能中断,后面的段不再接
        Run(s, 1500);
        Assert.Equal((1, HeroMotion.Rest), (s.SkillChains, s.Hero.Motion));
    }
}
