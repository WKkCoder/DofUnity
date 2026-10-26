using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// quest-flow-check:任务系统在副本里的一整圈(证据 E-123)。按用户自己的数据分两遍打同一座副本(自动机按固定心跳、同一个种子,每一局打出的都是同一局):
/// 第一遍挂一个只记账的 <see cref="IQuestEvents"/>,记下整局发出的击杀、清房、通关事件;再据此挑任务——目标正好是这一局里会发生的事、主角这个等级现在接得了的
/// 猎杀、过房间、通关各一个(猎杀与通关的数量要几局凑够就打几局,像玩家重复刷同一座副本);第二遍把真正的 <see cref="QuestBook"/> 接到副本会话上
/// (<see cref="DungeonSession.Create"/> 的 quests 参数,玩家实际用的那条路),先接下这几个任务再打,打完核对:击杀、清房、通关事件推进了计数、任务达成
/// (没有交任务 NPC 的自动完成、有的显示可以交再代玩家去交)、奖励发到主角身上、提示条与追踪取得到内容。
/// 任务、怪物与奖励的编号和数量来自用户自己的数据,只打印在本机终端,不要抄进入库的文件;通过与否只看这一圈走得通。
/// 没有合适的任务时(这个等级、这座副本里接得了的任务不多)只打印各等级的可选个数并跳过,换 <c>--level</c> 或 <c>--dungeon</c> 再试;
/// <c>--dungeon auto</c> 按这个等级现在接得了的任务把副本排序、依次试到三种任务都覆盖到或试完前 12 座为止。
/// </summary>
internal static class QuestFlowCheck
{
    /// <summary>--dungeon auto:自动选副本。</summary>
    public const string Auto = "auto";

    private const double FrameMs = 1000.0 / 60.0;
    private const double MaxSimulatedMs = 900_000;
    private const int MaxCandidates = 12;
    private const int MaxRuns = 8;
    private const int DefaultDamageScale = 300;
    private const int KindHunt = 1;
    private const int KindClear = 2;
    private const int KindMap = 4;

    /// <summary>只记账的事件接收器:第一遍用来知道这一局里会发生哪些事件。</summary>
    private sealed class Recorder : IQuestEvents
    {
        public List<(int Monster, bool Boss)> Kills { get; } = new List<(int Monster, bool Boss)>();

        public List<int> Maps { get; } = new List<int>();

        public int Clears { get; private set; }

        public void MonsterKilled(int dungeonId, int difficulty, int monsterCode, bool boss)
        {
            Kills.Add((monsterCode, boss));
        }

        public void MapCleared(int mapId)
        {
            Maps.Add(mapId);
        }

        public void DungeonCleared(int dungeonId, int difficulty, int clearMilliseconds)
        {
            Clears++;
        }
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        int level = opts.HeroLevel <= 1 ? 40 : opts.HeroLevel;
        if (!string.Equals(opts.Dungeon, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return RunOne(assets, opts, opts.Dungeon, level, out _);
        }

        // 自动选副本:列出这个等级接得了的任务的目标副本,按任务多少排序
        HeroProgress probe = HeroProgress.CreateForLocalHero(assets, level);
        QuestBook book = QuestBook.FromAssets(assets, probe, opts.Seed);
        var hero = new QuestHero(level, probe.Job, probe.Growtype);
        var weight = new Dictionary<int, int>();
        void Count(int dungeon)
        {
            if (dungeon >= 0)
            {
                weight[dungeon] = weight.TryGetValue(dungeon, out int w) ? w + 1 : 1;
            }
        }

        Dictionary<int, string> list = WorldMapDef.ParseList(assets.ReadScript(DungeonRoomPool.DungeonListPath), "dungeon/");
        Dictionary<int, HashSet<int>>? mapDungeons = null;       // 地图编号 → 房间池里有它的副本编号(第一次遇到过房间任务才建)
        foreach (QuestDef q in book.Catalog.All().Where(q => QuestRules.CheckOffer(q, hero, book.Log) == QuestOffer.Ok))
        {
            if (q.Kind == QuestKind.HuntMonster)
            {
                foreach (int dungeon in q.KillGroups().Select(g => g.Dungeon).Distinct())
                {
                    Count(dungeon);
                }
            }
            else if (q.Kind == QuestKind.ConditionUnderClear && q.SubType == QuestRules.SubClearDungeon && q.IntData.Length >= 2)
            {
                Count(q.IntData[0]);
            }
            else if (q.Kind == QuestKind.ClearMap && q.ClearMapId() >= 0)
            {
                if (mapDungeons == null)
                {
                    mapDungeons = new Dictionary<int, HashSet<int>>();
                    foreach (KeyValuePair<int, string> d in list)
                    {
                        foreach (RoomCandidate room in DungeonRoomPool.ReadFor(assets, d.Value).Rooms)
                        {
                            if (!mapDungeons.TryGetValue(room.MapId, out HashSet<int>? set))
                            {
                                mapDungeons[room.MapId] = set = new HashSet<int>();
                            }
                            set.Add(d.Key);
                        }
                    }
                }
                if (mapDungeons.TryGetValue(q.ClearMapId(), out HashSet<int>? owners))
                {
                    foreach (int dungeon in owners)
                    {
                        Count(dungeon);
                    }
                }
            }
        }
        List<KeyValuePair<int, string>> candidates = weight.Where(kv => list.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(MaxCandidates).Select(kv => new KeyValuePair<int, string>(kv.Key, list[kv.Key])).ToList();
        Console.WriteLine("自动选副本:主角 " + level + " 级现在接得了的任务指向 " + weight.Count + " 座副本,按任务多少依次试前 " + candidates.Count + " 座");
        int result = 0;
        int covered = 0;
        foreach (KeyValuePair<int, string> c in candidates)
        {
            result |= RunOne(assets, opts, c.Value, level, out int kinds);
            covered |= kinds;
            Console.WriteLine();
            if (covered == (KindHunt | KindClear | KindMap))
            {
                break;
            }
        }
        Console.WriteLine("覆盖的任务种类:猎杀 " + ((covered & KindHunt) != 0 ? "有" : "没有") + "、通关 " + ((covered & KindClear) != 0 ? "有" : "没有")
            + "、过房间 " + ((covered & KindMap) != 0 ? "有" : "没有") + "(没有的这个等级在试过的副本里凑不出,换 --level 再试)");
        if (covered == 0)
        {
            Console.WriteLine("试过的副本里都凑不出达得成的任务(换 --level 再试)。");
        }
        return result;
    }

    private static int RunOne(DnfAssets assets, Options opts, string dungeonPath, int level, out int kinds)
    {
        kinds = 0;
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        int dungeonId = DungeonRoomPool.DungeonIdOf(assets, dungeonPath);
        Console.WriteLine("副本 " + dungeonPath + "(编号 " + dungeonId + "),主角 " + level + " 级,种子 " + opts.Seed);

        // ---- 第一遍:记账
        var recorder = new Recorder();
        (bool bossCleared, double recordedMs, int rooms) = Play(assets, opts, dungeonPath, level, recorder, null, 0);
        Console.WriteLine("  第一遍(记账):模拟 " + (recordedMs / 1000.0).ToString("F1") + " 秒,进过 " + rooms + " 个房间,首领" + (bossCleared ? "清空" : "没清空") + ";击杀 "
            + recorder.Kills.Count + " 只(" + recorder.Kills.Select(k => k.Monster).Distinct().Count() + " 种)、清房 " + recorder.Maps.Count + " 间、通关 " + recorder.Clears + " 次");
        if (!bossCleared || recorder.Kills.Count == 0)
        {
            Console.WriteLine("  [跳过] 自动机没有把这座副本打通(主角等级不够或副本不在这一步的支持范围里)");
            return 0;
        }
        Report(true, "自动机把副本打通了,并发出了击杀事件");

        // ---- 挑任务
        HeroProgress progress = HeroProgress.CreateForLocalHero(assets, level);
        QuestBook book = QuestBook.FromAssets(assets, progress, opts.Seed);
        book.Warm();
        Report(book.ListedCount > 0, "任务清单里有 " + book.ListedCount + " 个任务");
        IReadOnlyList<QuestDef> all = book.Catalog.All();

        bool Here(int dungeon, int difficulty) => (dungeon == -1 || dungeon == dungeonId) && (difficulty == -1 || difficulty == 0);
        int EnemyTypeOf(bool boss) => boss ? QuestRules.EnemyNamed : QuestRules.EnemyNormal;
        int Killed(QuestKillGroup g) => recorder.Kills.Count(k => QuestTracker.KillMatches(g, dungeonId, 0, k.Monster, EnemyTypeOf(k.Boss)));

        // 要几局才凑够:猎杀 = 每组 数量 ÷ 一局里杀到的只数 向上取整,取最大;通关 = 要通关的次数;过房间 = 一局
        int RunsNeeded(QuestDef q)
        {
            switch (q.Kind)
            {
                case QuestKind.HuntMonster:
                    int worst = 0;
                    foreach (QuestKillGroup g in q.KillGroups())
                    {
                        int killed = Killed(g);
                        if (g.Count <= 0 || !Here(g.Dungeon, g.Difficulty) || killed <= 0)
                        {
                            return int.MaxValue;
                        }
                        worst = Math.Max(worst, (g.Count + killed - 1) / killed);
                    }
                    return q.KillGroups().Count == 0 ? int.MaxValue : worst;
                case QuestKind.ConditionUnderClear:
                    if (q.SubType != QuestRules.SubClearDungeon || q.IntData.Length < 2 || !Here(q.IntData[0], q.IntData[1]))
                    {
                        return int.MaxValue;
                    }
                    return q.IntData.Length == 3 && q.IntData[2] > 0 ? q.IntData[2] : 1;
                case QuestKind.ClearMap:
                    return recorder.Maps.Contains(q.ClearMapId()) ? 1 : int.MaxValue;
                default:
                    return int.MaxValue;
            }
        }

        List<QuestDef> Offerable(int heroLevel)
        {
            var hero = new QuestHero(heroLevel, progress.Job, progress.Growtype);
            return all.Where(q => QuestRules.CheckOffer(q, hero, book.Log) == QuestOffer.Ok).ToList();
        }

        bool Feasible(QuestDef q) => RunsNeeded(q) <= MaxRuns;
        List<QuestDef> offerable = Offerable(level);
        List<QuestDef> hunts = offerable.Where(q => q.Kind == QuestKind.HuntMonster && Feasible(q)).OrderBy(RunsNeeded).ThenBy(q => q.KillGroups().Count).ToList();
        List<QuestDef> clears = offerable.Where(q => q.Kind == QuestKind.ConditionUnderClear && Feasible(q)).OrderBy(RunsNeeded).ToList();
        List<QuestDef> maps = offerable.Where(q => q.Kind == QuestKind.ClearMap && Feasible(q)).ToList();
        Console.WriteLine("  这一级现在接得了的任务 " + offerable.Count + " 个;这一局(最多重复 " + MaxRuns + " 局)里达得成的:猎杀 " + hunts.Count + " 个、通关 " + clears.Count + " 个、过房间 "
            + maps.Count + " 个");

        var chosen = new List<QuestDef>();
        foreach (List<QuestDef> pool in new[] { hunts, clears, maps })
        {
            if (pool.Count > 0)
            {
                chosen.Add(pool[0]);
            }
        }
        if (chosen.Count == 0)
        {
            var perLevel = new List<string>();
            for (int l = 1; l <= 85; l += 4)
            {
                List<QuestDef> at = Offerable(l);
                perLevel.Add(l + "级:" + at.Count(q => q.Kind == QuestKind.HuntMonster && Feasible(q)) + "/" + at.Count(q => q.Kind == QuestKind.ConditionUnderClear && Feasible(q))
                    + "/" + at.Count(q => q.Kind == QuestKind.ClearMap && Feasible(q)));
            }
            Console.WriteLine("  [跳过] 这个等级在这局里没有达得成的任务。各等级(猎杀/通关/过房间)的可选个数:" + string.Join(" ", perLevel));
            return bad == 0 ? 0 : 1;
        }
        int runs = chosen.Max(RunsNeeded) + (chosen.Any(q => q.Kind == QuestKind.HuntMonster) ? 1 : 0);   // 猎杀多留一局:各局的种子不同,杀到的只数会有些出入

        // ---- 第二遍:接上任务系统再打
        progress = HeroProgress.CreateForLocalHero(assets, level);
        book = QuestBook.FromAssets(assets, progress, opts.Seed);
        var accepted = new List<QuestDef>();
        foreach (QuestDef def in chosen)
        {
            QuestActionResult result = book.Accept(def.Id);
            Report(result == QuestActionResult.Ok, "接下" + KindName(def) + "任务:" + result + "(交任务 NPC " + (QuestRules.HandInNpc(def) >= 0 ? "有" : "没有,自动完成") + ";要打 " + RunsNeeded(def) + " 局)");
            if (result == QuestActionResult.Ok)
            {
                accepted.Add(def);
            }
        }
        long exp0 = progress.Exp, gold0 = progress.Gold;
        int level0 = progress.Level, bag0 = progress.BagItems;
        var hud = new QuestHud();
        hud.Advance(FrameMs, book);
        Console.WriteLine("  接任务之后提示条 " + hud.ToastsShown + " 条、追踪 " + hud.Tracked.Count + " 行");
        Report(hud.Tracked.Count == Math.Min(accepted.Count, QuestHud.TrackedMax), "追踪里列出了接下的任务");

        var doneAt = new Dictionary<int, (int Run, double Ms)>();
        int roomsFirst = 0;
        double msFirst = 0;
        bool allCleared = true;
        for (int run = 1; run <= runs; run++)
        {
            int thisRun = run;
            (bool cleared, double playedMs, int roomsEntered) = Play(assets, opts, dungeonPath, level, book, progress, run - 1, (t, b) =>
            {
                foreach (QuestDef def in accepted)
                {
                    if (!doneAt.ContainsKey(def.Id) && IsDone(b, def))
                    {
                        doneAt[def.Id] = (thisRun, t);
                    }
                }
            });
            allCleared &= cleared;
            if (run == 1)
            {
                roomsFirst = roomsEntered;
                msFirst = playedMs;
            }
        }
        Console.WriteLine("  第二遍(接着任务):打了 " + runs + " 局,第一局模拟 " + (msFirst / 1000.0).ToString("F1") + " 秒、进过 " + roomsFirst + " 个房间;击杀事件推进过计数 " + book.KillsCounted + " 次");
        Report(allCleared, "自动机每一局都把副本打通了");
        Report(roomsFirst == rooms && Math.Abs(msFirst - recordedMs) < 1000, "第一局和第一遍打出的是同一局(房间数一致、用时相差不到一秒)");
        foreach (QuestDef def in accepted)
        {
            bool done = doneAt.TryGetValue(def.Id, out (int Run, double Ms) at);
            Report(done, KindName(def) + "任务在副本里达成(" + (done ? "第 " + at.Run + " 局的第 " + (at.Ms / 1000.0).ToString("F1") + " 秒" : "没有") + ")");
            if (done)
            {
                kinds |= def.Kind == QuestKind.HuntMonster ? KindHunt : def.Kind == QuestKind.ConditionUnderClear ? KindClear : KindMap;
            }
        }
        if (accepted.Any(d => d.Kind == QuestKind.HuntMonster))
        {
            Report(book.KillsCounted > 0, "击杀事件推进过任务计数(" + book.KillsCounted + " 次)");
        }

        // ---- 交任务:有交任务 NPC 的这里代替玩家去交;没有的应当已经自动完成(奖励已发)
        foreach (QuestDef def in accepted)
        {
            if (QuestRules.HandInNpc(def) >= 0)
            {
                QuestRow? row = book.RowOf(def.Id);
                Report(row != null && row.State == QuestRowState.Ready, KindName(def) + "任务在交任务 NPC 那里显示为可以交");
                QuestActionResult turned = book.TurnIn(def.Id, 0);
                Report(turned == QuestActionResult.Ok, "交" + KindName(def) + "任务:" + turned);
            }
            Report(!book.Log.IsActive(def.Id), KindName(def) + "任务交完之后不在进行中的列表里");
        }
        Report(book.Completed == accepted.Count, "完成了 " + book.Completed + " 个任务(接下 " + accepted.Count + " 个)");
        Report(progress.Exp != exp0 || progress.Gold != gold0 || progress.Level != level0 || progress.BagItems != bag0, "奖励发到了主角身上(经验、金币、等级或背包有变化)");

        // ---- 提示条与追踪取得到内容
        var noticeKinds = new SortedDictionary<QuestNoticeKind, int>();
        hud.Advance(FrameMs, book);
        foreach (QuestNotice n in hud.Toasts.Select(x => x.Notice))
        {
            noticeKinds[n.Kind] = noticeKinds.TryGetValue(n.Kind, out int c) ? c + 1 : 1;
        }
        Console.WriteLine("  提示条一共出过 " + hud.ToastsShown + " 条,眼下还留着 " + hud.Toasts.Count + " 条("
            + string.Join(" ", noticeKinds.Select(k => k.Key + "=" + k.Value)) + ");追踪 " + hud.Tracked.Count + " 行");
        Report(hud.ToastsShown >= accepted.Count * 2, "每个任务至少出过接下与完成两条提示");
        Report(noticeKinds.ContainsKey(QuestNoticeKind.Completed), "完成提示条里有完成任务的那一条");

        Console.WriteLine(bad == 0 ? "任务一整圈走通。" : "有 " + bad + " 项没通过。");
        return bad == 0 ? 0 : 1;
    }

    private static string KindName(QuestDef def)
    {
        switch (def.Kind)
        {
            case QuestKind.HuntMonster:
                return "猎杀";
            case QuestKind.ConditionUnderClear:
                return "通关";
            case QuestKind.ClearMap:
                return "过房间";
            default:
                return "其它";
        }
    }

    /// <summary>
    /// 让自动机按固定心跳把整座副本打完(首领清空、结算前的捡金币也走完);<paramref name="events"/> 是 <see cref="QuestBook"/> 时每帧再调一次它的 Update 与 <paramref name="tick"/>。
    /// <paramref name="run"/> 是第几局(从 0 起;战斗种子 = 命令行种子 + run,和流程里"再次挑战"同一个办法)。
    /// 返回(首领清空了没有, 模拟了多少毫秒, 进过几个房间)。
    /// </summary>
    private static (bool Cleared, double Ms, int Rooms) Play(DnfAssets assets, Options opts, string dungeonPath, int level, IQuestEvents events, HeroProgress? progress, int run,
        Action<double, QuestBook>? tick = null)
    {
        progress ??= HeroProgress.CreateForLocalHero(assets, level);
        // 这里只关心任务事件走不走得通,不是战斗平衡:--damage-scale 没给时把主角的伤害放大(测试旋钮,不是原版规则),让起始武器也能打通高等级副本;每一局同一个值
        int damageScale = opts.DamageScale > 1 ? opts.DamageScale : DefaultDamageScale;
        DungeonSession session = DungeonSession.Create(assets, dungeonPath, opts.Seed + run, opts.RandomLayout, progress: progress, heroDamageScale: damageScale,
            coins: new ReviveCoins(opts.Coins), quests: events);
        var bot = new DungeonBot(opts.Explore);
        var book = events as QuestBook;
        double t = 0;
        while (t < MaxSimulatedMs && (!session.Finished || bot.WantsToCollect(session)) && !session.Failed)
        {
            bot.Advance(session, FrameMs);
            t += FrameMs;
            if (book != null)
            {
                book.Update();
                tick?.Invoke(t, book);
            }
            if (session.HeroDead && session.Revive == null)
            {
                break;
            }
        }
        return (session.BossCleared, t, session.RoomsEntered);
    }

    /// <summary>任务达成了:自动完成的已经不在进行中的列表里(也算);有交任务 NPC 的显示为可以交。</summary>
    private static bool IsDone(QuestBook book, QuestDef def)
    {
        if (!book.Log.IsActive(def.Id))
        {
            return true;
        }
        QuestRow? row = book.RowOf(def.Id);
        return row != null && row.State == QuestRowState.Ready;
    }
}
