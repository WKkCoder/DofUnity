using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 头顶的名字(M30,证据 E-122):世界里的名字文字表(<see cref="WorldTexts"/>、<see cref="NpcNames"/>)、主角名牌与 NPC 名字的版式(<see cref="NamePlates"/>)、
/// 怪物目标血条窗口里的名字。文字表与字体都是合成的(方块字体只有实心块,没有任何真实字形),期望值是手算。
/// </summary>
public class NamePlateTests
{
    // ------------------------------------------------------------------ 工具

    private static StrLibrary Library(params (string Path, string Text)[] files)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string text) in files)
        {
            map[path] = Encoding.ASCII.GetBytes(text);
        }
        return new StrLibrary(p => map.TryGetValue(p, out byte[] data) ? data : null);
    }

    private static WorldTexts Texts()
    {
        return new WorldTexts(Library(
            ("npc/npc.kor.str", "name_1>Alice\r\nfield_1>Smith Alice\r\nname_2>Bob\r\nfield_2>\r\nname_3>Carol 100%%\r\n"),
            ("monster/monster.kor.str", "mob_1>Goblin\r\nmob_2>\r\n")));
    }

    private static ScriptLine Line(string tag, int kind, string key)
    {
        return new ScriptLine(tag, new[] { new ScriptValue(ScriptValueKind.Keyed, kind, key) });
    }

    private static OverlayArt Art() => new OverlayArt { Fonts = new BlockFonts() };

    private static SpriteFrame Solid(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        Array.Fill(rgba, (byte)255);
        return new SpriteFrame(w, h, 0, 0, w, h, rgba);
    }

    private static SceneSprite NpcSprite(int id, int x, int y, int w = 40, int h = 60)
    {
        return new SceneSprite(Solid(w, h), x, y, 100, id, "npc/test.ani", 0);
    }

    private static NpcNames Names(Dictionary<int, string> byId)
    {
        // 每个 NPC 的脚本:有名字的写一行 [field name](键 field_<id>),文字表里按 id 配好
        var lines = new Dictionary<int, ScriptLine[]>();
        foreach (int id in byId.Keys)
        {
            lines[id] = new[] { Line("[field name]", 8, "plate_" + id) };
        }
        var sb = new StringBuilder();
        foreach (KeyValuePair<int, string> kv in byId)
        {
            sb.Append("plate_").Append(kv.Key).Append('>').Append(kv.Value).Append("\r\n");
        }
        var texts = new WorldTexts(Library(("npc/npc.kor.str", sb.ToString())));
        return new NpcNames(id => lines.TryGetValue(id, out ScriptLine[] l) ? l : null, texts);
    }

    // ------------------------------------------------------------------ 文字表

    [Fact]
    public void ATextReferenceIsTheKindPlusTheKeyAndUnknownThingsGiveNull()
    {
        WorldTexts t = Texts();
        Assert.Equal("Alice", t.Text(StrLibrary.KindNpc, "name_1"));
        Assert.Equal("Goblin", t.Text(StrLibrary.KindMonster, "mob_1"));
        Assert.Null(t.Text(StrLibrary.KindNpc, "mob_1"));                      // 种类不对:去的是另一张表
        Assert.Null(t.Text(StrLibrary.KindNpc, "nope"));
        Assert.Null(t.Text(StrLibrary.KindNpc, ""));
        Assert.Null(t.Text(StrLibrary.KindNpc, null));
        Assert.Null(t.Text(99, "name_1"));                                     // 不认识的种类
        Assert.Null(t.Text(StrLibrary.KindMonster, "mob_2"));                  // 空值当作没有
        Assert.Equal("Carol 100%", t.Text(StrLibrary.KindNpc, "name_3"));      // %% 还原成 %
        Assert.Throws<ArgumentNullException>(() => new WorldTexts(null!));
    }

    [Fact]
    public void AMonsterNameComesFromTheMobsNameKeyInTheMonsterTable()
    {
        WorldTexts t = Texts();
        Assert.Equal("Goblin", t.MonsterName("mob_1"));
        Assert.Null(t.MonsterName((string)null!));
        Assert.Null(t.MonsterName("name_1"));                                  // 怪物表里没有这个键
        var def = new MonsterDef(7, "monster/test.mob", new[] { Line("[name]", StrLibrary.KindMonster, "mob_1") });
        Assert.Equal("Goblin", t.MonsterName(def));
        Assert.Null(t.MonsterName((MonsterDef)null!));
    }

    [Fact]
    public void TheFieldNameWinsOverTheNameAndFallsBackWhenItIsMissingOrEmpty()
    {
        WorldTexts t = Texts();
        Assert.Equal("Smith Alice", t.NpcPlateName(new[] { Line("[name]", 8, "name_1"), Line("[field name]", 8, "field_1") }));   // 标签的先后无关
        Assert.Equal("Alice", t.NpcPlateName(new[] { Line("[name]", 8, "name_1") }));                                             // 没有 [field name]
        Assert.Equal("Bob", t.NpcPlateName(new[] { Line("[name]", 8, "name_2"), Line("[field name]", 8, "field_2") }));           // [field name] 的文字是空的
        Assert.Equal("Alice", t.NpcPlateName(new[] { Line("[name]", 8, "name_1"), Line("[field name]", 8, "gone") }));            // 键不在表里
        Assert.Null(t.NpcPlateName(new[] { Line("[level]", 8, "name_1") }));
        Assert.Null(t.NpcPlateName(new ScriptLine[0]));
        Assert.Null(t.NpcPlateName(null!));

        // 写成普通字符串的值不是文字引用
        var plain = new ScriptLine("[name]", new[] { new ScriptValue(ScriptValueKind.String, 0, "name_1") });
        Assert.Null(t.NpcPlateName(new[] { plain }));
    }

    [Fact]
    public void ChineseTextsAreDecodedFromTheGbEncodedTable()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("name_9>").Concat(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }).Concat(new byte[] { 0x0D, 0x0A }).ToArray();   // "你好"
        var t = new WorldTexts(new StrLibrary(p => p == "npc/npc.kor.str" ? bytes : null));
        Assert.Equal("\u4F60\u597D", t.Text(StrLibrary.KindNpc, "name_9"));
    }

    // ------------------------------------------------------------------ NPC 编号 → 名字

    [Fact]
    public void ANpcNameIsLookedUpByIdAndCachedIncludingTheMissingOnes()
    {
        var calls = new List<int>();
        WorldTexts texts = Texts();
        var names = new NpcNames(id =>
        {
            calls.Add(id);
            return id == 1 ? new[] { Line("[field name]", 8, "field_1") } : null;
        }, texts);

        Assert.Equal("Smith Alice", names.PlateOf(1));
        Assert.Equal("Smith Alice", names.PlateOf(1));
        Assert.Null(names.PlateOf(2));                                          // 没有脚本
        Assert.Null(names.PlateOf(2));
        Assert.Equal(new[] { 1, 2 }, calls.ToArray());                          // 每个编号只读一次脚本

        Assert.Throws<ArgumentNullException>(() => new NpcNames(null!, texts));
        Assert.Throws<ArgumentNullException>(() => new NpcNames(_ => null, null!));
        Assert.Throws<ArgumentNullException>(() => NpcNames.Create(null!, texts));
    }

    // ------------------------------------------------------------------ 名牌的文字帧

    [Fact]
    public void TheHeroTextIsLvDotLevelAndTheNameWhenThereIsOne()
    {
        Assert.Equal("Lv.12 Hero", NamePlates.HeroText(12, "Hero"));
        Assert.Equal("Lv.3", NamePlates.HeroText(3, null));
        Assert.Equal("Lv.3", NamePlates.HeroText(3, ""));
    }

    [Fact]
    public void ATextFrameNeedsFontsAndTextAndIsCachedPerParameters()
    {
        Assert.Null(NamePlates.TextFrame(new OverlayArt(), "Name", NamePlates.NpcFill));       // 没有字体
        Assert.Null(NamePlates.TextFrame(null!, "Name", NamePlates.NpcFill));
        OverlayArt art = Art();
        Assert.Null(NamePlates.TextFrame(art, "", NamePlates.NpcFill));
        Assert.Null(NamePlates.TextFrame(art, null!, NamePlates.NpcFill));

        SpriteFrame a = NamePlates.TextFrame(art, "Name", NamePlates.NpcFill)!;
        Assert.NotNull(a);
        Assert.Same(a, NamePlates.TextFrame(art, "Name", NamePlates.NpcFill));
        Assert.NotSame(a, NamePlates.TextFrame(art, "Name", NamePlates.HeroFill));

        // 实心块里有字色(0xE6C89B),四周一圈是描边色(0x333333)
        bool hasFill = false, hasOutline = false;
        for (int i = 0; i < a.Rgba.Length; i += 4)
        {
            if (a.Rgba[i + 3] == 255 && a.Rgba[i] == 0xE6 && a.Rgba[i + 1] == 0xC8 && a.Rgba[i + 2] == 0x9B)
            {
                hasFill = true;
            }
            if (a.Rgba[i + 3] == 255 && a.Rgba[i] == 0x33 && a.Rgba[i + 1] == 0x33 && a.Rgba[i + 2] == 0x33)
            {
                hasOutline = true;
            }
        }
        Assert.True(hasFill && hasOutline);
    }

    [Fact]
    public void TheInkBottomIsTheLastNonEmptyRowMinusTheOutlineAndTheFontSizeIsTwelvePixels()
    {
        var rgba = new byte[10 * 10 * 4];
        for (int y = 1; y <= 6; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                rgba[(y * 10 + x) * 4 + 3] = 255;
            }
        }
        Assert.Equal(5, NamePlates.InkBottom(new SpriteFrame(10, 10, 0, 0, 10, 10, rgba)));                 // 最后一行有像素是第 6 行,去掉 1 像素描边
        Assert.Equal(9, NamePlates.InkBottom(new SpriteFrame(10, 10, 0, 0, 10, 10, new byte[400])));        // 空帧:最后一行
        Assert.Throws<ArgumentNullException>(() => NamePlates.InkBottom(null!));

        OverlayArt art = Art();
        NamePlates.TextFrame(art, "Name", NamePlates.NpcFill);
        Assert.Equal(new[] { 12 }, ((BlockFonts)art.Fonts).Requested.Distinct().ToArray());                  // 要的是 12 像素的字号
    }

    // ------------------------------------------------------------------ 主角名牌

    [Fact]
    public void TheHeroPlateIsCenteredOnTheFeetWithItsInkBottomRowOneHundredTwentyTwoAboveTheGround()
    {
        OverlayArt art = Art();
        var list = new List<SceneSprite>();
        int order = 5;
        Assert.True(NamePlates.AddHero(list, ref order, art, 12, "Hero", 400, 430));
        Assert.Equal(6, order);
        SceneSprite plate = Assert.Single(list);
        Assert.Equal("plate-hero", plate.AnimationPath);
        Assert.Equal(OverlayArt.UiRank, plate.LayerRank);
        Assert.Equal(400 - plate.Frame.Width / 2, plate.ScreenX);
        Assert.Equal(430 - 122, plate.ScreenY + NamePlates.InkBottom(plate.Frame));                         // 最下面一行墨迹在脚底上方 122
        Assert.Same(NamePlates.TextFrame(art, "Lv.12 Hero", NamePlates.HeroFill), plate.Frame);

        // 没有字体:什么也不画,order 不动
        var none = new List<SceneSprite>();
        int o = 0;
        Assert.False(NamePlates.AddHero(none, ref o, new OverlayArt(), 12, "Hero", 400, 430));
        Assert.Empty(none);
        Assert.Equal(0, o);
    }

    // ------------------------------------------------------------------ NPC 名字

    [Fact]
    public void ANpcNameIsCenteredOnTheWorldXOneToTheLeftWithItsInkBottomFiveRowsAboveTheSprite()
    {
        OverlayArt art = Art();
        NpcNames names = Names(new Dictionary<int, string> { [5] = "Alice" });
        // NPC 世界 x 420、镜头 100 → 屏幕 320;精灵 (300, 250) 宽 40
        var scene = new List<SceneSprite> { NpcSprite(9, 0, 0), NpcSprite(5, 300, 250) };
        var npcs = new[] { new TownNpc(5, 420, 300, 0) };
        var list = new List<SceneSprite>();
        int order = 0;
        Assert.Equal(1, NamePlates.AddNpcs(list, ref order, art, names, scene, npcs, 100));
        SceneSprite plate = Assert.Single(list);
        Assert.Equal("plate-npc", plate.AnimationPath);
        Assert.Equal(320 - 1 - plate.Frame.Width / 2, plate.ScreenX);
        Assert.Equal(250 - 5, plate.ScreenY + NamePlates.InkBottom(plate.Frame));
        Assert.Same(NamePlates.TextFrame(art, "Alice", NamePlates.NpcFill), plate.Frame);
        Assert.Equal(1, order);
    }

    [Fact]
    public void NpcsWithoutANameOrASpriteOrWithoutFontsGetNothing()
    {
        OverlayArt art = Art();
        NpcNames names = Names(new Dictionary<int, string> { [5] = "Alice", [6] = "Bob" });
        var scene = new List<SceneSprite> { NpcSprite(5, 300, 250), NpcSprite(7, 100, 100) };
        var npcs = new[]
        {
            new TownNpc(5, 420, 300, 0),     // 有名字有精灵
            new TownNpc(6, 500, 300, 0),     // 有名字但画面里没有它的精灵
            new TownNpc(7, 200, 300, 0),     // 有精灵但没有名字
        };
        var list = new List<SceneSprite>();
        int order = 0;
        Assert.Equal(1, NamePlates.AddNpcs(list, ref order, art, names, scene, npcs, 100));

        Assert.Equal(0, NamePlates.AddNpcs(new List<SceneSprite>(), ref order, new OverlayArt(), names, scene, npcs, 100));   // 没有字体
        Assert.Equal(0, NamePlates.AddNpcs(new List<SceneSprite>(), ref order, art, null!, scene, npcs, 100));
        Assert.Equal(0, NamePlates.AddNpcs(new List<SceneSprite>(), ref order, art, names, null!, npcs, 100));
        Assert.Equal(0, NamePlates.AddNpcs(new List<SceneSprite>(), ref order, art, names, scene, null!, 100));
    }

    [Fact]
    public void TwoNpcsWithTheSameIdAreToldApartByWhichSpriteCoversTheirWorldX()
    {
        var scene = new List<SceneSprite> { NpcSprite(5, 100, 200), NpcSprite(5, 500, 260) };
        var left = new TownNpc(5, 120, 300, 0);
        var right = new TownNpc(5, 520, 300, 0);
        Assert.Same(scene[0], NamePlates.SpriteOf(scene, left, 0));
        Assert.Same(scene[1], NamePlates.SpriteOf(scene, right, 0));
        Assert.Same(scene[0], NamePlates.SpriteOf(scene, new TownNpc(5, 900, 300, 0), 0));                  // 谁也不盖:取第一个同编号的
        Assert.Null(NamePlates.SpriteOf(scene, new TownNpc(6, 120, 300, 0), 0));
        Assert.Null(NamePlates.SpriteOf(scene, new TownNpc(0, 120, 300, 0), 0));                            // 编号 0 不是 NPC
    }

    // ------------------------------------------------------------------ 怪物目标血条窗口里的名字

    private static MonsterActor Mob(DnfAssets assets, MonsterPanelInfo panel)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 340, 100, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, 100, 222.5, 1500, 0) { Panel = panel };
    }

    private static List<SceneSprite> Build(MonsterHpWindow w)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        w.Build(list, ref order);
        return list;
    }

    [Fact]
    public void TheMonsterWindowShowsTheNameRightAfterTheLevelWhenFontsAndTextsAreThere()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var panel = new MonsterPanelInfo(MonsterKind.Normal, WindowSprites.FaceImage, 3, new[] { "[human]" }, 120, "mob_1");

        var w = MonsterHpWindow.For(WindowSprites.Window(), Art())!;
        w.Texts = Texts();
        w.SetTarget(Mob(assets, panel), 0);
        List<SceneSprite> list = Build(w);
        SceneSprite level = Assert.Single(list, s => s.AnimationPath == "mhp-text");
        SceneSprite name = Assert.Single(list, s => s.AnimationPath == "mhp-name");
        Assert.Equal(level.ScreenX + level.Frame.Width + NamePlates.MonsterNameGap, name.ScreenX);          // 紧接在 "Lv 等级" 后面
        Assert.Equal(97 + 14, name.ScreenY + NamePlates.InkBottom(name.Frame));                             // 牌子行(97)再往下 14 行
        Assert.Equal(NamePlates.TextFrame(Art(), "Goblin", 0xFFFFFFu)!.Width, name.Frame.Width);
        Assert.True(list.IndexOf(name) > list.IndexOf(level));

        // 名字用不了的各种情况:窗口保持原样(没有 mhp-name)
        var noTexts = MonsterHpWindow.For(WindowSprites.Window(), Art())!;
        noTexts.SetTarget(Mob(assets, panel), 0);
        Assert.DoesNotContain(Build(noTexts), s => s.AnimationPath == "mhp-name");                          // 没有文字表(共用的也没装)

        var noFonts = MonsterHpWindow.For(WindowSprites.Window(), new OverlayArt())!;
        noFonts.Texts = Texts();
        noFonts.SetTarget(Mob(assets, panel), 0);
        Assert.DoesNotContain(Build(noFonts), s => s.AnimationPath == "mhp-name");                          // 没有字体

        var noKey = MonsterHpWindow.For(WindowSprites.Window(), Art())!;
        noKey.Texts = Texts();
        noKey.SetTarget(Mob(assets, new MonsterPanelInfo(MonsterKind.Normal, WindowSprites.FaceImage, 3, new[] { "[human]" }, 120)), 0);
        Assert.DoesNotContain(Build(noKey), s => s.AnimationPath == "mhp-name");                            // 怪物没有 [name]

        var emptyText = MonsterHpWindow.For(WindowSprites.Window(), Art())!;
        emptyText.Texts = Texts();
        emptyText.SetTarget(Mob(assets, new MonsterPanelInfo(MonsterKind.Normal, WindowSprites.FaceImage, 3, new[] { "[human]" }, 120, "mob_2")), 0);
        Assert.DoesNotContain(Build(emptyText), s => s.AnimationPath == "mhp-name");                        // 表里的文字是空的
    }

    [Fact]
    public void ThePanelInfoCarriesTheNameKeyOfTheMob()
    {
        Assert.Null(new MonsterPanelInfo(MonsterKind.Normal, null, -1, null, 10).NameKey);
        Assert.Equal("mob_1", new MonsterPanelInfo(MonsterKind.Normal, null, -1, null, 10, "mob_1").NameKey);
    }
}
