using System.Diagnostics;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// shop-check:NPC 商店的检查(证据 E-119)。整库读 <c>itemshop.lst</c> 与每家店的脚本、<c>npc.lst</c> 与每个 NPC 脚本的 <c>[role]</c>、城镇区域里摆着的 NPC、价格倍率表,检查结构:
/// 每家店都读得出来、每件商品都认得、NPC 角色里写的商店编号都在列表里(店主 NPC 与角色互相指回)、城镇里摆着商店 NPC 而且走到它身边(本项目自己设的交互范围,
/// <see cref="TownShopNpcs.RangeX"/> × <see cref="TownShopNpcs.RangeY"/>)总够得着;再对每家店的每件商品演习买一件、卖回去(以及一次买三件卖三件),核对钱与背包的账、
/// 商店窗口的光标与滚动、无人值守探针;最后把几家店画一遍。数值、商店内容、物品名与 NPC 对应关系是用户自己的数据,只打印在本机的终端上(不要抄进入库的文件);通过与否只看结构。
/// </summary>
internal static class ShopCheck
{
    /// <summary>演习时钱包里的钱(买什么都买得起)。</summary>
    private const long Wallet = 2_000_000_000L;

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        // 1. 商店目录与每家店的脚本
        var watch = Stopwatch.StartNew();
        var catalog = new ItemShopCatalog(data);
        IReadOnlyList<int> indices = catalog.Indices();
        Report(indices.Count > 0, "itemshop.lst 列了 " + indices.Count + " 家店");
        if (indices.Count == 0)
        {
            return 1;
        }
        var defs = new List<ItemShopDef>();
        foreach (int index in indices)
        {
            ItemShopDef? def = catalog.Get(index);
            if (def != null)
            {
                defs.Add(def);
            }
        }
        watch.Stop();
        Report(defs.Count == indices.Count, "每家店的脚本都读得出来(读不出的 " + (indices.Count - defs.Count) + " 家,用时 " + watch.ElapsedMilliseconds + " 毫秒)");
        int stocked = defs.Count(d => d.ItemCount > 0);
        int maxTabs = defs.Max(d => d.Tabs.Count);
        int maxRows = defs.Max(d => d.Tabs.Count == 0 ? 0 : d.Tabs.Max(t => t.RowCount));
        int ragged = defs.Sum(d => d.Tabs.Count(t => t.Cells.Count % ItemShopDef.Columns != 0));
        Console.WriteLine("  有商品的店 " + stocked + " 家,商品共 " + defs.Sum(d => d.ItemCount) + " 件(不去重),最多 " + maxTabs + " 页,一页最多 " + maxRows
            + " 行;格子数不是 " + ItemShopDef.Columns + " 的整数倍的页 " + ragged + " 个");
        Report(stocked > 0, "有商品的店不是空的");

        // 2. 商品都认得
        var items = new ItemCatalog(data);
        var codes = new SortedSet<int>();
        foreach (ItemShopDef def in defs)
        {
            foreach (ItemShopTab tab in def.Tabs)
            {
                foreach (int code in tab.Cells)
                {
                    if (code >= 0)
                    {
                        codes.Add(code);
                    }
                }
            }
        }
        List<int> missing = codes.Where(c => items.Get(c) == null).ToList();
        Console.WriteLine("  商品(去重)" + codes.Count + " 种,物品脚本读不出来的 " + missing.Count);
        int listedButUnreadable = 0;
        if (missing.Count > 0)
        {
            // 商店脚本里有些编号两张物品列表里都没有(数据里就悬空,服务端也不认):窗口里画成色块、点了提示"没有这件商品",只当观察记下;
            // 列了却读不出脚本的才是我们这边的毛病,才算失败。
            Dictionary<int, string> listedEquipment = data.ReadList(ItemCatalog.EquipmentListPath) ?? new Dictionary<int, string>();
            Dictionary<int, string> listedStackable = data.ReadList(ItemCatalog.StackableListPath) ?? new Dictionary<int, string>();
            int notListed = missing.Count(c => !listedEquipment.ContainsKey(c) && !listedStackable.ContainsKey(c));
            listedButUnreadable = missing.Count - notListed;
            Console.WriteLine("  其中不在 equipment.lst 与 stackable.lst 里的 " + notListed + " 种,列了但脚本读不出的 " + listedButUnreadable + " 种;样本编号 "
                + string.Join(",", missing.Take(8)) + ";涉及的店 " + defs.Count(d => d.Tabs.Any(t => t.Cells.Any(c => missing.Contains(c)))) + " 家");
        }
        Report(listedButUnreadable == 0, "列在 equipment.lst 与 stackable.lst 里的商品都读得出物品脚本(读不出的 " + listedButUnreadable + " 种)");
        List<ItemInfo> goods = codes.Select(c => items.Get(c)).Where(i => i != null).Select(i => i!).ToList();
        int equip = goods.Count(i => i.IsEquipment);
        int stack = goods.Count(i => i.IsStackable);
        int title = goods.Count(i => i.IsEquipment && i.Part == EquipmentParts.TitleName);
        int free = goods.Count(i => i.Price == 0);
        int noSale = goods.Count(i => !ShopRules.IsSellable(i));
        Console.WriteLine("  商品构成:装备 " + equip + "、可叠加 " + stack + "、其它 " + (goods.Count - equip - stack) + ";称号 " + title + ";价格为 0 的 " + free + ";商店不收的 " + noSale);
        Report(goods.All(i => i.Price >= 0 && i.Value >= 0), "价格与价值都不是负数");

        // 3. 价格倍率表
        ItemPriceRates? read = ItemPriceRates.Read(data);
        Report(read != null, "价格倍率表读得出来(equipment/pricetable.tbl 的 [percent] 三个数)");
        ItemPriceRates rates = read ?? ItemPriceRates.Neutral;
        Report(rates.Normal >= 0 && rates.Normal <= 1000 && rates.KiriProtectable >= 0 && rates.KiriProtectable <= 1000, "收购倍率都在 0 到 1000 之间(收购价不高于物品价值)");
        int dearer = goods.Count(i => ItemPricing.SellPrice(i, 1, rates) > ItemPricing.BuyPrice(i, 1));
        Console.WriteLine("  收购价高于买入价的商品 " + dearer + " 种(价格为 0 的不算在内的话看下一行)");
        int dearerPriced = goods.Count(i => i.Price > 0 && ItemPricing.SellPrice(i, 1, rates) > ItemPricing.BuyPrice(i, 1));
        Console.WriteLine("  其中买入价不为 0 的 " + dearerPriced + " 种");

        // 4. NPC 的 [role] 与商店编号
        TownShopNpcs shopNpcs = TownShopNpcs.FromAssets(assets);
        TownShopNpcs roleNpcs = TownShopNpcs.FromAssets(assets, onlyReadableShops: false);
        Dictionary<int, string> npcCatalog = TownScene.ReadNpcCatalog(assets);
        int npcsWithShop = 0, dangling = 0, danglingStillShop = 0;
        var referenced = new HashSet<int>();
        var known = new HashSet<int>(indices);
        var danglingNpcs = new List<(int Npc, int Shop)>();
        foreach (int id in npcCatalog.Keys.OrderBy(k => k))
        {
            int role = roleNpcs.ShopOf(id);
            if (role < 0)
            {
                continue;
            }
            if (!known.Contains(role))
            {
                // 角色里写的店不在 itemshop.lst 里(数据里就悬空):运行时把这个 NPC 当成不开商店
                dangling++;
                danglingNpcs.Add((id, role));
                if (shopNpcs.ShopOf(id) >= 0)
                {
                    danglingStillShop++;
                }
                continue;
            }
            npcsWithShop++;
            referenced.Add(role);
        }
        Console.WriteLine("  npc.lst 里 " + npcCatalog.Count + " 个 NPC,其中开商店的 " + npcsWithShop + " 个(另有角色写了商店编号却不在列表里的 " + dangling + " 个),开的商店共 " + referenced.Count + " 家;没有 NPC 指向的店 " + known.Count(i => !referenced.Contains(i)) + " 家");
        Report(npcsWithShop > 0, "有 NPC 的角色是商店");
        Report(danglingStillShop == 0, "角色里写的商店不在 itemshop.lst 里的 NPC,运行时都当成不开商店(漏网的 " + danglingStillShop + " 个)");
        int ownerKnown = defs.Count(d => d.NpcId >= 0);
        int ownerAgrees = defs.Count(d => d.NpcId >= 0 && shopNpcs.ShopOf(d.NpcId) == d.Index);
        Console.WriteLine("  商店脚本里写了店主 NPC 的 " + ownerKnown + " 家,其中该 NPC 的角色确实指回这家店的 " + ownerAgrees + " 家");

        // 5. 城镇区域里摆着的商店 NPC:站在摆放点上找得到,而且走得到它身边(可走区域里离它最近的点在交互范围内)
        TownAtlas atlas = TownAtlas.Open(assets);
        int areas = 0, areasWithShopNpcs = 0, placed = 0, findFailed = 0, unreachable = 0, unreadable = 0;
        var placedIds = new HashSet<int>();
        var gaps = new List<(int Npc, int Town, int Area, int X, int Y)>();
        foreach (int town in atlas.TownIds.OrderBy(t => t))
        {
            TownDef? def = atlas.GetTown(town);
            if (def == null)
            {
                continue;
            }
            foreach (TownArea area in def.Areas)
            {
                TownAreaInfo? info = atlas.GetArea(town, area.Id);
                if (info == null)
                {
                    continue;
                }
                areas++;
                List<TownNpc> npcs;
                try
                {
                    npcs = TownScene.ReadMapNpcs(assets, info.MapPath);
                }
                catch (DnfFormatException)
                {
                    unreadable++;
                    continue;
                }
                bool any = false;
                foreach (TownNpc npc in npcs)
                {
                    placedIds.Add(npc.Id);
                    if (shopNpcs.ShopOf(npc.Id) < 0)
                    {
                        continue;
                    }
                    any = true;
                    placed++;
                    if (!shopNpcs.TryFindNear(npcs, npc.X, npc.Y, out TownNpc onSpot, out int shopHere) || shopHere < 0 || onSpot.X != npc.X || onSpot.Y != npc.Y)
                    {
                        findFailed++;
                    }
                    info.NearestWalkable(npc.X, npc.Y, out double wx, out double wy);
                    if (!shopNpcs.TryFindNear(npcs, wx, wy, out _, out _))
                    {
                        unreachable++;
                        gaps.Add((npc.Id, town, area.Id, (int)Math.Abs(wx - npc.X), (int)Math.Abs(wy - npc.Y)));
                    }
                }
                if (any)
                {
                    areasWithShopNpcs++;
                }
            }
        }
        Console.WriteLine("  城镇区域 " + areas + " 块(地图读不出的 " + unreadable + " 块),其中摆着商店 NPC 的 " + areasWithShopNpcs + " 块,商店 NPC 共 " + placed + " 个");
        if (danglingNpcs.Count > 0)
        {
            Console.WriteLine("  悬空的 NPC(编号:商店编号):" + string.Join(",", danglingNpcs.Select(p => p.Npc + ":" + p.Shop)) + ";其中摆在城镇里的 " + danglingNpcs.Count(p => placedIds.Contains(p.Npc)) + " 个");
        }
        if (gaps.Count > 0)
        {
            Console.WriteLine("  够不着的商店 NPC(NPC 编号@城镇/区域=离可走区域最近点的横向:纵深距离):" + string.Join(" ", gaps.Select(g => g.Npc + "@" + g.Town + "/" + g.Area + "=" + g.X + ":" + g.Y)));
        }
        Report(placed > 0, "城镇里摆着开商店的 NPC");
        Report(findFailed == 0, "主角站在商店 NPC 的摆放点上,总找得到它(找不到的 " + findFailed + " 个)");
        Report(unreachable == 0, "可走区域里离商店 NPC 最近的点,总在交互范围(" + TownShopNpcs.RangeX + " × " + TownShopNpcs.RangeY + ")之内(够不着的 " + unreachable + " 个)");

        // 6. 买卖演习:每家店的每件商品买一件、卖回去,再买三件、卖三件,核对钱与背包的账
        ShopBook book = ShopBook.FromData(data);
        HeroProgress progress = HeroProgress.CreateForLocalHero(assets, opts.HeroLevel);
        int buys = 0, sells = 0, refusedTitles = 0, refusedSales = 0, triples = 0;
        var mismatches = new List<string>();
        foreach (ItemShopDef def in defs)
        {
            foreach (ItemShopTab tab in def.Tabs)
            {
                foreach (int code in tab.Cells)
                {
                    ItemInfo? item = code < 0 ? null : book.Item(code);
                    if (item == null)
                    {
                        continue;
                    }
                    progress.Bag.Clear();
                    if (progress.Gold < Wallet / 2)
                    {
                        progress.AddGold(Wallet);
                    }
                    long before = progress.Gold;
                    ShopResult r = progress.Buy(def, item, 1);
                    if (!ShopRules.IsBuyable(item))
                    {
                        refusedTitles++;
                        if (r != ShopResult.NotBuyable || progress.Gold != before || progress.Bag.CountOf(code) != 0)
                        {
                            mismatches.Add(code + ":称号买入 " + r);
                        }
                        continue;
                    }
                    if (r != ShopResult.Ok || before - progress.Gold != ItemPricing.BuyPrice(item, 1) || progress.Bag.CountOf(code) != 1)
                    {
                        mismatches.Add(code + ":买一件 " + r + " 扣 " + (before - progress.Gold) + " 应扣 " + ItemPricing.BuyPrice(item, 1) + " 背包 " + progress.Bag.CountOf(code));
                        continue;
                    }
                    buys++;
                    if (!SellBack(progress, item, 1, rates, ref sells, ref refusedSales))
                    {
                        mismatches.Add(code + ":卖一件");
                    }
                    if (item.IsStackable && Inventory.StackLimitOf(item) >= 3)
                    {
                        // 一格放得下三件才演习"买三件卖三件"(卖是按格卖的,一格放不下三件时窗口里本来就要分格卖)
                        progress.Bag.Clear();
                        before = progress.Gold;
                        ShopResult r3 = progress.Buy(def, item, 3);
                        if (r3 != ShopResult.Ok || before - progress.Gold != ItemPricing.BuyPrice(item, 3) || progress.Bag.CountOf(code) != 3)
                        {
                            mismatches.Add(code + ":买三件 " + r3 + " 扣 " + (before - progress.Gold) + " 应扣 " + ItemPricing.BuyPrice(item, 3) + " 背包 " + progress.Bag.CountOf(code));
                        }
                        else if (!SellBack(progress, item, 3, rates, ref sells, ref refusedSales))
                        {
                            mismatches.Add(code + ":卖三件");
                        }
                        else
                        {
                            triples++;
                        }
                    }
                }
            }
        }
        Console.WriteLine("  演习:买成 " + buys + " 次(一次买三件的 " + triples + " 次)、卖回 " + sells + " 次;商店拒收的 " + refusedSales + " 次、称号拒买的 " + refusedTitles + " 次");
        if (mismatches.Count > 0)
        {
            Console.WriteLine("  对不上的(前 12 处):" + string.Join(";", mismatches.Take(12)));
        }
        Report(mismatches.Count == 0, "钱与背包的账都对得上(对不上的 " + mismatches.Count + " 处)");

        // 7. 商店窗口:光标与滚动、无人值守探针、画面
        var window = new ShopWindow();
        window.Use(book);
        int scrollBad = 0, probeShops = 0, probeMiss = 0, drawn = 0, iconFrames = 0, iconBlocks = 0, drawBad = 0;
        var art = new OverlayArt();
        foreach (ItemShopDef def in defs)
        {
            window.Show(GameStage.Town, def.Index);
            for (int t = 0; t < window.TabCount; t++)
            {
                window.Show(GameStage.Town, def.Index);
                for (int i = 0; i < t; i++)
                {
                    window.NextTab(1);
                }
                for (int step = 0; step < window.BuyRows + 2; step++)
                {
                    window.Move(step % 3 == 2 ? 1 : 0, 1);
                    int row = window.Cursor / ShopWindow.BuyColumns;
                    if (row < window.ScrollRow || row >= window.ScrollRow + ShopWindow.VisibleRows || window.ScrollRow > Math.Max(0, window.BuyRows - ShopWindow.VisibleRows))
                    {
                        scrollBad++;
                    }
                }
            }
            if (def.ItemCount == 0)
            {
                continue;
            }
            probeShops++;
            progress.Bag.Clear();
            progress.AddGold(Wallet);
            window.Show(GameStage.Town, def.Index);
            bool hasBuyable = def.Tabs.Any(t => t.Cells.Any(c => c >= 0 && ShopRules.IsBuyable(book.Item(c))));
            (bool bought, bool sold) = window.TryTradeAny(progress, out _);
            if (hasBuyable && !bought)
            {
                probeMiss++;
            }
            if (sold && progress.Bag.TotalCount != 0)
            {
                probeMiss++;
            }
        }
        Report(scrollBad == 0, "买入页的光标一路下移,光标行总在屏内、滚动行不越界(出错 " + scrollBad + " 次)");
        Report(probeMiss == 0, "无人值守探针在每家有可买商品的店都买成、卖回之后背包清空(有 " + probeShops + " 家有商品的店,出错 " + probeMiss + " 家)");

        foreach (ItemShopDef def in defs.OrderByDescending(d => d.ItemCount).Take(5))
        {
            progress.Bag.Clear();
            window.Show(GameStage.Town, def.Index);
            for (int pass = 0; pass < 3; pass++)
            {
                if (pass == 1)
                {
                    window.NextTab(1);
                }
                else if (pass == 2)
                {
                    window.ToggleMode();
                }
                List<SceneSprite> sprites = ShopWindowArt.Build(window, progress, assets, art);
                drawn++;
                if (sprites.Count == 0 || !sprites.Select((s, i) => s.SortKey == i).All(ok => ok))
                {
                    drawBad++;
                }
                iconFrames += sprites.Count(s => s.AnimationPath == "bag-icon" && s.ScreenX < ShopWindow.DetailX);
                iconBlocks += sprites.Count(s => s.AnimationPath == "bag-icon-block" && s.ScreenX < ShopWindow.DetailX);
            }
        }
        Console.WriteLine("  画面:" + drawn + " 张(买入 2 页、出售 1 页,各取商品最多的几家店),格子里的图标取到素材 " + iconFrames + " 个、退回色块 " + iconBlocks + " 个");
        Report(drawn > 0 && drawBad == 0, "商店画面不是空的、精灵排序键就是画的次序(出错 " + drawBad + " 张)");
        Report(iconFrames > 0, "商品图标至少有一部分从素材里取到了");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// 把刚买的 <paramref name="count"/> 件 <paramref name="item"/> 卖回去,核对结果:商店收的(<see cref="ShopRules.IsSellable"/>)要成交、
    /// 钱涨的正好是 <see cref="ItemPricing.SellPrice"/>、背包里这件清零;商店不收的要拒收、钱与背包都不动。账对返回 true。
    /// </summary>
    private static bool SellBack(HeroProgress progress, ItemInfo item, int count, ItemPriceRates rates, ref int sells, ref int refused)
    {
        InventoryTab tab = Inventory.TabOf(item);
        IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(tab);
        int slot = -1;
        for (int i = 0; i < stacks.Count; i++)
        {
            if (stacks[i].Info.Code == item.Code)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            return false;
        }
        long before = progress.Gold;
        ShopResult r = progress.Sell(tab, slot, count, rates, out long earned);
        if (ShopRules.IsSellable(item))
        {
            sells++;
            return r == ShopResult.Ok && earned == ItemPricing.SellPrice(item, count, rates) && progress.Gold - before == earned && progress.Bag.CountOf(item.Code) == 0;
        }
        refused++;
        return r == ShopResult.NotSellable && earned == 0 && progress.Gold == before && progress.Bag.CountOf(item.Code) == count;
    }
}
