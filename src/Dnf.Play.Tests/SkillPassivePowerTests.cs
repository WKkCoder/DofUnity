using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 被动威力列加成接到快捷槽伤害(M81,证据 E-181):主动技能 901,被动 902,[static data] 提供增量,合成数据。
/// </summary>
public class SkillPassivePowerTests
{
    private const int Passive = 902;
    private const int BonusStatic = 80;

    private static SkillRecipes Recipes() => new SkillRecipes(new[]
    {
        new SkillRecipe(AvatarFixture.ChainSkill,
            new[] { new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: 2, effectIndex: -1, effectFrame: 0) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1,
            passivePowerLinks: new[] { new SkillPassivePowerLink(Passive, 0) }),
    });

    private static HeroSkills Load(GameData data, int passiveLevel)
    {
        CharacterDef chr = data.GetCharacter(0);
        var defs = SkillCatalog.Load(data, 0).Skills.ToList();
        defs.Add(new SkillDef(Passive, "skill/test/passive902.skl", new[]
        {
            new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[passive]") }),
            new ScriptLine("[executable states]", new[] { new ScriptValue(ScriptValueKind.Int, 0, null) }),
            new ScriptLine("[static data]", new[]
            {
                new ScriptValue(ScriptValueKind.Int, BonusStatic, null),
            }),
        }));
        var catalog = new SkillCatalog(0, defs);
        var book = new SkillBook(catalog);
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 2));
        if (passiveLevel > 0)
        {
            levels.Add(new KeyValuePair<int, int>(Passive, passiveLevel));
        }
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);
        return HeroSkills.Create(data, chr, 0, book, Recipes());
    }

    [Theory]
    [InlineData(0, 180, 40)]
    [InlineData(1, 324, 72)]
    public void StageStrikeScalesWithPassive(int passiveLevel, int expectRate, int expectFixed)
    {
        using var fx = new AvatarFixture();
        GameData data = new GameData(new PvfScriptSource(DnfAssets.Open(fx.Root).Pvf));
        HeroSkills skills = Load(data, passiveLevel);
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        HeroStrikeInfo strike = slot.StrikeAt(0).Value;
        Assert.Equal(expectRate, strike.SkillRatePercent);
        Assert.Equal(expectFixed, strike.SkillFixed);
    }
}
