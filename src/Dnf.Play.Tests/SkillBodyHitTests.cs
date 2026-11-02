using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 单段身体命中类技能(M64,证据 E-162,客户端状态 0x41 与 0x42):进入块一次设好动作、攻击信息、伤害列与特效,没有逐帧逻辑,动画播完状态就结束;
/// 身体的攻击框打人,不滑行、没有被动对象、没有第二段。
/// 数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):第一段动作 400 毫秒,第 2 帧(200 毫秒起)有攻击框;
/// [level info] 的 2 级是 (180, 300, 40, 60):取第 0 列(没有固定值列);预载特效两个;冷却 3000、耗蓝 20。
/// </summary>
public class SkillBodyHitTests
{
    private static SkillRecipes Recipes(bool twoEffects = true)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: twoEffects ? new[] { 1 } : null),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false),
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

    private static TownHero NewHero(DnfAssets assets, double x = 100, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), x, 50, facingLeft, TownHero.DungeonRestTag);
    }

    private static RoomCombatTests.Arena NewArena(SkillRecipes recipes = null, HeroVitals vitals = null, (int X, int Y)[] monsters = null)
    {
        var arena = new RoomCombatTests.Arena(monsters ?? new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, vitals: vitals,
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

    // ------------------------------------------------------------------ 默认表

    private static SkillRecipe SingleStageByMotion(int motionIndex)
    {
        SkillRecipe found = null;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.Stages.Count == 1 && r.Stages[0].MotionIndex == motionIndex)
            {
                Assert.Null(found);
                found = r;
            }
        }
        return found;
    }

    [Fact]
    public void TheDefaultTableHasTheTwoSingleStageBodyHitRecipesWithTheEngineConstants()
    {
        // 按动作项(客户端状态码里写死的 [etc motion] 下标)找配方:每个下标只有一份
        SkillRecipe a = SingleStageByMotion(120);
        SkillRecipe b = SingleStageByMotion(121);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a.SkillId, b.SkillId);
        foreach (SkillRecipe r in new[] { a, b })
        {
            Assert.True(HeroSkills.IsImplemented(r.SkillId));
            SkillStageSpec st = Assert.Single(r.Stages);
            // 身体打人、只有百分比列(第 0 列)、没有固定值列;特效一进状态就放;不滑行;没有别的机制
            Assert.True(st.HasOwnStrike);
            Assert.Equal((0, -1), (st.DamageRateColumn, st.DamageFixedColumn));
            Assert.Equal((0, -1, 0, 0), (st.EffectFrame, st.EndFrame, st.CastHoldFrames, st.RequiresSkill));
            Assert.Equal(int.MaxValue, r.ChainFromFrame);                // 只有一段:没有接段帧
            Assert.True(r.SlideSpeedIndex < 0 && r.SlideAccelIndex < 0 && r.ForwardSpeedIndex < 0 && r.ForwardAccelIndex < 0 && !r.SlideFollowsHeldDirection);
            Assert.Empty(r.Objects);
            Assert.Null(r.Hop);
            Assert.Null(r.Dive);
            Assert.Null(r.JumpAttack);
            Assert.False(r.AutoChain);
            Assert.Equal(0, r.MultiHitPassiveSkillId);
        }
        // 两个技能各自的动作项、攻击信息项与特效个数(客户端状态码里写死的下标)
        Assert.Equal((120, 84), (a.Stages[0].MotionIndex, a.Stages[0].AttackInfoIndex));
        Assert.Equal(new[] { 0, 1 }, a.Stages[0].EffectIndices.ToArray());
        Assert.Equal((121, 85), (b.Stages[0].MotionIndex, b.Stages[0].AttackInfoIndex));
        Assert.Empty(b.Stages[0].EffectIndices);
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void ASlotReadsOneBodyStrikeFromTheDamageRateColumnAndBothEffects()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkillSlot slot = Load(new GameData(new PvfScriptSource(assets.Pvf))).SlotOf(SkillKeys.A);

        Assert.Single(slot.StageStrikes);
        HeroStrikeInfo body = slot.StageStrikes[0];
        Assert.Equal((true, 180, 0), (body.IsSkillDamage, body.SkillRatePercent, body.SkillFixed));      // 第 0 列的百分比、没有固定值
        Assert.True(slot.StageHasOwnStrike(0));
        Assert.Empty(slot.ObjectStrikes);
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2 }, slot.StageEffects.ToArray());
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void InTheRoomTheCastHitsOnceWithTheBodyAndEndsWhenTheAnimationEndsWithNothingElse()
    {
        // 主角 (300, 100) 朝右;怪物在 (340, 100):第 2 帧(200 毫秒起)的身体攻击框够得着
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);
        double startX = s.Hero.X;

        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((1, HeroMotion.Skill, 480), (s.SkillCasts, s.Hero.Motion, s.Vitals.Mp));
        Run(s, 190);                                                 // 200 毫秒:攻击框刚到
        Run(s, 20);
        Assert.Equal(1, s.Hits);
        double body = PlaceholderDamage.SkillBaseHit * 180 / 100.0;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(body), (long)Math.Floor(body) + 1);

        // 没有第二段、没有对象、不滑行;400 毫秒播完回到站立
        Run(s, 200);
        Assert.Equal((HeroMotion.Rest, 0, 0, 0), (s.Hero.Motion, s.Hero.SkillStage, s.SkillChains, s.HeroObjectsSpawned));
        Assert.Equal(startX, s.Hero.X);
        Assert.Equal(1, s.Hits);                                     // 一次挥击只打一下
        Assert.Equal(1, s.SkillCasts);
        Assert.InRange(slot.CooldownLeftMs, 2300.0, 3000.0);         // 3000 毫秒冷却从起手算起
    }

    [Fact]
    public void BothEffectsAreOnTheScreenFromTheFirstStepOfTheCast()
    {
        using var arena = NewArena(monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        Assert.Empty(s.Effects);
        Run(s, 10, (0, SkillKeys.A));
        Assert.Contains(s.Effects, e => e.Path == AvatarFixture.ChainEffect1);
        Assert.Contains(s.Effects, e => e.Path == AvatarFixture.ChainEffect2);

        // 对照:配方只挂第 0 个特效时只出现第一个
        using var one = NewArena(Recipes(twoEffects: false), monsters: new[] { (900, 100) });
        Run(one.Session, 10, (0, SkillKeys.A));
        Assert.Contains(one.Session.Effects, e => e.Path == AvatarFixture.ChainEffect1);
        Assert.DoesNotContain(one.Session.Effects, e => e.Path == AvatarFixture.ChainEffect2);
    }

    [Fact]
    public void ThePlayedDirectionKeysDoNotSlideAHeroThatCastsASingleStageBodyHitSkill()
    {
        using var arena = NewArena(monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        double startX = s.Hero.X;
        for (int t = 0; t < 400; t += 10)
        {
            s.Update(10, 1, 0, false, false, t == 0 ? SkillKeys.A : SkillKeys.None);      // 一直按着右
        }
        Assert.Equal(startX, s.Hero.X);                              // 技能里不滑行、也不听方向键
    }

    [Fact]
    public void AKeyPressedAgainWhileTheSingleStageSkillRunsDoesNothing()
    {
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0), monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        Run(s, 400, (0, SkillKeys.A), (250, SkillKeys.A));          // 只有一段:再按没有下一段可接(冷却中也不是新的施放)
        Assert.Equal((1, 0, 480, HeroMotion.Rest), (s.SkillCasts, s.SkillChains, s.Vitals.Mp, s.Hero.Motion));
    }
}
