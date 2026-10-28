using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// dungeon-check:整座副本的检查(证据 E-070)。
/// 先读副本定义的迷宫、选出每一格的房间并打印布局(每格的出口码、用途、房间地图、四个出口的种类);
/// 不带 --layout-only 时再按固定的 1/60 秒步长模拟:--bot 让 <see cref="DungeonBot"/> 自动从起点打到首领清空,
/// 否则按 --play 的时间表替主角按键。过程中打印进出房间(位置、朝向、镜头)、清房时刻与最后的统计,--shots-at 在指定时刻输出 800×600 画面。
/// 战斗里主角打怪物的伤害:带生命值(不加 --no-damage)时按原版公式算(证据 E-110),--no-damage 时是占位伤害(<see cref="PlaceholderDamage"/>)。
/// 带生命值时主角有等级进度(证据 E-111):--level 指定起始等级(默认 1),杀怪给经验、升级,--no-exp 关掉;结束时多打印一行经验小结。
/// </summary>
internal static class DungeonCheck
{
    /// <summary>不给 --seconds 时最长模拟多少秒(够逛遍并打完一座副本)。</summary>
    private const double DefaultSeconds = 900;

    /// <summary>--watch 从给定的那一秒起往后打印多少秒。</summary>
    private const double WatchSeconds = 6;

    public static int Run(DnfLocation loc, Options opts)
    {
        var shotTimes = new List<double>();
        if (opts.ShotsAt != null)
        {
            foreach (string item in opts.ShotsAt.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                shotTimes.Add(double.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            shotTimes.Sort();
            if (opts.PngFile == null)
            {
                throw new UserError("--shots-at 需要 --png <文件名前缀>(每个时刻写成 前缀-秒.png)");
            }
        }

        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Random? random = opts.RandomLayout ? new Random(opts.Seed) : null;
        DungeonMaze maze = DungeonMaze.Read(assets, opts.Dungeon, random) ?? throw new UserError("副本定义里没有可用的迷宫: " + opts.Dungeon);
        DungeonRoomPool pool = DungeonRoomPool.ReadFor(assets, opts.Dungeon);
        DungeonLayout layout = DungeonLayout.Create(maze, pool, random);
        PrintLayout(opts.Dungeon, layout, pool);
        if (opts.LayoutOnly)
        {
            return 0;
        }

        DungeonSession session = DungeonSession.Create(assets, opts.Dungeon, opts.Seed, opts.RandomLayout, opts.HeroWeapon, heroVitals: !opts.NoDamage,
            progress: ExpCheck.ProgressFor(assets, opts), heroDamageScale: opts.DamageScale, coins: new ReviveCoins(opts.Coins), monsterDamageScale: opts.MonsterDamageScale,
            difficulty: opts.Difficulty);
        var bot = new DungeonBot(opts.Explore) { SmashObjects = opts.Smash };
        CombatScript script = ParsePlay(opts.Play);
        if (!opts.Bot && script.IsEmpty)
        {
            throw new UserError("dungeon-check 需要 --bot 或 --play(或者只看布局:--layout-only)");
        }

        double seconds = opts.SecondsGiven ? opts.Seconds : DefaultSeconds;
        Console.WriteLine();
        Console.WriteLine("模拟:" + (opts.Bot ? "自动机" : "时间表") + ",最长 " + F(seconds) + " 秒,战斗种子 " + opts.Seed);
        LogEnter(0, session, null);

        double totalMs = seconds * 1000.0;
        double baseFrameMs = opts.FrameMs > 0 ? opts.FrameMs : CombatScript.StepMs;
        var jitterRandom = opts.Jitter > 0 ? new Random(opts.Seed * 7919 + 13) : null;
        double frameMs = baseFrameMs;
        double t = 0;
        int shotIndex = 0;
        int lastCell = session.CellY * maze.Cols + session.CellX;
        int lastCleared = session.RoomsCleared;
        string lastClearText = string.Empty;
        double afterClearMs = opts.AfterClearSeconds * 1000.0;
        double finishedAt = -1;
        bool settleLogged = false;
        int lastDeaths = 0;
        int lastRevives = 0;
        while (t < totalMs && (!session.Finished || (finishedAt >= 0 && t - finishedAt < afterClearMs) || (opts.Bot && bot.WantsToCollect(session))))
        {
            int fromX = session.CellX, fromY = session.CellY;
            double fromHeroY = session.Hero.Y;
            if (jitterRandom != null)
            {
                frameMs = baseFrameMs * (1.0 + opts.Jitter * (jitterRandom.NextDouble() * 2.0 - 1.0));
            }
            if (session.Finished && !(opts.Bot && bot.WantsToCollect(session)))
            {
                // 清空之后(--after-clear):主角站着不动,只让时间走,结算画面随之出现
                session.Update(frameMs, 0, 0);
            }
            else if (opts.Bot)
            {
                // 与播放器同一个办法:自动机按固定的 1/60 秒心跳补步,所以 --frame-ms、--jitter 不会改变它打出的这一局
                bot.Advance(session, frameMs);
            }
            else
            {
                bool attack = script.Poll(t, out int dx, out int dy, out bool jump, out SkillKeys skills);
                session.Update(frameMs, dx, dy, attack, jump, skills);
            }
            t += frameMs;
            if (session.Finished && finishedAt < 0)
            {
                finishedAt = t;
            }
            if (session.SettlementActive && !settleLogged)
            {
                settleLogged = true;
                ClearCamp? camp = session.Camp;
                Console.WriteLine("  t=" + Sec(t) + "s  结算开始(首领清空后 " + Sec(session.ClockMs - session.BossClearedAtMs) + " 秒):营火 " + (camp?.FireLayers ?? 0)
                    + " 层动画、站着的 NPC " + (camp?.NpcCount ?? 0) + " 个");
            }

            HeroRevive? revive = session.Revive;
            if (revive != null)
            {
                // 死亡与复活(证据 E-115):躺平的那一刻窗口打开算一次死亡;自动机会按键用币复活
                if (revive.Deaths != lastDeaths)
                {
                    lastDeaths = revive.Deaths;
                    Console.WriteLine("  t=" + Sec(t) + "s  主角倒下(第 " + revive.Deaths + " 次;格 (" + session.CellX + "," + session.CellY + "),被打中 " + session.HeroHits
                        + " 次);复活窗口 " + F(HeroRevive.CountdownMs / 1000.0) + " 秒,库存 " + (revive.Coins?.Count ?? 0) + " 个币");
                }
                if (revive.Revives != lastRevives)
                {
                    lastRevives = revive.Revives;
                    Console.WriteLine("  t=" + Sec(t) + "s  主角复活(第 " + revive.Revives + " 次;已用币 " + revive.Used + ",库存 " + (revive.Coins?.Count ?? 0) + ")");
                }
                if (session.Failed)
                {
                    Console.WriteLine("  t=" + Sec(t) + "s  副本失败(复活窗口走完、主角没有复活;格 (" + session.CellX + "," + session.CellY + "),死亡 " + revive.Deaths + " 次,复活 " + revive.Revives + " 次)");
                    break;
                }
            }
            else if (session.HeroDead)
            {
                // 没有复活对象(旧行为):主角死了就躺着,继续模拟没有意义
                Console.WriteLine("  t=" + Sec(t) + "s  主角死亡(格 (" + session.CellX + "," + session.CellY + "),被打中 " + session.HeroHits + " 次)");
                break;
            }
            if (session.RoomsCleared != lastCleared)
            {
                lastCleared = session.RoomsCleared;
                string text = "  t=" + Sec(t) + "s  清房 格 (" + session.CellX + "," + session.CellY + ")" + (session.BossCleared ? "  首领房清空" : string.Empty);
                if (text != lastClearText)
                {
                    Console.WriteLine(text);
                    lastClearText = text;
                }
            }
            int cell = session.CellY * maze.Cols + session.CellX;
            if (cell != lastCell)
            {
                LogEnter(t, session, (fromX, fromY, fromHeroY));
                lastCell = cell;
            }
            if (opts.Watch.HasValue && t >= opts.Watch.Value * 1000.0 && t <= (opts.Watch.Value + WatchSeconds) * 1000.0 && (long)(t / 100.0) != (long)((t - frameMs) / 100.0))
            {
                Console.WriteLine("  [细跟踪 " + Sec(t) + "s] 格 (" + session.CellX + "," + session.CellY + ") 主角 (" + F(session.Hero.X) + "," + F(session.Hero.Y) + ") " + session.Hero.Motion
                    + (opts.Bot ? " 意图 " + bot.Intent : string.Empty) + (session.InTransition ? " 转场" : string.Empty));
            }
            if (opts.Trace && (long)(t / 1000.0) != (long)((t - frameMs) / 1000.0))
            {
                MonsterActor? nearest = null;
                foreach (MonsterActor m in session.Current.Monsters)
                {
                    if (m.IsAlive && (nearest == null || Math.Abs(m.X - session.Hero.X) + 4 * Math.Abs(m.Y - session.Hero.Y) < Math.Abs(nearest.X - session.Hero.X) + 4 * Math.Abs(nearest.Y - session.Hero.Y)))
                    {
                        nearest = m;
                    }
                }
                Console.WriteLine("  [跟踪 " + Sec(t) + "s] 格 (" + session.CellX + "," + session.CellY + ") 主角 (" + F(session.Hero.X) + "," + F(session.Hero.Y)
                    + ") 怪物剩 " + session.Current.AliveCount + "/" + session.Current.Monsters.Count + " 命中 " + session.Hits
                    + (nearest == null ? string.Empty : "  最近 #" + nearest.Index + " (" + F(nearest.X) + "," + F(nearest.Y) + ") " + nearest.Phase));
            }
            while (shotIndex < shotTimes.Count && shotTimes[shotIndex] * 1000.0 <= t)
            {
                WriteShot(session, shotTimes[shotIndex], opts.PngFile!);
                shotIndex++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("结束:t=" + Sec(t) + "s  " + (session.BossCleared ? "首领清空" : session.ExitReached ? "走出迷宫" : session.Failed ? "副本失败" : "未走完")
            + "  进过 " + session.RoomsEntered + " 个房间,清空 " + session.RoomsCleared + " 个,换房间 " + session.Transitions + " 次;命中 " + session.Hits
            + " 次,击杀 " + session.Kills + " 个,总伤害 " + session.TotalDamage
            + (session.Vitals != null ? ";主角被打中 " + session.HeroHits + " 次,生命 " + session.Vitals.Hp + "/" + session.Vitals.HpMax + ",魔法 " + session.Vitals.Mp + "/" + session.Vitals.MpMax : ";怪物不还手(--no-damage)"));
        Console.WriteLine("脚本(E-130):进过的房间里出招带 .act 触发器的怪物 " + session.ActMonsters + " 只,脚本放出被动对象 " + session.PassivesSpawned + " 个,打中主角 "
            + session.PassiveHits + " 次");
        Console.WriteLine("召唤(E-134):脚本召唤出怪物 " + session.SummonsSpawned + " 只,被拒绝 " + session.SummonsRefused + " 次,召唤物被打死 " + session.SummonedKills + " 只");
        Console.WriteLine("异常状态(E-136):" + (session.Vitals != null
            ? "挂到主角身上 " + session.StatusApplied + " 次,概率没中 " + session.StatusMissed + " 次,持续伤害共 " + session.StatusTickDamage + " 点"
            : "怪物不还手,没有状态"));
        if (session.Revive != null && session.Revive.Deaths > 0)
        {
            Console.WriteLine("死亡与复活(E-115):死亡 " + session.Revive.Deaths + " 次,复活 " + session.Revive.Revives + " 次,用币 " + session.Revive.Used + " 个,库存剩 "
                + (session.Revive.Coins?.Count ?? 0) + " 个" + (session.Failed ? ",副本失败" : string.Empty));
        }
        if (session.Progress != null)
        {
            Console.WriteLine(ExpCheck.Summary(session.Progress, opts.HeroLevel));
        }
        MonsterDrops drops = MonsterDrops.Load(assets);
        Console.WriteLine("金币(E-116):" + (drops.Available
            ? "掉出 " + session.GoldDropped + " 堆共 " + session.GoldDroppedAmount + ",捡起 " + session.GoldPicked + " 堆共 " + session.GoldEarned
                + (session.Progress != null ? ",身上 " + session.Progress.Gold : string.Empty)
            : "没有掉落数据(" + drops.Error + ")"));
        if (drops.Available)
        {
            Console.WriteLine("物品(E-117):" + (drops.Pool != null
                ? "池里 " + drops.Pool.Items + " 件;掉出 " + session.ItemsDropped + " 件,捡起 " + session.ItemsPicked + " 件"
                    + (session.Progress != null ? ",背包 " + session.Progress.Bag.TotalCount + " 件占 " + session.Progress.Bag.StackCount + " 格" : string.Empty)
                    + (session.ItemsBagFull > 0 ? ",背包满了留在地上 " + session.ItemsBagFull + " 件" : string.Empty)
                : "没有物品池(读不到装备或物品列表)"));
        }
        if (session.ObjectsBreakable > 0 || opts.Smash)
        {
            Console.WriteLine("物件(E-126):进过的房间里有 " + session.ObjectsBreakable + " 个可破坏物件,挥中 " + session.ObjectsHit + " 次,打翻 " + session.ObjectsDestroyed
                + " 个,放出 [item] 动作 " + session.ObjectItemActions + " 次,掉出 " + session.ObjectItemsDropped + " 件" + (opts.Smash ? string.Empty : "(没带 --smash,自动机不会去打物件)"));
            if (opts.Smash)
            {
                // 诊断:每个物件的位置、受击与状态(只打印在终端,原版数值不入库)
                for (int cy = 0; cy < maze.Rows; cy++)
                {
                    for (int cx = 0; cx < maze.Cols; cx++)
                    {
                        RoomCombatSession room = session.SessionAt(cx, cy);
                        for (int bi = 0; room != null && bi < room.Breakables.Count; bi++)
                        {
                            BreakableObject b = room.Breakables[bi];
                            Console.WriteLine("  格 (" + cx + "," + cy + ") 物件 #" + bi + " 位置 (" + b.Object.X + "," + b.Object.Y + ") 受击框 x " + b.HurtBoxes[0].MinX + ".." + b.HurtBoxes[0].MaxX
                                + " 要打 " + b.Data.HitsNeeded + " 下,挨了 " + b.HitsTaken + " 下," + (b.Destroyed ? "已打翻" : b.Triggered ? "已触发" : "没打完")
                                + (b.Object.Special != null && b.Object.Special.Has(SpecialActionKind.Item) ? ",带 [item] 动作" : string.Empty));
                        }
                    }
                }
            }
        }
        if (drops.Available)
        {
            // 诊断:这座副本的标准等级选到哪一行概率行,普通怪与首领的金币概率各是万分之几(只打印在终端)
            int standard = GoldDrop.StandardLevel(assets.ReadScript(opts.Dungeon));
            int row = drops.Table.FindRow(standard);
            Console.WriteLine("  标准等级 " + standard + ",概率行 " + row + (row >= 0
                ? ",普通怪 " + drops.Table.GoldProbability(row, 0, 0, 1) + "/10000,首领 " + drops.Table.GoldProbability(row, 3, 0, 1) + "/10000"
                : "(没有行,不掉)"));
        }
        for (int y = 0; y < maze.Rows; y++)
        {
            var sb = new StringBuilder("  ");
            for (int x = 0; x < maze.Cols; x++)
            {
                sb.Append(session.IsCleared(x, y) ? "[清]" : session.IsVisited(x, y) ? "[进]" : "[ ]");
            }
            Console.WriteLine(sb.ToString());
        }
        while (shotIndex < shotTimes.Count)
        {
            WriteShot(session, shotTimes[shotIndex], opts.PngFile!);
            shotIndex++;
        }
        if (opts.Bot && bot.PathEngaged > 0)
        {
            Console.WriteLine("自动机寻路接管 " + bot.PathEngaged + " 次,其中找不到路的步数 " + bot.PathBlockedSteps);
        }
        if (opts.Bot && !session.Finished)
        {
            PrintStuckRoom(session, bot);
        }
        return opts.Bot && !session.Finished ? 2 : 0;
    }

    /// <summary>在竖线 x 上每 1 像素问一次可走区域,把连续可走的 y 压成区间文字(诊断用)。</summary>
    private static string WalkRuns(IWalkArea? walk, double x)
    {
        if (walk == null || walk.IsUnrestricted)
        {
            return "不限制";
        }
        var sb = new StringBuilder();
        int start = -1;
        for (int y = 0; y <= 700; y++)
        {
            bool ok = y < 700 && walk.Contains(x, y);
            if (ok && start < 0)
            {
                start = y;
            }
            else if (!ok && start >= 0)
            {
                sb.Append(sb.Length > 0 ? " " : string.Empty).Append('[').Append(start).Append("..").Append(y - 1).Append(']');
                start = -1;
            }
        }
        return sb.Length > 0 ? sb.ToString() : "没有";
    }

    /// <summary>主角往 (dx, dy) 方向挪一像素后:可走区域说能不能站、障碍说挡不挡(诊断用)。</summary>
    private static string Probe(TownHero hero, int dx, int dy, string name)
    {
        double x = hero.X + dx, y = hero.Y + dy;
        bool walk = hero.Walk == null || hero.Walk.Contains(x, y);
        object? by = hero.Blocker?.BlockedBy(x, y);
        return name + (walk ? "可走" : "不可走") + (by == null ? string.Empty : "+被" + by.GetType().Name + "挡");
    }

    /// <summary>没打完时的现场:所在房间、主角位置、剩下的活怪物(位置、生命、阶段、当前动画),帮着看是够不着、打不动还是别的原因。</summary>
    private static void PrintStuckRoom(DungeonSession session, DungeonBot? bot = null)
    {
        TownHero hero = session.Hero;
        var room = session.Current;
        Console.WriteLine("未走完的现场:格 (" + session.CellX + "," + session.CellY + ") " + session.MapPath + ",主角 (" + F(hero.X) + "," + F(hero.Y) + "),活怪物 " + room.AliveCount + "/" + room.Monsters.Count);
        Console.WriteLine("  主角所在竖线 x=" + F(hero.X) + " 上可走的 y 区间:" + WalkRuns(hero.Walk, hero.X));
        if (bot != null)
        {
            Console.WriteLine("  自动机:" + bot.Intent + (bot.PathReport.Length > 0 ? ";寻路 " + bot.PathReport : string.Empty));
        }
        Console.WriteLine("  主角动作 " + hero.Motion + ",朝" + (hero.FacingLeft ? "左" : "右") + (session.InTransition ? ";正在转场" : string.Empty));
        Console.WriteLine("  主角身边一像素:" + Probe(hero, -1, 0, "左") + " " + Probe(hero, 1, 0, "右") + " " + Probe(hero, 0, -1, "上") + " " + Probe(hero, 0, 1, "下"));
        PrintWalkMap(room, hero);
        foreach (RoomGate gate in room.Room.Content.Gates)
        {
            Console.WriteLine("  出口 " + gate.Side + " 位置 (" + gate.X + "," + gate.Y + ") 状态 " + gate.State + (gate.Body == null ? " 无门体" : " 有门体")
                + " 识别矩形 x " + gate.Area.X + ".." + (gate.Area.X + gate.Area.Width) + " y " + gate.Area.Y + ".." + (gate.Area.Y + gate.Area.Height));
        }
        int shown = 0;
        foreach (MonsterActor m in room.Monsters)
        {
            if (!m.IsAlive)
            {
                continue;
            }
            if (shown++ >= 12)
            {
                Console.WriteLine("  …");
                break;
            }
            Console.WriteLine("  怪物 #" + m.Index + " 编号 " + m.Code + (m.IsBoss ? "(首领)" : string.Empty) + " 位置 (" + F(m.X) + "," + F(m.Y) + "," + F(m.Z) + ") 生命 " + m.Hp + "/" + m.HpMax
                + " 阶段 " + m.Phase + " 动画 " + m.CurrentAnimationPath + " 受击框 " + m.DamageBoxes().Count + "(动画 " + FramesWithDamageBox(m.CurrentAni) + "/" + (m.CurrentAni?.Frames.Length ?? 0) + " 帧有" + (m.CanEverBeHit ? string.Empty : ",整个模型都没有") + ")"
                + (m.Script != null ? " 脚本驱动" + (m.IsAware ? "(已发现主角)" : "(未发现主角)") + " 招式冷却 " + CoolingList(m.Script) : string.Empty));
        }
    }

    private static int FramesWithDamageBox(AniFile? ani)
    {
        int n = 0;
        if (ani == null)
        {
            return 0;
        }
        foreach (AniFrame f in ani.Frames)
        {
            foreach (AniBox b in f.Boxes)
            {
                if (b.Type == AniBoxType.Damage)
                {
                    n++;
                    break;
                }
            }
        }
        return n;
    }

    /// <summary>脚本怪物各招还剩的冷却毫秒数(取整,逗号分隔),诊断用。</summary>
    private static string CoolingList(MonsterScript script)
    {
        var parts = new List<string>();
        for (int i = 0; i < script.AttackCount; i++)
        {
            parts.Add(((int)script.CoolingMs(i)).ToString());
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// 房间的粗略平面图(每格 <c>StepX</c> × <c>StepY</c> 像素):. 可走,# 走不了(可走区域之外),b 被可破坏物件挡,o 被别的障碍物挡,G 被出口的门体挡,H 主角,M 活着的怪物。
    /// 没打完时帮着看主角被什么围住了。
    /// </summary>
    private static void PrintWalkMap(RoomCombatSession room, TownHero hero)
    {
        const int stepX = 24, stepY = 16;
        RoomMap map = room.Room.Map;
        int cols = (map.Width + stepX - 1) / stepX, rows = (map.HeightPixels + stepY - 1) / stepY;
        Console.WriteLine("  平面图(每格 " + stepX + "×" + stepY + " 像素;. 可走 # 走不了 b 可破坏物件 o 障碍物 G 门体 H 主角 M 怪物):");
        for (int r = 0; r < rows; r++)
        {
            var line = new System.Text.StringBuilder();
            double y = r * stepY + stepY * 0.5;
            bool any = false;
            for (int c = 0; c < cols; c++)
            {
                double x = c * stepX + stepX * 0.5;
                char ch;
                if (hero.Walk != null && !hero.Walk.Contains(x, y))
                {
                    ch = '#';
                }
                else
                {
                    object? by = hero.Blocker?.BlockedBy(x, y);
                    ch = by == null ? '.' : by is RoomObject ro ? (ro.Def.Breakable != null ? 'b' : 'o') : by is RoomGate ? 'G' : 'o';
                }
                if (Math.Abs(hero.X - x) < stepX * 0.5 && Math.Abs(hero.Y - y) < stepY * 0.5)
                {
                    ch = 'H';
                }
                foreach (MonsterActor m in room.Monsters)
                {
                    if (m.IsAlive && Math.Abs(m.X - x) < stepX * 0.5 && Math.Abs(m.Y - y) < stepY * 0.5)
                    {
                        ch = 'M';
                    }
                }
                any |= ch != '#';
                line.Append(ch);
            }
            if (any)
            {
                Console.WriteLine("  " + r.ToString("D2") + " " + line);
            }
        }
    }

    private static void PrintLayout(string dungeon, DungeonLayout layout, DungeonRoomPool pool)
    {
        DungeonMaze maze = layout.Maze;
        Console.WriteLine("副本 " + dungeon + ":迷宫 " + maze.Cols + " 列 × " + maze.Rows + " 行,房间候选 " + pool.Rooms.Count + " 张,"
            + (maze.IsConsistent() ? "相邻格的出口两侧一致" : "相邻格的出口两侧不一致!") + ",出口物件编号 " + (maze.GateSet == null ? "无" : string.Join("/", maze.GateSet.Ids)));
        Console.WriteLine("  起点格 (" + layout.StartX + "," + layout.StartY + ")" + (layout.HasBoss ? ",首领格 (" + layout.BossX + "," + layout.BossY + ")" : ",没有首领格")
            + ",小地图图包 " + (maze.MinimapImage ?? DungeonMinimapArt.DefaultTileImage));
        for (int y = 0; y < maze.Rows; y++)
        {
            for (int x = 0; x < maze.Cols; x++)
            {
                RoomCandidate? room = layout.RoomAt(x, y);
                if (room == null)
                {
                    Console.WriteLine("  格 (" + x + "," + y + ") 码 " + (char)('A' + maze.ExitsAt(x, y)) + " 空格");
                    continue;
                }
                string role = x == layout.StartX && y == layout.StartY ? (layout.IsBossCell(x, y) ? "起点兼首领" : "起点") : layout.IsBossCell(x, y) ? "首领" : "普通";
                Console.WriteLine("  格 (" + x + "," + y + ") 码 " + (char)('A' + maze.ExitsAt(x, y)) + " " + role + "  " + room.MapPath + "  左 " + Kind(layout.GateKindAt(x, y, GateSide.Left))
                    + " 右 " + Kind(layout.GateKindAt(x, y, GateSide.Right)) + " 上 " + Kind(layout.GateKindAt(x, y, GateSide.Up)) + " 下 " + Kind(layout.GateKindAt(x, y, GateSide.Down)));
            }
        }
    }

    private static string Kind(GateKind kind) => kind switch
    {
        GateKind.Normal => "普通",
        GateKind.Boss => "首领",
        GateKind.Wall => "墙",
        _ => "无",
    };

    private static void LogEnter(double t, DungeonSession session, (int X, int Y, double HeroY)? from)
    {
        TownHero h = session.Hero;
        string via = from.HasValue ? "  自 (" + from.Value.X + "," + from.Value.Y + ") 走出口,离开时 y=" + F(from.Value.HeroY) : "  出生";
        Console.WriteLine("  t=" + Sec(t) + "s  进入格 (" + session.CellX + "," + session.CellY + ") " + session.MapPath + via + "  主角 (" + F(h.X) + "," + F(h.Y) + ") 朝"
            + (h.FacingLeft ? "左" : "右") + " 镜头 " + session.Camera.IntX + "  怪物 " + session.Current.AliveCount + "/" + session.Current.Monsters.Count
            + "  站姿 " + (h.BattleStance ? "战斗" : "普通"));
    }

    private static void WriteShot(DungeonSession session, double seconds, string prefix)
    {
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, session.Build((long)(seconds * 1000.0)));
        string path = prefix + "-" + seconds.ToString("0.00", CultureInfo.InvariantCulture) + ".png";
        PngWriter.Write(path, 800, 600, canvas.Pixels);
        Console.WriteLine("  已写 " + path);
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static string Sec(double ms) => (ms / 1000.0).ToString("F2", CultureInfo.InvariantCulture);

    private static CombatScript ParsePlay(string? text)
    {
        try
        {
            return CombatScript.Parse(text ?? string.Empty);
        }
        catch (FormatException e)
        {
            throw new UserError("--play:" + e.Message);
        }
    }
}
