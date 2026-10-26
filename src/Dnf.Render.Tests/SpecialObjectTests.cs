using Dnf.Data;
using Dnf.Render;

namespace Dnf.Render.Tests;

/// <summary>
/// 特殊物件与可破坏物件的数据层(证据 E-126):地图 [special passive object] 的文法(服务端 procSpecialPassiveObject)、
/// .obj 里 [int data]、[string data]、[etc motion] 的槽位解释,以及被打碎的物件不再画、不再挡人、受击动画替换基础动画。
/// 数据全是合成的(编号、坐标、关键字之外的数都没有来历)。
/// </summary>
public class SpecialObjectTests
{
    private static ScriptValue I(int v) => new(ScriptValueKind.Int, v, null);

    private static ScriptValue S(string text) => new(ScriptValueKind.String, 0, text);

    private static ScriptValue[] Values(params ScriptValue[] values) => values;

    // ------------------------------------------------------------------ 文法

    [Fact]
    public void AnEntryIsFiveNumbersFollowedByItsActionsEachAKeywordAndFiveNumbers()
    {
        List<SpecialPlacement> list = SpecialPlacement.Parse(Values(
            I(210), I(100), I(60), I(0), I(2),
            S("[item]"), I(0), I(3), I(1), I(0), I(0),
            S("[monster]"), I(5), I(20), I(50), I(100), I(0),
            I(211), I(300), I(70), I(5), I(0)));
        Assert.Equal(2, list.Count);

        SpecialPlacement first = list[0];
        Assert.Equal((0, 210, 100, 60, 0), (first.Ordinal, first.Id, first.X, first.Y, first.Z));
        Assert.Equal(2, first.Actions.Count);
        Assert.Equal(SpecialActionKind.Item, first.Actions[0].Kind);
        Assert.Equal((0, 3, 1, 0, 0), (first.Actions[0].P1, first.Actions[0].P2, first.Actions[0].P3, first.Actions[0].P4, first.Actions[0].P5));
        Assert.Equal(SpecialActionKind.Monster, first.Actions[1].Kind);
        Assert.Equal((5, 20, 50, 100), (first.Actions[1].P1, first.Actions[1].P2, first.Actions[1].P3, first.Actions[1].P4));
        Assert.True(first.Has(SpecialActionKind.Item));
        Assert.True(first.Has(SpecialActionKind.Monster));
        Assert.False(first.Has(SpecialActionKind.Quest));

        SpecialPlacement second = list[1];
        Assert.Equal((1, 211, 300, 70, 5), (second.Ordinal, second.Id, second.X, second.Y, second.Z));
        Assert.Empty(second.Actions);
        Assert.False(second.Has(SpecialActionKind.Item));
    }

    [Theory]
    [InlineData("[monster]", SpecialActionKind.Monster)]
    [InlineData("[trap]", SpecialActionKind.Trap)]
    [InlineData("[item]", SpecialActionKind.Item)]
    [InlineData("[quest]", SpecialActionKind.Quest)]
    [InlineData("[ITEM]", SpecialActionKind.Item)]
    public void ActionKeywordsMapToTheServersKindCodes(string keyword, SpecialActionKind kind)
    {
        List<SpecialPlacement> list = SpecialPlacement.Parse(Values(I(1), I(0), I(0), I(0), I(1), S(keyword), I(1), I(2), I(3), I(4), I(5)));
        Assert.Equal(kind, Assert.Single(list).Actions[0].Kind);
        Assert.Equal((int)kind, (int)list[0].Actions[0].Kind);
    }

    [Fact]
    public void OnlyObjectsWithAnItemActionGetAnItemIndexAndTheNumbersContinueFromTheGivenStart()
    {
        ScriptValue[] values = Values(
            I(1), I(0), I(0), I(0), I(1), S("[trap]"), I(0), I(0), I(0), I(0), I(0),
            I(2), I(0), I(0), I(0), I(2), S("[monster]"), I(0), I(0), I(0), I(0), I(0), S("[item]"), I(0), I(1), I(0), I(0), I(0),
            I(3), I(0), I(0), I(0), I(0),
            I(4), I(0), I(0), I(0), I(1), S("[item]"), I(1), I(0), I(1), I(0), I(0));
        List<SpecialPlacement> list = SpecialPlacement.Parse(values);
        Assert.Equal(new[] { -1, 0, -1, 1 }, list.Select(p => p.ItemIndex).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, list.Select(p => p.Ordinal).ToArray());

        List<SpecialPlacement> later = SpecialPlacement.Parse(values, firstOrdinal: 10, firstItemIndex: 5);
        Assert.Equal(new[] { -1, 5, -1, 6 }, later.Select(p => p.ItemIndex).ToArray());
        Assert.Equal(new[] { 10, 11, 12, 13 }, later.Select(p => p.Ordinal).ToArray());
    }

    [Fact]
    public void ParsingStopsAtAnUnknownKeywordKeepingTheEntriesBeforeItAndDroppingTheHalfReadOne()
    {
        List<SpecialPlacement> list = SpecialPlacement.Parse(Values(
            I(1), I(10), I(20), I(0), I(0),
            I(2), I(30), I(40), I(0), I(1), S("[hellparty]"), I(1), I(1), I(1), I(1), I(1),
            I(3), I(50), I(60), I(0), I(0)));
        SpecialPlacement only = Assert.Single(list);
        Assert.Equal(1, only.Id);
    }

    [Fact]
    public void ATruncatedEntryOrANonsensicalCountEndsTheList()
    {
        // 动作只有三个数就没了
        Assert.Empty(SpecialPlacement.Parse(Values(I(1), I(0), I(0), I(0), I(1), S("[item]"), I(0), I(1), I(2))));
        // 头部不够五个数
        Assert.Empty(SpecialPlacement.Parse(Values(I(1), I(0), I(0))));
        // 个数是负数
        Assert.Empty(SpecialPlacement.Parse(Values(I(1), I(0), I(0), I(0), I(-1))));
        // 个数比整个标签的值还多
        Assert.Empty(SpecialPlacement.Parse(Values(I(1), I(0), I(0), I(0), I(99))));
        // 第一项完整,第二项的动作位置上不是关键字
        List<SpecialPlacement> list = SpecialPlacement.Parse(Values(I(1), I(0), I(0), I(0), I(0), I(2), I(0), I(0), I(0), I(1), I(7), I(7), I(7), I(7), I(7), I(7)));
        Assert.Single(list);
        Assert.Empty(SpecialPlacement.Parse(null));
        Assert.Empty(SpecialPlacement.Parse(Values()));
    }

    // ------------------------------------------------------------------ 可破坏物件的槽位

    private static ScriptLine[] Doc(int[] ints, string[] strings, string[] motions)
    {
        var lines = new List<ScriptLine>();
        if (ints != null)
        {
            lines.Add(new ScriptLine("[int data]", ints.Select(I).ToArray()));
        }
        if (strings != null)
        {
            lines.Add(new ScriptLine("[string data]", strings.Select(S).ToArray()));
        }
        if (motions != null)
        {
            lines.Add(new ScriptLine("[etc motion]", motions.Select(S).ToArray()));
        }
        return lines.ToArray();
    }

    [Fact]
    public void BreakableDataReadsHitsDestroyFlagSoundsAndResolvedReactionMotions()
    {
        BreakableData data = BreakableData.Read("passiveobject/mapobject/breakableobject/crate.obj", Doc(
            new[] { 1, 3, 1, 0, 0, 30, 1 },
            new[] { "", "", "Particle/A.ptl", "", "HIT_KEY", "CRASH_KEY" },
            new[] { "animation/hit0.ani", "", "Animation\\Hit2.ani" }))!;
        Assert.Equal(3, data.HitsNeeded);
        Assert.True(data.DestroyedWhenDone);
        Assert.Equal("HIT_KEY", data.HitSound);
        Assert.Equal("CRASH_KEY", data.DestroySound);
        Assert.Equal("passiveobject/mapobject/breakableobject/animation/hit0.ani", data.ReactionMotion(0));
        Assert.Null(data.ReactionMotion(1));                                        // 空槽
        Assert.Equal("passiveobject/mapobject/breakableobject/animation/hit2.ani", data.ReactionMotion(2));
        Assert.Null(data.ReactionMotion(3));
        Assert.Null(data.ReactionMotion(-1));
    }

    [Fact]
    public void AnActionObjectThatIsNotDestroyedKeepsStandingAndEmptySoundSlotsAreNull()
    {
        BreakableData data = BreakableData.Read("p/q/x.obj", Doc(new[] { 1, 2, 0, 5, 0, 75, 1 }, new[] { "", "damage.ptl", "", "", "", "" }, null))!;
        Assert.Equal(2, data.HitsNeeded);
        Assert.False(data.DestroyedWhenDone);
        Assert.Null(data.HitSound);
        Assert.Null(data.DestroySound);
        Assert.Empty(data.ReactionMotions);
    }

    [Fact]
    public void ShortOrOddIntDataFallsBackToOneHitAndGrassLikeTwoNumbersNeverDisappear()
    {
        BreakableData grass = BreakableData.Read("p/g.obj", Doc(new[] { 42, 1 }, null, null))!;
        Assert.Equal(1, grass.HitsNeeded);
        Assert.False(grass.DestroyedWhenDone);                                       // 没有槽 2:不消失
        Assert.Null(grass.HitSound);

        BreakableData empty = BreakableData.Read("p/e.obj", Doc(new int[0], null, null))!;
        Assert.Equal(1, empty.HitsNeeded);
        Assert.False(empty.DestroyedWhenDone);

        BreakableData zeroHits = BreakableData.Read("p/z.obj", Doc(new[] { 0, 0, 1 }, null, null))!;
        Assert.Equal(1, zeroHits.HitsNeeded);                                        // 0 与负数按 1
        Assert.True(zeroHits.DestroyedWhenDone);
        Assert.Equal(1, new BreakableData(new[] { 1, -4, 1 }, null, null).HitsNeeded);
    }

    [Fact]
    public void WithoutIntDataTheObjectIsNotBreakable()
    {
        Assert.Null(BreakableData.Read("p/n.obj", Doc(null, new[] { "a" }, null)));
        Assert.Null(BreakableData.Read("p/n.obj", null));
    }

    [Fact]
    public void KeyedStringDataKeepsItsSlotPositionInsteadOfShiftingTheOthers()
    {
        var line = new ScriptLine("[string data]", new[]
        {
            S(""), new ScriptValue(ScriptValueKind.Keyed, 9, "string_data_key"), S("x"),
        });
        BreakableData data = BreakableData.Read("p/k.obj", new[] { new ScriptLine("[int data]", new[] { I(1) }), line })!;
        Assert.Equal(new[] { "", "string_data_key", "x" }, data.Strings.ToArray());
    }

    // ------------------------------------------------------------------ 地图与房间

    [Fact]
    public void TheMapReadsItsSpecialObjectsInOrderWithTheirActions()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = RoomMap.Read(assets, RoomFixture.SpecialMapPath);
        Assert.Equal(4, map.SpecialObjects.Count);
        Assert.Equal(new[] { RoomFixture.CrateId, RoomFixture.CrateId, RoomFixture.TreeId, RoomFixture.GrassId }, map.SpecialObjects.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { 2, 0, 1, 1 }, map.SpecialObjects.Select(p => p.Actions.Count).ToArray());
        Assert.Equal(new[] { 0, -1, -1, 1 }, map.SpecialObjects.Select(p => p.ItemIndex).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, map.SpecialObjects.Select(p => p.Ordinal).ToArray());
        Assert.Equal(SpecialActionKind.Quest, map.SpecialObjects[2].Actions[0].Kind);

        // 没有这个标签的地图:空列表
        Assert.Empty(RoomMap.Read(assets, RoomFixture.MapPath).SpecialObjects);
    }

    [Fact]
    public void TheRoomLoadsSpecialObjectsAfterThePlainOnesAndOnlyTheBreakableFolderCarriesBreakableData()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomContent content = RoomContent.Load(assets, RoomMap.Read(assets, RoomFixture.SpecialMapPath));
        // 四项里的树不在可破坏物件目录里(它带的是任务动作,这一版没有对应的机制):只摆可破坏物件,所以树不进房间,其余三项按顺序进、序号照旧
        Assert.Equal(3, content.Objects.Count);
        Assert.All(content.Objects, o =>
        {
            Assert.NotNull(o.Special);
            Assert.NotNull(o.Def.Breakable);
        });
        Assert.Equal(new[] { 0, 1, 3 }, content.Objects.Select(o => o.Special!.Ordinal).ToArray());

        RoomObject crate = content.Objects[0];
        Assert.Equal((100, 60), (crate.X, crate.Y));
        Assert.NotNull(crate.Def.Breakable);
        Assert.Equal(2, crate.Def.Breakable!.HitsNeeded);
        Assert.True(crate.Def.Breakable.DestroyedWhenDone);
        Assert.Equal("CRATE_HIT", crate.Def.Breakable.HitSound);
        Assert.Equal(3, content.Objects[2].Special!.Ordinal);                       // 树(序号 2)被跳过,草丛排在第三个

        // 普通地图里的物件没有特殊项
        RoomContent plain = RoomContent.Load(assets, RoomMap.Read(assets, RoomFixture.MapPath));
        Assert.All(plain.Objects, o => Assert.Null(o.Special));
    }

    [Fact]
    public void ADestroyedObjectIsNeitherDrawnNorBlocking()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomContent content = RoomContent.Load(assets, RoomMap.Read(assets, RoomFixture.SpecialMapPath));
        RoomObject crate = content.Objects[0];
        var projection = new TownProjection(0, 200);
        Assert.True(crate.Blocks(100, 55));
        Assert.NotNull(crate.SpriteAt(assets, projection, 0));
        var walk = new RoomWalkArea(content.Map, content.Objects);
        Assert.False(walk.Contains(100, 55));                                       // 通行表上能走(y 在 24 到 60 之间),但被木箱挡住
        int CratesDrawn() => RoomScene.Build(assets, content, 0, 200, 0, null).Count(s => s.AnimationPath.EndsWith("animation/crate.ani", StringComparison.Ordinal));
        Assert.Equal(2, CratesDrawn());                                             // 地图上摆了两个木箱

        crate.Destroy();
        Assert.True(crate.Destroyed);
        Assert.False(crate.Blocks(100, 55));
        Assert.Null(crate.SpriteAt(assets, projection, 0));
        Assert.False(crate.PlayReaction(assets, 0));                                // 打碎了就不再受击
        Assert.True(walk.Contains(100, 55));                                        // 同一个通行表对象:不用重建
        Assert.Equal(1, CratesDrawn());                                             // 只剩没打碎的那个
    }

    [Fact]
    public void AReactionReplacesTheBaseMotionUntilItEndsThenTheBaseMotionReturns()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomContent content = RoomContent.Load(assets, RoomMap.Read(assets, RoomFixture.SpecialMapPath));
        RoomObject crate = content.Objects[0];
        var projection = new TownProjection(0, 200);
        SceneSprite basic = crate.SpriteAt(assets, projection, 0)!;
        Assert.EndsWith("animation/crate.ani", basic.AnimationPath);
        Assert.False(crate.Reacting);

        Assert.False(crate.PlayReaction(assets, 1));                                // 槽 1 是空的
        Assert.False(crate.PlayReaction(assets, 7));                                // 越界
        Assert.False(crate.Reacting);
        Assert.True(crate.PlayReaction(assets, 0));
        Assert.True(crate.Reacting);
        SceneSprite hit = crate.SpriteAt(assets, projection, 12345)!;               // 受击动画从自己的计时走,不看外面给的时间
        Assert.EndsWith("animation/crate-hit.ani", hit.AnimationPath);
        Assert.Equal((basic.ScreenX + 3, basic.ScreenY + 4), (hit.ScreenX, hit.ScreenY));

        crate.Advance(60);
        Assert.True(crate.Reacting);
        crate.Advance(60);                                                          // 动画共 100 毫秒、不循环:播完回到基础动画
        Assert.False(crate.Reacting);
        Assert.EndsWith("animation/crate.ani", crate.SpriteAt(assets, projection, 0)!.AnimationPath);

        // 再受击会从头播;销毁时受击动画一并取消
        Assert.True(crate.PlayReaction(assets, 0));
        crate.Destroy();
        Assert.False(crate.Reacting);
    }
}
