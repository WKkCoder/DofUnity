using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 城镇定义(.twn)、出口([town movable area])、出口触发器、城镇图册(落脚点与路线)与进区域的镜头(证据 E-095)。
/// 数据全是合成的(标签是格式本身的名字,数值与路径是随便编的),不含任何原版内容。
/// </summary>
public class TownDefTests
{
    private static ScriptLine Line(string tag, params object[] values)
    {
        var list = new List<ScriptValue>();
        foreach (object v in values)
        {
            list.Add(v is int i ? new ScriptValue(ScriptValueKind.Int, i, null) : new ScriptValue(ScriptValueKind.String, 0, (string)v));
        }
        return new ScriptLine(tag, list.ToArray());
    }

    private static ScriptLine Area(params object[] values) => Line("[area]", values);

    private static TownDef SampleTown()
    {
        return TownDef.Parse("town/demo.twn", new[]
        {
            Line("[entering title]", "Title/Demo.ani"),
            Line("[cutscene image]", "Map/CutScene/Demo.img", 2),
            new ScriptLine("[name]", new[] { new ScriptValue(ScriptValueKind.Keyed, 14, "name_demo") }),
            Area(0, "Demo/Main.map", "[minimap rect]", 1, 2, 30, 40, "[/minimap rect]", "[normal]"),
            Line("[/area]"),
            Area(1, "Demo/Gate.map", "[minimap rect]", 5, 6, 7, 8, "[/minimap rect]", "[gate]", 111, 222),
            Line("[/area]"),
            Area(2, "Demo/Dg.map", "[dungeon gate]", 3),
            Line("[/area]"),
            Area(3, "Demo/Shop.map", "[use private store]", 1, "[normal]"),
            Line("[/area]"),
            Area(4, "Demo/Safe.map", "[safe zone against b]"),
            Line("[limit level]", 5),
        })!;
    }

    [Fact]
    public void ATownReadsTitleCutsceneNameAndEveryArea()
    {
        TownDef def = SampleTown();
        Assert.Equal("town/title/demo.ani", def.EnteringTitle);                  // 相对 town/、小写
        Assert.Equal("sprite/map/cutscene/demo.img", def.CutsceneImage);        // 图包名
        Assert.Equal(2, def.CutsceneFrame);
        Assert.Equal("name_demo", def.NameKey);
        Assert.Equal(5, def.Areas.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, def.Areas.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void AnAreaReadsItsKindParametersMiniMapAndPrivateStoreFlag()
    {
        TownDef def = SampleTown();

        TownArea main = def.FindArea(0)!;
        Assert.Equal(TownAreaKind.Normal, main.Kind);
        Assert.Equal(new[] { 1, 2, 30, 40 }, main.MiniMapRect);
        Assert.Empty(main.Params);                                                // 小地图矩形的四个数不算类型参数

        TownArea gate = def.FindArea(1)!;
        Assert.Equal(TownAreaKind.Gate, gate.Kind);
        Assert.True(gate.TryGetGatePoint(out int gx, out int gy));
        Assert.Equal((111, 222), (gx, gy));

        TownArea dg = def.FindArea(2)!;
        Assert.Equal(TownAreaKind.DungeonGate, dg.Kind);
        Assert.Equal(3, dg.WorldMapId);
        Assert.Null(dg.MiniMapRect);
        Assert.False(dg.TryGetGatePoint(out _, out _));
        Assert.Equal(-1, main.WorldMapId);

        TownArea shop = def.FindArea(3)!;
        Assert.True(shop.UsesPrivateStore);
        Assert.Equal(TownAreaKind.Normal, shop.Kind);
        Assert.Empty(shop.Params);                                                // 开关后面的数是开关自己的值

        Assert.Equal(TownAreaKind.SafeZoneB, def.FindArea(4)!.Kind);
        Assert.Null(def.FindArea(99));
    }

    [Fact]
    public void AnUnrelatedScriptIsNotATownAndAnAreaWithoutMapOrIdIsDropped()
    {
        Assert.Null(TownDef.Parse("x", new[] { Line("[something else]", 1) }));
        Assert.Null(TownDef.Parse("x", null!));
        TownDef def = TownDef.Parse("x", new[] { Area(7, "[normal]"), Area("A.map", "[normal]"), Area(8, "B.map", "[normal]") })!;
        Assert.Single(def.Areas);
        Assert.Equal(8, def.Areas[0].Id);
    }

    [Fact]
    public void TheAreaMapPathGainsMapPrefixAndPrefersTheParenRVariantWhenItExists()
    {
        Assert.Equal("map/demo/main.map", TownDef.ResolveMapPath("Demo\\Main.map", _ => false));
        Assert.Equal("map/demo/(r)gate.map", TownDef.ResolveMapPath("Demo/Gate.map", p => p == "map/demo/(r)gate.map"));
        Assert.Equal("map/demo/gate.map", TownDef.ResolveMapPath("Demo/Gate.map", p => p == "map/demo/gate.map"));
        Assert.Null(TownDef.ResolveMapPath(null!, _ => true));
    }

    [Fact]
    public void ATownListMapsIdsToTownScriptsUnderTheTownDirectory()
    {
        Dictionary<int, string> list = WorldMapDef.ParseList(new[]
        {
            new ScriptLine(null, new[]
            {
                new ScriptValue(ScriptValueKind.Int, 1, null), new ScriptValue(ScriptValueKind.String, 0, "Demo.twn"),
                new ScriptValue(ScriptValueKind.Int, 2, null), new ScriptValue(ScriptValueKind.String, 0, "Other.twn"),
            }),
        }, TownDef.Directory);
        Assert.Equal("town/demo.twn", list[1]);
        Assert.Equal("town/other.twn", list[2]);
    }

    [Fact]
    public void ExitsAreSixNumberRecordsAndTheirTargetsHaveThreeMeanings()
    {
        List<TownExit> exits = MapScript.ParseTownExits(new[]
        {
            Line("[town movable area]", 10, 20, 30, 40, 1, 2, 50, 60, 70, 80, -1, -1, 90, 91, 92, 93, -2, -2, 5, 6),   // 末尾 2 个数凑不满一条
            Line("[virtual movable area]", 0, 0, 5, 5),
        });
        Assert.Equal(3, exits.Count);
        Assert.Equal((10, 20, 30, 40), (exits[0].Rect.X, exits[0].Rect.Y, exits[0].Rect.Width, exits[0].Rect.Height));
        Assert.True(exits[0].HasTarget);
        Assert.False(exits[0].IsDungeonGate);
        Assert.Equal((1, 2), (exits[0].Town, exits[0].Area));
        Assert.True(exits[1].IsDungeonGate);
        Assert.False(exits[1].HasTarget);
        Assert.False(exits[2].HasTarget);                                         // (-2, -2):没有去处
        Assert.False(exits[2].IsDungeonGate);
        Assert.Empty(MapScript.ParseTownExits(new[] { Line("[sound]", "x") }));
    }

    // ---- 出口触发器 ----

    private static readonly WorldRect Walk = new WorldRect(0, 100, 1000, 200);       // 可走外包矩形:x 0..1000,y 100..300

    private static TownExit ExitAt(int x, int y, int w, int h, int town = 1, int area = 0) => new TownExit(new WorldRect(x, y, w, h), town, area);

    [Fact]
    public void TheOutwardDirectionIsTheBoundsEdgeNearestTheExitRectangle()
    {
        TownExitTracker.Outward(ExitAt(-10, 120, 30, 100).Rect, Walk, out int dx, out int dy);
        Assert.Equal((-1, 0), (dx, dy));                                          // 贴左边
        TownExitTracker.Outward(ExitAt(980, 120, 30, 100).Rect, Walk, out dx, out dy);
        Assert.Equal((1, 0), (dx, dy));
        TownExitTracker.Outward(ExitAt(400, 90, 100, 20).Rect, Walk, out dx, out dy);
        Assert.Equal((0, -1), (dx, dy));                                          // 贴上边
        TownExitTracker.Outward(ExitAt(400, 290, 100, 20).Rect, Walk, out dx, out dy);
        Assert.Equal((0, 1), (dx, dy));
    }

    [Fact]
    public void AnExitFiresWhenTheHeroStandsInsideAndPushesOutwardNotOtherwise()
    {
        var tracker = new TownExitTracker(new[] { ExitAt(980, 120, 30, 100) }, Walk);   // 右边出口
        Assert.Null(tracker.Step(500, 150, 1, 0));                                // 不在矩形里
        Assert.Null(tracker.Step(990, 150, 0, 0));                                // 在里面但没推
        Assert.Null(tracker.Step(990, 150, -1, 0));                               // 推的是反方向
        Assert.Null(tracker.Step(990, 150, 0, -1));                               // 推的是垂直方向
        TownExit? taken = tracker.Step(990, 150, 1, 1);                           // 外面方向 + 斜向也算
        Assert.True(taken.HasValue);
        Assert.Equal(1, taken!.Value.Town);
    }

    [Fact]
    public void AnExitTheHeroArrivedInstantlyStaysDisarmedUntilTheHeroLeavesIt()
    {
        var tracker = new TownExitTracker(new[] { ExitAt(400, 90, 100, 30) }, Walk);    // 上边出口(y 90..120)
        tracker.Arrive(450, 105);                                                 // 落在出口里
        Assert.Null(tracker.Step(450, 105, 0, -1));                               // 一落地就推也不触发
        Assert.Null(tracker.Step(450, 105, 0, -1));
        Assert.Null(tracker.Step(450, 150, 0, 1));                                // 走出矩形:重新生效
        Assert.True(tracker.Step(450, 110, 0, -1).HasValue);                      // 再走进来、推出去:触发
    }

    [Fact]
    public void ExitsWithoutADestinationAreIgnoredAndTheDungeonGateExitIsKept()
    {
        var tracker = new TownExitTracker(new[] { ExitAt(980, 120, 30, 100, -2, -2), ExitAt(-10, 120, 30, 100, -1, -1) }, Walk);
        Assert.Single(tracker.Exits);
        Assert.Null(tracker.Step(990, 150, 1, 0));                                // 无去处的出口不触发
        TownExit? gate = tracker.Step(5, 150, -1, 0);
        Assert.True(gate!.Value.IsDungeonGate);
        tracker.OutwardOf(gate.Value, out int dx, out int dy);
        Assert.Equal((-1, 0), (dx, dy));
    }

    [Fact]
    public void WithoutAWalkableBoundsAnyDirectionKeyInsideTheExitCounts()
    {
        var tracker = new TownExitTracker(new[] { ExitAt(0, 0, 50, 50) }, null);
        Assert.Null(tracker.Step(10, 10, 0, 0));
        Assert.True(tracker.Step(10, 10, 0, 1).HasValue);
    }

    // ---- 图册:落脚点与路线 ----

    /// <summary>三块区域排成一条:1/0(左)—1/1(中)—1/2(右,地下城入口);再加另一座城镇 2/0 通到 1/0。</summary>
    private static TownAtlas SampleAtlas()
    {
        var scripts = new Dictionary<string, ScriptLine[]>
        {
            ["town/a.twn"] = new[]
            {
                Area(0, "A/Left.map", "[normal]"), Area(1, "A/Mid.map", "[gate]", 400, 220), Area(2, "A/Right.map", "[dungeon gate]", 1),
            },
            ["town/b.twn"] = new[] { Area(0, "B/Only.map", "[normal]") },
            // 左:可走 0..400 × 100..300;右出口 (390..420, 120..280) → 1/1;左出口 (-20..10) → 2/0
            ["map/a/left.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 400, 200),
                Line("[town movable area]", 390, 120, 30, 160, 1, 1, -20, 120, 30, 160, 2, 0),
            },
            // 中(网关):可走 100..500 × 150..300;左出口 → 1/0;右出口 → 1/2
            ["map/a/(r)mid.map"] = new[]
            {
                Line("[virtual movable area]", 100, 150, 400, 150),
                Line("[town movable area]", 90, 160, 20, 130, 1, 0, 490, 160, 20, 130, 1, 2),
            },
            // 右(入口区域):可走 0..300 × 100..300;左出口 → 1/1;右出口 = 世界地图
            ["map/a/right.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", -10, 120, 20, 160, 1, 1, 290, 120, 20, 160, -1, -1),
            },
            ["map/b/only.map"] = new[]
            {
                Line("[virtual movable area]", 0, 0, 200, 200),
                Line("[town movable area]", 190, 20, 20, 160, 1, 0),
            },
        };
        return new TownAtlas(new Dictionary<int, string> { [1] = "town/a.twn", [2] = "town/b.twn" },
            p => scripts.TryGetValue(p, out ScriptLine[] doc) ? doc : null, p => p == "map/a/(r)mid.map");
    }

    [Fact]
    public void TheAtlasReadsAnAreaMapWithTheParenRVariantItsWalkableBoundsAndExits()
    {
        TownAtlas atlas = SampleAtlas();
        TownAreaInfo mid = atlas.GetArea(1, 1)!;
        Assert.Equal("map/a/(r)mid.map", mid.MapPath);
        Assert.Equal(TownAreaKind.Gate, mid.Kind);
        Assert.Equal(2, mid.Exits.Count);
        Assert.Equal((100, 150, 400, 150), (mid.Bounds!.Value.X, mid.Bounds.Value.Y, mid.Bounds.Value.Width, mid.Bounds.Value.Height));
        Assert.Null(atlas.GetArea(1, 9));
        Assert.Null(atlas.GetArea(7, 0));
        Assert.True(atlas.GetArea(1, 2)!.TryFindDungeonGate(out TownExit gate));
        Assert.Equal(290, gate.Rect.X);
        Assert.True(mid.TryFindExitTo(1, 2, out _));
        Assert.False(mid.TryFindExitTo(5, 5, out _));
    }

    [Fact]
    public void ArrivingThroughAnExitLandsAtTheCenterOfTheExitThatLeadsBackClampedIntoTheWalkableArea()
    {
        TownAtlas atlas = SampleAtlas();
        // 从 1/1 走到 1/0:1/0 里通往 1/1 的出口矩形 (390,120,30,160) 中心 (405, 200) 超出可走区域的右边界 400 → 挪回 (400, 200)
        TownArrival a = atlas.Arrive(new TownPlace(1, 0, TownEntry.Exit, 1, 1, facingLeft: true));
        Assert.Equal((400.0, 200.0), (a.X, a.Y));
        Assert.True(a.FacingLeft);                                                // 朝向是主角带过来的
        // 从 1/1 走到 1/2:1/2 里通往 1/1 的出口中心 (0, 200) → 在可走区域边界上
        TownArrival b = atlas.Arrive(new TownPlace(1, 2, TownEntry.Exit, 1, 1, facingLeft: false));
        Assert.Equal((0.0, 200.0), (b.X, b.Y));
        Assert.False(b.FacingLeft);
        Assert.True(atlas.Arrive(new TownPlace(1, 2, TownEntry.Exit, 1, 1, facingLeft: true)).FacingLeft);   // 带过来什么就是什么
    }

    [Fact]
    public void ReturningFromADungeonLandsAtTheDungeonGateExitAndLoginUsesTheGatePointOrTheBoundsCenter()
    {
        TownAtlas atlas = SampleAtlas();
        TownArrival back = atlas.Arrive(new TownPlace(1, 2, TownEntry.Return));
        Assert.Equal((300.0, 200.0), (back.X, back.Y));                           // 世界地图出口 (290..310) 的中心 300,在可走区域边界上
        Assert.True(back.FacingLeft);                                             // 从副本回来:朝向区域里面(落在右半边,朝左)

        TownArrival login = atlas.Arrive(new TownPlace(1, 1, TownEntry.Login));
        Assert.Equal((400.0, 220.0), (login.X, login.Y));                         // 网关:[gate] 给的位置
        Assert.False(login.FacingLeft);

        TownArrival plain = atlas.Arrive(new TownPlace(1, 0, TownEntry.Login));
        Assert.Equal((200.0, 200.0), (plain.X, plain.Y));                         // 没有 [gate]:可走区域外包矩形的中心
    }

    [Fact]
    public void AnArrivalWithoutAMatchingReturnExitFallsBackToTheGatePoint()
    {
        TownAtlas atlas = SampleAtlas();
        TownArrival a = atlas.Arrive(new TownPlace(1, 1, TownEntry.Exit, 9, 9));  // 1/1 里没有通往 9/9 的出口
        Assert.Equal((400.0, 220.0), (a.X, a.Y));
        Assert.Throws<DnfFormatException>(() => atlas.Arrive(new TownPlace(1, 9)));
    }

    [Fact]
    public void TheRouteToTheDungeonGateGoesThroughTheShortestChainOfExits()
    {
        TownAtlas atlas = SampleAtlas();
        Assert.True(atlas.TryRouteToDungeonGate(new TownPlace(2, 0), out TownExit first));
        Assert.Equal((1, 0), (first.Town, first.Area));                           // 2/0 → 1/0 → 1/1 → 1/2:第一步去 1/0
        Assert.True(atlas.TryRouteToDungeonGate(new TownPlace(1, 0), out first));
        Assert.Equal((1, 1), (first.Town, first.Area));
        Assert.True(atlas.TryRouteToDungeonGate(new TownPlace(1, 1), out first));
        Assert.Equal((1, 2), (first.Town, first.Area));
        Assert.True(atlas.TryRouteToDungeonGate(new TownPlace(1, 2), out first));
        Assert.True(first.IsDungeonGate);                                         // 已经在入口区域:下一步就是世界地图出口
    }

    [Fact]
    public void TheDungeonGateAreaIsFoundByItsWorldMapExit()
    {
        TownAtlas atlas = SampleAtlas();
        TownPlace? gate = atlas.FindDungeonGate(1);
        Assert.Equal((1, 2, TownEntry.Return), (gate!.Value.Town, gate.Value.Area, gate.Value.Entry));
        Assert.Null(atlas.FindDungeonGate(2));                                    // 城镇 2 里没有
        Assert.False(atlas.TryRouteToDungeonGate(new TownPlace(9, 9), out _));
    }

    // ---- 镜头 ----

    [Fact]
    public void TheArrivalCameraStartsAtTheMinimumThenFollowsTheHeroIntoTheDeadZone()
    {
        TownCamera c = TownCamera.AtArrival(0, 96, 474);                          // 主角离最小镜头很远:追到死区右缘
        Assert.Equal(24, c.X);                                                    // 474 − 450
        TownCamera d = TownCamera.AtArrival(0, 1000, 380);                        // 本来就在死区里(屏幕 350 到 450):镜头不动
        Assert.Equal(0, d.X);
        TownCamera e = TownCamera.AtArrival(0, 96, 5000);                         // 超出最大值:夹住
        Assert.Equal(96, e.X);
        TownCamera f = TownCamera.AtArrival(40, 800, 100);                        // 最小值不为 0:从最小值起
        Assert.Equal(40, f.X);
    }
}
