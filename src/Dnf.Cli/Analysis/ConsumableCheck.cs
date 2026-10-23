using System.Diagnostics;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// consumable-check:消耗品(药水)的检查(证据 E-120)。整库扫描 stackable.lst 里的脚本,看回复类物品([hp recovery]、[mp recovery])的字段读得对不对
/// (持续时间与冷却不为负、图标帧在图包里、种类认得),再让一个 85 级剑魂对每一种这个职业用得了的回复药演习一遍:
/// 放进背包(自动进快捷栏)、把生命压到 1 点、魔法压到 0、从快捷栏用一件,每 10 毫秒推进到发完,核对终值(生命魔法按"总量 + 持续毫秒"线性发放、夹在上下限里)、
/// 背包里少一件、冷却登记与"冷却中不能再用"、冷却走完再用一件;最后把六种药放满快捷栏做一次存档往返。
/// 数值、物品名与件数是用户自己的数据,只打印在本机的终端上(不要抄进入库的文件);通过与否只看结构与账。
/// </summary>
internal static class ConsumableCheck
{
    /// <summary>演习用的主角等级(取顶级,免得等级门槛拦掉药,等级门槛另外按 --level 统计)。</summary>
    private const int DrillLevel = 85;

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

        // 1. 目录:整库读一遍可叠加物品
        var catalog = new ItemCatalog(data);
        var watch = Stopwatch.StartNew();
        var all = new List<ItemInfo>();
        foreach (int code in catalog.StackableCodes())
        {
            ItemInfo item = catalog.Get(code);
            if (item != null)
            {
                all.Add(item);
            }
        }
        watch.Stop();
        Report(all.Count > 0, "可叠加物品 " + all.Count + " 件(列表里 " + catalog.StackableCodes().Count + " 个,读不出的 " + (catalog.StackableCodes().Count - all.Count) + " 个,用时 " + watch.ElapsedMilliseconds + " 毫秒)");
        List<ItemInfo> recovery = all.Where(ConsumableRules.HasRecovery).ToList();
        Report(recovery.Count > 0, "写了 [hp recovery] 或 [mp recovery] 的物品 " + recovery.Count + " 件");
        if (recovery.Count == 0)
        {
            return 1;
        }

        // 2. 字段结构
        int hpOnly = recovery.Count(i => i.HpRecovery != null && i.MpRecovery == null);
        int mpOnly = recovery.Count(i => i.MpRecovery != null && i.HpRecovery == null);
        int both = recovery.Count - hpOnly - mpOnly;
        int percent = recovery.Count(i => (i.HpRecovery != null && i.HpRecovery.Percent) || (i.MpRecovery != null && i.MpRecovery.Percent));
        int instant = recovery.Count(i => (i.HpRecovery == null || i.HpRecovery.DurationMs == 0) && (i.MpRecovery == null || i.MpRecovery.DurationMs == 0));
        int cooled = recovery.Count(i => i.CoolTimeMs > 0);
        int grouped = recovery.Count(i => i.CoolGroup >= 0);
        int groups = recovery.Where(i => i.CoolGroup >= 0).Select(i => i.CoolGroup).Distinct().Count();
        Console.WriteLine("  构成:只回血 " + hpOnly + "、只回蓝 " + mpOnly + "、两样都回 " + both + ";百分比的 " + percent + "、当场发完的 " + instant
            + ";有冷却时间的 " + cooled + "、写了冷却组的 " + grouped + "(" + groups + " 个组)");
        int badDuration = recovery.Count(i => (i.HpRecovery != null && i.HpRecovery.DurationMs < 0) || (i.MpRecovery != null && i.MpRecovery.DurationMs < 0));
        int badCool = recovery.Count(i => i.CoolTimeMs < 0 || i.CoolGroup < -1);
        int unknownType = recovery.Count(i => i.StackableType == StackableTypes.Unknown);
        int notConsumableType = recovery.Count(i => !ConsumableRules.IsConsumableType(i));
        Console.WriteLine("  种类:药(waste)与投掷物(throw)以外的回复类物品 " + notConsumableType + " 件(快捷栏放不进去)");
        Report(badDuration == 0, "持续时间都不为负(不合的 " + badDuration + " 件)");
        Report(badCool == 0, "冷却时间不为负、冷却组不小于 -1(不合的 " + badCool + " 件)");
        Report(unknownType == 0, "种类都认得(不认得的 " + unknownType + " 件)");
        int noIcon = recovery.Count(i => i.IconKey == null || i.IconFrame < 0 || !assets.TryGetFrame(i.IconKey, i.IconFrame, out _));
        Report(noIcon == 0, "图标的帧都在图包里(缺的 " + noIcon + " 件)");

        // 3. 使用规则:按 --level 的剑魂逐件检查
        int hero = opts.HeroLevel;
        var results = new int[Enum.GetValues(typeof(UseResult)).Length];
        foreach (ItemInfo item in recovery)
        {
            results[(int)ConsumableRules.Check(item, EquipRules.SwordmanJob, hero)]++;
        }
        Console.WriteLine("  剑魂 " + hero + " 级逐件检查:能用 " + results[(int)UseResult.Ok] + "、等级不够 " + results[(int)UseResult.LevelTooLow]
            + "、职业不符 " + results[(int)UseResult.WrongJob] + "、不是药类 " + results[(int)UseResult.NotConsumable]);
        Report(results[(int)UseResult.NoEffect] == 0, "写了回复的物品不会被判成 没有效果(" + results[(int)UseResult.NoEffect] + " 件)");
        Report(results[(int)UseResult.Ok] + results[(int)UseResult.LevelTooLow] + results[(int)UseResult.WrongJob] + results[(int)UseResult.NotConsumable] == recovery.Count,
            "四种结果加起来等于总数");

        // 4. 演习:85 级剑魂对每一种用得了的回复药各用一遍
        HeroProgress progress = HeroProgress.CreateForLocalHero(assets, DrillLevel);
        HeroSave blank = progress.Snapshot();
        List<ItemInfo> usable = recovery.Where(i => ConsumableRules.Check(i, EquipRules.SwordmanJob, DrillLevel) == UseResult.Ok).ToList();
        Console.WriteLine("  演习:" + DrillLevel + " 级剑魂用得了的回复药 " + usable.Count + " 种");
        Report(usable.Count > 0, "有用得了的回复药");
        int drilled = 0;
        int cannotHold = 0;
        var wrong = new List<string>();
        var channelMs = new List<int>();
        var watchDrill = Stopwatch.StartNew();
        foreach (ItemInfo item in usable)
        {
            progress.Restore(blank, code => null);
            if (!progress.AddItem(item, 2))
            {
                cannotHold++;
                continue;
            }
            HeroVitals v = HeroVitals.FromStats(progress.Level, progress.Stats);
            v.Damage(v.Hp - 1);
            v.TrySpendMp(v.Mp);
            if (progress.Quick.IndexOf(item.Code) != 0)
            {
                wrong.Add("没有自动放进快捷栏第一格:" + item.Code);
                continue;
            }
            int hpTotal = item.HpRecovery == null ? 0 : item.HpRecovery.TotalFor(v.HpMax);
            int mpTotal = item.MpRecovery == null ? 0 : item.MpRecovery.TotalFor(v.MpMax);
            int hpStart = v.Hp;
            int mpStart = v.Mp;
            int usedBefore = progress.ItemsUsed;
            UseResult first = progress.UseQuickSlot(0, v);
            if (first != UseResult.Ok)
            {
                wrong.Add("第一次用的结果是 " + first + ":" + item.Code);
                continue;
            }
            int span = Math.Max(item.HpRecovery == null ? 0 : item.HpRecovery.DurationMs, item.MpRecovery == null ? 0 : item.MpRecovery.DurationMs);
            channelMs.Add(span);
            int steps = 0;
            for (; steps < span / 10 + 3 && v.Recovering; steps++)
            {
                v.AdvanceRecovery(10);
            }
            int expectHp = Math.Min(v.HpMax, Math.Max(1, hpStart + hpTotal));
            int expectMp = Math.Min(v.MpMax, Math.Max(0, mpStart + mpTotal));
            if (v.Recovering || v.Hp != expectHp || v.Mp != expectMp)
            {
                wrong.Add("发完以后生命/魔法 " + v.Hp + "/" + v.Mp + " 应为 " + expectHp + "/" + expectMp + "(还在发 " + v.Recovering + "):" + item.Code);
                continue;
            }
            if (progress.Bag.CountOf(item.Code) != 1 || progress.ItemsUsed - usedBefore != 1)
            {
                wrong.Add("背包里应剩 1 件、用过 1 件,实际 " + progress.Bag.CountOf(item.Code) + "/" + (progress.ItemsUsed - usedBefore) + ":" + item.Code);
                continue;
            }
            UseResult second;
            if (item.CoolTimeMs > 0)
            {
                if (!progress.Cooldowns.IsCooling(item) || progress.Cooldowns.RemainingMs(item) != item.CoolTimeMs)
                {
                    wrong.Add("冷却没登记成完整的 " + item.CoolTimeMs + " 毫秒(剩 " + progress.Cooldowns.RemainingMs(item) + "):" + item.Code);
                    continue;
                }
                if (progress.UseQuickSlot(0, v) != UseResult.Cooling)
                {
                    wrong.Add("冷却中还能再用:" + item.Code);
                    continue;
                }
                progress.Cooldowns.Advance(item.CoolTimeMs);
                if (progress.Cooldowns.IsCooling(item))
                {
                    wrong.Add("冷却时间走完还在冷却:" + item.Code);
                    continue;
                }
            }
            second = progress.UseQuickSlot(0, v);
            if (second != UseResult.Ok || progress.Bag.CountOf(item.Code) != 0 || progress.QuickFilled != 0)
            {
                wrong.Add("第二次用的结果是 " + second + "、背包剩 " + progress.Bag.CountOf(item.Code) + "、快捷栏占 " + progress.QuickFilled + ":" + item.Code);
                continue;
            }
            drilled++;
        }
        watchDrill.Stop();
        foreach (string line in wrong.Take(8))
        {
            Console.WriteLine("    " + line);
        }
        Report(wrong.Count == 0, "逐种演习:" + drilled + " 种全对、放不进背包 " + cannotHold + " 种、出错 " + wrong.Count + " 种(用时 " + watchDrill.ElapsedMilliseconds + " 毫秒)");
        if (channelMs.Count > 0)
        {
            Console.WriteLine("  回复发放的持续时间:最短 " + channelMs.Min() + "、最长 " + channelMs.Max() + " 毫秒;当场发完的 " + channelMs.Count(ms => ms == 0) + " 种");
        }

        // 5. 存档往返:快捷栏放满六种,拍快照写成文本、读回来再还原,快捷栏与再拍的快照都要一样
        progress.Restore(blank, code => null);
        List<ItemInfo> six = usable.Where(QuickSlots.Eligible).Take(QuickSlots.Count).ToList();
        foreach (ItemInfo item in six)
        {
            progress.AddItem(item, 2);
        }
        Report(progress.QuickFilled == six.Count, "快捷栏放了 " + progress.QuickFilled + " 种(应为 " + six.Count + ")");
        string text = progress.Snapshot().ToText();
        bool parsed = HeroSave.TryParse(text, out HeroSave parsedSave, out string error);
        Report(parsed, "存档文本能读回来" + (parsed ? string.Empty : ":" + error));
        if (parsed)
        {
            HeroProgress restored = HeroProgress.CreateForLocalHero(assets, 1);
            SaveApplyReport applied = restored.RestoreFromData(parsedSave, assets);
            Report(applied.Dropped == 0, "按编号还原:丢掉 " + applied.Dropped + " 条");
            bool sameQuick = true;
            for (int slot = 0; slot < QuickSlots.Count; slot++)
            {
                sameQuick &= restored.QuickCodeAt(slot) == progress.QuickCodeAt(slot) && restored.QuickCountAt(slot) == progress.QuickCountAt(slot);
            }
            Report(sameQuick, "还原后快捷栏每一格的物品与件数都一样");
            Report(restored.Snapshot().ToText() == text, "还原之后再拍的快照与原来逐字节相同");
        }
        return bad == 0 ? 0 : 1;
    }
}
