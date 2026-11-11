using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 装扮宿主接线(r15-avatarhost)测试的共用夹具:装扮衣柜的合成夹具(<see cref="WardrobeRig"/>)、一个等级 99 的主角进度、
/// 一个装扮会话(<see cref="AvatarSession"/>),外加造物品与城镇主角的小工具。物品编号全是合成的 900 起,与原版编号无关;
/// 物品脚本是夹具现场写进合成 PVF 的,图片是纯色矩形,所以"外观里有哪些图层"可以不画任何像素就读出来。
/// </summary>
internal sealed class AvatarHostRig : IDisposable
{
    /// <summary>部位 0 到 8 的时装编号是 901 加部位号(<see cref="Avatar"/>);下面是别的几件。</summary>
    public const int CodeCoatTwo = 910;
    public const int CodeHatOtherJob = 911;
    public const int CodeAurora = 912;
    public const int CodeCoatNoImage = 913;
    public const int CodeWrongClassHat = 915;
    public const int CodeHighLevelHat = 916;
    public const int CodeWeapon = 930;
    public const int CodeGearCoat = 940;
    public const int CodeGearRing = 941;
    public const int CodeFillerFirst = 950;

    private readonly Dictionary<int, ItemInfo> _catalog = new();
    private int _fillers;

    /// <summary>默认的主角等级:够穿夹具里所有的时装,又没超过合成经验表的最高级(读档还原时等级会被夹到最高级,超过了存读就对不上)。</summary>
    public const int DefaultLevel = 80;

    public AvatarHostRig(int level = DefaultLevel)
    {
        Progress = ExpTests.Progress(level: level);
        Session = new AvatarSession(Rig.Assets, Progress, () => Rig.Base, WeaponStart.FullPath);       // 合成素材里的起手武器就是底座上的那一把
    }

    public WardrobeRig Rig { get; } = new();

    public HeroProgress Progress { get; }

    public AvatarSession Session { get; }

    /// <summary>按编号查物品(造过的才有;存档还原的"物品目录"用它)。</summary>
    public ItemInfo Lookup(int code) => _catalog.TryGetValue(code, out ItemInfo item) ? item : null;

    /// <summary>造一件物品:路径是装备脚本 <paramref name="equ"/> 在合成 PVF 里的完整路径,部位、最低等级、可用职业自己给。</summary>
    public ItemInfo Make(int code, Equ equ, int part, int level = 1, string job = "[swordman]")
    {
        var item = new ItemInfo(code, equ.FullPath, ItemKind.Equipment) { Part = part, MinimumLevel = level };
        item.UsableJobs.Add(job);
        _catalog[code] = item;
        return item;
    }

    /// <summary>部位 0 到 8 各一件可穿的时装(<see cref="PartCase"/> 那一件,编号 901 加部位号)。</summary>
    public ItemInfo Avatar(int part) => Make(901 + part, PartCase(part).Worn, part);

    /// <summary>一件普通装备(不是时装):路径随便取一个不存在的脚本(只有算属性才会读它)。</summary>
    public ItemInfo Gear(int code, int part, int level = 1)
    {
        var item = new ItemInfo(code, "equipment/test/gear" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level };
        item.UsableJobs.Add("[swordman]");
        _catalog[code] = item;
        return item;
    }

    /// <summary>把物品放进背包(放得下才算数)。</summary>
    public void Give(params ItemInfo[] items)
    {
        foreach (ItemInfo item in items)
        {
            Assert.True(Progress.AddItem(item), "背包放不下 " + item.Code);
        }
    }

    /// <summary>把装备页塞满普通装备(编号从 <see cref="CodeFillerFirst"/> 起),直到再也放不下;默认塞自己的进度,也可以塞别的进度(读档的"新一局")。</summary>
    public void FillBag(HeroProgress progress = null)
    {
        progress ??= Progress;
        for (int guard = 0; guard < Inventory.SlotsPerTab * 2; guard++)
        {
            ItemInfo filler = Gear(CodeFillerFirst + _fillers, EquipmentParts.Ring);
            if (!progress.Bag.CanAdd(filler))
            {
                return;
            }
            _fillers++;
            Assert.True(progress.AddItem(filler));
        }
        Assert.Fail("装备页怎么也塞不满");
    }

    /// <summary>某个编号的物品在装备页的第几格;背包里没有返回 −1。</summary>
    public int IndexOf(int code)
    {
        IReadOnlyList<ItemStack> tab = Progress.Bag.Tab(InventoryTab.Equipment);
        for (int i = 0; i < tab.Count; i++)
        {
            if (tab[i].Info.Code == code)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>装备页每一格的物品编号(按格的先后)。</summary>
    public int[] Codes() => Progress.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code).ToArray();

    /// <summary>装扮栏里每个部位穿着的物品编号(没穿的部位不列)。</summary>
    public (int Part, int Code)[] WornCodes() => WornCodes(Session);

    /// <summary>某个会话的装扮栏里每个部位穿着的物品编号(没穿的部位不列)。</summary>
    public static (int Part, int Code)[] WornCodes(AvatarSession session)
    {
        var list = new List<(int, int)>();
        for (int part = AvatarSlots.First; part <= AvatarSlots.Last; part++)
        {
            ItemInfo worn = session.Slots.Get(part);
            if (worn != null)
            {
                list.Add((part, worn.Code));
            }
        }
        return list.ToArray();
    }

    /// <summary>穿上编号为 <paramref name="code"/> 的时装(要在装备页里),断言成功。</summary>
    public void WearOk(int code)
    {
        int index = IndexOf(code);
        Assert.True(index >= 0, "背包里没有 " + code);
        Assert.Equal(EquipResult.Ok, Session.Wear(index));
    }

    private static AniFile EmptyAni() => AniFile.Parse(TestData.BuildAni(new[] { "A.img" }, Array.Empty<(ushort, byte[])>(), Array.Empty<TestData.AniFrameSpec>()));

    /// <summary>一个城镇主角:穿 <paramref name="outfit"/>(默认是夹具的基础外观),动画是空的(只看外观,不画)。</summary>
    public TownHero NewHero(AvatarOutfit outfit = null)
    {
        AniFile ani = EmptyAni();
        return new TownHero(outfit ?? Rig.Base, ani, "rest.ani", ani, "move.ani", new List<WorldRect>(), 0, 0, false);
    }

    /// <summary>接好装扮页、按 <paramref name="stage"/> 打开的背包窗口(开着物品页)。</summary>
    public BagWindow OpenWindow(GameStage stage = GameStage.Town)
    {
        var window = new BagWindow();
        window.UseAvatars(Session);
        Assert.True(window.Show(stage));
        return window;
    }

    /// <summary>城镇里开着、已经切到装扮页的背包窗口。</summary>
    public BagWindow OpenAvatarWindow()
    {
        BagWindow window = OpenWindow();
        Assert.True(window.TogglePage());
        return window;
    }

    /// <summary>把装扮页的光标放到第 <paramref name="index"/> 个时装格上:先回到开页的样子,再按方向键走过去(不经过边界夹取)。</summary>
    public static void SelectCell(BagWindow window, int index)
    {
        Assert.Equal(BagPage.Avatars, window.Page);
        window.Avatars.Reset();
        for (int i = 0; i < index / BagWindow.Columns; i++)
        {
            window.Move(0, 1);
        }
        for (int i = 0; i < index % BagWindow.Columns; i++)
        {
            window.Move(1, 0);
        }
        Assert.Equal((index, false), (window.Avatars.Cursor, window.Avatars.OnSlots));
    }

    /// <summary>把装扮页的光标放到装扮栏的 <paramref name="part"/> 号格上(部位 0 到 9)。</summary>
    public static void SelectSlot(BagWindow window, int part)
    {
        Assert.Equal(BagPage.Avatars, window.Page);
        window.Avatars.Reset();
        window.Move(-1, 0);                                                        // 从第一行第一列往左:进装扮栏第一行最右一格(部位 2)
        for (int i = 0; i < BagWindow.SlotColumns - 1 - part % BagWindow.SlotColumns; i++)
        {
            window.Move(-1, 0);
        }
        for (int i = 0; i < part / BagWindow.SlotColumns; i++)
        {
            window.Move(0, 1);
        }
        Assert.Equal((true, part), (window.Avatars.OnSlots, window.Avatars.SlotCursor));
    }

    /// <summary>外观里每个图层的图片路径,按绘制顺序(与渲染无关的"图层序列")。</summary>
    public static string[] Paths(AvatarOutfit outfit) => outfit.Layers.Select(l => l.ImagePath).ToArray();

    /// <summary>外观里各图层的层名,按绘制顺序、空格隔开。</summary>
    public static string Names(AvatarOutfit outfit) => WardrobeRig.Names(outfit);

    public void Dispose()
    {
        Rig.Dispose();
    }
}

/// <summary>带一个现成主角的城镇会话(只有主角,别的什么也没有)。</summary>
internal sealed class HeroTownSession : IHeroSession
{
    public HeroTownSession(TownHero hero)
    {
        Hero = hero;
    }

    public string MapPath => "avatarhost/test";

    public TownHero Hero { get; }

    public TownCamera Camera => null;

    public int LayerCount => 0;

    public int NpcCount => 0;

    public void Update(double deltaMs, int dirX, int dirY)
    {
    }

    public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
}

/// <summary>记着每个成员被调了几次的城镇世界:造的会话都是穿基础外观的新主角(<see cref="AvatarTownWorld"/> 的"被包的世界")。</summary>
internal sealed class RecordingTownWorld : ITownWorld
{
    private readonly Func<IHeroSession> _create;

    public RecordingTownWorld(Func<IHeroSession> create)
    {
        _create = create;
    }

    public int Creates { get; private set; }

    public int Splashes { get; private set; }

    public int Throughs { get; private set; }

    public int Routes { get; private set; }

    public TownPlace Start { get; set; } = new TownPlace(7, 70);

    public TownPlace Return { get; set; } = new TownPlace(7, 71, TownEntry.Return);

    public SplashScene SplashToGive { get; set; }

    public TownPlace? ThroughToGive { get; set; }

    public bool RouteToGive { get; set; }

    public TownPlace LastCreated { get; private set; }

    /// <summary>最近一次 <see cref="Splash"/> 收到的两个参数。</summary>
    public (TownPlace? From, TownPlace To) LastSplash { get; private set; }

    /// <summary>最近一次 <see cref="Through"/> 收到的三个参数。</summary>
    public (TownPlace From, TownExit Exit, bool FacingLeft) LastThrough { get; private set; }

    /// <summary>最近一次 <see cref="TryRoute"/> 收到的起点。</summary>
    public TownPlace LastRouteFrom { get; private set; }

    public IHeroSession Create(TownPlace place)
    {
        Creates++;
        LastCreated = place;
        return _create();
    }

    public SplashScene Splash(TownPlace? from, TownPlace to)
    {
        Splashes++;
        LastSplash = (from, to);
        return SplashToGive;
    }

    public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
    {
        Throughs++;
        LastThrough = (from, exit, facingLeft);
        return ThroughToGive;
    }

    public bool TryRoute(TownPlace from, out TownExit exit)
    {
        Routes++;
        LastRouteFrom = from;
        exit = new TownExit(new WorldRect(1, 2, 3, 4), 7, 72);
        return RouteToGive;
    }
}
