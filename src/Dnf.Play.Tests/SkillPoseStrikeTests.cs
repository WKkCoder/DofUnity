using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 姿势→挥击类技能(M65,证据 E-163,客户端状态 0x40)与 [cooltime level info] 冷却列:进入块按参数分两个模式,模式 0 播姿势动作(不设攻击信息,身体不打人),
/// 姿势动画播完那一刻自己接模式 1(挥击:另一个动作、攻击信息、[level info] 百分比第 0 列),接段不扣蓝、不进冷却;冷却不走 [cool time],取 [level info] 的一列。
/// 数据全是合成的(见 <see cref="AvatarFixture.PoseStrikeSkill"/>):姿势借 <see cref="AvatarFixture.ChainAAni"/>(400 毫秒,第 2 帧 200 毫秒起有攻击框,
/// 用来检查姿势段身体不打人),挥击借 <see cref="AvatarFixture.ChainBAni"/>(300 毫秒,第 1 帧 100 到 200 毫秒有攻击框);
/// [level info] 的 2 级是 (190, 2400):第 0 列百分比、第 1 列冷却毫秒;耗蓝 25。
/// </summary>
public class SkillPoseStrikeTests
{
    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.PoseStrikeSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                autoChain: true),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.PoseStrikeSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.PoseStrikeSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    private static RoomCombatTests.Arena NewArena(HeroVitals vitals = null, (int X, int Y)[] monsters = null)
    {
        var arena = new RoomCombatTests.Arena(monsters ?? new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, vitals: vitals,
            skillsFactory: data => Load(data, 2));
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

    // ------------------------------------------------------------------ 冷却列

    [Fact]
    public void ASkillWithACooltimeLevelInfoTagTakesItsCooldownFromThatLevelInfoColumnAndOthersKeepCoolTime()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        SkillDef def = data.GetSkill(0, AvatarFixture.PoseStrikeSkill);
        Assert.Equal(1, def.CoolTimeColumn);
        Assert.Empty(def.CoolTime);                                  // 带这个标签的技能没有 [cool time]
        Assert.Equal(AvatarFixture.PoseStrikeCool2, def.GetLevel(2, 1, false));

        // 槽的冷却跟着等级走:1 级、2 级各取那一级的第 1 列
        Assert.Equal(AvatarFixture.PoseStrikeCool1, Load(data, 1).SlotOf(SkillKeys.A).CooldownMs);
        Assert.Equal(AvatarFixture.PoseStrikeCool2, Load(data, 2).SlotOf(SkillKeys.A).CooldownMs);

        // 对照:没有这个标签的技能下标是 -1,冷却仍是 [cool time] 的插值
        SkillDef plain = data.GetSkill(0, AvatarFixture.ChainSkill);
        Assert.Equal(-1, plain.CoolTimeColumn);
        Assert.NotEmpty(plain.CoolTime);
    }

    // ------------------------------------------------------------------ 默认表

    [Fact]
    public void TheDefaultTableHasThePoseThenStrikeRecipeWithTheEngineConstants()
    {
        // 按动作项(客户端状态码里写死的 [etc motion] 下标)找配方:姿势动作第 112 项、挥击动作第 113 项,只有一份
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
        Assert.True(HeroSkills.IsImplemented(found.SkillId));
        Assert.True(found.AutoChain);

        // 姿势段身体不打人、没有伤害列与特效;挥击段取攻击信息第 78 项、[level info] 百分比第 0 列(没有固定值列)
        SkillStageSpec pose = found.Stages[0];
        SkillStageSpec strike = found.Stages[1];
        Assert.False(pose.HasOwnStrike);
        Assert.Equal((-1, -1, -1), (pose.AttackInfoIndex, pose.DamageRateColumn, pose.DamageFixedColumn));
        Assert.Empty(pose.EffectIndices);
        Assert.True(strike.HasOwnStrike);
        Assert.Equal((78, 0, -1), (strike.AttackInfoIndex, strike.DamageRateColumn, strike.DamageFixedColumn));
        Assert.Empty(strike.EffectIndices);
        foreach (SkillStageSpec st in found.Stages)
        {
            Assert.Equal((0, -1, 0, 0), (st.EffectFrame, st.EndFrame, st.CastHoldFrames, st.RequiresSkill));
        }

        // 不滑行、没有对象、不是跳跃/俯冲/空中攻击强化,没有多段命中;按键不接段所以没有接段帧
        Assert.True(found.SlideSpeedIndex < 0 && found.SlideAccelIndex < 0 && found.ForwardSpeedIndex < 0 && found.ForwardAccelIndex < 0 && !found.SlideFollowsHeldDirection);
        Assert.Empty(found.Objects);
        Assert.Null(found.Hop);
        Assert.Null(found.Dive);
        Assert.Null(found.JumpAttack);
        Assert.Equal(0, found.MultiHitPassiveSkillId);
        Assert.Equal(int.MaxValue, found.ChainFromFrame);
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void ASlotReadsNoStrikeForThePoseStageAndThePercentColumnForTheStrikeStage()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkillSlot slot = Load(new GameData(new PvfScriptSource(assets.Pvf))).SlotOf(SkillKeys.A);

        Assert.False(slot.StageHasOwnStrike(0));
        Assert.True(slot.StageHasOwnStrike(1));
        HeroStrikeInfo strike = slot.StageStrikes[1];
        Assert.Equal((true, AvatarFixture.PoseStrikeRate2, 0), (strike.IsSkillDamage, strike.SkillRatePercent, strike.SkillFixed));      // 第 0 列的百分比、没有固定值
        Assert.Equal(AvatarFixture.PoseStrikeMp, slot.ConsumeMp);
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void InTheRoomThePoseDoesNotHitThenTheStrikeStartsByItselfAndHitsOnce()
    {
        // 主角 (300, 100) 朝右;怪物在 (340, 100):两个动画的身体攻击框都够得着它
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((1, HeroMotion.Skill, 500 - AvatarFixture.PoseStrikeMp), (s.SkillCasts, s.Hero.Motion, s.Vitals.Mp));
        Assert.Equal((0, "chaina.ani"), (s.Hero.SkillStage, s.Hero.SkillMotionName));
        Run(s, 380);                                                 // 390 毫秒:姿势动画第 2 帧(200 毫秒起)有攻击框,但姿势段没有自己的命中:不打人,也还没接挥击
        Assert.Equal((0, 0, 0), (s.Hits, s.SkillChains, s.Hero.SkillStage));
        Assert.Equal(0, s.TotalDamage);

        Run(s, 10);                                                  // 400 毫秒:姿势播完,同一步里接上挥击(接段不算新的施放、不再扣蓝)
        Assert.Equal((1, 1, 1, "chainb.ani"), (s.SkillChains, s.Hero.SkillStage, s.SkillCasts, s.Hero.SkillMotionName));
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        Assert.Equal(500 - AvatarFixture.PoseStrikeMp, s.Vitals.Mp);
        Assert.Equal(0, s.Hits);

        Run(s, 200);                                                 // 挥击动画第 1 帧(100 到 200 毫秒)的框打中一次,伤害取 [level info] 第 0 列的百分比
        Assert.Equal(1, s.Hits);
        double strike = PlaceholderDamage.SkillBaseHit * AvatarFixture.PoseStrikeRate2 / 100.0;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(strike), (long)Math.Floor(strike) + 1);

        Run(s, 200);                                                 // 挥击 300 毫秒播完:回到站立,没有再接
        Assert.Equal((HeroMotion.Rest, 0, 1, 1, 1), (s.Hero.Motion, s.Hero.SkillStage, s.SkillChains, s.SkillCasts, s.Hits));
        Assert.Equal(500 - AvatarFixture.PoseStrikeMp, s.Vitals.Mp);

        // 冷却取 [level info] 第 1 列(2400 毫秒),从起手算起,接段没有把它重置回满;已过约 810 毫秒
        Assert.InRange(slot.CooldownLeftMs, 1500.0, 1700.0);
    }

    [Fact]
    public void TheCooldownColumnBlocksTheNextCastUntilItsDurationHasPassed()
    {
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0), monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        Run(s, 800, (0, SkillKeys.A));                               // 整个技能(400 + 300 毫秒)放完
        Assert.Equal((HeroMotion.Rest, 1, 1), (s.Hero.Motion, s.SkillCasts, s.SkillChains));
        Assert.True(slot.CooldownLeftMs > 0);

        Run(s, 20, (0, SkillKeys.A));                                // 冷却中再按:不是新的施放,不扣蓝
        Assert.Equal((1, HeroMotion.Rest, 500 - AvatarFixture.PoseStrikeMp), (s.SkillCasts, s.Hero.Motion, s.Vitals.Mp));

        Run(s, 1700);                                                // 2540 毫秒:冷却(2400 毫秒,从起手算起)已过
        Assert.Equal(0.0, slot.CooldownLeftMs);
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((2, HeroMotion.Skill, 500 - 2 * AvatarFixture.PoseStrikeMp), (s.SkillCasts, s.Hero.Motion, s.Vitals.Mp));
    }

    [Fact]
    public void APoseInterruptedByAHitNeverChainsOntoTheStrike()
    {
        using var arena = NewArena();
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 100);
        Assert.True(s.Hero.BeginHurt(100, 0, 0, true));              // 姿势播到一半被打:技能中断(姿势段没有命中,所以也没有打出去)
        Run(s, 900);
        Assert.Equal((0, 0, HeroMotion.Rest), (s.SkillChains, s.Hits, s.Hero.Motion));
    }
}
