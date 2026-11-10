using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 装扮部位的两张独立的表必须同序同数(r14-avatar):Dnf.Game 的装备部位表与装扮栏(<see cref="EquipmentParts"/>、<see cref="AvatarSlots"/>、<see cref="AvatarBagView"/>)
/// 和 Dnf.Render 的装扮衣柜(<see cref="AvatarWardrobe"/>)。两个程序集互不引用,只有宿主层同时看得见,所以对齐检查放在这里:
/// 哪一边的编号错一位,"穿上的帽子"就会去替换"头发"的图层。
/// </summary>
public class AvatarPartTableTests
{
    [Fact]
    public void TheWardrobeCoversExactlyTheTenAvatarSlots()
    {
        Assert.Equal(AvatarSlots.Count, AvatarWardrobe.PartCount);
        Assert.Equal(0, AvatarSlots.First);
        Assert.Equal(AvatarWardrobe.PartCount - 1, AvatarSlots.Last);
        for (int part = -1; part <= 12; part++)
        {
            bool avatarPart = part >= 0 && part < AvatarWardrobe.PartCount;
            Assert.Equal(avatarPart, AvatarSlots.IsSlot(part));
            Assert.Equal(avatarPart, EquipmentParts.IsAvatar(part));
        }
    }

    [Fact]
    public void BothSidesNameEachPartWithTheSameEquipmentTypeTag()
    {
        for (int part = 0; part < AvatarWardrobe.PartCount; part++)
        {
            string tag = AvatarWardrobe.TagOf(part);
            Assert.Equal(EquipmentParts.TagOf(part), tag);
            Assert.Equal(part, AvatarWardrobe.PartOf(tag));
            Assert.Equal(part, EquipmentParts.FromTag(tag));
        }
    }

    [Fact]
    public void NormalGearTagsAreNotAvatarPartsOnTheRenderSide()
    {
        for (int part = AvatarWardrobe.PartCount; part <= EquipmentParts.ArtifactGreen; part++)
        {
            string tag = EquipmentParts.TagOf(part);
            Assert.NotNull(tag);
            Assert.Equal(-1, AvatarWardrobe.PartOf(tag));
            Assert.False(AvatarSlots.IsSlot(part));
        }
        Assert.Equal(-1, AvatarWardrobe.PartOf("[weapon]"));
    }

    [Fact]
    public void TheSkinPartIsTheSamePartOnBothSides()
    {
        Assert.Equal(AvatarWardrobe.SkinPart, EquipmentParts.FromTag("[skin avatar]"));
        Assert.Equal("[skin avatar]", AvatarWardrobe.TagOf(AvatarWardrobe.SkinPart));
    }

    [Fact]
    public void TheBagViewCategoriesAreTheWardrobeParts()
    {
        AvatarBagView view = AvatarBagView.Build(new Inventory(), new AvatarSlots(), 0, 1);
        Assert.Equal(AvatarWardrobe.PartCount, view.Categories.Count);
        for (int part = 0; part < AvatarWardrobe.PartCount; part++)
        {
            Assert.Equal(part, view.Category(part).Part);
            Assert.Equal(AvatarWardrobe.TagOf(part), view.Category(part).Tag);
        }
    }
}
