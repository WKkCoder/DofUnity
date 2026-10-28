using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// status-check:异常状态的检查(M43,证据 E-136)。
/// 不带 --cases:读真实数据里的异常状态数值表(etc/activestatustable_ex.etc)与全部怪物 .atk 里的 [active status] 槽,检查结构——
/// 表的标签都读到(最高等级、18 个默认秒数、18 对威力 1 与威力 2、18 × 4 对附加参数)、每种状态的附加参数个数与 <see cref="ActiveStatusFormulas.MakeEtcParameters"/> 一致、
/// 持续时间公式不出负数、同步种类与伤害下标一一对应;再统计怪物攻击里各种状态的条数,以及附加参数是否落在表给的"一级到满级"范围内(只数个数,不判通过);
/// 最后把每个槽挂到 <see cref="ActiveStatusSet"/> 上走完时间,核对运行期的不变量(每跳不超过服务端允许值、总量不超过伤害量、时间步长不影响结果、状态到点解除)。
/// 表与 .atk 里的数是用户自己的数据,只打印汇总数在本机终端,不要抄进入库的文件。
/// 带 --cases:读 tools/py/active_status_oracle.py --write-cases 的输出,用 <see cref="ActiveStatusCaseReplay"/> 重算并与原版函数在模拟器里的输出逐条比较(不需要数据目录)。
/// </summary>
internal static class StatusCheck
{
    public static int RunCases(Options opts)
    {
        string path = opts.CasesFile ?? throw new UserError("status-check --cases 需要用例文件");
        if (!File.Exists(path))
        {
            throw new UserError("找不到用例文件 " + path + "(先跑 re/venv/bin/python tools/py/active_status_oracle.py --stress 20000 --write-cases " + path + ")");
        }
        ActiveStatusCaseReplay.Result r = ActiveStatusCaseReplay.Replay(File.ReadLines(path));
        foreach (string s in r.Samples)
        {
            Console.WriteLine("  不一致 " + s);
        }
        Console.WriteLine("按种类:" + string.Join(" ", r.PerKind.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)));
        Console.WriteLine("一致 " + (r.Cases - r.Bad) + ",不一致 " + r.Bad);
        return r.Bad == 0 && r.Cases > 0 ? 0 : 1;
    }

    private static readonly string[] KindNames =
    {
        "[slow]", "[freeze]", "[poison]", "[stun]", "[curse]", "[blind]", "[lightning]", "[stone]", "[sleep]",
        "[burn]", "[weapon break]", "[bleeding]", "[haste]", "[bless]", "[element]", "[confuse]", "[hold]", "[armor break]",
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

        // 1. 表
        ActiveStatusTable table = data.ActiveStatuses;
        Report(!ReferenceEquals(table, ActiveStatusTable.Empty) && table.MaxLevel > 0, "表读得到,最高等级 " + table.MaxLevel);
        int durations = 0, power1 = 0, power2 = 0, etc = 0;
        for (int k = 0; k < ActiveStatusTable.KindCount; k++)
        {
            durations += table.DurationSeconds(k) != 0 ? 1 : 0;
            power1 += table.Power1Start(k) != 0 || table.Power1End(k) != 0 ? 1 : 0;
            power2 += table.Power2Start(k) != 0 || table.Power2End(k) != 0 ? 1 : 0;
            for (int s = 0; s < ActiveStatusTable.EtcSlots; s++)
            {
                etc += table.EtcStart(k, s) != 0 || table.EtcEnd(k, s) != 0 ? 1 : 0;
            }
        }
        Console.WriteLine("  非零项:默认秒数 " + durations + "/18 种,威力 1 " + power1 + "/18 种,威力 2 " + power2 + "/18 种,附加参数 " + etc + "/72 项");
        Report(table.UserToUserDamageRate > 0 && table.DeathTowerUserToUserDamageRate > 0, "玩家对玩家系数读到(正数)");

        // 2. 公式的自洽:持续时间不出负数,附加参数个数与同步种类
        int negatives = 0;
        int powerKinds = 0, tableKinds = 0, zeroKinds = 0;
        for (int k = 0; k < ActiveStatusTable.KindCount; k++)
        {
            int bit = 1 << k;
            powerKinds += (bit & ActiveStatusFormulas.DurationFromPowerMask) != 0 ? 1 : 0;
            tableKinds += (bit & ActiveStatusFormulas.DurationFromTableMask) != 0 ? 1 : 0;
            zeroKinds += (bit & (ActiveStatusFormulas.DurationFromPowerMask | ActiveStatusFormulas.DurationFromTableMask)) == 0 ? 1 : 0;
            foreach (int level in new[] { -1, 0, 1, 2, 75, table.MaxLevel, table.MaxLevel + 50 })
            {
                negatives += ActiveStatusFormulas.DurationMs(table, k, level, 0, 1) < 0 ? 1 : 0;
                negatives += ActiveStatusFormulas.DurationMs(table, k, level, ActiveStatusFormulas.MonsterObjectType, 1) < 0 ? 1 : 0;
            }
        }
        Report(negatives == 0, "持续时间在 18 种 × 7 个等级 × 玩家与怪物下都不出负数");
        Report(powerKinds + tableKinds + zeroKinds == ActiveStatusTable.KindCount, "持续时间的两个位掩码与\"都不是\"合起来盖住 18 种(威力型 " + powerKinds + "、表型 " + tableKinds + "、无 " + zeroKinds + ")");
        int syncKinds = Enumerable.Range(0, ActiveStatusTable.KindCount).Count(ActiveStatusFormulas.IsSync);
        int indexed = Enumerable.Range(0, ActiveStatusTable.KindCount).Count(k => ActiveStatusFormulas.DamageIndex(k) >= 0);
        Report(syncKinds == 5 && indexed == 5, "同步种类 " + syncKinds + " 个,有伤害下标的 " + indexed + " 个(都应是 5)");

        // 3. 全库怪物 .atk 的 [active status] 槽
        var perKind = new int[ActiveStatusTable.KindCount];
        int files = 0, withStatus = 0, slots = 0, extraMismatch = 0, outOfRange = 0, extraTotal = 0;
        int zeroDuration = 0, alwaysHit = 0;
        var allSlots = new List<ActiveStatusEntry>();
        foreach (PvfEntry e in assets.Pvf.Entries)
        {
            string p = e.Path.ToLowerInvariant();
            if (!p.StartsWith("monster/", StringComparison.Ordinal) || !p.EndsWith(".atk", StringComparison.Ordinal))
            {
                continue;
            }
            AttackInfoData? info = data.GetAttackInfo(p)?.Data;
            if (info == null)
            {
                continue;
            }
            files++;
            bool any = false;
            foreach (ActiveStatusEntry s in info.Statuses)
            {
                if (s.Kind == ActiveStatusKind.None)
                {
                    continue;
                }
                any = true;
                slots++;
                allSlots.Add(s);
                int k = (int)s.Kind;
                perKind[k]++;
                zeroDuration += s.Duration <= 0 ? 1 : 0;
                alwaysHit += s.Probability >= 100f ? 1 : 0;
                int[] want = ActiveStatusFormulas.MakeEtcParameters(table, k, 1);
                if (want.Length != s.Extra.Length)
                {
                    extraMismatch++;
                    continue;
                }
                for (int i = 0; i < s.Extra.Length && i < ActiveStatusTable.EtcSlots; i++)
                {
                    extraTotal++;
                    float lo = Math.Min(table.EtcStart(k, i), table.EtcEnd(k, i));
                    float hi = Math.Max(table.EtcStart(k, i), table.EtcEnd(k, i));
                    if (k != 9 || i < 2)
                    {
                        outOfRange += s.Extra[i] < lo || s.Extra[i] > hi ? 1 : 0;
                    }
                }
            }
            withStatus += any ? 1 : 0;
        }
        Console.WriteLine("  怪物 .atk " + files + " 个,带状态的 " + withStatus + " 个,共 " + slots + " 个槽;按种类:"
            + string.Join(" ", perKind.Select((n, k) => n == 0 ? null : KindNames[k].Trim('[', ']') + "=" + n).Where(t => t != null)));
        Console.WriteLine("  持续时间为 0(走表里的默认值)的槽 " + zeroDuration + " 个,概率不小于 100 的槽 " + alwaysHit + " 个;附加参数 " + extraTotal
            + " 项里落在表范围外的 " + outOfRange + " 项(灼伤只看前两项;只统计,不判通过)");
        Report(files > 0 && slots > 0, "读到了怪物攻击里的状态槽");
        Report(extraMismatch == 0, "每个槽的附加参数个数都与 makeEtcParameters 的个数一致(不一致 " + extraMismatch + ")");

        // 4. 运行期模拟:每个槽各挂一次到一个空的 ActiveStatusSet 上、走完时间,核对不变量(只数个数)
        Simulate(table, allSlots, Report);

        // 5. 等级差修正:每个写了状态等级的槽,在受害者比状态等级低 10 级、同级、高 10 级、高 40 级各算一次,核对单调性与同级不变(只数个数)
        LevelDifference(table, allSlots, Report);
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// 等级差修正(<see cref="ActiveStatusFormulas.LevelAdjustedProbability"/> 与 <see cref="ActiveStatusFormulas.LevelAdjustedDuration"/>)在全库怪物状态槽上的不变量:
    /// 同级时概率、时长与附加参数都原样;受害者等级越高概率与时长越不大(表里三个比率都不为负时);修正后的概率、时长不为负。只打印汇总数(本机脚本扫描,非 REA)。
    /// </summary>
    private static void LevelDifference(ActiveStatusTable table, List<ActiveStatusEntry> slots, Action<bool, string> report)
    {
        int leveled = 0, sameChanged = 0, notMonotone = 0, negative = 0, zeroedAtPlus40 = 0, shortenedAtPlus10 = 0, extrasChangedSame = 0;
        foreach (ActiveStatusEntry entry in slots)
        {
            if (entry.Level == 0 || entry.Kind == ActiveStatusKind.Element)
            {
                continue;
            }
            leveled++;
            int kind = (int)entry.Kind;
            int baseDuration = entry.Duration > 0 ? entry.Duration : ActiveStatusFormulas.DurationMs(table, kind, Math.Max(1, entry.Level), 0, 1);
            float lowP = 0, sameP = 0, highP = 0, farP = 0;
            int lowD = 0, sameD = 0, highD = 0;
            int step = 0;
            foreach (int delta in new[] { -10, 0, 10, 40 })
            {
                int victim = Math.Max(1, entry.Level + delta);
                float p = ActiveStatusFormulas.LevelAdjustedProbability(table, entry.Probability, entry.Level, victim);
                int d = ActiveStatusFormulas.LevelAdjustedDuration(table, kind, baseDuration, entry.Level, victim, entry.Extra.Length);
                negative += p < 0f || d < 0 ? 1 : 0;
                switch (step++)
                {
                    case 0: lowP = p; lowD = d; break;
                    case 1: sameP = p; sameD = d; break;
                    case 2: highP = p; highD = d; break;
                    default: farP = p; break;
                }
            }
            int[] same = ActiveStatusFormulas.LevelAdjustedExtras(table, kind, entry.Extra, entry.Level, entry.Level);
            extrasChangedSame += !same.SequenceEqual(entry.Extra) ? 1 : 0;
            sameChanged += sameP != Math.Max(0f, entry.Probability) || sameD != baseDuration ? 1 : 0;
            notMonotone += lowP < sameP || sameP < highP || highP < farP || lowD < sameD || sameD < highD ? 1 : 0;
            zeroedAtPlus40 += entry.Probability > 0f && farP == 0f ? 1 : 0;
            shortenedAtPlus10 += highD < sameD ? 1 : 0;
        }
        Console.WriteLine("  等级差修正:写了状态等级的槽 " + leveled + " 个;受害者高出 40 级时概率变成 0 的 " + zeroedAtPlus40 + " 个;高出 10 级时时长变短的 " + shortenedAtPlus10 + " 个");
        report(leveled > 0, "有写了状态等级的槽");
        report(sameChanged == 0 && extrasChangedSame == 0, "受害者与状态同级时概率、时长与附加参数都原样(变了 " + sameChanged + " / " + extrasChangedSame + ")");
        report(notMonotone == 0, "受害者等级越高概率与时长越不大(不单调 " + notMonotone + ")");
        report(negative == 0, "修正后的概率与时长都不为负(为负 " + negative + ")");
    }

    /// <summary>
    /// 把全库怪物 .atk 里的状态槽逐个挂到主角的 <see cref="ActiveStatusSet"/> 上走完时间,核对运行期的不变量:写了毫秒数的槽都挂得上、
    /// 持续伤害的每一跳不超过服务端允许值、总量不超过伤害量、一次推完与每 16 毫秒推一次掉的伤害相同、失去控制的状态到点都解除、减速百分数在 0 到 90 内。
    /// 只打印汇总数(本机脚本扫描,非 REA)。
    /// </summary>
    private static void Simulate(ActiveStatusTable table, List<ActiveStatusEntry> slots, Action<bool, string> report)
    {
        int applied = 0, ignoredWithDuration = 0, ignoredNoDuration = 0, zeroChance = 0, wrongMiss = 0, dot = 0, dotWithAmount = 0, overAllowed = 0, overBudget = 0, stepMismatch = 0;
        int locks = 0, lockStuck = 0, slow = 0, slowOut = 0, tickCountMismatch = 0;
        long ticksTotal = 0;
        var ticks = new List<ActiveStatusTick>();
        foreach (ActiveStatusEntry entry in slots)
        {
            var set = new ActiveStatusSet(table);
            ActiveStatusApply result = set.Apply(entry, 0, 0.0);
            if (result != ActiveStatusApply.Applied)
            {
                if (result == ActiveStatusApply.Missed)
                {
                    // 随机数取最小的 0 仍没中:只有概率不是正数的槽才会这样
                    zeroChance++;
                    wrongMiss += entry.Probability > 0f ? 1 : 0;
                }
                else if (entry.Duration > 0)
                {
                    ignoredWithDuration++;
                }
                else
                {
                    ignoredNoDuration++;
                }
                continue;
            }
            applied++;
            ActiveStatusInstance inst = set.Find(entry.Kind)!;
            if (ActiveStatusRules.DealsDamage(entry.Kind))
            {
                dot++;
                if (inst.TickCount > 0)
                {
                    dotWithAmount++;
                    int amount = entry.Extra[0];
                    int allowed = ActiveStatusFormulas.ServerAllowedDamage(table, (int)entry.Kind, new ActiveStatusDamageInput(amount, inst.DurationMs));
                    overAllowed += inst.TickDamage > allowed ? 1 : 0;
                    overBudget += (long)inst.TickDamage * inst.TickCount > amount ? 1 : 0;
                }
            }
            if (ActiveStatusRules.LocksControl(entry.Kind))
            {
                locks++;
            }
            if (entry.Kind == ActiveStatusKind.Slow)
            {
                slow++;
                slowOut += set.MoveSlowPercent is < 0 or > 90 ? 1 : 0;
            }

            // 一次推完
            ticks.Clear();
            set.Advance(inst.DurationMs, ticks);
            long bigTotal = ticks.Sum(t => (long)t.Damage);
            int bigCount = ticks.Count;
            ticksTotal += bigCount;
            tickCountMismatch += bigCount != inst.TickCount ? 1 : 0;
            lockStuck += set.Count != 0 ? 1 : 0;

            // 每 16 毫秒推一次:掉的跳数与总量要相同
            var stepped = new ActiveStatusSet(table);
            stepped.Apply(entry, 0, 0.0);
            ticks.Clear();
            for (int t = 0; t < inst.DurationMs + 16 && stepped.Count > 0; t += 16)
            {
                stepped.Advance(16, ticks);
            }
            stepMismatch += ticks.Count != bigCount || ticks.Sum(t => (long)t.Damage) != bigTotal || stepped.Count != 0 ? 1 : 0;
        }
        Console.WriteLine("  运行期模拟:" + slots.Count + " 个槽,挂上 " + applied + " 个;概率不是正数而没中的 " + zeroChance + " 个;无效(时长不是正数)的 "
            + (ignoredWithDuration + ignoredNoDuration) + " 个,其中写了毫秒数的 " + ignoredWithDuration + " 个;持续伤害槽 " + dot + " 个(带伤害量 " + dotWithAmount + " 个),共掉 " + ticksTotal + " 跳;失去控制的槽 " + locks + " 个;减速槽 " + slow + " 个");
        report(slots.Count > 0 && applied > 0, "槽能挂到主角身上");
        report(ignoredWithDuration == 0, "写了毫秒数的槽不会因为时长无效而挂不上(无效 " + ignoredWithDuration + ")");
        report(wrongMiss == 0, "概率是正数的槽,随机数取 0 时一定中(没中 " + wrongMiss + ")");
        report(overAllowed == 0, "持续伤害的每一跳都不超过服务端允许值(超过 " + overAllowed + ")");
        report(overBudget == 0, "持续伤害的总量不超过伤害量(超过 " + overBudget + ")");
        report(tickCountMismatch == 0, "走完时间掉的跳数与算好的一致(不一致 " + tickCountMismatch + ")");
        report(stepMismatch == 0, "一次推完与每 16 毫秒推一次掉的跳数与总量相同、状态都到点摘除(不同 " + stepMismatch + ")");
        report(lockStuck == 0, "走完时间的状态都摘除了,包括失去控制的(没摘掉 " + lockStuck + ")");
        report(slowOut == 0, "减速的移动百分数都在 0 到 90 内(不在 " + slowOut + ")");
    }
}
