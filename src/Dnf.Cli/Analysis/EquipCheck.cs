using System.Diagnostics;
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
        EquipmentStats expected = picked.Aggregate(default(EquipmentStats), (sum, item) => sum.Plus(EquipmentStats.Read(assets, item.Path)));
        EquipmentStats panel = HeroPanel.WornEquipment(assets, null, progress);
        Report(panel.PhysicalAttack == expected.PhysicalAttack && panel.MagicalAttack == expected.MagicalAttack,
            "面板装备加成等于挑出各件的加总(物攻 " + panel.PhysicalAttack + "、魔攻 " + panel.MagicalAttack + ")");

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
        return bad == 0 ? 0 : 1;
    }
}
