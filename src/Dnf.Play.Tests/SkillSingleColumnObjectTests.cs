using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 放出的被动对象只用一列伤害时(M105,R7-he-04、R7-he-06:客户端有的状态只给对象传百分比、有的只传固定值,配方里没传的那一列写 -1):
/// 没传的那一项伤害是 0,传了的取 [level info] 当前等级那一列。数据全是合成的(<see cref="AvatarFixture.WaveSkill"/>):
/// [level info] 三列,1 级 (11, ?, 222)、2 级 (12, ?, 333) —— 第 0 列是固定值、第 2 列是百分比(取自 <c>HeroObjectTests</c> 里同一份夹具的已知数)。
/// </summary>
public class SkillSingleColumnObjectTests
{
    private static HeroSkillSlot Slot(GameData data, int level, int rateColumn, int fixedColumn)
    {
        var recipes = new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.WaveSkill,
                new[] { new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 3) },
                slideSpeedIndex: 1, slideAccelIndex: 2, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: new[] { new SkillObjectSpec(AvatarFixture.WaveObjectId, 0, 3, damageRateColumn: rateColumn, damageFixedColumn: fixedColumn) }),
        });
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.WaveSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.WaveSkill) }, 0, 0);
        return HeroSkills.Create(data, chr, 0, book, recipes).SlotOf(SkillKeys.A);
    }

    [Fact]
    public void AnObjectWithOnlyAFixedColumnDealsNoPercentAndAnObjectWithOnlyAPercentColumnDealsNoFixed()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroStrikeInfo fixedOnly = Slot(data, 2, rateColumn: -1, fixedColumn: 0).ObjectStrikes[0];
        Assert.Equal((true, 0, 12), (fixedOnly.IsSkillDamage, fixedOnly.SkillRatePercent, fixedOnly.SkillFixed));

        HeroStrikeInfo rateOnly = Slot(data, 2, rateColumn: 2, fixedColumn: -1).ObjectStrikes[0];
        Assert.Equal((true, 333, 0), (rateOnly.IsSkillDamage, rateOnly.SkillRatePercent, rateOnly.SkillFixed));
    }

    [Fact]
    public void TheColumnsFollowTheSkillLevelAndAMissingColumnStaysZeroAtEveryLevel()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 1 级:固定值第 0 列 11、百分比第 2 列 222;没传的那一列照样 0
        HeroStrikeInfo fixedOnly = Slot(data, 1, rateColumn: -1, fixedColumn: 0).ObjectStrikes[0];
        Assert.Equal((0, 11), (fixedOnly.SkillRatePercent, fixedOnly.SkillFixed));
        HeroStrikeInfo rateOnly = Slot(data, 1, rateColumn: 2, fixedColumn: -1).ObjectStrikes[0];
        Assert.Equal((222, 0), (rateOnly.SkillRatePercent, rateOnly.SkillFixed));

        // 边界:两列都没传 → 两项都是 0;两列都传(反例,这是已有对象的写法)→ 两项都取到
        HeroStrikeInfo none = Slot(data, 2, rateColumn: -1, fixedColumn: -1).ObjectStrikes[0];
        Assert.Equal((0, 0), (none.SkillRatePercent, none.SkillFixed));
        HeroStrikeInfo both = Slot(data, 2, rateColumn: 2, fixedColumn: 0).ObjectStrikes[0];
        Assert.Equal((333, 12), (both.SkillRatePercent, both.SkillFixed));
    }
}
