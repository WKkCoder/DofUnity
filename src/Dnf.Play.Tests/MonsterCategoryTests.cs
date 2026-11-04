using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>怪物脚本运行时类别 bitset(M71,E-171):通过 <see cref="MonsterActor.ActSelf"/> 增删与查询。</summary>
public sealed class MonsterCategoryTests
{
    private static MonsterActor Blob(DnfAssets assets, double x = 100, double y = 200)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, 100, 222.5, 1500, 0);
    }

    [Fact]
    public void ActSelfCarriesCategoriesAddedAndRemovedByTheScriptInterface()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        IActEntity self = Blob(assets).ActSelf;
        self.AddActCategory(12);
        Assert.True(self.HasActCategory(12));
        self.RemoveActCategory(12);
        Assert.False(self.HasActCategory(12));
    }

    [Fact]
    public void InvalidCategoryIdsAreIgnoredOnActSelf()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        IActEntity self = Blob(assets).ActSelf;
        self.AddActCategory(ActObjectCategories.InvalidId);
        Assert.False(self.HasActCategory(ActObjectCategories.InvalidId));
    }

    [Fact]
    public void RoomMonsterBuilderSeedsCategoryNamesFromMobDefinition()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        using var arena = new RoomCombatTests.Arena(System.Array.Empty<(int, int)>());
        var builder = new RoomMonsterBuilder(assets, new GameData(new PvfScriptSource(assets.Pvf)), MonsterCatalog.Read(assets),
            arena.Hero, 1120, 1, 0, null, null);
        var spawn = new RoomMonsterSpawn(AvatarFixture.CategorySeedMobCode, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");
        MonsterActor m = builder.Build(2, spawn);
        Assert.NotNull(m);
        int goblin = ActObjectCategories.Resolve("goblin");
        Assert.True(m.ActSelf.HasActCategory(goblin));
        Assert.NotNull(m.Defense);
        Assert.True(m.Defense.CategoryIds.Count >= 1);
    }

    [Fact]
    public void FixtureCategoryWritesAnchorCoordinatesOnActSelf()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 120, 240);
        m.ActSelf.AddActCategory(ActObjectCategories.AnchorCategoryId);
        Assert.True(m.CategoryAnchorActive);
        Assert.Equal(120f, m.CategoryAnchorX);
        Assert.Equal(240f, m.CategoryAnchorY);
        m.ActSelf.RemoveActCategory(ActObjectCategories.AnchorCategoryId);
        Assert.False(m.CategoryAnchorActive);
    }
}
