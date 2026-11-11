using System.Diagnostics;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// quest-check:任务系统的检查(证据 E-123)。整库读 <c>n_quest/quest.lst</c> 与每个 .qst 脚本、<c>questparameter.etc</c>,
/// 检查结构:每个任务读得出来、分类与类型都认得、这一步支持的任务引用的 NPC、物品、副本、怪物、地图都在各自的列表里、发布任务的 NPC 摆在城镇里;
/// 再对每个这一步支持的任务演习一遍完整的生命周期(挑一个合格的角色、满足前置、接取、按任务的目标逐个触发事件、条件满足、交任务、算奖励、完成后能否再接),
/// 核对"没做完不能交、做完才能交"。数量、编号与分布来自用户自己的数据,只打印在本机终端,不要抄进入库的文件;通过与否只看结构。
/// </summary>
internal static class QuestCheck
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

        // 1. 任务目录
        var watch = Stopwatch.StartNew();
        var catalog = new QuestCatalog(data);
        IReadOnlyList<int> ids = catalog.Ids();
        Report(ids.Count > 0, "quest.lst 列了 " + ids.Count + " 个任务");
        if (ids.Count == 0)
        {
            return 1;
        }
        IReadOnlyList<QuestDef> all = catalog.All();
        watch.Stop();
        Report(all.Count == ids.Count, "每个任务脚本都读得出来(读不出的 " + (ids.Count - all.Count) + " 个,整库解析用时 " + watch.ElapsedMilliseconds + " 毫秒)");
        int unknownKind = all.Count(q => q.Kind == QuestKind.Unknown);
        Report(unknownKind == 0, "[type] 都认得(不认得的 " + unknownKind + " 个)");
        int noName = all.Count(q => q.Name.IsEmpty);
        Console.WriteLine("  没有 [name] 的任务 " + noName + " 个;有发布 NPC 的 " + all.Count(q => q.NpcId >= 0) + " 个;有交任务 NPC 的 " + all.Count(q => q.CompleteNpcId >= 0) + " 个");
        Console.WriteLine("  分类(编号次序):" + string.Join(" ", Enum.GetValues<QuestGrade>().Select(g => (int)g + "=" + all.Count(q => q.Grade == g))));
        Console.WriteLine("  类型(编号次序):" + string.Join(" ", all.GroupBy(q => q.Kind).OrderBy(g => (int)g.Key).Select(g => (int)g.Key + "=" + g.Count())));

        // 2. 奖励参数
        QuestParameters p = catalog.Parameters;
        Report(p.ExpTableLength >= 100 && p.GoldTableLength >= 100, "奖励参数表读得出来(经验表 " + p.ExpTableLength + " 项、金币表 " + p.GoldTableLength + " 项)");
        Report(p.GreenPenalty >= 0 && p.GreenPenalty <= 100 && p.GreyPenalty >= 0 && p.GreyPenalty <= p.GreenPenalty, "等级差惩罚是百分数,灰色惩罚不高于绿色惩罚(" + p.GreenPenalty + "/" + p.GreyPenalty + ")");
        int missingWeight = all.Where(q => q.DifficultyLetter != '\0').Select(q => q.DifficultyLetter).Distinct().Count(c => p.Weight(c) == 100);
        Console.WriteLine("  任务里用到的难度字母在权重表里没有(按 100)的有 " + missingWeight + " 种");

        // 3. 支持范围(猎杀目标的怪物要在 monster.lst 里,否则杀不出来)
        var monsters = new HashSet<int>(data.MonsterIds());
        var supported = all.Where(q => QuestRules.IsSupported(q, monsters.Contains)).ToList();
        var reasons = all.Select(q => QuestRules.UnsupportedReason(q, monsters.Contains)).Where(r => r != null).GroupBy(r => r!).OrderByDescending(g => g.Count());
        Console.WriteLine("  这一步支持的任务 " + supported.Count + " 个;不支持的原因:" + string.Join(" ", reasons.Select(g => g.Key + "=" + g.Count())));
        Report(supported.Count > 0, "有任务在这一步的支持范围里");
        Report(all.Count(q => QuestRules.IsSupported(q)) >= supported.Count, "怪物过滤只会减少支持的任务");
        Console.WriteLine("  支持的任务按类型:" + string.Join(" ", supported.GroupBy(q => q.Kind).OrderBy(g => (int)g.Key).Select(g => (int)g.Key + "=" + g.Count())) + ";按分类:"
            + string.Join(" ", supported.GroupBy(q => q.Grade).OrderBy(g => (int)g.Key).Select(g => (int)g.Key + "=" + g.Count())));

        // 3a. 转职任务(M59,证据 E-153):奖励种类是换成长类型的,新的成长类型下标要在 1 到 5 之内(整库,不只是这一步支持的)
        List<QuestDef> growQuests = all.Where(q => q.IsGrowtypeReward).ToList();
        int growBad = growQuests.Count(q => QuestRules.GrowtypeOf(q) < 0);
        Console.WriteLine("  转职任务(奖励种类是换成长类型)" + growQuests.Count + " 个:新成长类型下标 "
            + string.Join(" ", growQuests.GroupBy(q => QuestRules.GrowtypeOf(q)).OrderBy(g => g.Key).Select(g => g.Key + "×" + g.Count()))
            + ";这一步支持的 " + growQuests.Count(q => QuestRules.IsSupported(q, monsters.Contains)) + " 个");
        Report(growBad == 0, "转职任务的新成长类型下标都在 1 到 " + (SkillRules.GrowtypeCount - 1) + " 之内(越界或没写的 " + growBad + " 个)");

        // 3a'. 觉醒任务(r15-awaken,服务端 quest_reward 的奖励类型 2):奖励种类是觉醒的,觉醒号要在 1 到 AwakeningRules.Count 之内(整库);
        //      觉醒链(成就类、[job change quest] 为 2)的等级下限不低于觉醒门槛;觉醒奖励任务都在觉醒链里(链外的会按各自的分类走,单独报数)
        List<QuestDef> awakeQuests = all.Where(q => q.IsAwakeningReward).ToList();
        int awakeBad = awakeQuests.Count(q => QuestRules.AwakeningOf(q) < 1);
        List<QuestDef> chain = all.Where(QuestRules.IsAwakeningChain).ToList();
        int chainLow = chain.Count(q => q.MinLevel < AwakeningRules.MinLevel);
        int awakeOutside = awakeQuests.Count(q => !QuestRules.IsAwakeningChain(q));
        Console.WriteLine("  觉醒任务(奖励种类是觉醒)" + awakeQuests.Count + " 个:觉醒号 "
            + string.Join(" ", awakeQuests.GroupBy(q => QuestRules.AwakeningOf(q)).OrderBy(g => g.Key).Select(g => g.Key + "×" + g.Count()))
            + ";在觉醒链之外的 " + awakeOutside + " 个;这一步支持的 " + awakeQuests.Count(q => QuestRules.IsSupported(q, monsters.Contains)) + " 个");
        Console.WriteLine("  觉醒链(成就类、[job change quest] " + AwakeningRules.QuestFlag + ")" + chain.Count + " 个:等级下限 "
            + string.Join(" ", chain.GroupBy(q => q.MinLevel).OrderBy(g => g.Key).Select(g => g.Key + "×" + g.Count()))
            + ";这一步支持的 " + chain.Count(q => QuestRules.IsSupported(q, monsters.Contains)) + " 个");
        Report(awakeBad == 0, "觉醒任务的觉醒号都在 1 到 " + AwakeningRules.Count + " 之内(越界或没写的 " + awakeBad + " 个)");
        Report(chainLow == 0, "觉醒链上的任务等级下限都不低于觉醒门槛 " + AwakeningRules.MinLevel + " 级(低于的 " + chainLow + " 个)");

        // 3b. 文字:名字与三段对话在整库里取不取得到、写法里有没有要特殊处理的字符(只打印个数,不打印文字)
        var library = new StrLibrary(path => assets.Pvf.TryGetEntry(path, out PvfEntry entry) ? assets.Pvf.Read(entry) : null);
        var texts = new (string Label, Func<QuestDef, QuestText> Pick)[]
        {
            ("名字", q => q.Name), ("接取对话", q => q.DependMessage), ("进行中对话", q => q.ConditionMessage), ("完成对话", q => q.SolveMessage),
        };
        foreach ((string label, Func<QuestDef, QuestText> pick) in texts)
        {
            int have = 0, missing = 0, newline = 0, percent = 0, angle = 0, brace = 0, bracket = 0, backslash = 0, control = 0, longest = 0;
            foreach (QuestDef q in supported)
            {
                QuestText reference = pick(q);
                if (reference.IsEmpty)
                {
                    continue;
                }
                string? text = library.Get(reference.Kind, reference.Key);
                if (text == null)
                {
                    missing++;
                    continue;
                }
                have++;
                longest = Math.Max(longest, text.Length);
                newline += text.IndexOf('\n') >= 0 ? 1 : 0;
                percent += text.IndexOf('%') >= 0 ? 1 : 0;
                angle += text.IndexOfAny(new[] { '<', '>' }) >= 0 ? 1 : 0;
                brace += text.IndexOfAny(new[] { '{', '}' }) >= 0 ? 1 : 0;
                bracket += text.IndexOfAny(new[] { '[', ']' }) >= 0 ? 1 : 0;
                backslash += text.IndexOf('\\') >= 0 ? 1 : 0;
                control += text.Any(c => c < ' ' && c != '\n') ? 1 : 0;
            }
            if (Environment.GetEnvironmentVariable("DNF_QUEST_TAGS") == "1")
            {
                var shapes = new SortedDictionary<string, int>();
                foreach (QuestDef q in supported)
                {
                    QuestText reference = pick(q);
                    string? text = reference.IsEmpty ? null : library.Get(reference.Kind, reference.Key);
                    if (text == null)
                    {
                        continue;
                    }
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[<\[][\x20-\x7E]{1,24}[>\]]|%[A-Za-z0-9.]+"))
                    {
                        shapes[m.Value] = shapes.TryGetValue(m.Value, out int n) ? n + 1 : 1;
                    }
                }
                Console.WriteLine("  [本机] " + label + " 的 ASCII 标记写法:" + string.Join(" ", shapes.Select(kv => kv.Key + "×" + kv.Value)));
            }
            Console.WriteLine("  文字 " + label + ":取得 " + have + ",键在表里查不到 " + missing + ";含换行 " + newline + "、百分号 " + percent + "、尖括号 " + angle
                + "、花括号 " + brace + "、方括号 " + bracket + "、反斜杠 " + backslash + "、其它控制字符 " + control + ",最长 " + longest + " 字");
        }

        // 4. 引用:NPC、物品、副本、怪物、地图
        Dictionary<int, string> npcCatalog = TownScene.ReadNpcCatalog(assets);
        var items = new ItemCatalog(data);
        Dictionary<int, string> dungeons = data.ReadList("dungeon/dungeon.lst") ?? new Dictionary<int, string>();
        Dictionary<int, string> maps = data.ReadList("map/map.lst") ?? new Dictionary<int, string>();
        var itemCache = new Dictionary<int, bool>();
        bool ItemKnown(int id)
        {
            if (!itemCache.TryGetValue(id, out bool known))
            {
                known = items.Get(id) != null;
                itemCache[id] = known;
            }
            return known;
        }

        int danglingNpc = 0, danglingItem = 0, danglingDungeon = 0, danglingMonster = 0, danglingMap = 0;
        var danglingItems = new SortedSet<int>();
        foreach (QuestDef q in supported)
        {
            if (!npcCatalog.ContainsKey(q.NpcId))
            {
                danglingNpc++;
            }
            int handIn = QuestRules.HandInNpc(q);
            if (handIn >= 0 && !npcCatalog.ContainsKey(handIn))
            {
                danglingNpc++;
            }
            var itemIds = new List<int>();
            itemIds.AddRange(q.Rewards.Where(r => r.Id != 0).Select(r => r.Id));
            itemIds.AddRange(q.SelectRewards.Select(r => r.Id));
            itemIds.AddRange(q.AcceptItems.Select(r => r.Id));
            itemIds.AddRange(q.SeekingItems().Select(r => r.Id));
            itemIds.AddRange(q.MonsterDrops.Select(d => d.Item));
            itemIds.AddRange(q.ClearDrops.Select(d => d.Item));
            foreach (int id in itemIds)
            {
                if (!ItemKnown(id))
                {
                    danglingItem++;
                    danglingItems.Add(id);
                }
            }
            var dungeonIds = new List<int>();
            foreach (QuestKillGroup g in q.KillGroups())
            {
                dungeonIds.Add(g.Dungeon);
                if (!monsters.Contains(g.Monster))
                {
                    danglingMonster++;
                }
            }
            if (q.Kind == QuestKind.ConditionUnderClear && q.IntData.Length > 0)
            {
                dungeonIds.Add(q.IntData[0]);
            }
            dungeonIds.AddRange(q.DungeonHints.Select(h => h.Key));
            danglingDungeon += dungeonIds.Count(d => d != -1 && !dungeons.ContainsKey(d));
            if (q.Kind == QuestKind.ClearMap && !maps.ContainsKey(q.ClearMapId()))
            {
                danglingMap++;
            }
        }
        Console.WriteLine("  支持的任务引用里悬空的(列表里没有):NPC " + danglingNpc + "、物品 " + danglingItem + "(" + danglingItems.Count + " 种)、副本 " + danglingDungeon + "、怪物 " + danglingMonster + "、地图 " + danglingMap);
        Report(danglingNpc == 0, "支持的任务引用的 NPC 都在 npc.lst 里(悬空的 " + danglingNpc + " 处)");
        Report(danglingMap == 0, "支持的过房间任务的地图都在 map.lst 里(悬空的 " + danglingMap + " 处)");
        Report(danglingMonster == 0, "支持的猎杀任务的怪物都在 monster.lst 里(悬空的 " + danglingMonster + " 处;怪物过滤之后应为 0)");

        // 5. 发布任务的 NPC 摆在城镇里
        TownAtlas atlas = TownAtlas.Open(assets);
        var placed = new HashSet<int>();
        foreach (int town in atlas.TownIds)
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
                try
                {
                    foreach (TownNpc npc in TownScene.ReadMapNpcs(assets, info.MapPath))
                    {
                        placed.Add(npc.Id);
                    }
                }
                catch (DnfFormatException)
                {
                }
            }
        }
        int offeredPlaced = supported.Count(q => placed.Contains(q.NpcId));
        Console.WriteLine("  城镇里摆着的 NPC 共 " + placed.Count + " 种;发布支持的任务的 NPC 有 " + supported.Select(q => q.NpcId).Distinct().Count() + " 个,其中摆在城镇里的 "
            + supported.Where(q => placed.Contains(q.NpcId)).Select(q => q.NpcId).Distinct().Count() + " 个,这些 NPC 发布的支持任务 " + offeredPlaced + " 个");
        Report(offeredPlaced > 0, "有支持的任务由摆在城镇里的 NPC 发布");

        // 6. 生命周期演习
        var failures = new List<string>();
        var unreachable = new List<int>();
        int drilled = 0, repeatable = 0, autoComplete = 0, handIns = 0;
        foreach (QuestDef q in supported)
        {
            string? error = Drill(q, p, out bool reachable);
            if (!reachable)
            {
                unreachable.Add(q.Id);
                continue;
            }
            drilled++;
            if (error != null)
            {
                failures.Add(q.Id + ":" + error);
            }
            if (QuestNames.IsRepeatable(q.Grade))
            {
                repeatable++;
            }
            if (QuestRules.HandInNpc(q) < 0)
            {
                autoComplete++;
            }
            else
            {
                handIns++;
            }
        }
        Console.WriteLine("  演习 " + drilled + " 个任务(可重复类 " + repeatable + "、条件满足即自动完成 " + autoComplete + "、要回 NPC 交 " + handIns + ");没有任何职业与转职方向能接的 " + unreachable.Count + " 个");
        Report(failures.Count == 0, "每个任务的生命周期演习都对(接取、逐个触发、未做完不能交、做完能交、奖励不为负、完成后的状态)" + (failures.Count == 0 ? "" : ":" + string.Join(" ", failures.Take(12))));

        // 7. 新角色开局时能看到多少任务(只数摆在城镇里的 NPC 发布的)
        foreach (int level in new[] { 1, 15, 30, 60 })
        {
            var log = new QuestLog();
            var hero = new QuestHero(level, EquipRules.SwordmanJob, 0);
            int offered = supported.Count(q => placed.Contains(q.NpcId) && QuestRules.CheckOffer(q, hero, log) == QuestOffer.Ok);
            int npcs = supported.Where(q => placed.Contains(q.NpcId) && QuestRules.CheckOffer(q, hero, log) == QuestOffer.Ok).Select(q => q.NpcId).Distinct().Count();
            Console.WriteLine("  " + level + " 级剑魂、没完成过任何任务:可接的任务 " + offered + " 个,分布在 " + npcs + " 个 NPC 身上");
        }

        Console.WriteLine(bad == 0 ? "quest-check 全部通过" : "quest-check 有 " + bad + " 项失败");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// 演习一个任务的完整生命周期;返回 null 表示没问题,否则是一个短词指出哪一步不对。
    /// <paramref name="reachable"/> 为 false 表示找不到能接这个任务的角色(没有任何职业与转职方向匹配)。
    /// </summary>
    private static string? Drill(QuestDef q, QuestParameters p, out bool reachable)
    {
        reachable = false;
        QuestHero hero = default;
        int level = Math.Max(1, Math.Min(q.MinLevel, q.MaxLevel));
        for (int job = 0; job < EquipRules.JobCount && !reachable; job++)
        {
            for (int grow = 0; grow < 6 && !reachable; grow++)
            {
                if (QuestRules.JobMatches(q, job, grow))
                {
                    hero = new QuestHero(level, job, grow);
                    reachable = true;
                }
            }
        }
        if (!reachable)
        {
            return null;
        }

        var log = new QuestLog();
        foreach (int[] group in q.PreRequired.Take(1))
        {
            foreach (int id in group)
            {
                log.MarkCleared(id);
            }
        }
        Func<int, QuestDef> find = id => id == q.Id ? q : null!;
        ActiveQuest? a = QuestRules.Accept(q, hero, log, out QuestOffer why);
        if (a == null)
        {
            return "accept-" + why;
        }
        if (QuestRules.IsReady(q, a, _ => 0))
        {
            return "ready-at-start";
        }

        var bag = new Dictionary<int, int>();
        int Count(int id) => bag.TryGetValue(id, out int n) ? n : 0;
        var steps = new List<Action>();
        switch (q.Kind)
        {
            case QuestKind.HuntMonster:
            case QuestKind.HuntEnemy:
                foreach (QuestKillGroup g in q.KillGroups())
                {
                    for (int i = 0; i < g.Count; i++)
                    {
                        QuestKillGroup captured = g;
                        steps.Add(() => QuestTracker.OnKill(log, find, captured.Dungeon == -1 ? 1 : captured.Dungeon, captured.Difficulty == -1 ? 0 : captured.Difficulty,
                            captured.Monster, captured.EnemyType == 0 ? QuestRules.EnemyNormal : captured.EnemyType));
                    }
                }
                break;
            case QuestKind.ClearMap:
                steps.Add(() => QuestTracker.OnMapClear(log, find, q.ClearMapId()));
                break;
            case QuestKind.MeetNpc:
                steps.Add(() => QuestTracker.OnTalk(log, find, q.MeetNpcId()));
                break;
            case QuestKind.ConditionUnderClear:
                for (int i = 0; i < a.Remaining[0]; i++)
                {
                    steps.Add(() => QuestTracker.OnDungeonClear(log, find, q.IntData[0] == -1 ? 1 : q.IntData[0], q.IntData[1] == -1 ? 0 : q.IntData[1], 1));
                }
                break;
            case QuestKind.Seeking:
                steps.Add(() =>
                {
                    foreach (QuestItem need in q.SeekingItems())
                    {
                        bag[need.Id] = Count(need.Id) + need.Count;
                    }
                });
                break;
        }
        if (steps.Count == 0)
        {
            return "no-steps";
        }
        for (int i = 0; i < steps.Count; i++)
        {
            steps[i]();
            bool ready = QuestRules.IsReady(q, a, Count);
            if (i < steps.Count - 1 && ready)
            {
                return "ready-early";
            }
            if (i == steps.Count - 1 && !ready)
            {
                return "not-ready-at-end";
            }
        }

        QuestReward reward = QuestRules.ComputeReward(q, hero, p, QuestRules.SelectableRewards(q, hero).Count > 0 ? 0 : -1);
        if (reward.Exp < 0 || reward.Gold < 0 || (q.Grade == QuestGrade.NormalRepeat && reward.Exp != 0))
        {
            return "reward";
        }
        if (q.IsGrowtypeReward != (reward.Growtype >= 0) || (reward.Growtype >= 0 && (reward.Growtype == hero.GrowType || reward.Items.Count != 0)))
        {
            return "growtype-reward";
        }
        if (q.IsAwakeningReward != (reward.Awakening >= 1) || (reward.Awakening >= 1 && (reward.Growtype >= 0 || reward.Items.Count != 0)))
        {
            return "awakening-reward";
        }
        if (!QuestRules.Complete(q, log) || log.IsActive(q.Id))
        {
            return "complete";
        }
        bool repeatable = QuestNames.IsRepeatable(q.Grade);
        if (log.IsCleared(q.Id) == repeatable)
        {
            return "cleared-state";
        }
        QuestOffer again = QuestRules.CheckOffer(q, hero, log);
        if (repeatable ? again != QuestOffer.Ok : again != QuestOffer.Cleared)
        {
            return "reoffer-" + again;
        }
        return null;
    }
}
