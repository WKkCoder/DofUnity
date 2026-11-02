using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 关键帧标志事件(M65,证据 E-163,客户端第二个逐帧函数 0x00cb0700):技能动画走到带标志的帧,配方里这一段的事件表就办——
/// 清命中名单(同一个目标再挨一下,事件开一个新的命中窗口),换命中的事件还把之后的命中换成事件的攻击信息与伤害列;
/// "已触发到哪个事件"属于一击,换一击就回到原来的命中。数据全是合成的(见 <see cref="AvatarFixture.FlagSkill"/>):
/// 五帧 × 100 毫秒的动画,第 0、1、3 帧有攻击框,第 1 帧标志 1、第 3 帧标志 2;[level info] 两列百分比 (130, 260)。
/// 攻击信息:基础是 [etc attack info] 第 1 项(普通受击),标志 2 的事件换成第 2 项(倒地)与百分比第 1 列。
/// </summary>
public class SkillKeyFlagTests
{
    private static SkillRecipes Recipes(params SkillFlagEvent[] events)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.FlagSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: AvatarFixture.FlagEtcIndex, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        flagEvents: events),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false),
        });
    }

    private static SkillFlagEvent ClearOnly()
    {
        return new SkillFlagEvent(AvatarFixture.FlagA);
    }

    private static SkillFlagEvent SwitchStrike()
    {
        return new SkillFlagEvent(AvatarFixture.FlagB, attackInfoIndex: 2, damageRateColumn: 1);
    }

    private static HeroSkills Load(GameData data, SkillRecipes recipes)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.FlagSkill, 1));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.FlagSkill) }, 0, 0);       // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    private static RoomCombatTests.Arena NewArena(SkillRecipes recipes, HeroVitals vitals = null, int weaponStun = 0)
    {
        var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 100000, frozen: true, chr: AvatarFixture.ActionChrPath, vitals: vitals,
            weaponStun: weaponStun, skillsFactory: data => Load(data, recipes));
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

    private static long Base(int percent)
    {
        return (long)Math.Floor(PlaceholderDamage.SkillBaseHit * percent / 100.0);
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void ASlotBuildsAStrikeOnlyForTheEventsThatChangeIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkillSlot slot = Load(data, Recipes(ClearOnly(), SwitchStrike())).SlotOf(SkillKeys.A);

        Assert.Equal(0, slot.FlagEventIndex(0, AvatarFixture.FlagA));
        Assert.Equal(1, slot.FlagEventIndex(0, AvatarFixture.FlagB));
        Assert.Equal(-1, slot.FlagEventIndex(0, 99));                        // 没有这个标志的事件
        Assert.False(slot.FlagEventChangesStrike(0, 0));
        Assert.True(slot.FlagEventChangesStrike(0, 1));
        Assert.False(slot.FlagEventChangesStrike(0, 5));                     // 越界当没有

        HeroStrikeInfo baseStrike = slot.StrikeAt(0).Value;
        Assert.Equal((AvatarFixture.FlagRate0, false, AvatarFixture.ChainAPushAside), (baseStrike.SkillRatePercent, baseStrike.KnockDown, baseStrike.PushAside));
        // 变体 0 是原来的;变体 1(第 0 个事件只清名单)与越界的变体都还是原来的;变体 2(标志 2 的事件)是换了的
        Assert.Equal(baseStrike.SkillRatePercent, slot.StrikeAt(0, 0).Value.SkillRatePercent);
        Assert.Equal(baseStrike.SkillRatePercent, slot.StrikeAt(0, 1).Value.SkillRatePercent);
        Assert.Equal(baseStrike.SkillRatePercent, slot.StrikeAt(0, 9).Value.SkillRatePercent);
        HeroStrikeInfo switched = slot.StrikeAt(0, 2).Value;
        Assert.Equal((AvatarFixture.FlagRate1, true, AvatarFixture.ChainBPushAside), (switched.SkillRatePercent, switched.KnockDown, switched.PushAside));
    }

    [Fact]
    public void ARecipeWithoutEventsHasNoFlagStrikesAndAnUnknownFlagFindsNothing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkillSlot slot = Load(data, Recipes()).SlotOf(SkillKeys.A);
        Assert.Equal(-1, slot.FlagEventIndex(0, AvatarFixture.FlagA));
        Assert.Equal(slot.StrikeAt(0).Value.SkillRatePercent, slot.StrikeAt(0, 3).Value.SkillRatePercent);
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void WithoutEventsAFlaggedAnimationHitsOnceBecauseTheListIsNeverCleared()
    {
        using var arena = NewArena(Recipes());
        RoomCombatSession s = arena.Session;
        Run(s, 600, (0, SkillKeys.A));
        Assert.Equal((1, 1, 0), (s.SkillCasts, s.Hits, s.KeyFlagEvents));     // 第 0、1、3 帧都有框,但同一个怪物一击只挨一下
        Assert.InRange(s.TotalDamage, Base(AvatarFixture.FlagRate0), Base(AvatarFixture.FlagRate0) + 1);
    }

    [Fact]
    public void AClearOnlyEventOpensASecondWindowOnTheFlaggedFrameWithTheSameStrike()
    {
        using var arena = NewArena(Recipes(ClearOnly()));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal(1, s.Hits);                                              // 第 0 帧:第一个窗口
        Run(s, 80);
        Assert.Equal((1, 0), (s.Hits, s.KeyFlagEvents));                      // 90 毫秒:标志帧还没到
        Run(s, 30);                                                           // 120 毫秒:走过标志 1 的帧(100 毫秒起),名单清空,这一帧的框重判:又打中一次
        Assert.Equal((2, 1), (s.Hits, s.KeyFlagEvents));
        Run(s, 300);                                                          // 第 3 帧有框,但这一版没有标志 2 的事件:不再开窗口
        Assert.Equal((2, 1), (s.Hits, s.KeyFlagEvents));
        Assert.InRange(s.TotalDamage, 2 * Base(AvatarFixture.FlagRate0), 2 * Base(AvatarFixture.FlagRate0) + 2);
    }

    [Fact]
    public void AFlagIsAppliedOnlyOnceWhileHitStopFreezesTheHero()
    {
        // 命中停顿(武器停顿 100 毫秒)把主角定住时外面不走主角的更新:标志帧那一步读到的标志不能在停顿里被每步重办一遍
        // (重办 → 再打中一次 → 再续一次停顿 → 永远出不来,真数据里一次出招办了上百次)
        using var arena = NewArena(Recipes(ClearOnly()), weaponStun: 100);
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal(1, s.Hits);                                              // 第 0 帧的框:第一个窗口,定住 100 毫秒
        Assert.True(s.HeroPauseMs > 0);
        Run(s, 1000);
        Assert.Equal((2, 1), (s.Hits, s.KeyFlagEvents));                      // 标志 1 的帧办一次:第二个窗口、第二下;没有第三下
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
    }

    [Fact]
    public void AFlagOfTheOldStageReadOnTheStepThatChainsToTheNextStageIsNotAppliedToTheNewStage()
    {
        // 第 0 段在第 3 帧开头(300 毫秒)结束并自动接第 1 段:这一步读到的标志 2 属于第 0 段(这一段没有标志 2 的事件),
        // 第 1 段恰好有标志 2 的事件,但标志带着自己的段号,不能算到新的一段头上
        var recipes = new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.FlagSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: AvatarFixture.FlagEtcIndex, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        endFrame: 3, flagEvents: new[] { ClearOnly() }),
                    new SkillStageSpec(motionIndex: AvatarFixture.FlagEtcIndex, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        flagEvents: new[] { SwitchStrike() }),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                autoChain: true),
        });
        using var arena = NewArena(recipes);
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 140);                                                          // 150 毫秒:第 0 段的标志 1 办过了
        Assert.Equal((0, 1), (s.Hero.SkillStage, s.KeyFlagEvents));
        Run(s, 170);                                                          // 320 毫秒:第 0 段在 300 毫秒结束、自动接了第 1 段
        Assert.Equal((1, 1), (s.Hero.SkillStage, s.KeyFlagEvents));           // 第 1 段还没走到自己的标志帧:事件数不变
    }

    [Fact]
    public void ASwitchEventChangesTheStrikeInfoAndTheDamageColumnForTheLaterHits()
    {
        using var arena = NewArena(Recipes(ClearOnly(), SwitchStrike()));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((1, AvatarFixture.FlagRate0, false), (s.Hits, s.CurrentHeroStrike.SkillRatePercent, s.CurrentHeroStrike.KnockDown));
        Run(s, 120);                                                          // 130 毫秒:标志 1:又一个窗口,命中不变
        Assert.Equal((2, AvatarFixture.FlagRate0, false), (s.Hits, s.CurrentHeroStrike.SkillRatePercent, s.CurrentHeroStrike.KnockDown));
        long afterSecond = s.TotalDamage;

        Run(s, 190);                                                          // 320 毫秒:标志 2 的帧(300 毫秒起):清名单、命中换成倒地与第 1 列,这一帧的框重判:第三下
        Assert.Equal((3, 2), (s.Hits, s.KeyFlagEvents));
        Assert.Equal((AvatarFixture.FlagRate1, true), (s.CurrentHeroStrike.SkillRatePercent, s.CurrentHeroStrike.KnockDown));
        long third = s.TotalDamage - afterSecond;
        Assert.InRange(third, Base(AvatarFixture.FlagRate1), Base(AvatarFixture.FlagRate1) + 1);                // 第三下按第 1 列算,比前两下重

        Run(s, 400);
        Assert.Equal((3, HeroMotion.Rest), (s.Hits, s.Hero.Motion));
    }

    [Fact]
    public void TheTriggeredEventIsForgottenWhenTheNextSwingStarts()
    {
        using var arena = NewArena(Recipes(ClearOnly(), SwitchStrike()));
        RoomCombatSession s = arena.Session;
        Run(s, 600, (0, SkillKeys.A));                                        // 整个动画走完:触发过标志 2 的事件
        Assert.Equal(3, s.Hits);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);

        Run(s, 3000);                                                         // 冷却 3000 毫秒过去
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal((2, HeroMotion.Skill), (s.SkillCasts, s.Hero.Motion));
        Assert.Equal((4, AvatarFixture.FlagRate0, false), (s.Hits, s.CurrentHeroStrike.SkillRatePercent, s.CurrentHeroStrike.KnockDown));   // 新的一击从原来的命中开始
    }

    [Fact]
    public void AnEventOnAnotherFlagValueIsIgnored()
    {
        using var arena = NewArena(Recipes(new SkillFlagEvent(77, attackInfoIndex: 2, damageRateColumn: 1)));
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 400);                                                          // 410 毫秒:两个带标志的帧都走过了,技能还在放
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        Assert.Equal((1, 0, AvatarFixture.FlagRate0), (s.Hits, s.KeyFlagEvents, s.CurrentHeroStrike.SkillRatePercent));
        Run(s, 200);
        Assert.Equal((1, 0, HeroMotion.Rest), (s.Hits, s.KeyFlagEvents, s.Hero.Motion));
    }
}
