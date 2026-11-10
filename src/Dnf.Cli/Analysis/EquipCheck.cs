using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// equip-check:装备栏与穿脱的检查(证据 E-118)。整库扫描能掉落的装备,检查穿戴规则对真实数据的结构:
/// 每件装备的类型都认得、按自己的类型放槽不会"类型不符"、可用职业的写法都能解成职业下标;再看剑魂能穿的武器——主角画面上的外观图层与
/// 每个攻击动作的动画(<see cref="HeroLook.RequiredWeaponMotions"/>)有没有;最后让一个等级 --level 的剑魂把背包里每个部位随机一件能穿的都穿上,
/// 核对装备栏、背包与面板加成的账。数值、物品名与件数是用户自己的数据,只打印在本机的终端上(不要抄进入库的文件);通过与否只看结构。
/// <para>
/// 装备属性与外观:穿戴演习里再核对属性出口(穿着的属性汇总、主角属性、版本号);<see cref="CheckStatsAndLook"/> 对整份装备清单
/// (不是只有能掉落的)核对读出的属性与外观的结构不变量,与旧的平读读法对照(只数个数),在真实数据上核对"汇总等于各件之和"。
/// 环境变量 <c>DNF_EQUIP_DUMP</c> 给了路径就把整库逐件转储成文本(<c>编号\t属性\t外观</c>,格式与 <c>tools/py/equip_stats_ref.py dump</c> 相同),
/// 再用那个脚本的 <c>compare</c> 逐字段比对两个独立实现(只报计数)。转储只写在被 git 忽略的 out/ 下,不入库。
/// </para>
/// </summary>
internal static class EquipCheck
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
        List<ItemInfo> gear = catalog.Droppable().Where(i => i.IsEquipment).ToList();
        watch.Stop();
        Report(gear.Count > 0, "能掉落的装备 " + gear.Count + " 件(整库扫描用时 " + watch.ElapsedMilliseconds + " 毫秒)");
        if (gear.Count == 0)
        {
            return 1;
        }

        // 2. 类型与可用职业的结构
        int slotted = gear.Count(i => EquipmentSlots.IsSlot(i.Part));
        int avatar = gear.Count(i => EquipmentParts.IsAvatar(i.Part));
        int creature = gear.Count(i => EquipmentParts.IsCreature(i.Part));
        int unknown = gear.Count(i => i.Part == EquipmentParts.Unknown);
        Console.WriteLine("  构成:有装备栏格子的(部位 " + EquipmentSlots.First + " 到 " + EquipmentSlots.Last + ")" + slotted + "、时装类 " + avatar + "、宠物类 " + creature + "、不认得 " + unknown);
        Report(slotted > 0, "有装备栏格子的装备不是空的");
        Report(unknown == 0, "装备类型都认得(不认得的 " + unknown + " 件)");
        Report(slotted + avatar + creature + unknown == gear.Count, "部位分类互不重叠、没有遗漏");

        int noJobs = gear.Count(i => i.UsableJobs.Count == 0);
        Console.WriteLine("  没写 [usable job] 的(没有任何职业能穿)" + noJobs + " 件");
        var perJob = new int[EquipRules.JobCount];
        foreach (ItemInfo item in gear.Where(i => EquipmentSlots.IsSlot(i.Part)))
        {
            for (int job = 0; job < EquipRules.JobCount; job++)
            {
                if (EquipRules.UsableBy(item, job))
                {
                    perJob[job]++;
                }
            }
        }
        Console.WriteLine("  各职业能穿的(有格子的装备):" + string.Join("/", perJob));
        Report(perJob[EquipRules.SwordmanJob] > 0, "剑魂有能穿的装备");

        // 3. 规则:按自己的类型放槽时,结果只可能是 通过 / 职业不符 / 等级不够 / 没有格子
        int hero = opts.HeroLevel;
        var results = new int[Enum.GetValues(typeof(EquipResult)).Length];
        foreach (ItemInfo item in gear)
        {
            results[(int)EquipRules.Check(item, EquipRules.SwordmanJob, hero)]++;
        }
        Console.WriteLine("  剑魂 " + hero + " 级逐件检查:通过 " + results[(int)EquipResult.Ok] + "、职业不符 " + results[(int)EquipResult.WrongJob]
            + "、等级不够 " + results[(int)EquipResult.LevelTooLow] + "、没有格子 " + results[(int)EquipResult.NotWearable]);
        Report(results[(int)EquipResult.NoItem] + results[(int)EquipResult.NotEquipment] + results[(int)EquipResult.WrongSlot] + results[(int)EquipResult.NotShowable] == 0,
            "按自己的类型放槽时不会出现 没有物品/不是装备/类型不符/画面穿不出来");
        Report(results[(int)EquipResult.NotWearable] == avatar + creature + unknown, "没有格子的正好是时装类、宠物类与不认得的");
        Report(results[(int)EquipResult.Ok] + results[(int)EquipResult.WrongJob] + results[(int)EquipResult.LevelTooLow] + results[(int)EquipResult.NotWearable] == gear.Count, "四种结果加起来等于总数");

        // 4. 剑魂能穿的武器:外观图层与每个攻击动作的动画
        List<ItemInfo> weapons = gear.Where(i => i.Part == EquipmentParts.Weapon && EquipRules.UsableBy(i, EquipRules.SwordmanJob)).ToList();
        Console.WriteLine("  剑魂能穿的武器(不论等级)" + weapons.Count + " 件");
        Report(weapons.Count > 0, "剑魂有能穿的武器");
        AvatarClass cls = AvatarClass.FromChr(assets, DemoHero.SwordmanChrPath, DemoHero.SwordmanJobTag);
        int noLook = 0, shown = 0;
        var missingMotion = new int[HeroLook.RequiredWeaponMotions.Length];
        var showable = new List<ItemInfo>();
        foreach (ItemInfo weapon in weapons)
        {
            AvatarItem look = AvatarItem.Read(assets, cls, weapon.Path);
            if (look == null || look.Layers.Count == 0)
            {
                noLook++;
                continue;
            }
            var outfit = new AvatarOutfit(cls, 0, new[] { look });
            bool all = true;
            for (int m = 0; m < HeroLook.RequiredWeaponMotions.Length; m++)
            {
                bool found = outfit.WeaponAnimationPaths(HeroLook.RequiredWeaponMotions[m]).Any(p => assets.LoadAni(p) != null);
                if (!found)
                {
                    missingMotion[m]++;
                    all = false;
                }
            }
            if (all)
            {
                shown++;
                showable.Add(weapon);
            }
        }
        Console.WriteLine("  其中没有剑魂外观图层的 " + noLook + " 件;外观齐全、每个攻击动作都有动画的 " + shown + " 件;缺动画的按动作:"
            + string.Join("/", HeroLook.RequiredWeaponMotions.Select((m, i) => m.Replace(".ani", string.Empty) + " " + missingMotion[i])));
        Report(shown > 0, "有能在画面上穿出来的武器");
        int canShowDisagrees = showable.Count(w => !HeroLook.CanShow(assets, w));
        Report(canShowDisagrees == 0, "HeroLook.CanShow 与逐动作检查一致(不一致的 " + canShowDisagrees + " 件)");

        // 起手武器(演示用)要穿得出来
        var starter = new ItemInfo(0, DemoHero.SwordmanEquipRoot + "/" + DemoHero.StarterWeapon, ItemKind.Equipment) { Part = EquipmentParts.Weapon };
        Report(HeroLook.CanShow(assets, starter), "起手武器的外观与每个攻击动作的动画齐全");

        // 5. 穿戴演习:每个有格子的部位随机挑一件剑魂这个等级穿得上的,放进背包再穿上,核对账目
        var random = new Random(opts.Seed);
        var progress = HeroProgress.CreateForLocalHero(assets, hero);
        var picked = new List<ItemInfo>();
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            int p = part;
            List<ItemInfo> candidates = gear.Where(i => i.Part == p && EquipRules.Check(i, EquipRules.SwordmanJob, hero) == EquipResult.Ok
                && (p != EquipmentParts.Weapon || showable.Contains(i))).ToList();
            if (candidates.Count == 0)
            {
                Console.WriteLine("  部位 " + part + ":这个等级没有能穿的候选");
                continue;
            }
            ItemInfo item = candidates[random.Next(candidates.Count)];
            picked.Add(item);
            Report(progress.AddItem(item), "部位 " + part + " 的候选放进背包");
        }
        int before = progress.Bag.Tab(InventoryTab.Equipment).Count;
        int versionBefore = progress.Version;
        StatBlock baseBefore = progress.BaseStats;
        int worn = 0;
        while (progress.Bag.Tab(InventoryTab.Equipment).Count > 0)
        {
            EquipResult r = progress.Equip(0, item => HeroLook.CanShow(assets, item));
            if (r != EquipResult.Ok)
            {
                Console.WriteLine("  穿不上:" + r);
                break;
            }
            worn++;
        }
        Report(worn == before && progress.EquippedCount == picked.Count, "挑出的 " + picked.Count + " 件都穿上了,装备栏 " + progress.EquippedCount + " 件,背包装备页剩 "
            + progress.Bag.Tab(InventoryTab.Equipment).Count + " 件");
        Report(progress.EquipVersion == worn, "装备栏的变化次数等于穿上的件数");

        // 属性出口:穿着装备的基础属性汇总进主角属性(穿上加);版本号随装备栏一起走,战斗会话据此换面板
        EquipStats wornExpected = EquipStats.Sum(picked.Select(i => i.Stats));
        Report(progress.WornStats.Equals(wornExpected), "穿着装备的属性汇总等于挑出各件之和(" + progress.WornStats.Count + " 项)");
        StatBlock statsExpected = progress.BaseStats;
        foreach (ItemInfo item in picked)
        {
            statsExpected += item.Stats.ToStatBlock();
        }
        Report(SameBlock(progress.Stats, statsExpected), "主角属性等于等级属性加各件折算之和");
        Report(SameBlock(progress.BaseStats, baseBefore), "穿装备没有动等级属性");
        Report(progress.Version == versionBefore + worn, "属性出口的版本号随穿脱一起变(加了 " + (progress.Version - versionBefore) + ",穿了 " + worn + " 件)");
        EquipmentStats expected = picked.Aggregate(default(EquipmentStats), (sum, item) => sum.Plus(EquipmentStats.Read(assets, item.Path)));
        EquipmentStats panel = HeroPanel.WornEquipment(assets, null, progress);
        Report(panel.PhysicalAttack == expected.PhysicalAttack && panel.MagicalAttack == expected.MagicalAttack,
            "面板装备加成等于挑出各件的加总(物攻 " + panel.PhysicalAttack + "、魔攻 " + panel.MagicalAttack + ")");

        // 存档往返(M26):再放几样可叠加物品、记一笔金币与经验,拍快照写成文本、读回来,还原到一个新进度上:物品都能按编号找到,再拍的快照与原来逐字节相同
        progress.AddGold(98765);
        progress.Gain(1234);
        foreach (ItemInfo stack in catalog.Droppable().Where(i => !i.IsEquipment).Take(5))
        {
            progress.AddItem(stack, 1 + random.Next(3));
        }
        string saveText = progress.Snapshot().ToText();
        bool parsed = HeroSave.TryParse(saveText, out HeroSave parsedSave, out string parseError);
        Report(parsed, "存档文本能读回来" + (parsed ? string.Empty : ":" + parseError));
        if (parsed)
        {
            HeroProgress restored = HeroProgress.CreateForLocalHero(assets, 1);
            SaveApplyReport applied = restored.RestoreFromData(parsedSave, assets);
            Report(applied.Dropped == 0 && applied.Moved == 0, "按编号还原:丢掉 " + applied.Dropped + " 条、挪回背包 " + applied.Moved + " 件");
            Report(restored.Snapshot().ToText() == saveText, "还原之后再拍的快照与原来逐字节相同(等级 " + restored.Level + "、金币 " + restored.Gold + "、穿着 " + restored.EquippedCount
                + " 件、背包 " + restored.BagItems + " 件占 " + restored.BagSlots + " 格)");
            Report(restored.WeaponPath == progress.WeaponPath && restored.EquippedCount == progress.EquippedCount, "穿着的件数与武器的路径一致");
            Report(restored.WornStats.Equals(progress.WornStats), "读档还原之后穿着装备的属性汇总与存档前相同");
        }

        // 再脱下来
        int off = 0;
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            if (progress.Worn.Get(part) != null && progress.Unequip(part))
            {
                off++;
            }
        }
        Report(off == worn && progress.EquippedCount == 0 && progress.Bag.Tab(InventoryTab.Equipment).Count == before, "全部脱下:脱了 " + off + " 件,背包装备页回到 " + before + " 件");
        Report(progress.WornStats.IsEmpty && SameBlock(progress.Stats, progress.BaseStats), "全部脱下之后属性回到只有等级的那份(脱下扣掉)");

        // 6. 装备属性与外观:整份装备清单
        bad += CheckStatsAndLook(assets, catalog, opts);
        return bad == 0 ? 0 : 1;
    }

    private static bool SameBlock(StatBlock a, StatBlock b)
    {
        return a.HpMax == b.HpMax && a.MpMax == b.MpMax && a.PhysicalAttack == b.PhysicalAttack && a.PhysicalDefense == b.PhysicalDefense
            && a.MagicalAttack == b.MagicalAttack && a.MagicalDefense == b.MagicalDefense && a.InventoryLimit == b.InventoryLimit
            && a.MpRegenSpeed == b.MpRegenSpeed && a.HitRecovery == b.HitRecovery;
    }

    /// <summary>
    /// 整份装备清单(equipment.lst 里的全部,不只是能掉落的)的属性与外观:读不出的个数、结构不变量(属性项按项次序排好不重复、武器都有外观而别的都没有)、
    /// 与旧的平读读法对照(<see cref="EquipmentStats"/> 的四项、<see cref="AvatarItem"/> 的剑魂外观;只数一致与不一致的件数)、
    /// 在真实数据上随机拼装备栏核对"汇总等于各件之和"。返回失败项数。
    /// </summary>
    private static int CheckStatsAndLook(DnfAssets assets, ItemCatalog catalog, Options opts)
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

        Console.WriteLine("  装备属性与外观(整份装备清单):");
        IReadOnlyList<int> codes = catalog.EquipmentCodes();
        var items = new List<ItemInfo>(codes.Count);
        int unreadable = 0;
        foreach (int code in codes)
        {
            ItemInfo info = catalog.Get(code);
            items.Add(info);
            if (info == null)
            {
                unreadable++;
            }
        }
        Report(codes.Count > 0 && unreadable == 0, "清单里 " + codes.Count + " 件装备都读得出来(读不出 " + unreadable + " 件)");

        // 结构不变量
        var perTag = new int[EquipStats.StatCount];
        int withStats = 0, entries = 0, withSecond = 0, disordered = 0, weapons = 0, lookWrong = 0, segments = 0, nestedFiles = 0, noSegment = 0;
        foreach (ItemInfo info in items.Where(i => i != null))
        {
            EquipStats stats = info.Stats;
            if (!stats.IsEmpty)
            {
                withStats++;
            }
            int last = -1;
            foreach (EquipStatEntry entry in stats.Entries)
            {
                entries++;
                perTag[(int)entry.Stat]++;
                if (entry.HasSecond)
                {
                    withSecond++;
                }
                if ((int)entry.Stat <= last)
                {
                    disordered++;
                }
                last = (int)entry.Stat;
            }
            bool isWeapon = info.Part == EquipmentParts.Weapon;
            if (isWeapon != (info.Look != null))
            {
                lookWrong++;
            }
            if (isWeapon && info.Look != null)
            {
                weapons++;
                segments += info.Look.Segments.Count;
                if (info.Look.Segments.Count == 0)
                {
                    noSegment++;
                }
                if (info.Look.NestedTagCount > 0)
                {
                    nestedFiles++;
                }
            }
        }
        Console.WriteLine("  有属性项的 " + withStats + " 件,共 " + entries + " 项(带第二个数的 " + withSecond + " 项);武器 " + weapons + " 件,职业段共 " + segments
            + " 段,没有职业段的 " + noSegment + " 件,块里还有备选外观标签的 " + nestedFiles + " 件");
        Console.WriteLine("  各属性项的件数:" + string.Join("、", Enumerable.Range(0, EquipStats.StatCount).Where(k => perTag[k] > 0)
            .Select(k => EquipStats.TagOf((EquipStat)k).Trim('[', ']') + " " + perTag[k])));
        Report(disordered == 0, "每件装备的属性项都按项次序排好、没有重复(乱序 " + disordered + " 项)");
        Report(lookWrong == 0, "武器都有外观数据、不是武器的都没有(不符 " + lookWrong + " 件)");

        string? dump = Environment.GetEnvironmentVariable("DNF_EQUIP_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            WriteDump(dump, codes, items);
            Console.WriteLine("  逐件转储已写出:" + dump + "(用 tools/py/equip_stats_ref.py compare 与独立的 Python 实现逐字段比对)");
        }

        // 旧读法对照:平读(任意深度第一次出现)与新读法(深度 0)有几件不同,只数个数
        int legacyOk = 0, legacyDiff = 0, legacyMissing = 0;
        foreach (ItemInfo info in items.Where(i => i != null))
        {
            ScriptLine[]? doc = ReadUncached(assets, info.Path);
            if (doc == null)
            {
                legacyMissing++;
                continue;
            }
            EquipmentStats old = EquipmentStats.From(new TagTable(doc));
            EquipStats s = info.Stats;
            bool same = old.PhysicalAttack == (int)s[EquipStat.EquipmentPhysicalAttack] && old.MagicalAttack == (int)s[EquipStat.EquipmentMagicalAttack]
                && old.PhysicalCritical == (float)s[EquipStat.PhysicalCriticalHit] && old.MagicalCritical == (float)s[EquipStat.MagicalCriticalHit];
            if (same)
            {
                legacyOk++;
            }
            else
            {
                legacyDiff++;
            }
        }
        Console.WriteLine("  对照旧读法 EquipmentStats(装备攻击与暴击四项,平读第一次出现):一致 " + legacyOk + " / 不一致 " + legacyDiff + " / 读不到 " + legacyMissing);

        AvatarClass cls = AvatarClass.FromChr(assets, DemoHero.SwordmanChrPath, DemoHero.SwordmanJobTag);
        int lookOk = 0, lookDiff = 0, lookDiffNested = 0, lookCompared = 0;
        foreach (ItemInfo info in items.Where(i => i != null && i.Look != null))
        {
            LookSegment seg = info.Look.ForJob(cls.JobTag);
            if (seg == null)
            {
                continue;
            }
            lookCompared++;
            AvatarItem? old = AvatarItem.Read(assets, cls, info.Path);
            bool same = old != null && old.HasVariation == seg.HasVariation && old.VariationA == seg.VariationA && old.VariationB == seg.VariationB
                && old.SubType == info.Look.SubType;
            if (old != null && same && old.HasVariation)
            {
                same = old.Layers.Count == seg.Layers.Count;
                for (int k = 0; same && k < seg.Layers.Count; k++)
                {
                    same = old.Layers[k].Key == seg.Layers[k].Order && old.Layers[k].Name == seg.Layers[k].Name;
                }
            }
            if (same)
            {
                lookOk++;
            }
            else
            {
                lookDiff++;
                if (info.Look.NestedTagCount > 0)
                {
                    lookDiffNested++;
                }
            }
        }
        Console.WriteLine("  对照旧读法 AvatarItem(剑魂的武器外观:变体、图层、子类型):比了 " + lookCompared + " 件,一致 " + lookOk + " / 不一致 " + lookDiff
            + "(其中块里有备选外观标签的 " + lookDiffNested + " 件,旧读法会把备选图层并进来)");

        // 真实数据上的"汇总等于各件之和":随机拼装备栏,装备栏的汇总与另一种求和(按标签名分桶)逐项相等
        var byPart = new Dictionary<int, List<ItemInfo>>();
        foreach (ItemInfo info in items.Where(i => i != null && EquipmentSlots.IsSlot(i.Part)))
        {
            if (!byPart.TryGetValue(info.Part, out List<ItemInfo>? list))
            {
                list = new List<ItemInfo>();
                byPart[info.Part] = list;
            }
            list.Add(info);
        }
        var random = new Random(opts.Seed + 1);
        int sets = 0, setsBad = 0, blocksBad = 0;
        for (int n = 0; n < 500; n++)
        {
            var slots = new EquipmentSlots();
            var parts = new List<ItemInfo>();
            for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
            {
                if (!byPart.TryGetValue(part, out List<ItemInfo>? list) || random.Next(5) == 0)
                {
                    continue;
                }
                ItemInfo pick = list[random.Next(list.Count)];
                slots.Restore(part, pick);
                parts.Add(pick);
            }
            sets++;
            if (!SumMatches(slots.TotalStats(), parts))
            {
                setsBad++;
            }
            StatBlock blocks = default;
            foreach (ItemInfo part in parts)
            {
                blocks += part.Stats.ToStatBlock();
            }
            if (!SameBlock(slots.TotalStatBlock(), blocks))
            {
                blocksBad++;
            }
        }
        Report(setsBad == 0, "真实装备随机拼 " + sets + " 套:装备栏属性汇总与按标签分桶的求和逐项相等(不等 " + setsBad + " 套)");
        Report(blocksBad == 0, "真实装备随机拼 " + sets + " 套:属性块汇总等于各件折算之和(不等 " + blocksBad + " 套)");
        return bad;
    }

    /// <summary>另一种求和:按标签名分桶累加各件的第一个数与第二个数,与装备栏的汇总逐项比对(与 <see cref="EquipStats.Plus"/> 的"有序合并"是两种写法)。</summary>
    private static bool SumMatches(EquipStats total, List<ItemInfo> parts)
    {
        var first = new Dictionary<string, double>();
        var second = new Dictionary<string, double>();
        var hasSecond = new HashSet<string>();
        foreach (ItemInfo item in parts)
        {
            foreach (EquipStatEntry entry in item.Stats.Entries)
            {
                string tag = EquipStats.TagOf(entry.Stat);
                first[tag] = (first.TryGetValue(tag, out double f) ? f : 0.0) + entry.First;
                second[tag] = (second.TryGetValue(tag, out double s) ? s : 0.0) + entry.Second;
                if (entry.HasSecond)
                {
                    hasSecond.Add(tag);
                }
            }
        }
        if (first.Count != total.Count)
        {
            return false;
        }
        foreach (EquipStatEntry entry in total.Entries)
        {
            string tag = EquipStats.TagOf(entry.Stat);
            if (!first.TryGetValue(tag, out double f) || !f.Equals(entry.First) || !second[tag].Equals(entry.Second) || hasSecond.Contains(tag) != entry.HasSecond)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>不进缓存地读一份脚本(整库对照时不能让 6 万份脚本都留在内存里);读不到或不是标记流返回 null。</summary>
    private static ScriptLine[]? ReadUncached(DnfAssets assets, string path)
    {
        if (!assets.Pvf.TryGetEntry(PvfPath.Normalize(path), out PvfEntry entry))
        {
            return null;
        }
        byte[] raw = assets.Pvf.Read(entry);
        return TokenScript.IsTokenScript(raw) ? ScriptDocument.Parse(raw, assets.Strings) : null;
    }

    /// <summary>逐件转储:每行 <c>编号\t属性\t外观</c>(按编号升序;读不出来的两列都是 &lt;none&gt;;外观列不是武器是 -)。</summary>
    private static void WriteDump(string path, IReadOnlyList<int> codes, List<ItemInfo> items)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        using var writer = new StreamWriter(full, false, new UTF8Encoding(false)) { NewLine = "\n" };
        for (int i = 0; i < codes.Count; i++)
        {
            ItemInfo info = items[i];
            writer.Write(codes[i].ToString(CultureInfo.InvariantCulture));
            writer.Write('\t');
            if (info == null)
            {
                writer.Write("<none>\t<none>\n");
                continue;
            }
            writer.Write(FormatStats(info.Stats));
            writer.Write('\t');
            writer.Write(info.Look == null ? "-" : FormatLook(info.Look));
            writer.Write('\n');
        }
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

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

    private static string FormatLook(EquipLook look)
    {
        var text = new StringBuilder();
        text.Append("sub=").Append(look.HasSubType ? look.SubType.ToString(CultureInfo.InvariantCulture) : "-");
        text.Append(";nested=").Append(look.NestedTagCount.ToString(CultureInfo.InvariantCulture));
        foreach (LookSegment seg in look.Segments)
        {
            text.Append("|J").Append(seg.Job ?? "-");
            text.Append(";V").Append(seg.HasVariation
                ? seg.VariationA.ToString(CultureInfo.InvariantCulture) + "," + seg.VariationB.ToString(CultureInfo.InvariantCulture)
                : "-");
            text.Append(";L").Append(seg.Layers.Count == 0
                ? "-"
                : string.Join(",", seg.Layers.Select(l => l.Order.ToString(CultureInfo.InvariantCulture) + ":" + l.Name)));
            text.Append(";S").Append(seg.AniScripts.Count == 0 ? "-" : string.Join(",", seg.AniScripts));
        }
        return text.ToString();
    }
}
