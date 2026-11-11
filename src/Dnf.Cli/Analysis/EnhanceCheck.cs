using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// enhance-check:强化、附魔、套装、品级对真实数据的检查。读本机的强化表(<c>Etc/upgrade.etc</c>、<c>Etc/amplifyupgrade.etc</c>)、
/// 品级样本(<c>Etc/equipmentgradesample.etc</c>)与整份物品清单,核对结构不变量:表读得进来、每行的惩罚类型与概率在服务端的取值范围里、
/// 每个稀有度能一路升到封顶、失败那一档的惩罚与表对得上、抽签的失败频率落在期望附近;套装"穿齐才生效、少一件不生效"、附魔卡有部位、
/// 真实装备穿上去之后汇总等于各件加叠加项之和。数值、物品编号与件数是用户自己的数据,只打印在本机的终端上(不要抄进入库的文件)。
/// <para>
/// 环境变量 <c>DNF_ENHANCE_DUMP</c> 给了目录就把下面七份规范文本写进去:<c>upgrade_normal.txt</c>、<c>upgrade_amplify.txt</c>(表的原始行、惩罚档、
/// 取行结果的网格)、<c>items.txt</c>(每件物品的套装、附魔、强化券、等级区间)、<c>grade.txt</c>(品级样本)、<c>pick.txt</c>(倍率与抽档的网格)、
/// <c>effect.txt</c>(每件装备在几个强化等级下换算出的属性,武器倍率用合成表)、<c>variation.txt</c>(每件装备在几颗品级值种子下的浮动差),
/// 再用 <c>tools/py/enhance_ref.py compare</c> 与独立的 Python 实现逐行比对(只报计数)。转储只写在被 git 忽略的 out/ 下,不入库。
/// 网格里取哪些稀有度、装备等级、强化等级、倍率、权重、随机数、种子,与 enhance_ref.py、enhance_effect_ref.py 里的同名常数一一对应,改一边要改另一边。
/// </para>
/// </summary>
internal static class EnhanceCheck
{
    private const string UpgradePath = "Etc/upgrade.etc";
    private const string AmplifyPath = "Etc/amplifyupgrade.etc";
    private const string GradePath = "Etc/equipmentgradesample.etc";
    private const string ParameterPath = "Etc/etcparameter.etc";

    // ---- 对拍网格(与 tools/py/enhance_ref.py 同名常数一致) ----
    private static readonly int[] FewLevels = { 0, 1, 7, 18, -1 };
    private static readonly (int Rarity, int Grade)[] Sampled = { (0, 0), (1, 33), (3, 50), (5, 99) };
    private const int GoldLevelsEnd = 20;
    private static readonly float[] Multipliers = { 0.0f, 0.5f, 1.0f, 1.5f, 2.37f, 10.0f, 0.001f };

    private static readonly int[][] WeightSets =
    {
        new[] { 1000, 1000, 1000, 1000, 1000 },
        new[] { 1, 2, 3, 4, 5 },
        new[] { 0, 0, 0, 0, 0 },
        new[] { 5000, 1, 1, 1, 1 },
        new[] { 7 },
        new[] { 2000000000, 2000000000, 1, 1, 1 },
    };

    // ---- 强化与品级换算的网格(与 tools/py/enhance_effect_ref.py 同名常数一致) ----
    private static readonly int[] EffectLevels = { 1, 2, 5, 10, 18, 24 };
    private static readonly int[] VariationSeeds = { 1, 2, 3, 7, 99, 12345, 999999998, 2147483647, -5, -2147483648 };
    private const int SyntheticRateCells = WeaponUpgradeRateTable.CellsWithLastJob;

    private static readonly int[] Rolls = { 0, 1, 2, 499, 500, 999, 1000, 1001, 1500, 2000, 2999, 3000, 3001, 4000, 4999, 5000, 5001, 7000, 100000 };

    private static readonly (string Name, (int Key, float Value)[] Table)[] MultiplierTables =
    {
        ("A", new[] { (50, 1.5f) }),
        ("B", new[] { (10, 2.0f), (30, 3.0f), (60, 4.0f) }),
        ("C", new[] { (60, 4.0f), (30, 3.0f), (10, 2.0f) }),
        ("D", new[] { (20, 2.0f), (20, 3.0f), (20, 4.0f) }),
        ("E", Array.Empty<(int, float)>()),
    };

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

        string? dump = Environment.GetEnvironmentVariable("DNF_ENHANCE_DUMP");
        string? dumpDir = string.IsNullOrEmpty(dump) ? null : Path.GetFullPath(dump);
        if (dumpDir != null)
        {
            Directory.CreateDirectory(dumpDir);
        }

        // 1. 强化表
        Console.WriteLine("强化表:");
        UpgradeTable? normal = LoadTable(data, UpgradePath);
        UpgradeTable? amplify = LoadTable(data, AmplifyPath);
        bad += CheckTable("普通强化表", normal);
        bad += CheckTable("增幅强化表", amplify);
        if (dumpDir != null)
        {
            WriteLines(Path.Combine(dumpDir, "upgrade_normal.txt"), UpgradeLines(normal));
            WriteLines(Path.Combine(dumpDir, "upgrade_amplify.txt"), UpgradeLines(amplify));
        }

        // 2. 品级样本与抽签
        Console.WriteLine("品级:");
        ScriptLine[]? gradeLines = data.ReadScript(GradePath);
        EquipGradeSamples? samples = null;
        bool gradeOk = gradeLines != null && EquipGradeSamples.TryParse(gradeLines, out samples);
        bad += CheckGrade(gradeOk ? samples : null, opts.Seed);
        if (dumpDir != null)
        {
            WriteLines(Path.Combine(dumpDir, "grade.txt"), GradeLines(gradeOk ? samples : null));
            WriteLines(Path.Combine(dumpDir, "pick.txt"), PickLines());
        }

        // 3. 物品清单:套装、附魔、强化券、等级区间
        Console.WriteLine("物品清单(套装、附魔、强化券、强化等级区间):");
        var catalog = new ItemCatalog(data);
        var watch = Stopwatch.StartNew();
        var codes = new List<int>(catalog.EquipmentCodes());
        var seen = new HashSet<int>(codes);
        foreach (int code in catalog.StackableCodes())
        {
            if (seen.Add(code))
            {
                codes.Add(code);
            }
        }
        var items = new List<ItemInfo?>(codes.Count);
        foreach (int code in codes)
        {
            items.Add(catalog.Get(code));
        }
        watch.Stop();
        int unreadable = items.Count(i => i == null);
        Report(codes.Count > 0, "清单里 " + codes.Count + " 件(读不出 " + unreadable + " 件,用时 " + watch.ElapsedMilliseconds + " 毫秒)");
        if (dumpDir != null)
        {
            string path = Path.Combine(dumpDir, "items.txt");
            var lines = new List<string>(codes.Count);
            for (int i = 0; i < codes.Count; i++)
            {
                lines.Add(codes[i].ToString(CultureInfo.InvariantCulture) + "\t" + FormatItem(items[i]));
            }
            WriteLines(path, lines);
            Console.WriteLine("  转储已写出:" + dumpDir + "(用 tools/py/enhance_ref.py compare 与独立的 Python 实现逐行比对)");
        }
        bad += CheckItems(catalog, items.Where(i => i != null).Select(i => i!).ToList(), opts.Seed);

        // 4. 强化走一遍
        Console.WriteLine("强化流程(真实表,合成装备;编号 900 起):");
        if (normal != null)
        {
            bad += CheckWalk("普通强化表", normal, opts.Seed);
        }
        if (amplify != null)
        {
            bad += CheckWalk("增幅强化表", amplify, opts.Seed);
        }
        if (normal != null)
        {
            bad += CheckRealGear(normal, items.Where(i => i != null).Select(i => i!).ToList());
        }

        // 5. 强化等级与品级值换算成属性
        Console.WriteLine("强化与品级换算成属性(真实表、真实装备;武器倍率表是合成的 0.5 + i/128):");
        ScriptLine[]? parameterLines = data.ReadScript(ParameterPath);
        EquipGradeVariation? variation = null;
        bool variationOk = parameterLines != null && EquipGradeVariation.TryParse(parameterLines, out variation);
        List<ItemInfo> gearList = items.Where(i => i != null && i.IsEquipment).Select(i => i!).OrderBy(i => i.Code).ToList();
        bad += CheckInstanceEffects(catalog, normal, variationOk ? variation : null, gearList, opts.Seed);
        if (dumpDir != null)
        {
            WriteLines(Path.Combine(dumpDir, "effect.txt"), EffectLines(normal, gearList));
            WriteLines(Path.Combine(dumpDir, "variation.txt"), VariationLines(variationOk ? variation : null, gearList));
        }
        return bad;
    }

    private static UpgradeTable? LoadTable(GameData data, string path)
    {
        ScriptLine[]? lines = data.ReadScript(path);
        return lines != null && UpgradeTable.TryParse(lines, out UpgradeTable table) ? table : null;
    }

    // ------------------------------------------------------------ 强化表的结构

    private static int CheckTable(string name, UpgradeTable? t)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        Report(t != null, name + "读得进来");
        if (t == null)
        {
            return bad;
        }
        int rows = t.RowCount;
        Report(rows > 0, name + "有 " + rows + " 行");
        if (t.Problems.Count > 0)
        {
            Console.WriteLine("  读表时服务端会记日志的问题 " + t.Problems.Count + " 条:" + string.Join("、", t.Problems));
        }
        var penalty = new int[4];
        int penaltyOther = 0, failOut = 0, withMaterial = 0, type2NoLevels = 0, zeroFail = 0, fullFail = 0;
        for (int level = 0; level < rows; level++)
        {
            UpgradeRow row = t.Row(level)!;
            if (row.PenaltyType >= 1 && row.PenaltyType <= 3)
            {
                penalty[row.PenaltyType]++;
            }
            else
            {
                penaltyOther++;
            }
            if (row.FailChance < 0 || row.FailChance > ItemUpgrade.ProbabilityDenominator)
            {
                failOut++;
            }
            if (row.FailChance == 0)
            {
                zeroFail++;
            }
            if (row.FailChance >= ItemUpgrade.ProbabilityDenominator)
            {
                fullFail++;
            }
            if (row.MaterialCode != 0 && row.MaterialCount > 0)
            {
                withMaterial++;
            }
            if (row.PenaltyType == 2 && row.PenaltyLevels <= 0)
            {
                type2NoLevels++;
            }
        }
        Console.WriteLine("  惩罚类型:无惩罚 " + penalty[1] + "、降级 " + penalty[2] + "、销毁 " + penalty[3] + "、别的 " + penaltyOther
            + ";必成功 " + zeroFail + " 行,必失败 " + fullFail + " 行;要材料的 " + withMaterial + " 行;降级但不降级数的 " + type2NoLevels + " 行");
        Report(penaltyOther == 0, "每行的惩罚类型都是 1 到 3(别的 " + penaltyOther + " 行)");
        Report(failOut == 0, "每行的失败概率都在 0 到 " + ItemUpgrade.ProbabilityDenominator + " 里(越界 " + failOut + " 行)");

        int capped = 0, destroyed = 0, capBeyond = 0;
        for (int rarity = 0; rarity < UpgradeTable.Rarities; rarity++)
        {
            if (t.MaxLevel(rarity) >= 0)
            {
                capped++;
                if (t.MaxLevel(rarity) > rows)
                {
                    capBeyond++;
                }
            }
            if (t.DestroyLevel(rarity) >= 0)
            {
                destroyed++;
                if (t.DestroyLevel(rarity) > rows)
                {
                    capBeyond++;
                }
            }
        }
        Console.WriteLine("  有封顶等级的稀有度 " + capped + " 档,有销毁等级的 " + destroyed + " 档");
        Report(capBeyond == 0, "封顶与销毁等级都不超过表的行数(越界 " + capBeyond + " 个)");

        // 取行的数值:有限、金币不为负
        int calls = 0, missing = 0, nonFinite = 0, negative = 0;
        foreach (int rarity in Enumerable.Range(0, UpgradeTable.Rarities))
        {
            foreach (int grade in new[] { 0, 1, 17, 50, 99 })
            {
                foreach (int level in new[] { 0, 1, 17, 18, rows - 1 })
                {
                    for (int part = UpgradeTable.FirstPart; part <= UpgradeTable.LastPart; part++)
                    {
                        calls++;
                        UpgradeInfo? info = t.GetInfo(rarity, grade, level, part);
                        if (info == null)
                        {
                            missing++;
                            continue;
                        }
                        if (info.GoldCost < 0)
                        {
                            negative++;
                        }
                        foreach (float x in info.StatsA.Concat(info.StatsB))
                        {
                            if (float.IsNaN(x) || float.IsInfinity(x))
                            {
                                nonFinite++;
                            }
                        }
                    }
                }
            }
        }
        Report(missing == 0 && nonFinite == 0 && negative == 0,
            "取行 " + calls + " 次:取不到 " + missing + "、数值不是有限数 " + nonFinite + "、金币为负 " + negative);
        int repairBad = 0;
        for (int level = 0; level < UpgradeTable.CostSlots; level++)
        {
            float rate = t.RepairCostRate(level);
            if (float.IsNaN(rate) || float.IsInfinity(rate) || rate < 0f)
            {
                repairBad++;
            }
        }
        Report(repairBad == 0, "修理系数都是非负的有限数(不是 " + repairBad + " 项)");
        return bad;
    }

    // ------------------------------------------------------------ 品级

    private static int CheckGrade(EquipGradeSamples? samples, int seed)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        Report(samples != null, "品级样本读得进来");
        if (samples == null)
        {
            return bad;
        }
        Report(samples.BandCount == EquipGradeSamples.Bands, "品级档数 " + samples.BandCount + "(服务端写死 " + EquipGradeSamples.Bands + ")");
        int empty = 0, outOfRange = 0, perBand = 0;
        for (int band = 0; band < samples.BandCount; band++)
        {
            IReadOnlyList<int> list = samples.Samples(band);
            if (list.Count == 0)
            {
                empty++;
            }
            perBand = Math.Max(perBand, list.Count);
            outOfRange += list.Count(v => v < 0 || v > 32767);
        }
        Console.WriteLine("  每档最多 " + perBand + " 个样本");
        Report(empty == 0, "每档都有样本(空档 " + empty + " 个)");
        Report(outOfRange == 0, "样本都在 0 到 32767 里(越界 " + outOfRange + " 个)");
        var reps = new HashSet<int>();
        for (int band = 0; band < samples.BandCount; band++)
        {
            reps.Add(samples.Representative(band));
        }
        Report(reps.Count == samples.BandCount, "每档的代表值互不相同(不同的 " + reps.Count + " 个)");

        // 五档各 1000 权重、倍率 1:各档频率落在 20% 上下
        var random = new DropRandom(seed);
        var weights = new[] { 1000, 1000, 1000, 1000, 1000 };
        const int Draws = 100000;
        var hits = new int[weights.Length];
        for (int i = 0; i < Draws; i++)
        {
            hits[EquipGradeGenerator.Pick(weights, 1f, random)]++;
        }
        bool even = hits.All(h => h > Draws * 0.19 && h < Draws * 0.21);
        Report(even, "五档等权重抽 " + Draws + " 次,各档频率都在 19% 到 21% 里");
        var table = new List<KeyValuePair<int, float>> { new KeyValuePair<int, float>(1, 1f) };
        int zeroDraws = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (EquipGradeGenerator.Generate(samples, random, new List<KeyValuePair<int, float>>(), 50) != samples.Representative(0))
            {
                zeroDraws++;
            }
        }
        Report(zeroDraws == 0, "倍率表为空时一律给第 0 档的代表值(例外 " + zeroDraws + " 次)");
        int notRep = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (samples.BandOf(EquipGradeGenerator.Generate(samples, random, table, 50)) < 0)
            {
                notRep++;
            }
        }
        Report(notRep == 0, "倍率表非空时生成的值一定是某档的代表值(例外 " + notRep + " 次)");
        return bad;
    }

    // ------------------------------------------------------------ 物品清单

    private static int CheckItems(ItemCatalog catalog, List<ItemInfo> items, int seed)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        var masters = new List<ItemInfo>();
        var cards = new List<ItemInfo>();
        int withData = 0, beads = 0, beadsResolved = 0, reinforce = 0, amplifyTickets = 0, ticketOut = 0, rangeBad = 0;
        int cardsWithParts = 0, cardsNoParts = 0, setMembersTotal = 0;
        var rangeTypes = new int[5];
        var cardPart = new int[EquipmentParts.MagicStone + 1];
        foreach (ItemInfo info in items)
        {
            EnhanceData? d = info.Enhance;
            if (d == null)
            {
                continue;
            }
            withData++;
            if (d.SetMembers.Count > 0)
            {
                masters.Add(info);
                setMembersTotal += d.SetMembers.Count;
            }
            if (d.EnchantStats != null)
            {
                cards.Add(info);
                if (d.EnchantParts.Count > 0)
                {
                    cardsWithParts++;
                    foreach (int part in d.EnchantParts)
                    {
                        if (part >= 0 && part < cardPart.Length)
                        {
                            cardPart[part]++;
                        }
                    }
                }
                else
                {
                    cardsNoParts++;
                }
            }
            if (d.MonsterCardId != 0)
            {
                beads++;
                ItemInfo? card = catalog.Get(d.MonsterCardId);
                if (card?.Enhance?.EnchantStats != null)
                {
                    beadsResolved++;
                }
            }
            if (d.ReinforceTicket is { } t1)
            {
                reinforce++;
                if (t1.Level < 0 || t1.Level > EquipInstance.MaxUpgrade || t1.RatePercent < 0 || t1.RatePercent > 100)
                {
                    ticketOut++;
                }
            }
            if (d.AmplifyTicket is { } t2)
            {
                amplifyTickets++;
                if (t2.Level < 0 || t2.Level > EquipInstance.MaxUpgrade || t2.RatePercent < 0 || t2.RatePercent > 100)
                {
                    ticketOut++;
                }
            }
            foreach (UpgradeLevelRange range in d.UpgradeRanges)
            {
                if (range.Type >= 1 && range.Type <= 4)
                {
                    rangeTypes[range.Type]++;
                }
                if (range.Min > range.Max)
                {
                    rangeBad++;
                }
            }
        }
        Console.WriteLine("  有周边数据的 " + withData + " 件;套装主件 " + masters.Count + " 件(成员共 " + setMembersTotal + " 个);带 [enchant] 的 " + cards.Count
            + " 件(有部位表 " + cardsWithParts + "、没有 " + cardsNoParts + ");有宝珠换卡编号的 " + beads + " 件;强化券 " + reinforce + "、增幅强化券 " + amplifyTickets
            + ";等级区间:普通 " + rangeTypes[1] + "、增幅 " + rangeTypes[2] + "、分离 " + rangeTypes[3] + "、编年史 " + rangeTypes[4]);
        Console.WriteLine("  附魔卡可附的部位(10 到 21 逐个):" + string.Join("/", Enumerable.Range(EquipmentParts.Weapon, cardPart.Length - EquipmentParts.Weapon).Select(p => cardPart[p])));
        Report(beadsResolved == beads, "宝珠指向的卡片都取得到、带 [enchant] 块(取不到 " + (beads - beadsResolved) + " 个)");
        Report(ticketOut == 0, "强化券的等级与成功率都在服务端的取值范围里(越界 " + ticketOut + " 个)");
        Report(rangeBad == 0, "强化等级区间的下限都不大于上限(颠倒 " + rangeBad + " 个)");

        // 套装:成员互异、成员取得到;按成员表分组(25 张成员表被好几个主件共用),每张表穿齐才生效、少一件不生效、同一张表只算一套
        ItemInfo? Lookup(int code) => catalog.Get(code);
        int duplicate = 0, memberMissing = 0, backRefDiffer = 0;
        var lists = new Dictionary<string, List<ItemInfo>>();
        foreach (ItemInfo master in masters)
        {
            EnhanceData d = master.Enhance!;
            if (new HashSet<int>(d.SetMembers).Count != d.SetMembers.Count)
            {
                duplicate++;
            }
            string key = Ints(new SortedSet<int>(d.SetMembers));
            if (!lists.TryGetValue(key, out List<ItemInfo>? carriers))
            {
                carriers = new List<ItemInfo>();
                lists[key] = carriers;
            }
            carriers.Add(master);
        }
        int checkedLists = 0, multiMaster = 0, undiscovered = 0, lostDiscovery = 0, singles = 0, dedupeBad = 0, fullBad = 0, missingOneBad = 0, tierBad = 0;
        int withFull = 0, withTiers = 0;
        foreach (List<ItemInfo> carriers in lists.Values)
        {
            var distinct = new HashSet<int>(carriers[0].Enhance!.SetMembers);
            var worn = new List<ItemInfo>();
            foreach (int code in distinct)
            {
                ItemInfo? member = Lookup(code);
                if (member == null)
                {
                    memberMissing++;
                    continue;
                }
                if (carriers.All(c => EquipSets.MasterOf(member) != c.Code))
                {
                    backRefDiffer++;
                }
                worn.Add(member);
            }
            if (worn.Count != distinct.Count)
            {
                continue;
            }
            checkedLists++;
            if (carriers.Count > 1)
            {
                multiMaster++;
            }

            // 穿着里按"成员表等于这张表"挑出来的套装状态
            List<EquipSetState> Matching(IEnumerable<ItemInfo> wornSet) => EquipSets.Evaluate(wornSet, Lookup)
                .Where(st => new HashSet<int>(Lookup(st.Master)?.Enhance?.SetMembers ?? Array.Empty<int>()).SetEquals(distinct)).ToList();

            // 状态是否符合"穿了 pieces 件":件数对、穿齐才生效、每一档按件数不大于现有件数
            bool Fits(EquipSetState st, int pieces)
            {
                EnhanceData md = Lookup(st.Master)!.Enhance!;
                if (st.Worn != pieces || st.Total != distinct.Count || st.FullActive != (md.SetFullBonus != null && pieces == distinct.Count))
                {
                    return false;
                }
                foreach (SetPieceBonus tier in md.SetPieceBonuses)
                {
                    if (st.HasTier(tier.Pieces) != (tier.Pieces >= 1 && tier.Pieces <= pieces))
                    {
                        return false;
                    }
                }
                return true;
            }

            List<EquipSetState> all = Matching(worn);
            if (all.Count > 1)
            {
                dedupeBad++;
            }
            if (all.Count == 0)
            {
                undiscovered++;
                continue;
            }
            EnhanceData data = Lookup(all[0].Master)!.Enhance!;
            if (data.SetFullBonus != null)
            {
                withFull++;
            }
            if (data.SetPieceBonuses.Count > 0)
            {
                withTiers++;
            }
            if (!Fits(all[0], distinct.Count))
            {
                fullBad++;
                foreach (SetPieceBonus tier in data.SetPieceBonuses)
                {
                    if (all[0].HasTier(tier.Pieces) != (tier.Pieces >= 1 && tier.Pieces <= distinct.Count))
                    {
                        tierBad++;
                    }
                }
            }
            if (distinct.Count == 1)
            {
                singles++;
                continue;
            }
            for (int skip = 0; skip < worn.Count; skip++)
            {
                var fewer = new List<ItemInfo>(worn);
                fewer.RemoveAt(skip);
                List<EquipSetState> one = Matching(fewer);
                if (one.Count == 0)
                {
                    lostDiscovery++;
                    continue;
                }
                if (one.Count > 1)
                {
                    dedupeBad++;
                }
                if (!Fits(one[0], distinct.Count - 1))
                {
                    missingOneBad++;
                }
            }
        }
        Console.WriteLine("  套装主件里成员表有重复编号的 " + duplicate + " 件;成员取不到的 " + memberMissing + " 个;成员表 " + lists.Count + " 张(被多个主件共用的 " + multiMaster
            + " 张);成员的主件编号不指向这张表任何主件的 " + backRefDiffer + " 个;核对了 " + checkedLists + " 张(单件套装 " + singles + " 张;带穿齐加成的 " + withFull + "、带满 N 件档的 " + withTiers + ")");
        Console.WriteLine("  整张表穿齐时没有任何穿着件能指到这套的(只能靠成员自己的 [set item master] 或自己是主件) " + undiscovered + " 张;少穿一件就找不到这套的 " + lostDiscovery + " 次(穿着件里只有被拿掉的那件指得到)");
        Report(duplicate == 0, "套装成员表没有重复编号(重复 " + duplicate + " 件)");
        Report(dedupeBad == 0, "同一张成员表只算一套(多算的 " + dedupeBad + " 次)");
        Report(fullBad == 0, "穿齐成员表里每一件:件数等于成员数、穿齐加成按有无块生效、每一档都已生效(不符 " + fullBad + " 张;档不符 " + tierBad + " 个)");
        Report(missingOneBad == 0, "少穿任意一件:件数少一、穿齐不生效、档按\"件数不大于现有件数\"(不符 " + missingOneBad + " 次)");

        bad += CheckWearOverlay(catalog, items, masters, cards, seed);
        return bad;
    }

    /// <summary>
    /// 真实装备穿上装备栏、挂上叠加项(套装与附魔)之后:汇总的属性块等于各件的折算加上每个叠加项的折算(与"总和等于各部分之和"同一口径)。
    /// 随机挑一批套装把成员穿齐(同部位只穿后来的),再随机挑装备配上它能附的卡。
    /// </summary>
    private static int CheckWearOverlay(ItemCatalog catalog, List<ItemInfo> items, List<ItemInfo> masters, List<ItemInfo> cards, int seed)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        ItemInfo? Lookup(int code) => catalog.Get(code);
        var random = new Random(seed + 2);
        int rounds = 0, mismatch = 0, withTerms = 0;
        for (int n = 0; n < 300 && masters.Count > 0; n++)
        {
            ItemInfo master = masters[random.Next(masters.Count)];
            var slots = new EquipmentSlots();
            var overlay = new EquipBonusOverlay(c => Lookup(c)!);
            overlay.Attach(slots);
            foreach (int code in new HashSet<int>(master.Enhance!.SetMembers))
            {
                ItemInfo? member = Lookup(code);
                if (member != null && EquipmentSlots.IsSlot(member.Part))
                {
                    slots.Restore(member.Part, member);
                }
            }
            if (!SameBlock(slots.TotalStatBlock(), Expected(slots, overlay)))
            {
                mismatch++;
            }
            if (overlay.Terms(slots).Count > 0)
            {
                withTerms++;
            }
            rounds++;
        }
        Report(mismatch == 0, "真实套装穿上 " + rounds + " 次(其中有叠加项的 " + withTerms + " 次):属性块汇总等于各件折算加叠加项折算(不等 " + mismatch + " 次)");

        var wearable = items.Where(i => i.IsEquipment && EquipmentSlots.IsSlot(i.Part) && EnchantRules.IsEnchantable(i.Part)).ToList();
        int enchanted = 0, enchantMismatch = 0, refused = 0, refusedWrong = 0;
        for (int n = 0; n < 500 && wearable.Count > 0 && cards.Count > 0; n++)
        {
            ItemInfo gear = wearable[random.Next(wearable.Count)];
            ItemInfo card = cards[random.Next(cards.Count)];
            EnchantResult result = EnchantRules.ApplyCard(gear, card);
            bool allowed = card.Enhance!.EnchantParts.Contains(gear.Part);
            if (result.Success != allowed || (!result.Success && result.Code != EnchantCodes.Part))
            {
                refusedWrong++;
            }
            if (!result.Success)
            {
                refused++;
                continue;
            }
            var slots = new EquipmentSlots();
            var overlay = new EquipBonusOverlay(c => Lookup(c)!);
            overlay.Attach(slots);
            slots.Restore(gear.Part, result.Equipment);
            enchanted++;
            if (!SameBlock(slots.TotalStatBlock(), Expected(slots, overlay)) || overlay.EnchantOf(result.Equipment) == null)
            {
                enchantMismatch++;
            }
        }
        Report(refusedWrong == 0, "随机装备配随机卡 " + (enchanted + refused) + " 次:能附与否等于\"部位在卡的部位表里\",不能附的一律是部位不符(不符 " + refusedWrong + " 次;拒绝 " + refused + " 次)");
        Report(enchantMismatch == 0, "附上卡的装备穿上后汇总等于各件折算加叠加项折算(不等 " + enchantMismatch + " 次,共 " + enchanted + " 次)");
        return bad;
    }

    private static StatBlock Expected(EquipmentSlots slots, EquipBonusOverlay overlay)
    {
        StatBlock expected = default;
        foreach (ItemInfo item in slots.Items.ToList())
        {
            expected += item.Stats.ToStatBlock();
        }
        foreach (EquipStats term in overlay.Terms(slots))
        {
            expected += term.ToStatBlock();
        }
        return expected;
    }

    private static bool SameBlock(StatBlock a, StatBlock b)
    {
        return a.HpMax == b.HpMax && a.MpMax == b.MpMax && a.PhysicalAttack == b.PhysicalAttack && a.PhysicalDefense == b.PhysicalDefense
            && a.MagicalAttack == b.MagicalAttack && a.MagicalDefense == b.MagicalDefense && a.InventoryLimit == b.InventoryLimit
            && a.MpRegenSpeed == b.MpRegenSpeed && a.HitRecovery == b.HitRecovery;
    }

    // ------------------------------------------------------------ 强化流程

    /// <summary>总是成功的随机源:返回上界(强化成功当且仅当随机数不小于失败阈值)。</summary>
    private sealed class AlwaysWin : IDropRandom
    {
        public int NextInclusive(int max) => max;

        public int Range(int lo, int hi) => hi;
    }

    /// <summary>总是失败的随机源:返回 0(阈值为正时一定落在失败区间)。</summary>
    private sealed class AlwaysLose : IDropRandom
    {
        public int NextInclusive(int max) => 0;

        public int Range(int lo, int hi) => lo;
    }

    /// <summary>合成的装备(编号 900 起,部位上衣,等级 50);走增幅表的装备带增幅标记(普通与增幅不能混着强化)。</summary>
    private static ItemInfo Gear(int rarity, int level, bool amplified)
    {
        var item = new ItemInfo(900 + rarity, "equipment/synthetic/g" + rarity + ".equ", ItemKind.Equipment)
        {
            Part = EquipmentParts.Coat,
            Rarity = rarity,
            Grade = 50,
            MinimumLevel = 1,
        };
        return level == 0 && !amplified ? item : item.WithInstance(new EquipInstance(level, amplified));
    }

    private static UpgradeRequest RequestFor(UpgradeTable table, ItemInfo gear, bool amplify)
    {
        int level = gear.StateOrDefault.Upgrade;
        UpgradeRow? row = table.Row(level);
        return new UpgradeRequest
        {
            Equipment = gear,
            RequestAmplify = amplify,
            Material = row == null ? null : new ItemInfo(row.MaterialCode, "stackable/synthetic/m.stk", ItemKind.Stackable),
            MaterialCount = row == null ? 0 : Math.Max(row.MaterialCount, 0),
            Gold = 1000000000L,
            CharacterLevel = 100,
        };
    }

    private static int CheckWalk(string name, UpgradeTable table, int seed)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        bool amplify = name.StartsWith("增幅", StringComparison.Ordinal);
        int rows = table.RowCount;
        var win = new UpgradeSettings { NormalTable = table, AmplifyTable = table, Random = new AlwaysWin() };
        var lose = new UpgradeSettings { NormalTable = table, AmplifyTable = table, Random = new AlwaysLose() };
        for (int rarity = 0; rarity < UpgradeTable.Rarities; rarity++)
        {
            int cap = table.MaxLevel(rarity);
            int expectedSteps = Math.Min(EquipInstance.MaxUpgrade, rows);
            if (cap >= 0)
            {
                expectedSteps = Math.Min(expectedSteps, cap);
            }
            int expectedCode = cap >= 0 && cap < Math.Min(EquipInstance.MaxUpgrade, rows) ? UpgradeCodes.LevelCap : UpgradeCodes.MaxLevel;

            // 总是成功:一路升到不能再升
            ItemInfo gear = Gear(rarity, 0, amplify);
            int steps = 0, stopCode = -1, wrongStep = 0;
            while (steps <= 300)
            {
                UpgradeResult res = ItemUpgrade.Run(win, RequestFor(table, gear, amplify));
                if (res.Outcome != UpgradeOutcome.Success)
                {
                    stopCode = res.Code;
                    break;
                }
                if (res.NewLevel != res.OldLevel + 1 || res.Equipment.StateOrDefault.Upgrade != res.NewLevel)
                {
                    wrongStep++;
                }
                gear = res.Equipment;
                steps++;
            }
            Report(steps == expectedSteps && stopCode == expectedCode && wrongStep == 0,
                "稀有度 " + rarity + " 总是成功:升了 " + steps + " 级停下,错误码 0x" + stopCode.ToString("x", CultureInfo.InvariantCulture)
                + ",预期 " + expectedSteps + " 级与 0x" + expectedCode.ToString("x", CultureInfo.InvariantCulture) + ",步进不对 " + wrongStep + " 次");

            // 总是失败:每个等级试一次,惩罚与表里的惩罚档一致
            int failRows = 0, penaltyWrong = 0, successByZero = 0;
            for (int level = 0; level < expectedSteps; level++)
            {
                ItemInfo at = Gear(rarity, level, amplify);
                UpgradeResult res = ItemUpgrade.Run(lose, RequestFor(table, at, amplify));
                UpgradeRow row = table.Row(level)!;
                if (res.Outcome == UpgradeOutcome.Rejected)
                {
                    penaltyWrong++;
                    continue;
                }
                if (res.Threshold != row.FailChance)
                {
                    penaltyWrong++;
                }
                if (row.FailChance <= 0)
                {
                    if (res.Outcome != UpgradeOutcome.Success)
                    {
                        penaltyWrong++;
                    }
                    successByZero++;
                    continue;
                }
                failRows++;
                int type = table.PenaltyTypeAt(level, rarity);
                bool ok = res.Outcome == UpgradeOutcome.Failure
                    && (type == 1 ? res.NewLevel == level && !res.Destroyed
                        : type == 2 ? res.NewLevel <= level && !res.Destroyed
                        : type == 3 && (res.Destroyed || res.Protected));
                if (!ok)
                {
                    penaltyWrong++;
                }
            }
            Report(penaltyWrong == 0, "稀有度 " + rarity + " 总是失败:" + failRows + " 个有失败概率的等级,惩罚都与表一致(不符 " + penaltyWrong + " 个;失败概率为 0 的 " + successByZero + " 个)");
        }

        // 失败频率:抽 N 次,与阈值 /(分母 + 1) 比(随机数取 0 到分母含上界);在有失败概率(不是必成功、不是必失败)的等级里均匀挑 6 个
        var random = new DropRandom(seed);
        var settings = new UpgradeSettings { NormalTable = table, AmplifyTable = table, Random = random };
        const int Draws = 4000;
        var eligible = new List<int>();
        for (int level = 0; level < Math.Min(rows, EquipInstance.MaxUpgrade); level++)
        {
            int fail = table.Row(level)!.FailChance;
            if (fail > 0 && fail < ItemUpgrade.ProbabilityDenominator)
            {
                eligible.Add(level);
            }
        }
        int picks = Math.Min(6, eligible.Count);
        int outliers = 0;
        for (int k = 0; k < picks; k++)
        {
            int level = eligible[k * eligible.Count / picks];
            UpgradeRow row = table.Row(level)!;
            int failures = 0;
            ItemInfo at = Gear(0, level, amplify);
            for (int i = 0; i < Draws; i++)
            {
                if (ItemUpgrade.Run(settings, RequestFor(table, at, amplify)).Outcome == UpgradeOutcome.Failure)
                {
                    failures++;
                }
            }
            double p = row.FailChance / (double)(ItemUpgrade.ProbabilityDenominator + 1);
            double sigma = Math.Sqrt(p * (1 - p) / Draws);
            if (Math.Abs(failures / (double)Draws - p) > 5 * sigma + 1.0 / Draws)
            {
                outliers++;
            }
        }
        Report(picks > 0 && outliers == 0, "失败频率:抽了 " + picks + " 个等级各 " + Draws + " 次,偏离期望超过 5 个标准差的 " + outliers + " 个");
        return bad;
    }

    /// <summary>
    /// 整份清单里每件能强化部位的真实装备(部位 10 到 21)在 0 级、资源给足、角色等级取它自己强化等级区间的下限时,强化预览都不被前置检查拒绝
    /// (称号部位 <see cref="EquipmentParts.TitleName"/> 按服务端一律拒绝为"不能强化")。
    /// </summary>
    private static int CheckRealGear(UpgradeTable table, List<ItemInfo> items)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        var settings = new UpgradeSettings { NormalTable = table, AmplifyTable = table, Random = new AlwaysWin() };
        int swept = 0, rejected = 0, titles = 0, titlesWrong = 0, ranged = 0, goldBad = 0;
        var codes = new SortedDictionary<int, int>();
        foreach (ItemInfo gear in items)
        {
            if (!gear.IsEquipment || gear.Part < UpgradeTable.FirstPart || gear.Part > UpgradeTable.LastPart)
            {
                continue;
            }
            UpgradeLevelRange? range = gear.Enhance?.RangeOf(1);
            if (range != null)
            {
                ranged++;
            }
            UpgradeRequest request = RequestFor(table, gear, false);
            request.CharacterLevel = range?.Min ?? 50;
            ItemUpgrade.Plan plan = ItemUpgrade.Preview(settings, request);
            if (gear.Part == EquipmentParts.TitleName)
            {
                titles++;
                if (plan.Code != UpgradeCodes.NotUpgradable)
                {
                    titlesWrong++;
                }
                continue;
            }
            swept++;
            if (plan.Code != UpgradeCodes.Ok)
            {
                rejected++;
                codes[plan.Code] = codes.TryGetValue(plan.Code, out int n) ? n + 1 : 1;
            }
            else if (plan.Gold < 0 || plan.Info == null || plan.Info.GoldCost < 0)
            {
                goldBad++;
            }
        }
        Console.WriteLine("  真实装备:部位 10 到 21 共 " + (swept + titles) + " 件(称号 " + titles + " 件;写了普通强化等级区间的 " + ranged + " 件)");
        Report(rejected == 0, "称号以外的 " + swept + " 件预览都不被前置检查拒绝(被拒 " + rejected + " 件" + (codes.Count == 0 ? string.Empty
            : ":" + string.Join("、", codes.Select(c => "0x" + c.Key.ToString("x", CultureInfo.InvariantCulture) + " × " + c.Value))) + ")");
        Report(goldBad == 0, "预览出来的金币都是非负整数(不符 " + goldBad + " 件)");
        Report(titlesWrong == 0, "称号部位一律是\"不能强化\"(例外 " + titlesWrong + " 件)");
        return bad;
    }

    // ------------------------------------------------------------ 强化与品级换算成属性

    private static float[] SyntheticRates()
    {
        var cells = new float[SyntheticRateCells];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = (float)(0.5 + (i / 128.0));          // (64 + i) / 128,单精度下精确
        }
        return cells;
    }

    /// <summary>
    /// 真实表与真实装备上的换算:读得到浮动数据;每件能强化的装备在每个强化等级都算得出(不抛错、浮点项有限、分路与部位一致、
    /// 武器没有对应基础攻击就没有那一路);品级浮动在每个种子下都落在"幅度"里、不动值为 0 的项、同一个种子结果相同;
    /// 随机穿一批真实装备、给每件随机的强化等级与品级值,汇总等于各件折算加各叠加项折算。只打印计数,不打印数值。
    /// </summary>
    private static int CheckInstanceEffects(ItemCatalog catalog, UpgradeTable? table, EquipGradeVariation? variation, List<ItemInfo> gear, int seed)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        Report(variation != null, "品级浮动的百分比与上限读得进来(至少 " + EquipGradeVariation.ModeledPairs + " 对)");
        Report(table != null, "强化表读得进来");
        var rates = new WeaponUpgradeRateTable(SyntheticRates());

        // 强化:每件、每个等级
        if (table != null)
        {
            int rows = table.RowCount;
            int items = 0, calls = 0, nonEmpty = 0, thrown = 0, levelWrong = 0, partWrong = 0, floatBad = 0, weaponGate = 0;
            var byClass = new int[5];          // 武器、防具、首饰、辅助与魔法石、其它(称号)在最高等级有属性的件数
            foreach (ItemInfo item in gear)
            {
                if (item.Part < UpgradeTable.FirstPart || item.Part > UpgradeTable.LastPart)
                {
                    continue;
                }
                items++;
                for (int level = 1; level <= rows; level++)
                {
                    calls++;
                    EquipUpgradeEffect effect;
                    try
                    {
                        effect = EquipUpgradeEffect.Of(item.WithInstance(new EquipInstance(level)), table, rates);
                    }
                    catch (Exception)
                    {
                        thrown++;
                        continue;
                    }
                    if (effect.IsEmpty)
                    {
                        continue;
                    }
                    nonEmpty++;
                    if (effect.Level != level)
                    {
                        levelWrong++;
                    }
                    foreach (UpgradeChannels group in new[] { effect.Normal, effect.Alternate })
                    {
                        if (float.IsNaN(group.ArmorFloat) || float.IsInfinity(group.ArmorFloat) || float.IsNaN(group.AccessoryFloat) || float.IsInfinity(group.AccessoryFloat))
                        {
                            floatBad++;
                        }
                        bool weapon = item.Part == EquipmentParts.Weapon;
                        bool armor = item.Part >= EquipmentParts.Coat && item.Part <= EquipmentParts.Waist;
                        bool accessory = item.Part >= EquipmentParts.Amulet && item.Part <= EquipmentParts.Ring;
                        bool aux = item.Part == EquipmentParts.Support || item.Part == EquipmentParts.MagicStone;
                        bool onlyOwn = weapon ? group.PhysicalDefense == 0 && group.MagicalDefense == 0 && group.AllStats == 0 && group.ArmorFloat == 0f && group.AccessoryFloat == 0f
                            : armor ? group.PhysicalAttack == 0 && group.MagicalAttack == 0 && group.MagicalDefense == 0 && group.AllStats == 0 && group.AccessoryFloat == 0f
                            : accessory ? group.PhysicalAttack == 0 && group.MagicalAttack == 0 && group.PhysicalDefense == 0 && group.AllStats == 0 && group.ArmorFloat == 0f
                            : aux && group.PhysicalAttack == 0 && group.MagicalAttack == 0 && group.PhysicalDefense == 0 && group.MagicalDefense == 0 && group.ArmorFloat == 0f && group.AccessoryFloat == 0f;
                        if (!onlyOwn)
                        {
                            partWrong++;
                        }
                        if (weapon && ((int)item.Stats[EquipStat.EquipmentPhysicalAttack] == 0 && group.PhysicalAttack != 0
                            || (int)item.Stats[EquipStat.EquipmentMagicalAttack] == 0 && group.MagicalAttack != 0))
                        {
                            weaponGate++;
                        }
                    }
                }
                EquipUpgradeEffect top = EquipUpgradeEffect.Of(item.WithInstance(new EquipInstance(rows)), table, rates);
                if (!top.IsEmpty)
                {
                    byClass[item.Part == EquipmentParts.Weapon ? 0
                        : item.Part >= EquipmentParts.Coat && item.Part <= EquipmentParts.Waist ? 1
                        : item.Part >= EquipmentParts.Amulet && item.Part <= EquipmentParts.Ring ? 2
                        : item.Part == EquipmentParts.Support || item.Part == EquipmentParts.MagicStone ? 3 : 4]++;
                }
            }
            Console.WriteLine("  能强化的部位 10 到 21 共 " + items + " 件,按每件每级算了 " + calls + " 次,有属性的 " + nonEmpty + " 次;最高等级有属性的件数:武器 "
                + byClass[0] + "、防具 " + byClass[1] + "、首饰 " + byClass[2] + "、辅助与魔法石 " + byClass[3] + "、称号 " + byClass[4]);
            Report(thrown == 0, "每件每级都算得出,不抛错(抛错 " + thrown + " 次)");
            Report(levelWrong == 0, "算出来的强化等级与给的一致(不符 " + levelWrong + " 次)");
            Report(partWrong == 0, "每一路只落在自己部位的格子里(串路 " + partWrong + " 次)");
            Report(floatBad == 0, "浮点项都是有限数(非有限 " + floatBad + " 次)");
            Report(weaponGate == 0, "武器没有对应基础攻击就没有那一路(例外 " + weaponGate + " 次)");
            Report(byClass[4] == 0, "称号在最高等级也没有强化属性(例外 " + byClass[4] + " 件)");
        }

        // 真实的武器倍率表(本机文件,不入库):只数计数,不打印数值
        string? rateFile = Environment.GetEnvironmentVariable("DNF_WEAPON_RATE_FILE");
        if (string.IsNullOrEmpty(rateFile) || !File.Exists(rateFile))
        {
            Console.WriteLine("  没给 DNF_WEAPON_RATE_FILE:跳过真实倍率表的检查(用 tools/py/weapon_rate_dump.py 在本机生成)");
        }
        else
        {
            bool loaded = WeaponUpgradeRateTable.TryParse(File.ReadAllLines(rateFile), out WeaponUpgradeRateTable? real);
            Report(loaded, "本机的武器倍率表文本读得进来(" + WeaponUpgradeRateTable.Cells + " 或 " + WeaponUpgradeRateTable.CellsWithLastJob + " 个数)");
            if (loaded && real != null)
            {
                int cells = 0, nonPositive = 0;
                for (int job = 0; job < WeaponUpgradeRateTable.Jobs; job++)
                {
                    for (int sub = 0; sub < WeaponUpgradeRateTable.SubTypes; sub++)
                    {
                        foreach (bool physical in new[] { true, false })
                        {
                            cells++;
                            float v = real.Cell(job, sub, physical);
                            if (!(v > 0f) || float.IsInfinity(v))
                            {
                                nonPositive++;
                            }
                        }
                    }
                }
                int weapons = 0, noJob = 0, subOut = 0, usable = 0, physicalBase = 0, physicalRated = 0, magicalBase = 0, magicalRated = 0, thief = 0;
                foreach (ItemInfo item in gear)
                {
                    if (item.Part != EquipmentParts.Weapon)
                    {
                        continue;
                    }
                    weapons++;
                    if (WeaponUpgradeRateTable.FirstJob(item) < 0)
                    {
                        noJob++;
                    }
                    else if ((uint)item.SubType >= WeaponUpgradeRateTable.SubTypes)
                    {
                        subOut++;
                    }
                    else
                    {
                        usable++;
                        if (WeaponUpgradeRateTable.FirstJob(item) >= WeaponUpgradeRateTable.Jobs)
                        {
                            thief++;
                        }
                    }
                    if ((int)item.Stats[EquipStat.EquipmentPhysicalAttack] != 0)
                    {
                        physicalBase++;
                        if (real.RateOf(item, true) > 0f)
                        {
                            physicalRated++;
                        }
                    }
                    if ((int)item.Stats[EquipStat.EquipmentMagicalAttack] != 0)
                    {
                        magicalBase++;
                        if (real.RateOf(item, false) > 0f)
                        {
                            magicalRated++;
                        }
                    }
                }
                Console.WriteLine("  真实倍率表:武器 " + weapons + " 件;没写可用职业 " + noJob + " 件、武器种类不在 0 到 5 的 " + subOut + " 件、能查倍率的 " + usable
                    + " 件(其中第一职业是第 11 个职业的 " + thief + " 件);带装备物理攻击的 " + physicalBase + " 件里倍率为正的 " + physicalRated
                    + " 件,带装备魔法攻击的 " + magicalBase + " 件里倍率为正的 " + magicalRated + " 件");
                Report(nonPositive == 0, "前 " + WeaponUpgradeRateTable.Jobs + " 个职业的 " + cells + " 格倍率都是正的有限数(不是 " + nonPositive + " 格)");
            }
        }

        // 品级浮动:幅度、零值、确定性
        if (variation != null)
        {
            int items = 0, calls = 0, varied = 0, outOfRange = 0, zeroMoved = 0, nondeterministic = 0, nonApplicableMoved = 0, gradeZeroMoved = 0;
            foreach (ItemInfo item in gear)
            {
                bool applies = EquipGradeVariation.AppliesTo(item.Part);
                if (!applies)
                {
                    if (!variation.DeltaOf(item.Stats, item.Part, 7).IsEmpty)
                    {
                        nonApplicableMoved++;
                    }
                    continue;
                }
                items++;
                if (!variation.DeltaOf(item.Stats, item.Part, 0).IsEmpty)
                {
                    gradeZeroMoved++;
                }
                bool any = false;
                foreach (int s in VariationSeeds)
                {
                    calls++;
                    EquipStats a = variation.DeltaOf(item.Stats, item.Part, s);
                    if (!a.Equals(variation.DeltaOf(item.Stats, item.Part, s)))
                    {
                        nondeterministic++;
                    }
                    any |= !a.IsEmpty;
                    for (int i = 0; i < EquipGradeVariation.ModeledPairs; i++)
                    {
                        EquipStat stat = EquipGradeVariation.Stats[i];
                        int baseValue = (int)item.Stats[stat];
                        int delta = (int)a[stat];
                        if (baseValue == 0)
                        {
                            if (delta != 0)
                            {
                                zeroMoved++;
                            }
                            continue;
                        }
                        int range = EquipGradeVariation.RoundHalfAway((float)variation.PercentOf(i) * (float)baseValue / 100f);
                        int bound = Math.Max(Math.Abs(range), Math.Max(variation.CapOf(i), 0));
                        if (Math.Abs(delta) > bound)
                        {
                            outOfRange++;
                        }
                    }
                }
                if (any)
                {
                    varied++;
                }
            }
            Console.WriteLine("  品级浮动:适用的装备 " + items + " 件,每件 " + VariationSeeds.Length + " 颗种子共算了 " + calls + " 次,至少浮出一项的 " + varied + " 件");
            Report(outOfRange == 0, "浮动差都不超过\"幅度与上限里较大的那个\"(越界 " + outOfRange + " 项)");
            Report(zeroMoved == 0, "基础值为 0 的项不浮动(被动了 " + zeroMoved + " 项)");
            Report(nondeterministic == 0, "同一颗种子结果相同(不同 " + nondeterministic + " 次)");
            Report(nonApplicableMoved == 0, "称号、宠物、时装与未知部位不浮动(例外 " + nonApplicableMoved + " 件)");
            Report(gradeZeroMoved == 0, "品级值 0(没有品级)不浮动(例外 " + gradeZeroMoved + " 件)");
        }

        // 随机穿一批真实装备:每件随机强化等级与品级值,汇总等于各件折算加叠加项折算
        if (table != null)
        {
            var bonus = EquipInstanceBonus.For(table, variation, rates);
            var perPart = new Dictionary<int, List<ItemInfo>>();
            foreach (ItemInfo item in gear)
            {
                if (EquipmentSlots.IsSlot(item.Part))
                {
                    if (!perPart.TryGetValue(item.Part, out List<ItemInfo>? list))
                    {
                        list = new List<ItemInfo>();
                        perPart[item.Part] = list;
                    }
                    list.Add(item);
                }
            }
            var random = new Random(seed + 7);
            int rounds = 0, mismatch = 0, statsMismatch = 0, withInstanceTerm = 0, termsChange = 0;
            for (int n = 0; n < 300 && perPart.Count > 0; n++)
            {
                var slots = new EquipmentSlots();
                var overlay = new EquipBonusOverlay(c => catalog.Get(c)!) { InstanceBonus = bonus };
                overlay.Attach(slots);
                var plain = new List<ItemInfo>();
                foreach (KeyValuePair<int, List<ItemInfo>> pair in perPart)
                {
                    ItemInfo item = pair.Value[random.Next(pair.Value.Count)];
                    int level = random.Next(0, table.RowCount + 1);
                    int grade = random.Next(0, 3) == 0 ? 0 : random.Next(1, 32768);
                    ItemInfo worn = item.WithInstance(new EquipInstance(level, gradeValue: grade));
                    if (slots.Restore(item.Part, worn))
                    {
                        plain.Add(item);
                    }
                }
                if (!SameBlock(slots.TotalStatBlock(), Expected(slots, overlay)))
                {
                    mismatch++;
                }
                var wornStats = EquipStats.Sum(slots.Items.Select(i => i.Stats));
                EquipStats expectedStats = EquipStats.Sum(overlay.Terms(slots).Prepend(wornStats));
                if (!slots.TotalStats().Equals(expectedStats))
                {
                    statsMismatch++;
                }
                if (slots.Items.Any(i => bonus.BonusOf(i) != null))
                {
                    withInstanceTerm++;
                    if (!slots.TotalStats().Equals(EquipStats.Sum(plain.Select(i => i.Stats))))
                    {
                        termsChange++;
                    }
                }
                rounds++;
            }
            Report(mismatch == 0, "随机穿真实装备 " + rounds + " 次(每件随机强化等级与品级值):属性块汇总等于各件折算加叠加项折算(不等 " + mismatch + " 次)");
            Report(statsMismatch == 0, "同上,装备属性表汇总等于各件加各叠加项(不等 " + statsMismatch + " 次)");
            Report(withInstanceTerm == 0 || termsChange > 0, "有个体加成的 " + withInstanceTerm + " 次里,汇总确实不同于没有个体状态时的(不同 " + termsChange + " 次)");
        }
        return bad;
    }

    private static string ChannelsLine(UpgradeChannels c)
    {
        return Ints(new[] { c.PhysicalAttack, c.MagicalAttack, c.PhysicalDefense, c.MagicalDefense, c.AllStats }) + "," + Bits(c.ArmorFloat) + "," + Bits(c.AccessoryFloat);
    }

    private static List<string> EffectLines(UpgradeTable? table, List<ItemInfo> gear)
    {
        var lines = new List<string>();
        if (table == null)
        {
            lines.Add("V\tinvalid");
            return lines;
        }
        var rates = new WeaponUpgradeRateTable(SyntheticRates());
        var levels = new List<int>(EffectLevels) { table.RowCount, table.RowCount + 1 };
        foreach (ItemInfo item in gear)
        {
            if (item.Part < UpgradeTable.FirstPart || item.Part > UpgradeTable.LastPart)
            {
                continue;
            }
            foreach (int level in levels)
            {
                // 个体状态的强化等级是一个字节(0 到 255,正常强化只到 MaxUpgrade);表有 255 行时的 rows + 1 不可能存在,按"没有效果"写,与 Python 一侧的越界行一致
                EquipUpgradeEffect effect = level > byte.MaxValue
                    ? EquipUpgradeEffect.None
                    : EquipUpgradeEffect.Of(item.WithInstance(new EquipInstance(level)), table, rates);
                lines.Add(item.Code.ToString(CultureInfo.InvariantCulture) + " " + level.ToString(CultureInfo.InvariantCulture) + "\t"
                    + (effect.IsEmpty ? "-" : ChannelsLine(effect.Normal) + "|" + ChannelsLine(effect.Alternate)));
            }
        }
        return lines;
    }

    private static List<string> VariationLines(EquipGradeVariation? variation, List<ItemInfo> gear)
    {
        var lines = new List<string>();
        if (variation == null)
        {
            lines.Add("V\tinvalid");
            return lines;
        }
        foreach (ItemInfo item in gear)
        {
            if (!EquipGradeVariation.AppliesTo(item.Part))
            {
                continue;
            }
            foreach (int seed in VariationSeeds)
            {
                EquipStats delta = variation.DeltaOf(item.Stats, item.Part, seed);
                lines.Add(item.Code.ToString(CultureInfo.InvariantCulture) + " " + seed.ToString(CultureInfo.InvariantCulture) + "\t"
                    + Ints(EquipGradeVariation.Stats.Select(stat => (int)delta[stat])));
            }
        }
        return lines;
    }

    // ------------------------------------------------------------ 转储

    private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    private static string Ints(IEnumerable<int> values) => string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static void WriteLines(string path, IEnumerable<string> lines)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (string line in lines)
        {
            writer.Write(line);
            writer.Write('\n');
        }
    }

    private static List<string> UpgradeLines(UpgradeTable? t)
    {
        var lines = new List<string>();
        if (t == null)
        {
            lines.Add("V\tinvalid");
            return lines;
        }
        int rows = t.RowCount;
        lines.Add("M\t" + rows + " " + t.NoticeLevel);
        for (int r = 0; r < UpgradeTable.Rarities; r++)
        {
            lines.Add("D " + r + "\t" + t.DestroyLevel(r) + " " + t.MaxLevel(r));
        }
        for (int l = 0; l < UpgradeTable.CostSlots; l++)
        {
            lines.Add("P " + l + "\t" + Bits(t.RepairCostRate(l)));
        }
        for (int l = 0; l < rows; l++)
        {
            UpgradeRow row = t.Row(l)!;
            lines.Add("R " + l + "\t" + row.FailChance + " " + row.PenaltyType + " " + row.PenaltyLevels + " " + row.MaterialCode + " " + row.MaterialCount
                + " " + Ints(row.Amplification) + " " + string.Join(",", row.StatsA.Select(Bits)) + " " + string.Join(",", row.StatsB.Select(Bits)));
        }
        for (int r = -1; r < 8; r++)
        {
            for (int l = -1; l < rows + 2; l++)
            {
                lines.Add("T " + r + " " + l + "\t" + t.PenaltyTypeAt(l, r));
            }
        }
        if (rows == 0)
        {
            return lines;
        }
        foreach (int r in new[] { -1, 0, 5, 6 })
        {
            foreach (int g in new[] { -1, 0, 99, 100 })
            {
                foreach (int l in new[] { -1, 0, rows - 1, rows })
                {
                    foreach (int p in new[] { 9, 10, 21, 22 })
                    {
                        lines.Add("N " + r + " " + g + " " + l + " " + p + "\t" + (t.GetInfo(r, g, l, p) != null ? "ok" : "null"));
                    }
                }
            }
        }
        for (int r = 0; r < UpgradeTable.Rarities; r++)
        {
            for (int g = 0; g < UpgradeTable.CostSlots; g++)
            {
                foreach (int few in FewLevels)
                {
                    int lv = few == -1 ? rows - 1 : few;
                    AddScaledAndGold(lines, t, r, g, lv, true);
                }
            }
        }
        foreach ((int r, int g) in Sampled)
        {
            for (int lv = 0; lv < rows; lv++)
            {
                UpgradeInfo info = t.GetInfo(r, g, lv, UpgradeTable.FirstPart)!;
                lines.Add(ScaledLine(r, g, lv, info));
            }
            var levels = new List<int>();
            for (int lv = 0; lv <= GoldLevelsEnd; lv++)
            {
                if (lv < rows)
                {
                    levels.Add(lv);
                }
            }
            levels.Add(rows - 1);
            foreach (int lv in levels)
            {
                AddScaledAndGold(lines, t, r, g, lv, false);
            }
        }
        return lines;
    }

    private static string ScaledLine(int r, int g, int lv, UpgradeInfo info)
    {
        return "S " + r + " " + g + " " + lv + "\t" + string.Join(",", info.StatsA.Select(Bits)) + " " + string.Join(",", info.StatsB.Select(Bits));
    }

    private static void AddScaledAndGold(List<string> lines, UpgradeTable t, int r, int g, int lv, bool withScaled)
    {
        var golds = new List<int>();
        UpgradeInfo? first = null;
        for (int part = UpgradeTable.FirstPart; part <= UpgradeTable.LastPart; part++)
        {
            UpgradeInfo info = t.GetInfo(r, g, lv, part)!;
            first ??= info;
            golds.Add(info.GoldCost);
        }
        if (withScaled)
        {
            lines.Add(ScaledLine(r, g, lv, first!));
        }
        lines.Add("G " + r + " " + g + " " + lv + "\t" + Ints(golds));
    }

    private static List<string> GradeLines(EquipGradeSamples? samples)
    {
        var lines = new List<string>();
        if (samples == null)
        {
            lines.Add("V\tinvalid");
            return lines;
        }
        lines.Add("V\t" + samples.BandCount);
        for (int band = 0; band < samples.BandCount; band++)
        {
            IReadOnlyList<int> list = samples.Samples(band);
            long sum = 0;
            foreach (int v in list)
            {
                sum += v;
            }
            lines.Add("B " + band + "\t" + list.Count + " " + (list.Count > 0 ? list[0] : 0) + " " + sum);
        }
        return lines;
    }

    /// <summary>按剧本给数的随机源:返回 min(剧本数, 上界),记下被问的上界(没被问是 -1)。</summary>
    private sealed class RollStub : IDropRandom
    {
        private readonly int _roll;

        public RollStub(int roll)
        {
            _roll = roll;
        }

        public int LastMax { get; private set; } = -1;

        public int NextInclusive(int max)
        {
            LastMax = max;
            return Math.Min(_roll, max);
        }

        public int Range(int lo, int hi) => throw new NotSupportedException();
    }

    private static List<string> PickLines()
    {
        var lines = new List<string>();
        foreach ((string name, (int Key, float Value)[] table) in MultiplierTables)
        {
            var list = table.Select(x => new KeyValuePair<int, float>(x.Key, x.Value)).ToList();
            for (int level = 0; level <= 80; level++)
            {
                lines.Add("L " + name + " " + level + "\t" + Bits(EquipGradeGenerator.MultiplierFor(list, level)));
            }
        }
        foreach (float mult in Multipliers)
        {
            foreach (int[] weights in WeightSets)
            {
                foreach (int roll in Rolls)
                {
                    var stub = new RollStub(roll);
                    int band = EquipGradeGenerator.Pick(weights, mult, stub);
                    lines.Add("K " + Bits(mult) + " " + Ints(weights) + " " + roll + "\t" + band + " " + stub.LastMax);
                }
            }
        }
        return lines;
    }

    // ---- 物品的规范文本(与 enhance_ref.py 的 read_item 同格式) ----

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatStats(EquipStats stats)
    {
        if (stats.IsEmpty)
        {
            return "-";
        }
        var parts = new List<KeyValuePair<string, string>>();
        foreach (EquipStatEntry entry in stats.Entries)
        {
            string numbers = FormatNumber(entry.First) + (entry.HasSecond ? "," + FormatNumber(entry.Second) : string.Empty);
            parts.Add(new KeyValuePair<string, string>(EquipStats.TagOf(entry.Stat), numbers));
        }
        parts.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return string.Join(";", parts.Select(p => p.Key + "=" + p.Value));
    }

    private static string FormatItem(ItemInfo? info)
    {
        if (info == null)
        {
            return "<none>";
        }
        EnhanceData? d = info.Enhance;
        if (d == null)
        {
            return "set=0/-/~/-|ench=~/0/-|tick=-/-|rng=-";
        }
        string members = d.SetMembers.Count == 0 ? "-" : Ints(d.SetMembers);
        string full = d.SetFullBonus == null ? "~" : FormatStats(d.SetFullBonus);
        string pieces = d.SetPieceBonuses.Count == 0
            ? "-"
            : string.Join("^", d.SetPieceBonuses.Select(p => p.Pieces.ToString(CultureInfo.InvariantCulture) + ":" + FormatStats(p.Stats)));
        string enchant = d.EnchantStats == null ? "~" : FormatStats(d.EnchantStats);
        string parts = d.EnchantParts.Count == 0 ? "-" : Ints(d.EnchantParts);
        string reinforce = d.ReinforceTicket is { } r ? r.Level + ":" + r.RatePercent : "-";
        string amplify = d.AmplifyTicket is { } a ? a.Level + ":" + a.RatePercent : "-";
        string ranges = d.UpgradeRanges.Count == 0 ? "-" : string.Join(",", d.UpgradeRanges.Select(x => x.Type + ":" + x.Min + ":" + x.Max));
        return "set=" + d.SetMaster + "/" + members + "/" + full + "/" + pieces + "|ench=" + enchant + "/" + d.MonsterCardId + "/" + parts
            + "|tick=" + reinforce + "/" + amplify + "|rng=" + ranges;
    }
}
