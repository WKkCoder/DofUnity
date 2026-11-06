using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

internal static partial class ActRunCheck
{
    /// <summary>实跑世界收到的 [APPEND HIT] 请求只数个数(世界里没有主角的受击结算,真正扣血在房间战斗会话里)。</summary>
    private sealed partial class LiveWorld : IActMessageHost
    {
        public int AppendHits;

        /// <summary>[WHICH] [PASSIVE] 解析成世界里还活着的被动对象(房间里的写法;别的实跑不开,保持它们原有的数)。</summary>
        public bool ResolvePassives;

        public void AppendHit(IActEntity owner, IActEntity target, float x, float y, float z)
        {
            AppendHits++;
        }
    }

    /// <summary>替身也收"执行第 n 组"的消息,只数条数(替身没有动作,不会执行)。</summary>
    private sealed partial class Stand : IActGroupReceiver
    {
        public int GroupsPosted;

        public bool PostActGroup(int group)
        {
            GroupsPosted++;
            return true;
        }
    }

    /// <summary>
    /// 消息族行为([SEND DO BEHAVIOR]、[SEND MESSAGE]、[CHANGE AI]、[CAMOUFLAGE]、[APPEND HIT])的招式实跑统计(汇总数,本机,非 REA):空场地、目标替身站在怪物右边 400 像素,
    /// 把每只怪物每一条"自己带这类行为、或者放出的被动对象(顺着 [CREATE PASSIVEOBJECT] 往下数)带这类行为"的招式单独放一遍,最多 <see cref="MaxSteps"/> 步(每步 10 毫秒),
    /// 招式放完以后只把被动对象再往前推,直到它们都消失。数收件(怪物与被动对象各自的"执行第 n 组"信箱收下与丢弃的条数)、伪装起了几次、AI 消息到了几条、追加命中请求发了几次;
    /// 只数个数,不输出路径与怪物编号。
    /// </summary>
    private sealed class LiveMessageTally
    {
        public const int MaxSteps = 3000;

        public int Monsters;
        public int NoModel;
        public int Attacks;
        public int NotEntered;
        public int Failed;
        public int StillRunning;
        public long GroupsPostedToMonsters;
        public long GroupsDroppedByMonsters;
        public long GroupsPostedToObjects;
        public long GroupsDroppedByObjects;
        public long GroupsPostedToHeroes;
        public int AttacksWithPostedGroups;
        public long Camouflages;
        public int AttacksWithCamouflage;
        public long AiMessages;
        public long AppendHits;
        public int AttacksWithAppendHit;
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  消息族招式实跑(空场地、目标替身在右边 400 像素、每招最多 " + MaxSteps / 100 + " 秒;怪物自己或放出的被动对象带 [SEND DO BEHAVIOR]、[SEND MESSAGE]、[CHANGE AI]、[CAMOUFLAGE]、[APPEND HIT]):怪物 "
                + Monsters + " 只(模型读不出 " + NoModel + " 只),共 " + Attacks + " 招(没切进去 " + NotEntered + " 招,抛异常 " + Failed + " 招,到时间还在跑 " + StillRunning + " 招)");
            Console.WriteLine("    [SEND DO BEHAVIOR] 的\"执行第 n 组\"消息:怪物收下 " + GroupsPostedToMonsters + " 条、丢弃 " + GroupsDroppedByMonsters + " 条;被动对象收下 " + GroupsPostedToObjects + " 条、丢弃 "
                + GroupsDroppedByObjects + " 条;发给替身主角 " + GroupsPostedToHeroes + " 条;有消息被收下的招式 " + AttacksWithPostedGroups + " 招");
            Console.WriteLine("    [CAMOUFLAGE] 开启 " + Camouflages + " 次(" + AttacksWithCamouflage + " 招);[SEND MESSAGE] 到达 AI 从属的消息 " + AiMessages + " 条;[APPEND HIT] 请求 " + AppendHits + " 次("
                + AttacksWithAppendHit + " 招)" + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
        }

        public object ToJson() => new
        {
            Monsters, NoModel, Attacks, NotEntered, Failed, StillRunning,
            GroupsPostedToMonsters, GroupsDroppedByMonsters, GroupsPostedToObjects, GroupsDroppedByObjects, GroupsPostedToHeroes, AttacksWithPostedGroups,
            Camouflages, AttacksWithCamouflage, AiMessages, AppendHits, AttacksWithAppendHit, Errors,
        };
    }

    /// <summary>
    /// 消息族行为所在的行为组是被哪种触发器记录引用的(静态数,按 行为种类 → 引用方式 → 行为组个数;同一组同一种引用方式只算一次):
    /// 引用方式是 [DO BEHAVIOR] 的目标选择器名,[DIRECT DO BEHAVIOR] 与 [DELAY DO BEHAVIOR] 另记,没有任何触发器记录引用的记"无"。
    /// 用来看消息发出去时目标是自己、拥有者、目标还是别的对象,不输出路径与脚本里的值。
    /// </summary>
    private sealed class MessageSelectorCensus
    {
        private readonly SortedDictionary<string, SortedDictionary<string, int>> _by = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _families = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Note(ActProgram program)
        {
            for (int k = 0; k < program.Behaviors.Count; k++)
            {
                var kinds = new SortedSet<string>(StringComparer.Ordinal);
                foreach (ActBehavior b in program.Behaviors[k].Items)
                {
                    if (IsMessageKind(b.Kind))
                    {
                        kinds.Add(b.Kind.ToString());
                    }
                    if (b.Kind == ActBehaviorKind.SendMessage && b.Msg != null)
                    {
                        string family = b.Msg.Family.ToString();
                        _families[family] = _families.TryGetValue(family, out int count) ? count + 1 : 1;
                    }
                }
                if (kinds.Count == 0)
                {
                    continue;
                }
                var ways = new SortedSet<string>(StringComparer.Ordinal);
                foreach (ActTrigger t in program.Triggers)
                {
                    foreach (ActRecord r in t.Records)
                    {
                        if ((r.Kind != ActRecordKind.DoBehavior && r.Kind != ActRecordKind.DirectDoBehavior && r.Kind != ActRecordKind.DelayDoBehavior) || r.Behavior == null)
                        {
                            continue;
                        }
                        foreach (int candidate in r.Behavior.Candidates())
                        {
                            if (candidate == k)
                            {
                                ways.Add(r.Kind == ActRecordKind.DoBehavior ? r.Selector.ToString() : r.Kind.ToString());
                                break;
                            }
                        }
                    }
                }
                if (ways.Count == 0)
                {
                    ways.Add("无");
                }
                foreach (string kind in kinds)
                {
                    if (!_by.TryGetValue(kind, out SortedDictionary<string, int>? map))
                    {
                        map = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        _by[kind] = map;
                    }
                    foreach (string way in ways)
                    {
                        map[way] = map.TryGetValue(way, out int n) ? n + 1 : 1;
                    }
                }
            }
        }

        public void Print()
        {
            Console.WriteLine("  消息族行为所在的行为组被触发器引用的方式(按行为种类,引用方式 组数):");
            foreach (KeyValuePair<string, SortedDictionary<string, int>> kv in _by)
            {
                Console.WriteLine("    " + kv.Key + ":" + Join(kv.Value, 12));
            }
            Console.WriteLine("  [SEND MESSAGE] 的子关键字(按行为条数;None 是子关键字缺失或不认识):" + Join(_families, 8));
        }

        public object ToJson() => new { References = _by, SendMessageFamilies = _families };
    }

    private static bool IsMessageKind(ActBehaviorKind kind) => kind == ActBehaviorKind.SendDoBehavior || kind == ActBehaviorKind.SendMessage || kind == ActBehaviorKind.ChangeAi
        || kind == ActBehaviorKind.Camouflage || kind == ActBehaviorKind.AppendHit;

    private static bool HasMessageKind(ActProgram program)
    {
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (ActBehavior b in g.Items)
            {
                if (IsMessageKind(b.Kind))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>这个程序自己、或者它放出的被动对象(含对象再放对象)里有没有消息族行为;<paramref name="memo"/> 按对象编号记结果(环路按"没有"算)。</summary>
    private static bool ReachesMessageKind(ActLibrary library, ActProgram program, Dictionary<int, bool> memo)
    {
        if (HasMessageKind(program))
        {
            return true;
        }
        foreach (int index in SpawnIndices(program))
        {
            if (ObjectReachesMessageKind(library, index, memo))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ObjectReachesMessageKind(ActLibrary library, int index, Dictionary<int, bool> memo)
    {
        if (memo.TryGetValue(index, out bool known))
        {
            return known;
        }
        memo[index] = false;
        PassiveTemplate? template = library.Passive(index);
        bool found = false;
        if (template != null)
        {
            found = ReachesMessageKind(library, template.Basic.Program, memo);
            for (int n = 0; !found && n < template.EtcCount; n++)
            {
                PassiveAction? action = template.EtcAction(n);
                found = action != null && ReachesMessageKind(library, action.Program, memo);
            }
        }
        memo[index] = found;
        return found;
    }

    private static LiveMessageTally RunLiveMessages(DnfAssets assets, GameData data, ActLibrary library, MessageSelectorCensus census)
    {
        var tally = new LiveMessageTally();
        var walk = new RectUnionArea(new List<WorldRect>());
        var target = new Stand(ActEntityKind.Character, 0, 700f, 100f);
        var memo = new Dictionary<int, bool>();
        foreach (int id in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(id);
            if (def == null)
            {
                continue;
            }
            MonsterModel model;
            try
            {
                model = MonsterModel.Read(assets, def.Path);
            }
            catch (Exception)
            {
                tally.NoModel++;
                continue;
            }
            IReadOnlyList<string> paths = model.AttackActionPaths;
            var programs = new ActProgram?[paths.Count];
            var chosen = new List<int>();
            for (int i = 0; i < programs.Length; i++)
            {
                ActProgram? p = library.Program(paths[i]);
                if (p != null && p.Triggers.Count > 0)
                {
                    programs[i] = p;
                    census.Note(p);
                    if (ReachesMessageKind(library, p, memo))
                    {
                        chosen.Add(i);
                    }
                }
            }
            if (chosen.Count == 0)
            {
                continue;
            }
            tally.Monsters++;
            foreach (int a in chosen)
            {
                var actor = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 300, 100, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000000, 222.5, 1500, 0);
                var world = new LiveWorld(library, actor);
                actor.ActTarget = target;
                world.Heroes.Add(target);
                world.ResolvePassives = true;
                actor.SetActs(programs!, world);
                tally.Attacks++;
                int heroesBefore = target.GroupsPosted;
                try
                {
                    actor.ActSelf.SetAction(ActActionKind.Attack, a, true, false);
                    if (actor.Phase != MonsterPhase.Attack)
                    {
                        tally.NotEntered++;
                        continue;
                    }
                    int steps = 0;
                    for (; steps < LiveMessageTally.MaxSteps; steps++)
                    {
                        bool attacking = actor.Phase == MonsterPhase.Attack;
                        if (attacking)
                        {
                            actor.Update(10, target.X, target.Y, true, walk);
                        }
                        world.Tick(10);
                        if (!attacking && !AnyLive(world))
                        {
                            break;
                        }
                    }
                    if (steps >= LiveMessageTally.MaxSteps)
                    {
                        tally.StillRunning++;
                    }
                }
                catch (Exception e)
                {
                    tally.Failed++;
                    tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int c) ? c + 1 : 1;
                    continue;
                }
                long toObjects = 0;
                foreach (PassiveObject o in world.All)
                {
                    toObjects += o.ActGroupMessagesPosted;
                    tally.GroupsPostedToObjects += o.ActGroupMessagesPosted;
                    tally.GroupsDroppedByObjects += o.ActGroupMessagesDropped;
                }
                int toHeroes = target.GroupsPosted - heroesBefore;
                tally.GroupsPostedToHeroes += toHeroes;
                tally.GroupsPostedToMonsters += actor.ActGroupMessagesPosted;
                tally.GroupsDroppedByMonsters += actor.ActGroupMessagesDropped;
                tally.AttacksWithPostedGroups += toObjects + toHeroes + actor.ActGroupMessagesPosted > 0 ? 1 : 0;
                tally.Camouflages += actor.CamouflagesStarted;
                tally.AttacksWithCamouflage += actor.CamouflagesStarted > 0 ? 1 : 0;
                tally.AiMessages += actor.AiMessagesReceived;
                tally.AppendHits += world.AppendHits;
                tally.AttacksWithAppendHit += world.AppendHits > 0 ? 1 : 0;
            }
        }
        foreach (int index in memo.Keys)
        {
            PassiveTemplate? template = library.Passive(index);
            if (template == null)
            {
                continue;
            }
            census.Note(template.Basic.Program);
            for (int n = 0; n < template.EtcCount; n++)
            {
                PassiveAction? action = template.EtcAction(n);
                if (action != null)
                {
                    census.Note(action.Program);
                }
            }
        }
        return tally;
    }

    private static bool AnyLive(LiveWorld world)
    {
        foreach (PassiveObject o in world.All)
        {
            if (!o.Finished)
            {
                return true;
            }
        }
        return false;
    }
}
