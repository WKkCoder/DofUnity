using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>JumpStrikeOf 纯逻辑(D-M82-01):不跑跳跃时序。</summary>
public sealed class SkillJumpStrikeResolveTests
{
    private static HeroStrikeInfo BaseJump => new HeroStrikeInfo(100, 5, true);

    [Fact]
    public void NullSlotReturnsBaseJump()
    {
        Assert.Equal(BaseJump.SkillRatePercent, HeroJumpStrikeResolve.Apply(BaseJump, null).SkillRatePercent);
    }

    [Fact]
    public void JumpAttackWithoutSkillDamageColumnReturnsBaseJump()
    {
        using var fx = new AvatarFixture();
        GameData data = new GameData(new PvfScriptSource(DnfAssets.Open(fx.Root).Pvf));
        var recipes = new SkillRecipes(new[]
        {
            new SkillRecipe(SkillJumpAttackTests.JumpSkillId,
                new[] { new SkillStageSpec(motionIndex: -1, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                jumpAttack: new SkillJumpAttackSpec(AvatarFixture.JumpSkillAEtcIndex, AvatarFixture.JumpSkillBEtcIndex, effectFrame: 2, effectsA: new[] { 4, 6 }, effectsB: new[] { 5, 7 })),
        });
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        book.Restore(new[] { new KeyValuePair<int, int>(SkillJumpAttackTests.JumpSkillId, 2) }, Array.Empty<KeyValuePair<int, int>>(), 0, 0);
        HeroSkills skills = HeroSkills.Create(data, chr, 0, book, recipes);
        HeroSkillSlot slot = skills.SlotOfSkill(SkillJumpAttackTests.JumpSkillId);
        HeroStrikeInfo r = HeroJumpStrikeResolve.Apply(BaseJump, slot);
        Assert.Equal(BaseJump.SkillRatePercent, r.SkillRatePercent);
        Assert.Equal(BaseJump.SkillFixed, r.SkillFixed);
    }

    [Fact]
    public void ReplacesSkillDamageFromScaledStageStrike()
    {
        using var fx = new AvatarFixture();
        GameData data = new GameData(new PvfScriptSource(DnfAssets.Open(fx.Root).Pvf));
        HeroSkills skills = SkillJumpAttackTests.LoadWithPassive(data, 1, 2);
        HeroSkillSlot slot = skills.SlotOfSkill(SkillJumpAttackTests.JumpSkillId);
        HeroStrikeInfo? scaled = slot.StrikeAtWithPassiveNativeState(0, SkillPassivePowerRate.JumpAttackNativeState);
        Assert.True(scaled.HasValue);
        HeroStrikeInfo r = HeroJumpStrikeResolve.Apply(BaseJump, slot);
        Assert.Equal(scaled.Value.SkillRatePercent, r.SkillRatePercent);
        Assert.Equal(BaseJump.SkillFixed, r.SkillFixed);
    }
}
