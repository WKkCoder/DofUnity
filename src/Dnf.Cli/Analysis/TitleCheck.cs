using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// title-check:称号与成就的定义数据检查。整库读称号物品(equipment.lst 里放在称号目录下的)的外观与时限字段、称号气泡、图鉴定义(titlebook 脚本)
/// 与全部分类 [title] 的任务(成就),核对结构不变量;再用真实定义演习成就框架:每个成就按初始计数一次触发就完成、差一个不完成再补一个就完成、已完成再触发被拒、
/// 角色等级低一级被拒;完成时称号放进的格与定义里列出它的第一格一致;全部完成之后邮件与收集加成与另一种算法的结果一致;登录核对与补发清单按定义走。
/// 数值、编号与件数来自用户自己的数据,只打印在本机的终端上(不要抄进入库的文件);通过与否只看结构。
/// <para>
/// 环境变量 <c>DNF_TITLE_DUMP</c> 给了路径就把整库逐记录转储成文本(格式与 <c>tools/py/title_ref.py dump</c> 相同),
/// 再用那个脚本的 <c>compare</c> 逐记录比对两个独立实现(只报计数)。转储只写在被 git 忽略的 out/ 下,不入库。
/// </para>
/// </summary>
internal static class TitleCheck
{
    /// <summary>演习用的角色等级:高到不会被任何成就的最低等级挡住(等级门槛另测)。</summary>
    private const int DrillLevel = 400;

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

        var watch = Stopwatch.StartNew();
        var titles = new TitleCatalog(data);
        var items = new ItemCatalog(data);
        var quests = new QuestCatalog(data);
        IReadOnlyList<int> codes = titles.Codes();
        Report(codes.Count > 0, "称号目录下的物品 " + codes.Count + " 件");
        if (codes.Count == 0)
        {
            return 1;
        }

        // 1. 称号物品的外观与时限
        var appearances = new List<TitleAppearance?>();
        int unreadable = 0, notTitle = 0, withAnim = 0, brokenAnim = 0, withName = 0, withName2 = 0, limited = 0, withIcon = 0;
        var periods = new SortedDictionary<int, int>();
        foreach (int code in codes)
        {
            TitleAppearance? a = titles.AppearanceOf(code);
            appearances.Add(a);
            if (a == null)
            {
                unreadable++;
                continue;
            }
            notTitle += a.IsTitle ? 0 : 1;
            withName += a.NameKey != null ? 1 : 0;
            withName2 += a.Name2Key != null ? 1 : 0;
            withIcon += a.IconPath != null ? 1 : 0;
            if (a.HasAnimation)
            {
                withAnim++;
                if (!data.TryReadBytes(a.ResolvedAnimationPath, out _))
                {
                    brokenAnim++;
                }
            }
            if (a.IsTimeLimited)
            {
                limited++;
                periods.TryGetValue(a.UsablePeriodDays, out int n);
                periods[a.UsablePeriodDays] = n + 1;
            }
        }
        Console.WriteLine("  外观:有 [name] " + withName + "、有 [name2] " + withName2 + "、有 [icon] " + withIcon + "、有 [custom animation] " + withAnim
            + "(路径读不到 " + brokenAnim + ")、限时 " + limited + "(" + periods.Count + " 种天数:" + string.Join("、", periods.Select(p => p.Key + "天×" + p.Value)) + ")");
        Report(unreadable == 0, "称号脚本都读得出来(读不出 " + unreadable + " 件)");
        Report(notTitle == 0, "装备类型都是称号(不是的 " + notTitle + " 件)");
        Report(withAnim > 0 && brokenAnim * 10 < withAnim, "称号动画路径几乎都读得到(读不到 " + brokenAnim + " / " + withAnim + ")");
        Report(titles.Balloons().Count > 0, "称号气泡 " + titles.Balloons().Count + " 条");

        // 2. 图鉴定义
        TitleBookDef book = titles.Book();
        var sectionsOf = new int[TitleCategories.Count];
        foreach (TitleSectionDef s in book.Sections)
        {
            sectionsOf[(int)s.Category]++;
        }
        int bound = 0, unboundFilled = 0, emptySlots = 0, listed = 0;
        for (int c = 0; c < TitleCategories.Count; c++)
        {
            IReadOnlyList<TitleSlotDef> slots = book.SlotsOf((TitleCategory)c);
            int filled = slots.Count(s => !s.IsEmpty);
            int boundHere = slots.Count(s => !s.IsEmpty && s.IsBound);
            bound += boundHere;
            unboundFilled += filled - boundHere;
            emptySlots += slots.Count - filled;
            listed += slots.Sum(s => s.Items.Count);
            Console.WriteLine("  图鉴 " + TitleCategories.NameOf((TitleCategory)c) + ":定义 " + slots.Count + " 格(有内容 " + filled + "、绑成就 " + boundHere + "),页 "
                + sectionsOf[c] + " 个,存档上限 " + TitleCategories.CapacityOf((TitleCategory)c) + " 格");
        }
        int rewardStat = 0, rewardEmotion = 0, rewardPostal = 0;
        foreach (TitleReward r in book.Sections.SelectMany(s => s.Rewards).Concat(book.DefaultRewards))
        {
            rewardStat += r.Kind == TitleRewardKind.Stat ? 1 : 0;
            rewardEmotion += r.Kind == TitleRewardKind.Emotion ? 1 : 0;
            rewardPostal += r.Kind == TitleRewardKind.Postal ? 1 : 0;
        }
        Console.WriteLine("  图鉴:默认奖励 " + book.DefaultRewards.Count + " 条;页奖励 属性 " + rewardStat + " / 表情 " + rewardEmotion + " / 邮件 " + rewardPostal
            + ";特殊条件文字 " + book.ConditionMessages.Count + " 条;补发清单 " + book.Adjusts.Count + " 项;事件页标记 " + book.ViewEventMark
            + ";事件页最低等级 " + (book.HasEventMinLevel ? book.EventMinLevel.ToString(CultureInfo.InvariantCulture) : "没写")
            + ";顶层跳过的标签行 " + book.SkippedTags);
        Report(book.TruncatedSlotLists == 0, "格定义都读完整(读不全 " + book.TruncatedSlotLists + " 次)");
        Report(book.SlotIndexMismatches == 0, "格定义里写的序号都等于向量位置(不等 " + book.SlotIndexMismatches + " 次)");
        Report(book.SkippedUnknownCategory == 0, "分类关键字都认得(不认得 " + book.SkippedUnknownCategory + " 次)");
        Report(book.Sections.Count > 0 && bound > 0, "图鉴有页(" + book.Sections.Count + ")也有绑成就的格(" + bound + ")");
        Report(Enumerable.Range(0, TitleCategories.Count).All(c => book.SlotsOf((TitleCategory)c).Count <= TitleCategories.CapacityOf((TitleCategory)c)),
            "每个分类定义的格数都不超过存档上限");

        // 页的几何:同分类的页号连续、起点 = 页号 × 10、终点落在 [起点, 起点 + 10];写满 10 格的页,下一页的第一格按服务端的"≤ 终点"会被判给这一页
        int badGeometry = 0, fullPages = 0, partialPages = 0, conflictSlots = 0, pagesWithoutBound = 0;
        foreach (TitleCategory category in Enum.GetValues<TitleCategory>())
        {
            int expectedPage = 0;
            foreach (TitleSectionDef s in book.Sections.Where(p => p.Category == category))
            {
                if (s.PageIndex != expectedPage || s.Start != expectedPage * 10 || s.End < s.Start || s.End > s.Start + 10)
                {
                    badGeometry++;
                }
                expectedPage++;
                IReadOnlyList<TitleSlotDef> slots = book.SlotsOf(category);
                bool full = s.End == s.Start + 10;
                fullPages += full ? 1 : 0;
                partialPages += full ? 0 : 1;
                if (full && book.Sections.Any(p => p.Category == category && p.Start == s.End)
                    && book.SectionIndexAt(category, s.End) != book.Sections.ToList().FindIndex(p => p.Category == category && p.Start == s.End))
                {
                    conflictSlots++;
                }
                bool anyBound = false;
                for (int k = s.Start; k < s.End && k < slots.Count; k++)
                {
                    anyBound |= !slots[k].IsEmpty && slots[k].IsBound;
                }
                pagesWithoutBound += anyBound ? 0 : 1;
            }
        }
        Console.WriteLine("  页:写满 10 格的 " + fullPages + "、截短的 " + partialPages + "、一格成就也没绑的 " + pagesWithoutBound + "、写满页的下一页首格被判给上一页的 " + conflictSlots);
        Report(badGeometry == 0, "页号连续、起点 = 页号 × 10、终点在 [起点, 起点 + 10](不合 " + badGeometry + " 页)");

        // 3. 成就
        AchievementCatalog achievements = AchievementCatalog.Load(data, quests);
        watch.Stop();
        Report(achievements.Count > 0, "成就(分类 [title] 的任务)" + achievements.Count + " 个(整库读用时 " + watch.ElapsedMilliseconds + " 毫秒)");
        Console.WriteLine("  完成条件种类:" + string.Join("、", achievements.CountsByCondition().Select(p => p.Key + " " + p.Value)));
        var arities = new SortedDictionary<int, int>();
        int withTitle = 0, withPostal = 0, verifiable = 0, nonDefaultInit = 0, levelGated = 0;
        foreach (AchievementDef def in achievements.All())
        {
            arities.TryGetValue(def.CheckCounts.Count, out int n);
            arities[def.CheckCounts.Count] = n + 1;
            withTitle += def.TitleItemCode != 0 ? 1 : 0;
            withPostal += def.Postal.IsEmpty ? 0 : 1;
            verifiable += def.IsVerifiable ? 1 : 0;
            nonDefaultInit += def.InitialCount(0) != 1 || def.InitialCount(1) != 0 || def.InitialCount(2) != 0 ? 1 : 0;
            levelGated += def.MinLevel > 1 ? 1 : 0;
        }
        Console.WriteLine("  [check count] 的整数个数分布:" + string.Join("、", arities.Select(p => p.Key + "个×" + p.Value)) + ";有称号奖励 " + withTitle + "、有邮件奖励 " + withPostal
            + "、登录时可核对 " + verifiable + "、初始计数不是 (1,0,0) 的 " + nonDefaultInit + "、有最低等级的 " + levelGated);
        Report(withTitle > 0, "有称号奖励的成就 " + withTitle + " 个");

        // 4. 图鉴与成就、物品的互相指回
        int slotsBound = 0, slotNoAchievement = 0, slotNoTitleReward = 0, slotRewardNotListed = 0, slotItemTotal = 0, slotItemMissing = 0, slotItemNotTitleDir = 0, slotItemNotTitlePart = 0;
        var titleCodeSet = new HashSet<int>(codes);
        foreach (TitleCategory category in Enum.GetValues<TitleCategory>())
        {
            foreach (TitleSlotDef slot in book.SlotsOf(category))
            {
                if (slot.IsEmpty)
                {
                    continue;
                }
                foreach (int code in slot.Items.Where(i => i > 0))
                {
                    slotItemTotal++;
                    ItemInfo? item = items.Get(code);
                    slotItemMissing += item == null ? 1 : 0;
                    slotItemNotTitleDir += titleCodeSet.Contains(code) ? 0 : 1;
                    slotItemNotTitlePart += item != null && item.Part != EquipmentParts.TitleName ? 1 : 0;
                }
                if (!slot.IsBound)
                {
                    continue;
                }
                slotsBound++;
                AchievementDef? def = achievements.Get(slot.AchievementId);
                if (def == null)
                {
                    slotNoAchievement++;
                    continue;
                }
                slotNoTitleReward += def.TitleItemCode == 0 ? 1 : 0;
                slotRewardNotListed += def.TitleItemCode != 0 && !slot.Lists(def.TitleItemCode) ? 1 : 0;
            }
        }
        Console.WriteLine("  互相指回:绑成就的格 " + slotsBound + "(成就不存在 " + slotNoAchievement + "、成就没有称号奖励 " + slotNoTitleReward + "、奖励的称号不在这一格的清单里 " + slotRewardNotListed
            + ");格里列的称号物品 " + slotItemTotal + " 个(物品读不到 " + slotItemMissing + "、不在称号目录下 " + slotItemNotTitleDir + "、装备类型不是称号 " + slotItemNotTitlePart + ")");
        Report(slotNoAchievement == 0, "绑定的成就都是分类 [title] 的任务(不是的 " + slotNoAchievement + " 个)");
        Report(slotItemMissing == 0 && slotItemNotTitlePart == 0, "格里列的称号物品都读得到、装备类型都是称号");
        int adjustNoQuest = book.Adjusts.Count(a => quests.Get(a.QuestId) == null);
        int adjustNoAchievement = book.Adjusts.Count(a => achievements.Get(a.AchievementId) == null);
        int adjustItemDiffers = book.Adjusts.Count(a => achievements.Get(a.AchievementId) is AchievementDef d && d.TitleItemCode != a.ItemCode);
        Console.WriteLine("  补发清单 " + book.Adjusts.Count + " 项:任务不存在 " + adjustNoQuest + "、成就不存在 " + adjustNoAchievement + "、清单里的物品与成就奖励的称号不同 " + adjustItemDiffers);
        Report(adjustNoAchievement == 0, "补发清单里的成就都存在(不存在 " + adjustNoAchievement + " 项)");

        // 5. 演习:成就框架在真实定义上
        bad += Drill(book, achievements, code => items.Get(code), opts.Seed);

        // 6. 逐记录转储(给独立的 Python 实现比对)
        string? dump = Environment.GetEnvironmentVariable("DNF_TITLE_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            WriteDump(dump, codes, titles, book, achievements);
            Console.WriteLine("  逐记录转储已写出:" + dump + "(用 tools/py/title_ref.py compare 与独立的 Python 实现逐记录比对)");
        }
        return bad == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 演习

    private static int Drill(TitleBookDef book, AchievementCatalog achievements, Func<int, ItemInfo> lookup, int seed)
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

        Console.WriteLine("  成就框架演习(真实定义,角色等级按 " + DrillLevel + " 级,等级门槛另测):");
        int reached = 0, reachedBad = 0, shortOk = 0, shortBad = 0, afterOk = 0, afterBad = 0, dupOk = 0, dupBad = 0, gateOk = 0, gateBad = 0, gateChecked = 0, skippedZero = 0;
        int titleMatch = 0, titleBad = 0, placedExpected = 0, placedBad = 0, placedHere = 0, placedElsewhere = 0, notListed = 0, postalOk = 0, postalBad = 0;
        foreach (AchievementDef def in achievements.All())
        {
            int[] need = { def.InitialCount(0), def.InitialCount(1), def.InitialCount(2) };

            // 等级门槛:低一级被拒、不留进度;够了照常
            if (def.MinLevel > 1)
            {
                gateChecked++;
                var gate = NewSystem(book, achievements, lookup);
                TitleTriggerOutcome low = gate.Trigger(def.Id, need[0], need[1], need[2], def.MinLevel - 1);
                bool untouched = gate.Achievements.EntryCount == 0 && gate.Version == 0;
                TitleTriggerOutcome ok = gate.Trigger(def.Id, need[0], need[1], need[2], def.MinLevel);
                if (low.Code == AchievementTriggerCode.Invalid && !low.Completed && untouched && ok.Code == AchievementTriggerCode.Ok && ok.Completed)
                {
                    gateOk++;
                }
                else
                {
                    gateBad++;
                }
            }

            // 刚好够:一次触发完成
            var sys = NewSystem(book, achievements, lookup);
            TitleTriggerOutcome hit = sys.Trigger(def.Id, need[0], need[1], need[2], DrillLevel);
            reached++;
            if (hit.Code != AchievementTriggerCode.Ok || !hit.Completed || !sys.Achievements.IsCleared(def.Id))
            {
                reachedBad++;
                continue;
            }

            // 称号:编号与成就定义一致;放进的格就是定义里列出它的第一格(条件是物品读得到且是装备)
            if (def.TitleItemCode != 0)
            {
                titleMatch += hit.TitleCode == def.TitleItemCode ? 1 : 0;
                titleBad += hit.TitleCode == def.TitleItemCode ? 0 : 1;
                bool listedSomewhere = book.TryFindFitSlot(def.TitleItemCode, out TitleCategory category, out int slot);
                ItemInfo item = lookup(def.TitleItemCode);
                bool expectPlaced = listedSomewhere && item != null && item.IsEquipment;
                placedExpected += expectPlaced ? 1 : 0;
                if (hit.TitlePlaced != expectPlaced || (expectPlaced && (hit.Category != category || hit.Slot != slot || sys.Book.CodeAt(category, slot) != def.TitleItemCode)))
                {
                    placedBad++;
                }
                if (!listedSomewhere)
                {
                    notListed++;
                }
                else if (book.SlotAt(category, slot).AchievementId == def.Id)
                {
                    placedHere++;
                }
                else
                {
                    placedElsewhere++;
                }
            }
            else if (hit.TitlePlaced || hit.TitleCode != 0)
            {
                titleBad++;
            }

            // 邮件:成就自带的邮件在、整页邮件只在这一页收齐且有邮件奖励时才有
            int achievementPostals = hit.Postals.Count(p => p.Source == TitlePostalSource.Achievement);
            if (achievementPostals == (def.Postal.IsEmpty ? 0 : 1) && hit.Postals.Count(p => p.Source == TitlePostalSource.Section) <= 1)
            {
                postalOk++;
            }
            else
            {
                postalBad++;
            }

            // 已完成再触发:被拒,进度不动
            int versionBefore = sys.Version;
            TitleTriggerOutcome again = sys.Trigger(def.Id, 1, 0, 0, DrillLevel);
            if (again.Code == AchievementTriggerCode.Rejected && !again.Completed && sys.Version == versionBefore)
            {
                dupOk++;
            }
            else
            {
                dupBad++;
            }

            // 差一个:第一个不为零的计数少触发 1 → 不完成;再补 1 → 完成
            int first = Array.FindIndex(need, v => v > 0);
            if (first < 0)
            {
                skippedZero++;
                continue;
            }
            var near = NewSystem(book, achievements, lookup);
            int[] almost = (int[])need.Clone();
            almost[first]--;
            TitleTriggerOutcome miss = near.Trigger(def.Id, almost[0], almost[1], almost[2], DrillLevel);
            if (miss.Code == AchievementTriggerCode.Ok && !miss.Completed && !near.Achievements.IsCleared(def.Id))
            {
                shortOk++;
            }
            else
            {
                shortBad++;
            }
            var rest = new int[3];
            rest[first] = 1;
            TitleTriggerOutcome last = near.Trigger(def.Id, rest[0], rest[1], rest[2], DrillLevel);
            if (last.Code == AchievementTriggerCode.Ok && last.Completed && near.Achievements.IsCleared(def.Id))
            {
                afterOk++;
            }
            else
            {
                afterBad++;
            }
        }
        Report(reachedBad == 0, "每个成就按初始计数一次触发就完成(" + reached + " 个,不完成 " + reachedBad + ")");
        Report(shortBad == 0, "差一个不完成(" + shortOk + " 个;" + skippedZero + " 个初始计数全零跳过;不合 " + shortBad + ")");
        Report(afterBad == 0, "差一个之后再补一个就完成(" + afterOk + " 个,不合 " + afterBad + ")");
        Report(dupBad == 0, "已完成再触发一律被拒、进度不动(" + dupOk + " 个,不合 " + dupBad + ")");
        Report(gateBad == 0, "角色等级低一级被拒、够了才算数(" + gateChecked + " 个有最低等级的,不合 " + gateBad + ")");
        Report(titleBad == 0, "完成时给的称号编号与成就定义一致(" + titleMatch + " 个,不合 " + titleBad + ")");
        Report(placedBad == 0, "称号放进的格与定义里列出它的第一格一致(应放进 " + placedExpected + " 个,不合 " + placedBad + ";放进它自己绑的格 " + placedHere + "、放进别的格 "
            + placedElsewhere + "、不在任何格的清单里 " + notListed + ")");
        Report(postalBad == 0, "邮件:成就自带的恰好一封、整页的至多一封(" + postalOk + " 个,不合 " + postalBad + ")");

        // 全部完成:邮件数与收集加成与另一种算法一致
        var all = NewSystem(book, achievements, lookup);
        var order = achievements.All().ToList();
        var random = new Random(seed);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        int notCompleted = 0;
        foreach (AchievementDef def in order)
        {
            TitleTriggerOutcome o = all.Trigger(def.Id, def.InitialCount(0), def.InitialCount(1), def.InitialCount(2), DrillLevel);
            notCompleted += o.Completed ? 0 : 1;
        }
        Report(notCompleted == 0 && all.Achievements.ClearedCount == achievements.Count, "随机次序全部完成(" + all.Achievements.ClearedCount + " / " + achievements.Count + ")");
        int expectedAchievementPostals = achievements.All().Count(a => !a.Postal.IsEmpty);
        int gotAchievementPostals = all.Outbox.Count(p => p.Source == TitlePostalSource.Achievement);
        Report(gotAchievementPostals == expectedAchievementPostals, "成就自带的邮件共 " + gotAchievementPostals + " 封(应有 " + expectedAchievementPostals + ")");
        int sectionPostals = all.Outbox.Count(p => p.Source == TitlePostalSource.Section);
        int pagesWithPostal = book.Sections.Count(s => s.PostalReward() != null);
        int pagesPostalComplete = book.Sections.Count(s => s.PostalReward() != null && book.IsSectionComplete(s, all.Achievements.IsCleared));
        int postalFromIncomplete = all.Outbox.Count(p => p.Source == TitlePostalSource.Section && !book.IsSectionComplete(book.Sections[p.SourceId], all.Achievements.IsCleared));
        int pagesWithoutLetter = book.Sections.Count(s => s.PostalReward() != null && book.IsSectionComplete(s, all.Achievements.IsCleared)
            && !all.Outbox.Any(p => p.Source == TitlePostalSource.Section && p.SourceId == book.Sections.ToList().IndexOf(s)));
        Console.WriteLine("  整页邮件:共 " + sectionPostals + " 封;有邮件奖励的页 " + pagesWithPostal + "、其中全部完成后收齐的 " + pagesPostalComplete + "、收齐却没发信的 " + pagesWithoutLetter);
        Report(postalFromIncomplete == 0, "没收齐的页没有发过整页邮件(发过的 " + postalFromIncomplete + " 封)");

        TitleBonus bonus = all.Bonus(true);
        var expected = new Dictionary<EquipStat, double>();
        int expectedApplied = 0;
        foreach (TitleReward reward in book.DefaultRewards)
        {
            Fold(reward, expected, ref expectedApplied);
        }
        int expectedPages = 0;
        foreach (TitleSectionDef section in book.Sections)
        {
            bool anyBound = false;
            bool allCleared = true;
            IReadOnlyList<TitleSlotDef> slots = book.SlotsOf(section.Category);
            for (int k = section.Start; k < section.End && k < slots.Count; k++)
            {
                if (slots[k].IsEmpty || !slots[k].IsBound)
                {
                    continue;
                }
                anyBound = true;
                allCleared &= all.Achievements.IsCleared(slots[k].AchievementId);
            }
            if (!anyBound || !allCleared)
            {
                continue;
            }
            expectedPages++;
            foreach (TitleReward reward in section.Rewards)
            {
                Fold(reward, expected, ref expectedApplied);
            }
        }
        bool same = bonus.PagesCounted == expectedPages && bonus.RewardsApplied == expectedApplied
            && Enum.GetValues<EquipStat>().All(s => bonus.Stats[s] == (expected.TryGetValue(s, out double v) ? v : 0.0));
        Report(same, "全部完成后的收集加成与另一种累加算法逐项一致(计入 " + bonus.PagesCounted + " 页、" + bonus.RewardsApplied + " 条奖励,只计数没折算 " + bonus.RewardsSkipped + " 条)");
        Report(all.Bonus(false).RewardsApplied == bonus.RewardsApplied - book.DefaultRewards.Count(r => r.Kind == TitleRewardKind.Stat && r.IsAbsolute && r.TryGetStat(out _)),
            "不含默认奖励时只少默认奖励那几条");

        // 登录核对与补发:一个任务都没完成时什么也不动;全部任务都算完成时按定义走
        var login = NewSystem(book, achievements, lookup);
        Report(login.Verify(_ => false, 0) == 0 && login.Adjust(_ => false) == 0 && login.Version == 0, "没有任何任务完成时,登录核对与补发什么也不动");
        int expectedVerify = 0;
        var seen = new HashSet<int>();
        foreach (TitleCategory category in Enum.GetValues<TitleCategory>())
        {
            foreach (TitleSlotDef slot in book.SlotsOf(category))
            {
                if (slot.IsEmpty || !slot.IsBound || !seen.Add(slot.AchievementId))
                {
                    continue;
                }
                AchievementDef? def = achievements.Get(slot.AchievementId);
                expectedVerify += def != null && def.IsVerifiable && def.TitleItemCode != 0 ? 1 : 0;
            }
        }
        int verified = login.Verify(_ => true, 0);
        Report(verified == expectedVerify, "登录核对(所有任务都算完成):补成完成的成就 " + verified + " 个(按定义应有 " + expectedVerify + ")");
        int handled = login.Adjust(_ => true);
        int expectedAdjust = book.Adjusts.Count(a => achievements.Get(a.AchievementId) != null);
        Report(handled == expectedAdjust, "补发(所有任务都算完成):处理 " + handled + " 项(按定义应有 " + expectedAdjust + ")");
        return bad;
    }

    private static TitleSystem NewSystem(TitleBookDef book, AchievementCatalog achievements, Func<int, ItemInfo> lookup)
    {
        return new TitleSystem(book, achievements, lookup);
    }

    private static void Fold(TitleReward reward, Dictionary<EquipStat, double> into, ref int applied)
    {
        if (reward.Kind != TitleRewardKind.Stat || !string.Equals(reward.ValueType, "+", StringComparison.Ordinal) || !EquipStats.TryFromTag(reward.StatTag, out EquipStat stat))
        {
            return;
        }
        into.TryGetValue(stat, out double sum);
        into[stat] = sum + reward.Value;
        applied++;
    }

    // ------------------------------------------------------------------ 转储

    private static string Opt(string? s) => string.IsNullOrEmpty(s) ? "-" : s;

    private static string Txt(TitleText t) => t.IsEmpty ? "-" : t.Kind.ToString(CultureInfo.InvariantCulture) + ":" + t.Key;

    private static string Num(double x)
    {
        if (double.IsNaN(x))
        {
            return "nan";
        }
        if (double.IsInfinity(x))
        {
            return x > 0 ? "inf" : "-inf";
        }
        if (Math.Floor(x) == x && Math.Abs(x) < 1e15)
        {
            return ((long)x).ToString(CultureInfo.InvariantCulture);
        }
        return x.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string RewardsText(IReadOnlyList<TitleReward> rewards)
    {
        if (rewards.Count == 0)
        {
            return "-";
        }
        return string.Join("|", rewards.Select(r => r.Kind switch
        {
            TitleRewardKind.Emotion => "E," + I(r.EmotionId),
            TitleRewardKind.Postal => "P," + I(r.ItemCode) + "," + I(r.ItemCount) + "," + (r.Texts.Length == 0 ? "-" : string.Join(";", r.Texts.Select(Txt))),
            _ => "S," + r.StatTag + "," + Opt(r.ValueType) + "," + Num(r.Value),
        }));
    }

    private static void WriteDump(string path, IReadOnlyList<int> codes, TitleCatalog titles, TitleBookDef book, AchievementCatalog achievements)
    {
        var rows = new List<string>();
        foreach (int code in codes)
        {
            TitleAppearance? a = titles.AppearanceOf(code);
            if (a == null)
            {
                rows.Add("T\t" + I(code) + "\t<none>");
                continue;
            }
            rows.Add("T\t" + I(code) + "\t" + Opt(a.PartTag) + "\t" + (a.NameKey == null ? "-" : I(a.TextKind) + ":" + a.NameKey) + "\t" + Opt(a.Name2Key) + "\t"
                + Opt(a.AnimationPath) + "\t" + I(a.UsablePeriodDays) + "\t" + Opt(a.IconPath) + ":" + I(a.IconFrame));
        }
        int n = 0;
        foreach (TitleBalloon b in titles.Balloons())
        {
            rows.Add("L\t" + I(n++) + "\t" + I(b.TitleCode) + "\t" + I(b.SecondCode) + "\t" + Opt(b.StayAnimation) + "\t" + Opt(b.MoveAnimation) + "\t" + I(b.OffsetX) + "\t" + I(b.OffsetY));
        }
        foreach (TitleCategory category in Enum.GetValues<TitleCategory>())
        {
            IReadOnlyList<TitleSlotDef> slots = book.SlotsOf(category);
            for (int k = 0; k < slots.Count; k++)
            {
                TitleSlotDef s = slots[k];
                rows.Add("B\tslot\t" + I((int)category) + "\t" + I(k) + "\t" + I(s.Flag) + "\t" + I(s.AchievementId) + "\t" + (s.Items.Count == 0 ? "-" : string.Join(",", s.Items.Select(I))));
            }
        }
        foreach (TitleSectionDef s in book.Sections)
        {
            rows.Add("B\tpage\t" + I((int)s.Category) + "\t" + I(s.PageIndex) + "\t" + I(s.Start) + "\t" + I(s.End) + "\t" + Txt(s.SectionName) + "\t" + Opt(s.IconPath) + ":" + I(s.IconFrame)
                + "\t" + I(s.FrameType) + "\t" + Txt(s.LockMessage) + "\t" + Txt(s.RewardName) + "\t" + Opt(s.ImagePath) + ":" + I(s.ImageFrame) + "\t" + Opt(s.EffectPath)
                + "\t" + RewardsText(s.Rewards));
        }
        rows.Add("B\tdefault\t" + RewardsText(book.DefaultRewards));
        n = 0;
        foreach (TitleConditionMessage m in book.ConditionMessages)
        {
            rows.Add("B\tmsg\t" + I(n++) + "\t" + I((int)m.Category) + "\t" + I(m.Slot) + "\t" + Txt(m.Text));
        }
        n = 0;
        foreach (TitleAdjust a in book.Adjusts)
        {
            rows.Add("B\tadjust\t" + I(n++) + "\t" + I(a.QuestId) + "\t" + I(a.ItemCode) + "\t" + I(a.AchievementId));
        }
        rows.Add("B\tmisc\tview=" + (book.ViewEventMark ? "1" : "0") + ";minlevel=" + (book.HasEventMinLevel ? I(book.EventMinLevel) : "-") + ";skipped=" + I(book.SkippedTags)
            + ";trunc=" + I(book.TruncatedSlotLists) + ";mismatch=" + I(book.SlotIndexMismatches) + ";unknowncat=" + I(book.SkippedUnknownCategory));
        foreach (AchievementDef d in achievements.All())
        {
            rows.Add("A\t" + I(d.Id) + "\t" + Opt(d.ConditionTag) + "\t" + I(d.ClearQuestId) + "\t" + (d.CheckCounts.Count == 0 ? "-" : string.Join(",", d.CheckCounts.Select(I))) + "\t"
                + I(d.InitialCount(0)) + "," + I(d.InitialCount(1)) + "," + I(d.InitialCount(2)) + "\t" + I(d.TitleItemCode) + "\t"
                + I(d.Postal.ItemCode) + "," + I(d.Postal.Count) + "," + Txt(d.Postal.Title) + "," + Txt(d.Postal.Body) + "\t" + I(d.MinLevel));
        }

        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        using var writer = new StreamWriter(full, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (string row in rows)
        {
            writer.Write(row);
            writer.Write('\n');
        }
    }
}
