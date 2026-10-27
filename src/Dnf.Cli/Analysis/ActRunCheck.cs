using System.Text.Json;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf act-run-check [--json &lt;文件&gt;]`(证据 E-130;汇总数,本机脚本扫描,非 REA):从怪物出发看 .act 触发器运行期的覆盖度。
///   第一部分:每只怪物的出招 .act(.mob 的 [attack action] 或 [action list])——有多少怪物带触发器、触发器里多少是运行器认得的;
///   第二部分:这些 .act 能放出的被动对象(沿 [CREATE PASSIVEOBJECT] 的编号顺藤摸瓜,含对象再放对象)——有多少个、它们自己的 .act 的触发器认得多少;
///   第三部分:被动对象全表的总体情况。
///   最后一段是检查(每项一行 [通过]/[失败],有失败时退出码为 1,verify-all 的 actrun 项用它):引用读得到、没有读不通的 [CREATE PASSIVEOBJECT]、
///   运行器认得的触发器占比不低于下限、能放出的被动对象的 .obj 读得到、模板(基础动画)配得出。
///   只输出个数与标签名,不输出路径与脚本里的值;输出放 out/,不入库。
/// </summary>
internal static class ActRunCheck
{
    /// <summary>一类 .act(按文件去重)的汇总。</summary>
    private sealed class Tally
    {
        public int Programs;
        public int WithTriggers;
        public int Triggers;
        public int Supported;
        public int FrameTriggers;
        public int EventTriggers;
        public int ConditionOnly;
        public int Groups;
        public int GroupsWithUnsupported;
        public int Items;
        public int WithSpawn;
        public int SubAnis;
        public int CreateBroken;
        public int CreateNoIndex;
        public int CreateNoPos;
        public readonly SortedDictionary<string, int> UnsupportedTriggerTags = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> UnsupportedBehaviorTags = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> BehaviorKinds = new(StringComparer.Ordinal);

        public void Add(ActProgram program)
        {
            Programs++;
            if (program.Triggers.Count > 0)
            {
                WithTriggers++;
            }
            bool spawns = false;
            foreach (ActMotion motion in program.Motions)
            {
                SubAnis += motion.SubAnis.Count;
            }
            foreach (ActTrigger t in program.Triggers)
            {
                Triggers++;
                if (t.Supported)
                {
                    Supported++;
                }
                else
                {
                    var tags = new SortedSet<string>(t.UnsupportedTags, StringComparer.Ordinal);
                    foreach (ActRecord r in t.Records)
                    {
                        if (r.Kind == ActRecordKind.Unsupported && r.Tag != null)
                        {
                            tags.Add(r.Tag);
                        }
                    }
                    foreach (string tag in tags)
                    {
                        UnsupportedTriggerTags.TryGetValue(tag, out int c);
                        UnsupportedTriggerTags[tag] = c + 1;
                    }
                }
                bool isEvent = false;
                foreach (ActRecord r in t.Records)
                {
                    if (r.Kind == ActRecordKind.OnSetAction || r.Kind == ActRecordKind.OnAttackSuccess || r.Kind == ActRecordKind.OnDamage || r.Kind == ActRecordKind.OnOtherEvent)
                    {
                        isEvent = true;
                    }
                }
                if (isEvent)
                {
                    EventTriggers++;
                }
                else if (t.HasFrame)
                {
                    FrameTriggers++;
                }
                else
                {
                    ConditionOnly++;
                }
            }
            foreach (ActBehaviorGroup g in program.Behaviors)
            {
                Groups++;
                if (g.UnsupportedTags.Count > 0)
                {
                    GroupsWithUnsupported++;
                    foreach (string tag in new SortedSet<string>(g.UnsupportedTags, StringComparer.Ordinal))
                    {
                        UnsupportedBehaviorTags.TryGetValue(tag, out int c);
                        UnsupportedBehaviorTags[tag] = c + 1;
                    }
                }
                foreach (ActBehavior b in g.Items)
                {
                    Items++;
                    string kind = b.Kind.ToString();
                    BehaviorKinds.TryGetValue(kind, out int k);
                    BehaviorKinds[kind] = k + 1;
                    if (b.Kind == ActBehaviorKind.CreatePassive)
                    {
                        spawns = true;
                    }
                    else if (b.Kind == ActBehaviorKind.Unsupported && b.Tag == "[CREATE PASSIVEOBJECT]")
                    {
                        CreateBroken++;
                        CreateNoIndex += b.Index == null ? 1 : 0;
                        CreateNoPos += b.PosX == null || b.PosY == null || b.PosZ == null ? 1 : 0;
                    }
                }
            }
            if (spawns)
            {
                WithSpawn++;
            }
        }

        public void Print(string title)
        {
            Console.WriteLine(title + ":.act " + Programs + " 个(去重),含触发器 " + WithTriggers + " 个;触发器 " + Triggers + " 条,运行器认得的 " + Supported + " 条("
                + Percent(Supported, Triggers) + ")");
            Console.WriteLine("  触发器按唤醒方式:带事件记录 " + EventTriggers + ",带 [FRAME] 的 " + FrameTriggers + ",只有条件 " + ConditionOnly
                + ";行为组 " + Groups + " 个(含读不懂标签的 " + GroupsWithUnsupported + " 个),行为 " + Items + " 条;带 [CREATE PASSIVEOBJECT] 的 .act " + WithSpawn + " 个;SUB ANI " + SubAnis + " 个");
            Console.WriteLine("  行为种类:" + Join(BehaviorKinds, 30));
            Console.WriteLine("  读不通的 [CREATE PASSIVEOBJECT] " + CreateBroken + " 条(缺 [INDEX] " + CreateNoIndex + ",缺 [POS] " + CreateNoPos + ")");
            Console.WriteLine("  读不懂的触发器标签(种数 " + UnsupportedTriggerTags.Count + ",按触发器条数):" + Join(UnsupportedTriggerTags, 25));
            Console.WriteLine("  读不懂的行为标签(种数 " + UnsupportedBehaviorTags.Count + ",按行为组个数):" + Join(UnsupportedBehaviorTags, 25));
        }

        public object ToJson() => new
        {
            Programs, WithTriggers, Triggers, Supported, FrameTriggers, EventTriggers, ConditionOnly, Groups, GroupsWithUnsupported, Items, WithSpawn, SubAnis,
            CreateBroken, CreateNoIndex, CreateNoPos,
            UnsupportedTriggerTags, UnsupportedBehaviorTags, BehaviorKinds,
        };
    }

    private static string Percent(long a, long b) => b == 0 ? "-" : (a * 100.0 / b).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string Join(SortedDictionary<string, int> map, int limit)
    {
        if (map.Count == 0)
        {
            return "无";
        }
        var items = new List<KeyValuePair<string, int>>(map);
        items.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        var parts = new List<string>();
        for (int i = 0; i < items.Count && i < limit; i++)
        {
            parts.Add(items[i].Key + " " + items[i].Value);
        }
        return string.Join(",", parts) + (items.Count > limit ? ",…共 " + items.Count + " 种" : string.Empty);
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        var cache = new Dictionary<string, ActProgram?>(StringComparer.OrdinalIgnoreCase);

        ActProgram? Program(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (cache.TryGetValue(path, out ActProgram? cached))
            {
                return cached;
            }
            ActProgram? program = null;
            ScriptLine[]? lines = data.ReadScript(path);
            if (lines != null)
            {
                program = ActProgram.Compile(path, ActParser.Parse(lines));
            }
            cache[path] = program;
            return program;
        }

        // ---- 第一部分:怪物的出招 .act
        int monsters = 0, withAttackList = 0, withActFile = 0, withTriggers = 0, withRunnable = 0, allRunnable = 0, withSpawn = 0, missingActs = 0, actRefs = 0;
        var attackActs = new Dictionary<string, ActProgram>(StringComparer.OrdinalIgnoreCase);
        var spawnRoots = new SortedSet<int>();
        foreach (int id in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(id);
            if (def == null)
            {
                continue;
            }
            monsters++;
            string[] list = def.AttackList;
            if (list.Length == 0)
            {
                continue;
            }
            withAttackList++;
            bool anyFile = false, anyTriggers = false, anyRunnable = false, anyBroken = false, anySpawn = false;
            foreach (string raw in list)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                string path = PvfPath.ResolveRelativeTo(def.Path, raw);
                actRefs++;
                ActProgram? program = Program(path);
                if (program == null)
                {
                    missingActs++;
                    continue;
                }
                anyFile = true;
                attackActs[path] = program;
                foreach (ActTrigger t in program.Triggers)
                {
                    anyTriggers = true;
                    if (t.Supported)
                    {
                        anyRunnable = true;
                    }
                    else
                    {
                        anyBroken = true;
                    }
                }
                foreach (int index in SpawnIndices(program))
                {
                    anySpawn = true;
                    spawnRoots.Add(index);
                }
            }
            withActFile += anyFile ? 1 : 0;
            withTriggers += anyTriggers ? 1 : 0;
            withRunnable += anyRunnable ? 1 : 0;
            allRunnable += anyTriggers && !anyBroken ? 1 : 0;
            withSpawn += anySpawn ? 1 : 0;
        }
        Console.WriteLine("怪物 " + monsters + " 个(读得到 .mob 的);有招式列表的 " + withAttackList + " 个,招式的 .act 读得到的 " + withActFile + " 个(读不到的 .act 引用 " + missingActs + " 处)");
        Console.WriteLine("  其中带触发器的 " + withTriggers + " 个(" + Percent(withTriggers, withActFile) + "),至少有一条运行器认得的触发器 " + withRunnable + " 个,触发器全部认得的 " + allRunnable
            + " 个,招式里有 [CREATE PASSIVEOBJECT] 的 " + withSpawn + " 个");
        var monsterTally = new Tally();
        foreach (ActProgram p in attackActs.Values)
        {
            monsterTally.Add(p);
        }
        monsterTally.Print("怪物出招的 .act");

        // ---- 第二部分:顺着 [CREATE PASSIVEOBJECT] 能到的被动对象
        var reached = new SortedSet<int>();
        var queue = new Queue<int>(spawnRoots);
        foreach (int r in spawnRoots)
        {
            reached.Add(r);
        }
        int missingObjects = 0, objectsWithTriggers = 0, objectsRunnable = 0, objectsDepth2 = 0, objectsAllRunnable = 0;
        var objectActs = new Dictionary<string, ActProgram>(StringComparer.OrdinalIgnoreCase);
        var depth = new Dictionary<int, int>();
        foreach (int r in spawnRoots)
        {
            depth[r] = 1;
        }
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            Dnf.Game.PassiveObjectDef? def = data.GetPassiveObject(id);
            if (def == null)
            {
                missingObjects++;
                continue;
            }
            bool anyTriggers = false, anyRunnable = false, anyBroken = false;
            var paths = new List<string?> { def.BasicActionPath };
            paths.AddRange(def.EtcActionPaths);
            foreach (string? path in paths)
            {
                ActProgram? program = Program(path);
                if (program == null)
                {
                    continue;
                }
                objectActs[path!] = program;
                foreach (ActTrigger t in program.Triggers)
                {
                    anyTriggers = true;
                    anyRunnable |= t.Supported;
                    anyBroken |= !t.Supported;
                }
                foreach (int next in SpawnIndices(program))
                {
                    if (reached.Add(next))
                    {
                        depth[next] = depth[id] + 1;
                        queue.Enqueue(next);
                    }
                }
            }
            objectsWithTriggers += anyTriggers ? 1 : 0;
            objectsRunnable += anyRunnable ? 1 : 0;
            objectsAllRunnable += anyTriggers && !anyBroken ? 1 : 0;
            objectsDepth2 += depth[id] >= 2 ? 1 : 0;
        }
        Console.WriteLine();
        Console.WriteLine("被动对象:怪物出招能放出的 " + reached.Count + " 个(含对象再放对象;其中第二层及更深 " + objectsDepth2 + " 个),.obj 读不到的 " + missingObjects
            + " 个;带触发器的 " + objectsWithTriggers + " 个,至少一条触发器认得的 " + objectsRunnable + " 个,触发器全部认得的 " + objectsAllRunnable + " 个");
        var objectTally = new Tally();
        foreach (ActProgram p in objectActs.Values)
        {
            objectTally.Add(p);
        }
        objectTally.Print("这些被动对象自己的 .act");

        // ---- 第三部分:被动对象全表
        int[] allIds = data.PassiveObjectIds();
        int readable = 0, basicAct = 0, oldStyle = 0, etcActs = 0, anyTrig = 0, allRun = 0, timeLimit = 0, endOfAnim = 0, neither = 0;
        var allActs = new Dictionary<string, ActProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (int id in allIds)
        {
            Dnf.Game.PassiveObjectDef? def = data.GetPassiveObject(id);
            if (def == null)
            {
                continue;
            }
            readable++;
            basicAct += def.BasicActionPath != null ? 1 : 0;
            oldStyle += def.BasicActionPath == null && def.BasicMotionPath != null ? 1 : 0;
            etcActs += def.EtcActionPaths.Length > 0 ? 1 : 0;
            timeLimit += def.TimeLimitMs >= 0 ? 1 : 0;
            endOfAnim += def.DestroyOnAnimationEnd ? 1 : 0;
            neither += def.TimeLimitMs < 0 && !def.DestroyOnAnimationEnd ? 1 : 0;
            bool anyT = false, broken = false;
            var paths = new List<string?> { def.BasicActionPath };
            paths.AddRange(def.EtcActionPaths);
            foreach (string? path in paths)
            {
                ActProgram? program = Program(path);
                if (program == null)
                {
                    continue;
                }
                allActs[path!] = program;
                foreach (ActTrigger t in program.Triggers)
                {
                    anyT = true;
                    broken |= !t.Supported;
                }
            }
            anyTrig += anyT ? 1 : 0;
            allRun += anyT && !broken ? 1 : 0;
        }
        Console.WriteLine();
        Console.WriteLine("被动对象全表 " + allIds.Length + " 个(.obj 读得到 " + readable + "):写 [basic action] 的 " + basicAct + " 个,只有旧式 [basic motion] 的 " + oldStyle + " 个,有 [etc action] 的 "
            + etcActs + " 个;消失条件:有 [time limite] 的 " + timeLimit + " 个,有 [on end of animation] 的 " + endOfAnim + " 个,两样都没有的 " + neither + " 个");
        Console.WriteLine("  带触发器的 " + anyTrig + " 个,触发器全部认得的 " + allRun + " 个(" + Percent(allRun, anyTrig) + ")");
        var allTally = new Tally();
        foreach (ActProgram p in allActs.Values)
        {
            allTally.Add(p);
        }
        allTally.Print("被动对象全表的 .act");

        // ---- 检查
        var library = new ActLibrary(assets, data);
        int templates = 0;
        foreach (int id in reached)
        {
            if (library.Passive(id) != null)
            {
                templates++;
            }
        }
        int failed = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("检查:");
        Report(withActFile > 0 && attackActs.Count > 0, "怪物招式的 .act 读得到并编得出程序(" + attackActs.Count + " 个)");
        Report(missingActs * 50 <= actRefs, "怪物招式引用的 .act 读不到的不超过 2%(" + missingActs + "/" + actRefs + ")");
        Report(monsterTally.CreateBroken == 0 && objectTally.CreateBroken == 0, "每个 [CREATE PASSIVEOBJECT] 都带对象编号,编得出(读不通 " + (monsterTally.CreateBroken + objectTally.CreateBroken) + " 条)");
        Report(monsterTally.Triggers > 0 && monsterTally.Supported * 100 >= monsterTally.Triggers * 85,
            "怪物出招里运行器认得的触发器占比不低于 85%(" + Percent(monsterTally.Supported, monsterTally.Triggers) + ")");
        Report(objectTally.Triggers > 0 && objectTally.Supported * 100 >= objectTally.Triggers * 80,
            "被动对象自己的 .act 里运行器认得的触发器占比不低于 80%(" + Percent(objectTally.Supported, objectTally.Triggers) + ")");
        Report(reached.Count > 0 && missingObjects * 50 <= reached.Count, "怪物出招能放出的被动对象的 .obj 读不到的不超过 2%(" + missingObjects + "/" + reached.Count + ")");
        Report(reached.Count > 0 && templates * 100 >= reached.Count * 95, "能放出的被动对象里模板(基础动画读得到)配得出的不低于 95%(" + templates + "/" + reached.Count + ")");
        Report(withRunnable > 0 && withSpawn > 0, "有怪物带运行器认得的触发器,也有怪物的招式会放被动对象");

        if (opts.JsonFile != null)
        {
            var summary = new
            {
                monsters, withAttackList, withActFile, withTriggers, withRunnable, allRunnable, withSpawn, missingActs, actRefs,
                monsterActs = monsterTally.ToJson(),
                reachedObjects = reached.Count, templates, missingObjects, objectsWithTriggers, objectsRunnable, objectsAllRunnable, objectsDepth2,
                objectActs = objectTally.ToJson(),
                allObjects = allIds.Length, readable, basicAct, oldStyle, etcActs, anyTrig, allRun, timeLimit, endOfAnim, neither,
                allObjectActs = allTally.ToJson(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.JsonFile))!);
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("已写出 -> " + opts.JsonFile);
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>一个程序里所有 [CREATE PASSIVEOBJECT] 可能放出的被动对象编号(随机选的编号全算)。</summary>
    private static IEnumerable<int> SpawnIndices(ActProgram program)
    {
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (ActBehavior b in g.Items)
            {
                if (b.Kind != ActBehaviorKind.CreatePassive || b.Index == null)
                {
                    continue;
                }
                foreach (int index in b.Index.Candidates())
                {
                    yield return index;
                }
            }
        }
    }
}
