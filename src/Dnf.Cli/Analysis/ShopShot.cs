using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// shop-shot:NPC 商店窗口(<see cref="ShopWindow"/> 与 <see cref="ShopWindowArt"/>,证据 E-119)的 800×600 画面,写成 PNG,拿来看版式有没有叠字、挤出格子、颜色是否合理。
/// 一个 --level 级的剑魂,钱包里放一笔钱,背包里放些装备与可叠加物品(出售页用);--shop 指定商店编号(<c>itemshop.lst</c> 的编号,默认第一家有商品的),
/// --sell 看出售页,--tab 翻到第几页(0 到 3),--select 把光标放在第几格(从 1 数),--prompt 在光标处按一次回车(可叠加物品会弹出数量框;装备会直接成交)。
/// 商店内容与物品是用户自己的数据,所以这张图只在本机看,不入库、不贴到别处。
/// </summary>
internal static class ShopShot
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("shop-shot 需要 --png <输出.png>");
        }
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        ShopBook book = ShopBook.FromData(data);
        int index = opts.ShopIndex >= 0 ? opts.ShopIndex : book.FirstShopWithItems()?.Index ?? -1;
        if (index < 0)
        {
            throw new UserError("没有找到有商品的商店(用 --shop N 指定)");
        }

        // 背包里放些东西:出售页看得到装备与可叠加物品
        var random = new Random(opts.Seed);
        HeroProgress progress = HeroProgress.CreateForLocalHero(assets, opts.HeroLevel);
        progress.AddGold(123456);
        var catalog = new ItemCatalog(data);
        IReadOnlyList<ItemInfo> all = catalog.Droppable();
        List<ItemInfo> gear = all.Where(i => i.IsEquipment).ToList();
        List<ItemInfo> stacks = all.Where(i => !i.IsEquipment).ToList();
        for (int i = 0; i < 24 && gear.Count > 0; i++)
        {
            progress.AddItem(gear[random.Next(gear.Count)]);
        }
        for (int i = 0; i < 20 && stacks.Count > 0; i++)
        {
            ItemInfo stack = stacks[random.Next(stacks.Count)];
            progress.AddItem(stack, 1 + random.Next(Math.Max(1, Math.Min(stack.StackLimit, 120))));
        }

        // 窗口:开着、翻到页签、光标放到格子上
        var window = new ShopWindow();
        window.Use(book);
        if (!window.Show(GameStage.Town, index))
        {
            throw new UserError("读不出商店 " + index);
        }
        if (opts.ShopSell)
        {
            window.SetMode(ShopMode.Sell);
        }
        for (int t = 0; t < opts.BagTab; t++)
        {
            window.NextTab(1);
        }
        int columns = window.Columns;
        int cell = Math.Max(0, opts.SelectIndex - 1);
        for (int r = 0; r < cell / columns; r++)
        {
            window.Move(0, 1);
        }
        for (int c = 0; c < cell % columns; c++)
        {
            window.Move(1, 0);
        }
        if (opts.ShopPrompt)
        {
            window.Activate(progress);
        }

        List<SceneSprite> sprites = ShopWindowArt.Build(window, progress, assets, new OverlayArt());
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);
        Console.WriteLine("商店窗口:商店 " + index + "(" + window.ShopItemCount + " 件商品," + window.TabCount + " 页)," + (window.Mode == ShopMode.Buy ? "买入" : "出售")
            + " 第 " + (window.Mode == ShopMode.Buy ? window.ShopTab : (int)window.SellTab) + " 页,光标 " + (window.Cursor + 1) + ",数量框 " + (window.IsPromptOpen ? "开" : "关")
            + ",消息 \"" + window.Message + "\",绘制列表 " + sprites.Count + " 个精灵");
        Console.WriteLine("已写 " + opts.PngFile);
        return 0;
    }
}
