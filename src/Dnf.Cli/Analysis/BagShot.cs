using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// bag-shot:背包窗口(<see cref="BagWindow"/> 与 <see cref="BagWindowArt"/>,证据 E-118)的 800×600 画面,写成 PNG,拿来看版式有没有叠字、挤出格子、颜色是否合理。
/// 一个 --level 级的剑魂,按部位各挑一件穿得上的装备全穿上(武器要画面上穿得出来),背包里再放几件穿不上的装备(等级不够、职业不符)、几件能穿的、一些可叠加物品;
/// --tab 看第几个页签(0 到 3),--select 把光标放在第几格(从 1 数),--panel 把光标放进装备栏。
/// 物品是用户自己的数据,所以这张图只在本机看,不入库、不贴到别处。
/// </summary>
internal static class BagShot
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("bag-shot 需要 --png <输出.png>");
        }
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        var catalog = new ItemCatalog(data);
        var random = new Random(opts.Seed);
        int hero = opts.HeroLevel;
        HeroProgress progress = HeroProgress.CreateForLocalHero(assets, hero);
        progress.AddGold(123456);

        IReadOnlyList<ItemInfo> all = catalog.Droppable();
        List<ItemInfo> gear = all.Where(i => i.IsEquipment).ToList();
        List<ItemInfo> stacks = all.Where(i => !i.IsEquipment).ToList();

        // 先每个部位挑一件穿得上的穿上
        var wearable = new Dictionary<int, List<ItemInfo>>();
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            int p = part;
            wearable[part] = gear.Where(i => i.Part == p && EquipRules.Check(i, EquipRules.SwordmanJob, hero) == EquipResult.Ok
                && (p != EquipmentParts.Weapon || HeroLook.CanShow(assets, i))).ToList();
        }
        int worn = 0;
        foreach (KeyValuePair<int, List<ItemInfo>> pair in wearable)
        {
            if (pair.Value.Count == 0)
            {
                continue;
            }
            progress.AddItem(pair.Value[random.Next(pair.Value.Count)]);
            if (progress.Equip(0, item => HeroLook.CanShow(assets, item)) == EquipResult.Ok)
            {
                worn++;
            }
        }
        // 背包里:每个部位再留一件能穿的,加上随机的装备(穿不穿得上不管)
        foreach (KeyValuePair<int, List<ItemInfo>> pair in wearable)
        {
            if (pair.Value.Count > 1)
            {
                progress.AddItem(pair.Value[random.Next(pair.Value.Count)]);
            }
        }
        for (int i = 0; i < 24 && gear.Count > 0; i++)
        {
            progress.AddItem(gear[random.Next(gear.Count)]);
        }
        for (int i = 0; i < 20 && stacks.Count > 0; i++)
        {
            ItemInfo stack = stacks[random.Next(stacks.Count)];
            progress.AddItem(stack, 1 + random.Next(Math.Max(1, Math.Min(stack.StackLimit, 120))));
        }

        // 窗口:开着、翻到页签、光标放到格子上(或装备栏上)
        var window = new BagWindow();
        window.UseAssets(assets);
        window.Show(GameStage.Town);
        for (int t = 0; t < opts.BagTab; t++)
        {
            window.NextTab(1);
        }
        int cell = Math.Max(0, opts.SelectIndex - 1);
        for (int r = 0; r < cell / BagWindow.Columns; r++)
        {
            window.Move(0, 1);
        }
        for (int c = 0; c < cell % BagWindow.Columns; c++)
        {
            window.Move(1, 0);
        }
        if (opts.BagPanel)
        {
            for (int c = 0; c < cell % BagWindow.Columns + 1; c++)
            {
                window.Move(-1, 0);
            }
        }

        // 面板加成:穿着的每一件加总(这里没有主角的外观,默认武器不算)
        EquipmentStats stats = default;
        foreach (ItemInfo item in progress.Worn.Items)
        {
            stats = stats.Plus(EquipmentStats.Read(assets, item.Path));
        }

        List<SceneSprite> sprites = BagWindowArt.Build(window, progress, assets, new OverlayArt(), stats);
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);
        Console.WriteLine("背包窗口:页签 " + opts.BagTab + ",光标 " + (window.OnEquipment ? "在装备栏第 " + (window.EquipCursor + 1) + " 格" : "在格子 " + (window.Cursor + 1))
            + ",穿上 " + worn + " 件,背包 " + progress.BagItems + " 件占 " + progress.BagSlots + " 格,绘制列表 " + sprites.Count + " 个精灵");
        Console.WriteLine("已写 " + opts.PngFile);
        return 0;
    }
}
