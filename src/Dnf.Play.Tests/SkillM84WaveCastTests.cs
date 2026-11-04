using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>M84 波系配方合成放技能(E-232);技能 904,状态族 0x18 与 E-135 同模型。</summary>
public sealed class SkillM84WaveCastTests
{
    private const int WaveSkill = 904;

    private static SkillRecipes WaveRecipe() => new SkillRecipes(new[]
    {
        new SkillRecipe(WaveSkill,
            new[] { new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: 1, castHoldFrames: 3) },
            slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1,
            objects: new[] { new SkillObjectSpec(objectId: 20003, stage: 0, frame: 3, damageRateColumn: 0, damageFixedColumn: 1) }),
    });

    private static HeroSkills Load(GameData data)
    {
        CharacterDef chr = data.GetCharacter(0);
        var catalog = SkillCatalog.Load(data, 0);
        var book = new SkillBook(catalog);
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        book.Restore(new[] { new KeyValuePair<int, int>(WaveSkill, 1) }, Array.Empty<KeyValuePair<int, int>>(), 0, 0);
        return HeroSkills.Create(data, chr, 0, book, WaveRecipe());
    }

    [Fact]
    public void CommandSlotExistsAndObjectStrikeReady()
    {
        using var fx = new AvatarFixture();
        GameData data = new GameData(new PvfScriptSource(DnfAssets.Open(fx.Root).Pvf));
        HeroSkills skills = Load(data);
        HeroSkillSlot slot = skills.CommandSlots.Single(s => s.SkillId == WaveSkill);
        Assert.NotNull(slot.Recipe);
        Assert.Single(slot.ObjectStrikes);
        Assert.True(HeroSkills.CommandCasts(slot.Def.Command));
    }
}
