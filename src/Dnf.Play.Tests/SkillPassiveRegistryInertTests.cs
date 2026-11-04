using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>未学被动时登记路径不改变建槽倍率(E-231)。</summary>
public sealed class SkillPassiveRegistryInertTests
{
    [Fact]
    public void GivenSkillsMatchLegacySlotDamage()
    {
        using var fx = new AvatarFixture();
        GameData data = new GameData(new PvfScriptSource(DnfAssets.Open(fx.Root).Pvf));
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        HeroSkills legacy = HeroSkills.Create(data, chr, 0);
        HeroSkills withBook = HeroSkills.Create(data, chr, 0, book);
        Assert.Equal(legacy.Slots.Count, withBook.Slots.Count);
        for (int i = 0; i < legacy.Slots.Count; i++)
        {
            HeroStrikeInfo? a = legacy.Slots[i].StrikeAt(0);
            HeroStrikeInfo? b = withBook.Slots[i].StrikeAt(0);
            Assert.Equal(a?.SkillRatePercent, b?.SkillRatePercent);
            Assert.Equal(a?.SkillFixed, b?.SkillFixed);
        }
    }
}
