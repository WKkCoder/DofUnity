using System.Diagnostics;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// item-check:怪物物品掉落的检查(证据 E-117)。
/// 整库扫描 equipment.lst / stackable.lst 里的物品脚本,统计能进掉落池的物品(<c>[creation rate]</c> 不为 0),
/// 检查它们的类型、稀有度、等级、地上图与图标是否都读得到(地上图包里有没有那一帧),再用真实的掉落表建池、按怪物等级 --level 掷 --samples 只怪物,
/// 汇总各类物品、各稀有度掉了多少件。数值与物品名是用户自己的数据,只打印在本机的终端上(不要抄进入库的文件);通过与否只看结构。
/// </summary>
internal static class ItemCheck
{
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

        // 1. 目录
        var catalog = new ItemCatalog(data);
        var watch = Stopwatch.StartNew();
        IReadOnlyList<ItemInfo> droppable = catalog.Droppable();
        watch.Stop();
        Report(catalog.ListedCount > 0, "目录:两张列表里一共列了 " + catalog.ListedCount + " 件");
        Report(catalog.Failed == 0, "整库扫描读了 " + catalog.Scanned + " 个脚本,读不出的 " + catalog.Failed + " 个,用时 " + watch.ElapsedMilliseconds + " 毫秒");
        Report(droppable.Count > 0, "能掉的物品(创建率不为 0)" + droppable.Count + " 件");
        if (droppable.Count == 0)
        {
            return 1;
        }

        // 2. 字段结构
        int equipments = droppable.Count(i => i.IsEquipment);
        int stackables = droppable.Count - equipments;
        int special = droppable.Count(i => i.SpecialMonsterDrop);
        int badGrade = droppable.Count(i => i.Grade < 0 || i.Grade > ItemPool.GradeMax);
        int badRarity = droppable.Count(i => i.Rarity < 0 || i.Rarity >= MonsterDropTable.RarityCount);
        int unknownPart = droppable.Count(i => i.IsEquipment && i.Part == EquipmentParts.Unknown);
        int unknownStackable = droppable.Count(i => i.IsStackable && i.StackableType == StackableTypes.Unknown);
        int noName = droppable.Count(i => string.IsNullOrEmpty(i.NameKey));
        Console.WriteLine("  构成:装备 " + equipments + "、可叠加物品 " + stackables + "(其中特殊怪物才掉的 " + special + ")");
        Report(badGrade == 0, "物品等级都在 0 到 " + ItemPool.GradeMax + " 里(不合的 " + badGrade + " 件)");
        Report(badRarity == 0, "稀有度都在 0 到 " + (MonsterDropTable.RarityCount - 1) + " 里(不合的 " + badRarity + " 件)");
        Report(unknownPart == 0, "装备类型都认得(不认得的 " + unknownPart + " 件)");
        Report(unknownStackable == 0, "可叠加物品的种类都认得(不认得的 " + unknownStackable + " 件)");
        Report(noName == 0, "都有 [name] 的字符串键(没有的 " + noName + " 件)");

        // 3. 地上图与图标:图包里要有对应的帧
        int noFieldTag = 0;
        int noFieldFrame = 0;
        int noIconFrame = 0;
        foreach (ItemInfo item in droppable)
        {
            if (item.FieldImageKey == null || item.FieldFrame < 0)
            {
                noFieldTag++;
            }
            else if (!assets.TryGetFrame(item.FieldImageKey, item.FieldFrame, out _))
            {
                noFieldFrame++;
            }
            if (item.IconKey == null || item.IconFrame < 0 || !assets.TryGetFrame(item.IconKey, item.IconFrame, out _))
            {
                noIconFrame++;
            }
        }
        Report(noFieldTag == 0, "都写了 [field image](没写的 " + noFieldTag + " 件)");
        Report(noFieldFrame == 0, "地上图的帧都在图包里(缺的 " + noFieldFrame + " 件)");
        Report(noIconFrame == 0, "图标的帧都在图包里(缺的 " + noIconFrame + " 件)");

        // 4. 掉落池
        MonsterDrops drops = MonsterDrops.Load(assets);
        Report(drops.Available, "怪物掉落表读到了" + (drops.Error == null ? string.Empty : "(" + drops.Error + ")"));
        if (!drops.Available || drops.Table == null)
        {
            return 1;
        }
        var pool = new ItemPool(droppable, drops.Table);
        Report(pool.Items > 0, "掉落池收了 " + pool.Items + " 件(排除特殊怪物掉落与创建率不大于 0 的)");
        for (int type = 1; type < MonsterDropTable.TypeCount; type++)
        {
            int cells = 0;
            int maxCandidates = 0;
            for (int rarity = 0; rarity < MonsterDropTable.RarityCount; rarity++)
            {
                for (int level = 1; level <= ItemPool.GradeMax; level++)
                {
                    int n = pool.CandidateCount(type, rarity, level);
                    if (n > 0)
                    {
                        cells++;
                        maxCandidates = Math.Max(maxCandidates, n);
                    }
                }
            }
            Console.WriteLine("  类型 " + type + ":有候选的(稀有度 × 等级)格 " + cells + " / " + MonsterDropTable.RarityCount * ItemPool.GradeMax + ",单格最多 " + maxCandidates + " 件");
            Report(cells > 0, "类型 " + type + " 的池不是空的");
        }

        // 5. 抽样:怪物等级 --level、副本等级 = 怪物等级、普通怪(种类 0)与首领(种类 3)各 --samples 只
        int monsterLevel = opts.HeroLevel;
        foreach (int role in new[] { 0, 3 })
        {
            var random = new DropRandom(opts.Seed + role);
            var byType = new int[MonsterDropTable.TypeCount];
            var byRarity = new int[MonsterDropTable.RarityCount];
            int total = 0;
            var context = new DropContext(monsterLevel, role, monsterLevel, 0, 1, 0, 100, 1);
            for (int i = 0; i < opts.Samples; i++)
            {
                foreach (RolledItem rolled in ItemDrop.Roll(drops.Table, pool, context, random))
                {
                    byType[rolled.Type]++;
                    byRarity[Math.Clamp(rolled.Info.Rarity, 0, MonsterDropTable.RarityCount - 1)]++;
                    total++;
                }
            }
            Console.WriteLine("  抽样:怪物等级 " + monsterLevel + "、种类 " + role + "、" + opts.Samples + " 只 → 掉出 " + total + " 件;按类型 "
                + string.Join("/", byType.Skip(1)) + ",按稀有度 " + string.Join("/", byRarity));
            Report(true, "抽样没有出错");
        }
        return bad == 0 ? 0 : 1;
    }
}
