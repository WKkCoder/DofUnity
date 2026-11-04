using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>被动威力登记 native 与 ChangeSkillEffect 路径(E-231);合成技能 901/902。</summary>
public sealed class SkillPassiveRegistrationTests
{
    private const int Active = 901;
    private const int Passive = 902;
    private const int StaticBonus = 50;

    private static (GameWorld World, GameObject Hero) Build()
    {
        var fx = new Fixture();
        fx.List("character/character.lst", (0, "test/test.chr"));
        fx.Tokens("character/test/test.chr", w => w
            .Tag("[job]").Str("[test]")
            .Tag("[waiting motion]").Str("animation/stand.ani")
            .Tag("[etc motion]").Str("animation/cast.ani"));
        fx.Ani("character/test/animation/stand.ani", new AniBuilder().Loop().Frame(100));
        fx.Ani("character/test/animation/cast.ani", new AniBuilder().Frame(10000).Frame(30).Frame(30).Frame(30));
        fx.List("skill/skilllist.lst", (0, "testskill.lst"));
        fx.List("skill/testskill.lst", (Active, "test/active.skl"), (Passive, "test/passive.skl"));
        fx.Tokens("skill/test/active.skl", w => w
            .Tag("[type]").Str("[active]")
            .Tag("[level info]").Ints(2, 100, 10));
        fx.Tokens("skill/test/passive.skl", w => w
            .Tag("[type]").Str("[passive]")
            .Tag("[executable states]").Ints(0)
            .Tag("[static data]").Ints(0, StaticBonus));
        fx.Nut("sqr/loadstate.nut", @"
IRDSQRCharacter.pushScriptFiles(""test/header.nut"");
IRDSQRCharacter.pushState(0, ""test/passive902.nut"", ""Passive902"", 0, 902);
");
        fx.Nut("sqr/test/header.nut", "STATE_STAND <- 0;");
        fx.Nut("sqr/test/passive902.nut", @"
function onChangeSkillEffect_Passive902(chr, skill, data)
{
	chr.sq_addPassiveSkillAttackBonusRate(901, 1);
	::R_RATE1000 <- (chr.sq_GetPassiveAttackRate(901, 0) * 1000).tointeger();
}
function onChangeSkillEffect_Remove(chr, skill, data)
{
	chr.sq_addPassiveSkillAttackBonusRate(901, 0);
	chr.sq_RemovePassiveSkillAttackBonusRate(901);
	::R_COUNT <- chr.sq_GetPassiveAttackRate(901, 0).tointeger();
}
");
        GameWorld world = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        GameObject hero = world.SpawnCharacter(0, "hero", 0, 0, 0, Direction.Right);
        hero.SkillLevels[Passive] = 1;
        return (world, hero);
    }

    [Fact]
    public void ChangeSkillEffectRegistersLink()
    {
        (GameWorld world, GameObject hero) = Build();
        using (world)
        {
            SkillPassivePowerStore.BeginChangeEffect(Passive);
            try
            {
                world.ChangeSkillEffect(hero, Passive, Array.Empty<byte>());
            }
            finally
            {
                SkillPassivePowerStore.EndChangeEffect();
            }
            IReadOnlyList<SkillPassivePowerRate.Link> links = SkillPassivePowerStore.Snapshot(hero);
            Assert.Single(links);
            SkillPassivePowerRate.Link link = links[0];
            Assert.Equal(Passive, link.PassiveSkillId);
            Assert.Equal(Active, link.ActiveSkillId);
            Assert.Equal(1, link.StaticIndex);
        }
    }

    [Fact]
    public void GetPassiveAttackRateUsesRegisteredLink()
    {
        (GameWorld world, GameObject hero) = Build();
        using (world)
        {
            SkillPassivePowerStore.BeginChangeEffect(Passive);
            try
            {
                world.ChangeSkillEffect(hero, Passive, Array.Empty<byte>());
            }
            finally
            {
                SkillPassivePowerStore.EndChangeEffect();
            }
            Assert.True(world.Vm.TryGetGlobalInteger("R_RATE1000", out long scaled));
            int expect = (int)Math.Round((1f + StaticBonus * SkillPassivePowerRate.ProtectedRateScale) * 1000f);
            Assert.Equal(expect, scaled);
        }
    }

    [Fact]
    public void RemoveClearsLinksForActive()
    {
        (GameWorld world, GameObject hero) = Build();
        using (world)
        {
            SkillPassivePowerStore.Add(hero, Passive, Active, 0, false);
            SkillPassivePowerStore.RemoveForActive(hero, Active);
            Assert.Empty(SkillPassivePowerStore.Snapshot(hero));
        }
    }
}
