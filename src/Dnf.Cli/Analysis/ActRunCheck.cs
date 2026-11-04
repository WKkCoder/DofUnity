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
///   最后一段是检查(每项一行 [通过]/[失败],有失败时退出码为 1,verify-all 的 actrun 项用它):引用读得到、没有读不通的 [CREATE PASSIVEOBJECT] 与 [ACTIVE STATUS](E-144)、
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

        /// <summary>[SET ACTION] 切到哪一族动作(按行为条数;E-144 起记,看哪些族还没接到怪物身上)。</summary>
        public readonly SortedDictionary<string, int> SetActionTargets = new(StringComparer.Ordinal);

        /// <summary>[ACTIVE STATUS](E-144):读通的按种类的条数、读不通的条数(种类读不出)、读完后多出来没读的整数个数(应当是 0)。</summary>
        public readonly SortedDictionary<string, int> ActiveStatusKinds = new(StringComparer.Ordinal);
        public int ActiveStatusBroken;
        public int ActiveStatusSpill;
        public int ActiveStatusMissing;

        /// <summary>[TRIGGER] 开关行为按作用的动作族分的条数(E-145;没写种类的是 Own)与读不懂的条数。</summary>
        public readonly SortedDictionary<string, int> TriggerSwitchPools = new(StringComparer.Ordinal);
        public int TriggerSwitchBroken;

        /// <summary>引用了含 [ACTIVE STATUS] 的行为组的触发器,按 [DO BEHAVIOR] 的目标选择器(目标是 [CHECKUP OBJECT] 的取最近一个 [WHICH] 的选择器)分的条数。</summary>
        public readonly SortedDictionary<string, int> ActiveStatusTargets = new(StringComparer.Ordinal);

        /// <summary>
        /// [SET POS FORCE](M58,E-152)按块计:读通的块数、读不通的块数(含 [MOVE ACCELATE]、[LASTATTACK POSITION]、值读不出的)、动的是拥有者的块数、
        /// 值里带随机的块数,以及基准点、朝向来源、各轴写了几个值(0、1、2、更多)的分布。
        /// </summary>
        public int ForceBlocks;
        public int ForceBroken;
        public int ForceMoveMe;
        public int ForceRandom;
        public readonly SortedDictionary<string, int> ForceOrigins = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> ForceDirections = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> ForceAxes = new(StringComparer.Ordinal);

        /// <summary>
        /// [CASTING](M69b,E-168)按行为条数计:读通的条数、读不通的条数(两个值读不全)、行为组号越界的条数、指向紧跟本组的下一组的条数、指向别的组的条数、
        /// 值带随机的条数,以及到期要执行的行为组第一条的种类分布(一个值有多个可能的,每个可能都算)。
        /// </summary>
        public int CastUses;
        public int CastBroken;
        public int CastOutOfRange;
        public int CastNext;
        public int CastOther;
        public int CastRandom;
        public readonly SortedDictionary<string, int> CastFirstKinds = new(StringComparer.Ordinal);

        /// <summary>
        /// 朝目标的移动与转向(M70,E-169)按行为条数计:[MOVE TO TARGET]、[CHANGE DIRECTION BY TARGET]、[IS DIRECTION TO MOVE]、[SET SAVED TARGET]、[CHANGE MY TARGET] 各读通几条、
        /// 读不通几条(写法缺值)。
        /// </summary>
        public readonly SortedDictionary<string, int> TargetMotionUses = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> TargetMotionBroken = new(StringComparer.Ordinal);

        private static string? TargetMotionTag(ActBehaviorKind kind) => kind switch
        {
            ActBehaviorKind.MoveToTarget => "[MOVE TO TARGET]",
            ActBehaviorKind.SetFacingByTarget => "[CHANGE DIRECTION BY TARGET]",
            ActBehaviorKind.SetFacingInAttack => "[IS DIRECTION TO MOVE]",
            ActBehaviorKind.UseSavedTarget => "[SET SAVED TARGET]",
            ActBehaviorKind.ChangeMyTarget => "[CHANGE MY TARGET]",
            _ => null,
        };

        private static bool IsTargetMotionTag(string tag) => tag == "[MOVE TO TARGET]" || tag == "[CHANGE DIRECTION BY TARGET]" || tag == "[IS DIRECTION TO MOVE]"
            || tag == "[SET SAVED TARGET]" || tag == "[CHANGE MY TARGET]";

        private static void Bump(SortedDictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out int n);
            map[key] = n + 1;
        }

        private void CountCast(ActProgram program, ActBehavior b, int own)
        {
            CastUses++;
            CastRandom += b.CastMs.IsConstant && b.CastGroup.IsConstant ? 0 : 1;
            bool outOfRange = false;
            bool next = false;
            bool other = false;
            foreach (int k in b.CastGroup.Candidates())
            {
                if (k < 1 || k >= program.Behaviors.Count)
                {
                    outOfRange = true;
                    continue;
                }
                if (k == own + 1)
                {
                    next = true;
                }
                else
                {
                    other = true;
                }
                ActBehaviorGroup target = program.Behaviors[k];
                Bump(CastFirstKinds, target.Items.Count == 0 ? "(空)" : target.Items[0].Kind.ToString());
            }
            CastOutOfRange += outOfRange ? 1 : 0;
            CastNext += next ? 1 : 0;
            CastOther += other ? 1 : 0;
        }

        private void CountForce(ActBehavior b)
        {
            ForceBlocks++;
            ActForceOrigin origin = ActForceOrigin.Target;
            ActForceDirection direction = ActForceDirection.Target;
            bool moveMe = false;
            bool random = false;
            var counts = new int[3];
            foreach (ActForceToken t in b.ForceTokens)
            {
                switch (t.Code)
                {
                    case ActForceCode.XStart:
                    case ActForceCode.YStart:
                    case ActForceCode.ZStart:
                        counts[(int)t.Code]++;
                        random |= !t.Value.IsConstant;
                        break;
                    case ActForceCode.XEnd:
                    case ActForceCode.YEnd:
                    case ActForceCode.ZEnd:
                        counts[(int)t.Code - 3]++;
                        random |= !t.Value.IsConstant;
                        break;
                    case ActForceCode.UseMyDirection: direction = ActForceDirection.Owner; break;
                    case ActForceCode.NotUseObjectDirection: direction = ActForceDirection.Right; break;
                    case ActForceCode.UseTargetPos: origin = ActForceOrigin.Target; break;
                    case ActForceCode.UseMyBasePos: origin = ActForceOrigin.Owner; break;
                    case ActForceCode.UseMapPos: origin = ActForceOrigin.Map; break;
                    case ActForceCode.MoveTarget: moveMe = false; break;
                    case ActForceCode.MoveMe: moveMe = true; break;
                }
            }
            ForceMoveMe += moveMe ? 1 : 0;
            ForceRandom += random ? 1 : 0;
            Bump(ForceOrigins, origin.ToString());
            Bump(ForceDirections, direction.ToString());
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                Bump(ForceAxes, axes[i] + ":" + (counts[i] > 2 ? "3+" : counts[i].ToString()));
            }
        }

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
                ActSelector which = ActSelector.WorkingSet;
                foreach (ActRecord r in t.Records)
                {
                    if (r.Kind == ActRecordKind.Which)
                    {
                        which = r.Selector;
                    }
                    else if ((r.Kind == ActRecordKind.DoBehavior || r.Kind == ActRecordKind.DirectDoBehavior) && r.Behavior != null)
                    {
                        foreach (int idx in r.Behavior.Candidates())
                        {
                            if (idx >= 0 && idx < program.Behaviors.Count && program.Behaviors[idx].Items.Any(b => b.Kind == ActBehaviorKind.ActiveStatus))
                            {
                                string target = (r.Selector == ActSelector.WorkingSet ? which : r.Selector).ToString();
                                ActiveStatusTargets.TryGetValue(target, out int n);
                                ActiveStatusTargets[target] = n + 1;
                                break;
                            }
                        }
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
            int groupIndex = -1;
            foreach (ActBehaviorGroup g in program.Behaviors)
            {
                Groups++;
                groupIndex++;
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
                    if (b.Kind == ActBehaviorKind.SetAction)
                    {
                        string target = b.Action.ToString();
                        SetActionTargets.TryGetValue(target, out int sa);
                        SetActionTargets[target] = sa + 1;
                    }
                    if (b.Kind == ActBehaviorKind.TriggerSwitch)
                    {
                        string pool = b.TriggerPool.ToString();
                        TriggerSwitchPools.TryGetValue(pool, out int tp);
                        TriggerSwitchPools[pool] = tp + 1;
                    }
                    else if (b.Kind == ActBehaviorKind.Unsupported && b.Tag == "[TRIGGER]")
                    {
                        TriggerSwitchBroken++;
                    }
                    if (b.Kind == ActBehaviorKind.ActiveStatus)
                    {
                        string status = b.Status.Kind.ToString();
                        ActiveStatusKinds.TryGetValue(status, out int ak);
                        ActiveStatusKinds[status] = ak + 1;
                        ActiveStatusSpill += b.StatusSpill;
                        ActiveStatusMissing += b.StatusMissing;
                    }
                    else if (b.Kind == ActBehaviorKind.Unsupported && b.Tag == "[ACTIVE STATUS]")
                    {
                        ActiveStatusBroken++;
                    }
                    if (b.Kind == ActBehaviorKind.Casting)
                    {
                        CountCast(program, b, groupIndex);
                    }
                    else if (b.Kind == ActBehaviorKind.Unsupported && b.Tag == "[CASTING]")
                    {
                        CastBroken++;
                    }
                    string? motionTag = TargetMotionTag(b.Kind);
                    if (motionTag != null)
                    {
                        Bump(TargetMotionUses, motionTag);
                    }
                    else if (b.Kind == ActBehaviorKind.Unsupported && b.Tag != null && IsTargetMotionTag(b.Tag))
                    {
                        Bump(TargetMotionBroken, b.Tag);
                    }
                    if (b.Kind == ActBehaviorKind.SetPosForce)
                    {
                        CountForce(b);
                    }
                    else if (b.Kind == ActBehaviorKind.Unsupported && b.Tag == "[SET POS FORCE]")
                    {
                        ForceBroken++;
                    }
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
            Console.WriteLine("  [SET ACTION] 切到的动作族:" + Join(SetActionTargets, 12));
            Console.WriteLine("  [ACTIVE STATUS] 读通 " + ActiveStatusKinds.Values.Sum() + " 条(按种类:" + Join(ActiveStatusKinds, 18) + "),读不通 " + ActiveStatusBroken
                + " 条,整数多写 " + ActiveStatusSpill + " 个、少写 " + ActiveStatusMissing + " 个");
            Console.WriteLine("  引用含 [ACTIVE STATUS] 行为组的触发器,按目标选择器:" + Join(ActiveStatusTargets, 18));
            Console.WriteLine("  [TRIGGER] 开关读通 " + TriggerSwitchPools.Values.Sum() + " 条(按作用的动作族:" + Join(TriggerSwitchPools, 12) + "),读不通 " + TriggerSwitchBroken + " 条");
            Console.WriteLine("  [SET POS FORCE] 读通 " + ForceBlocks + " 块(动自己 " + ForceMoveMe + ",值带随机 " + ForceRandom + ";基准点:" + Join(ForceOrigins, 6) + ";朝向来源:"
                + Join(ForceDirections, 6) + ";各轴写了几个值:" + Join(ForceAxes, 12) + "),读不通 " + ForceBroken + " 块");
            Console.WriteLine("  [CASTING] 读通 " + CastUses + " 条(读不通 " + CastBroken + ";行为组号越界 " + CastOutOfRange + ",指向紧跟的下一组 " + CastNext + "、别的组 " + CastOther + ",值带随机 " + CastRandom
                + ";到期执行的行为组第一条按种类:" + Join(CastFirstKinds, 12) + ")");
            Console.WriteLine("  朝目标的移动与转向(E-169)读通 " + TargetMotionUses.Values.Sum() + " 条(" + Join(TargetMotionUses, 8) + "),读不通 " + TargetMotionBroken.Values.Sum() + " 条("
                + Join(TargetMotionBroken, 8) + ")");
            Console.WriteLine("  读不通的 [CREATE PASSIVEOBJECT] " + CreateBroken + " 条(缺 [INDEX] " + CreateNoIndex + ",缺 [POS] " + CreateNoPos + ")");
            Console.WriteLine("  读不懂的触发器标签(种数 " + UnsupportedTriggerTags.Count + ",按触发器条数):" + Join(UnsupportedTriggerTags, 25));
            Console.WriteLine("  读不懂的行为标签(种数 " + UnsupportedBehaviorTags.Count + ",按行为组个数):" + Join(UnsupportedBehaviorTags, 25));
        }

        public object ToJson() => new
        {
            Programs, WithTriggers, Triggers, Supported, FrameTriggers, EventTriggers, ConditionOnly, Groups, GroupsWithUnsupported, Items, WithSpawn, SubAnis,
            CreateBroken, CreateNoIndex, CreateNoPos,
            UnsupportedTriggerTags, UnsupportedBehaviorTags, BehaviorKinds, SetActionTargets,
            ActiveStatusKinds, ActiveStatusBroken, ActiveStatusSpill, ActiveStatusMissing, ActiveStatusTargets,
            TriggerSwitchPools, TriggerSwitchBroken,
            ForceBlocks, ForceBroken, ForceMoveMe, ForceRandom, ForceOrigins, ForceDirections, ForceAxes,
            CastUses, CastBroken, CastOutOfRange, CastNext, CastOther, CastRandom, CastFirstKinds,
            TargetMotionUses, TargetMotionBroken,
        };
    }

    /// <summary>
    /// 跨招 [TRIGGER](E-145)按"怪物 × 出招列表"的汇总:每条按动作族写法的开关指到的招与触发器存不存在(出招序号就是 .mob 招式列表的下标,与 .ai 的 is attack action enable 同一套),
    /// 初值关着的触发器有多少、其中多少被别的招的 [ON] 指到(静态可达)。
    /// </summary>
    private sealed class CrossSwitchTally
    {
        public int Monsters;
        public int Switches;
        public int ToAttack;
        public int Resolved;
        public int NoSlot;
        public int OutOfRange;
        public int OtherPools;
        public int InitiallyOff;
        public int InitiallyOffReachable;
        public int ToAttackOn;
        public int ToAttackOff;
        public int ToAttackReset;

        public void Print()
        {
            Console.WriteLine("  跨招 [TRIGGER](E-145,按怪物×出招列表):含跨招开关的怪物 " + Monsters + " 个,开关 " + Switches + " 条,其中指向出招 " + ToAttack + " 条(落到存在的招与触发器 " + Resolved
                + " 条,没有这一招或没装触发器 " + NoSlot + ",触发器序号越界 " + OutOfRange + ";命令 [ON] " + ToAttackOn + "、[OFF] " + ToAttackOff + "、[RESET] " + ToAttackReset + "),指向其它动作族 " + OtherPools + " 条;初值关着的触发器 " + InitiallyOff + " 个,其中被别的招 [ON] 指到 " + InitiallyOffReachable + " 个");
        }

        public object ToJson() => new { Monsters, Switches, ToAttack, Resolved, NoSlot, OutOfRange, OtherPools, InitiallyOff, InitiallyOffReachable, ToAttackOn, ToAttackOff, ToAttackReset };
    }

    private static void CountCrossSlotSwitches(ActProgram?[] slots, CrossSwitchTally tally)
    {
        bool any = false;
        var reachable = new HashSet<(int Slot, int Trigger)>();
        for (int slot = 0; slot < slots.Length; slot++)
        {
            ActProgram? program = slots[slot];
            if (program == null)
            {
                continue;
            }
            foreach (ActBehaviorGroup g in program.Behaviors)
            {
                foreach (ActBehavior b in g.Items)
                {
                    if (b.Kind != ActBehaviorKind.TriggerSwitch || b.TriggerPool == ActTriggerPool.Own)
                    {
                        continue;
                    }
                    any = true;
                    tally.Switches++;
                    if (b.TriggerPool != ActTriggerPool.Attack)
                    {
                        tally.OtherPools++;
                        continue;
                    }
                    tally.ToAttack++;
                    switch (b.Command)
                    {
                        case ActTriggerCommand.On:
                            tally.ToAttackOn++;
                            break;
                        case ActTriggerCommand.Off:
                            tally.ToAttackOff++;
                            break;
                        default:
                            tally.ToAttackReset++;
                            break;
                    }
                    ActProgram? target = (uint)b.TriggerSlot < (uint)slots.Length ? slots[b.TriggerSlot] : null;
                    if (target == null || target.Triggers.Count == 0)
                    {
                        tally.NoSlot++;
                    }
                    else if ((uint)b.TriggerIndex >= (uint)target.Triggers.Count)
                    {
                        tally.OutOfRange++;
                    }
                    else
                    {
                        tally.Resolved++;
                        if (b.Command == ActTriggerCommand.On)
                        {
                            reachable.Add((b.TriggerSlot, b.TriggerIndex));
                        }
                    }
                }
            }
        }
        if (any)
        {
            tally.Monsters++;
        }
        for (int slot = 0; slot < slots.Length; slot++)
        {
            ActProgram? program = slots[slot];
            if (program == null)
            {
                continue;
            }
            foreach (ActTrigger t in program.Triggers)
            {
                if (!t.Enabled)
                {
                    tally.InitiallyOff++;
                    if (reachable.Contains((slot, t.Index)))
                    {
                        tally.InitiallyOffReachable++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 怪物自定义动作([etc action],M62-b,E-159)的静态审计(汇总数,本机,非 REA):有多少只怪物写了、各项的 .act 读不读得出、动画循不循环、带不带攻击框与触发器,
    /// 以及怪物自己各个 .act 里 [SET ACTION] [CUSTOM] n 的 n 落不落在列表里(落在列表里的比例是"n 就是 [etc action] 的下标"这个读法的证据)。
    /// </summary>
    private sealed class CustomTally
    {
        public int Monsters;
        public int Entries;
        public int Holes;
        public int ActsMissing;
        public int WithAnimation;
        public int Looping;
        public int LoopingNoExit;
        public int WithAttackBoxes;
        public int WithTriggers;
        public int ChangeFileInEtc;
        public int UsingMonsters;
        public int Uses;
        public int UsesInRange;
        public readonly SortedDictionary<string, int> UsesBySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> SizeHistogram = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("怪物的自定义动作(E-159,[etc action]):");
            Console.WriteLine("  写了列表的怪物 " + Monsters + " 只,共 " + Entries + " 项(各列表长度:" + Join(SizeHistogram, 12) + ",空位 " + Holes + ");.act 读不到 " + ActsMissing + " 项,带主动画 " + WithAnimation
                + " 项(循环 " + Looping + "、其中脚本里没有 [SET ACTION] 的 " + LoopingNoExit + "、带攻击框 " + WithAttackBoxes + "),带触发器 " + WithTriggers + " 项;自定义动作里的 [CHANGE ACTION FILE] " + ChangeFileInEtc + " 条");
            Console.WriteLine("  怪物自己的 .act 里 [SET ACTION] [CUSTOM] n:用到的怪物 " + UsingMonsters + " 只,共 " + Uses + " 条,n 落在列表里 " + UsesInRange + " 条;按来源 " + Join(UsesBySource, 12));
        }

        public object ToJson() => new { Monsters, Entries, Holes, ActsMissing, WithAnimation, Looping, LoopingNoExit, WithAttackBoxes, WithTriggers, ChangeFileInEtc, UsingMonsters, Uses, UsesInRange, UsesBySource, SizeHistogram };
    }

    /// <summary>程序里有没有 [SET ACTION](任何一族):循环动画的自定义动作靠它(或挨打)才离得开。</summary>
    private static bool HasSetAction(ActProgram program)
    {
        foreach (ActBehaviorGroup group in program.Behaviors)
        {
            foreach (ActBehavior b in group.Items)
            {
                if (b.Kind == ActBehaviorKind.SetAction)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static CustomTally CountCustoms(GameData data, ActLibrary library, Func<string?, ActProgram?> program)
    {
        var tally = new CustomTally();
        foreach (int id in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(id);
            if (def == null || def.EtcActions.Length == 0)
            {
                continue;
            }
            tally.Monsters++;
            int count = def.EtcActions.Length;
            tally.Entries += count;
            string size = count < 10 ? "0" + count : count.ToString();
            tally.SizeHistogram[size] = tally.SizeHistogram.TryGetValue(size, out int hist) ? hist + 1 : 1;
            for (int i = 0; i < count; i++)
            {
                string? path = def.EtcActionPath(i);
                if (path == null)
                {
                    tally.Holes++;
                    continue;
                }
                PassiveAction? action = library.Action(path, null);
                if (action == null)
                {
                    tally.ActsMissing++;
                    continue;
                }
                if (action.HasTriggers)
                {
                    tally.WithTriggers++;
                }
                if (action.Ani != null)
                {
                    tally.WithAnimation++;
                    if (action.Ani.Loops)
                    {
                        tally.Looping++;
                        if (!HasSetAction(action.Program))
                        {
                            tally.LoopingNoExit++;
                        }
                    }
                    bool boxes = false;
                    foreach (AniFrame frame in action.Ani.Frames)
                    {
                        foreach (AniBox box in frame.Boxes)
                        {
                            if (box.Type == AniBoxType.Attack)
                            {
                                boxes = true;
                            }
                        }
                    }
                    if (boxes)
                    {
                        tally.WithAttackBoxes++;
                    }
                }
                foreach (ActBehaviorGroup group in action.Program.Behaviors)
                {
                    foreach (ActBehavior b in group.Items)
                    {
                        if (b.Kind == ActBehaviorKind.ChangeActionFile)
                        {
                            tally.ChangeFileInEtc++;
                        }
                    }
                }
            }
            // 怪物自己的各个 .act:出招列表、各动作族、自定义动作本身
            var files = new List<(string Source, string? Path)>();
            string[] list = def.AttackList;
            for (int i = 0; i < list.Length; i++)
            {
                files.Add(("出招", string.IsNullOrEmpty(list[i]) ? null : PvfPath.ResolveRelativeTo(def.Path, list[i])));
            }
            files.Add(("待机", def.WaitingActionPath));
            files.Add(("行走", def.MoveActionPath));
            files.Add(("坐下", def.SitActionPath));
            files.Add(("受击", def.Damage1ActionPath));
            files.Add(("受击", def.Damage2ActionPath));
            files.Add(("倒地", def.DownActionPath));
            files.Add(("翻倒", def.OverturnActionPath));
            files.Add(("最后一幕", def.ActionPath("[last action]")));
            files.Add(("过程", def.ActionPath("[proc action]")));
            for (int i = 0; i < count; i++)
            {
                files.Add(("自定义", def.EtcActionPath(i)));
            }
            bool used = false;
            foreach ((string source, string? path) in files)
            {
                ActProgram? p = program(path);
                if (p == null)
                {
                    continue;
                }
                foreach (ActBehaviorGroup group in p.Behaviors)
                {
                    foreach (ActBehavior b in group.Items)
                    {
                        if (b.Kind != ActBehaviorKind.SetAction || b.Action != ActActionKind.Custom || b.ActionIndex == null)
                        {
                            continue;
                        }
                        foreach (int n in b.ActionIndex.Candidates())
                        {
                            tally.Uses++;
                            used = true;
                            if ((uint)n < (uint)count)
                            {
                                tally.UsesInRange++;
                            }
                            tally.UsesBySource[source] = tally.UsesBySource.TryGetValue(source, out int c) ? c + 1 : 1;
                        }
                    }
                }
            }
            if (used)
            {
                tally.UsingMonsters++;
            }
        }
        return tally;
    }

    /// <summary>
    /// 实跑的小世界(E-158):只有被动对象自己,没有主角与目标;对象放出的对象也在里面跑(同时最多 <see cref="MaxObjects"/> 个,多了拒绝)。
    /// 用来验证真实脚本在开关板宿主上跑得起来(不抛异常),并数开关的读写;不是战斗模拟。
    /// </summary>
    private sealed class LiveWorld : IActHost
    {
        public const int MaxObjects = 200;

        private readonly ActLibrary _library;
        private readonly MonsterActor _root;
        private readonly Random _rng = new Random(1);

        public readonly List<PassiveObject> All = new List<PassiveObject>();
        public long Ms;
        public int Refused;
        public int Summons;
        public int CastsArmed;
        public int CastsFired;
        public int MovesStarted;
        public int MovesStopped;

        public LiveWorld(ActLibrary library, MonsterActor root)
        {
            _library = library;
            _root = root;
        }

        public int Random(int n) => _rng.Next(n);

        public long NowMs => Ms;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
            PassiveTemplate? template = All.Count >= MaxObjects ? null : _library.Passive(index);
            if (template == null)
            {
                Refused++;
                return;
            }
            All.Add(new PassiveObject(template, owner, _root, this, x, y, z, facingLeft));
        }

        /// <summary>主角与怪物的替身(M67 追踪实跑用;别的实跑里是空的)。</summary>
        public readonly List<IActEntity> Heroes = new List<IActEntity>();

        public readonly List<IActEntity> Monsters = new List<IActEntity>();

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            if (selector == ActSelector.Character || selector == ActSelector.AllEnemy || selector == ActSelector.AllCharacterTeam)
            {
                into.AddRange(Heroes);
            }
            else if (selector == ActSelector.Monster || selector == ActSelector.AllMonsterTeam || selector == ActSelector.AllOurTeam)
            {
                into.AddRange(Monsters);
            }
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
            Summons++;
        }

        public void CastArmed(IActEntity owner, int milliseconds, int group)
        {
            CastsArmed++;
        }

        public void CastFired(IActEntity owner, int group)
        {
            CastsFired++;
        }

        public void MoveToTargetStarted(IActEntity mover, int speed, int durationMs)
        {
            MovesStarted++;
        }

        public void MoveToTargetStopped(IActEntity mover)
        {
            MovesStopped++;
        }

        /// <summary>把世界里还活着的被动对象推进 <paramref name="ms"/> 毫秒(怪物的实跑用;对象的运行器一起跑)。</summary>
        public void Tick(int ms)
        {
            Ms += ms;
            for (int i = 0; i < All.Count; i++)
            {
                PassiveObject o = All[i];
                if (!o.Finished)
                {
                    o.Update(ms);
                }
            }
        }

        /// <summary>放一个对象进去,按 10 毫秒一步跑 <paramref name="seconds"/> 秒(全部消失就提前结束)。</summary>
        public void Run(PassiveTemplate template, int seconds)
        {
            All.Add(new PassiveObject(template, _root.ActSelf, _root, this, 300, 100, 0, false));
            for (int t = 0; t < seconds * 1000; t += 10)
            {
                int live = 0;
                for (int i = 0; i < All.Count; i++)
                {
                    PassiveObject o = All[i];
                    if (!o.Finished)
                    {
                        live++;
                        o.Update(10);
                    }
                }
                Ms += 10;
                if (live == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>站着不动的替身(追踪实跑里的"主角"或"怪物"):活着、没有目标、血量很大。</summary>
    private sealed class Stand : IActEntity
    {
        public Stand(ActEntityKind kind, int objectIndex, float x, float y)
        {
            Kind = kind;
            ObjectIndex = objectIndex;
            X = x;
            Y = y;
        }

        public ActEntityKind Kind { get; }
        public int ObjectIndex { get; }
        public float X { get; }
        public float Y { get; }
        public float Z => 0;
        public bool FacingLeft => false;
        public bool IsAlive => true;
        public IActEntity? Target => null;
        public IActEntity? Master => null;
        public int MotionVersion => 0;
        public int Hp => 1000000;
        public int HpMax => 1000000;
        public int Level => 1;

        public void SetFacing(bool left)
        {
        }

        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
        }

        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
        }

        public void Destroy(bool force)
        {
        }

        public void AttackRect(ActAttackRectMode mode)
        {
        }

        public void SetFrame(int frame)
        {
        }

        public void SetDamageBox(ActDamageBoxMode mode)
        {
        }

        public void SetFloatingHeight(int height)
        {
        }

        public bool Teleport(float x, float y, float z) => false;

        public void SetHp(int hp)
        {
        }
    }

    /// <summary>
    /// 被动对象追踪(M67,E-165;汇总数,本机,非 REA):全表里带 [homing] 块的 .obj 的分布(启用数、追踪种类、各开关的出现数),
    /// 以及每个启用了追踪的对象的实跑(主角替身站在前方 400 像素、纵深差 60 处;怪物替身与它同位置;每个对象按 10 毫秒一步最多跑 5 秒):
    /// 抛不抛异常、位置与速度出不出现非数、控制器检查过没有、定过方向没有、离替身最近的距离比出发时近了没有。不是战斗模拟:替身不动,没有碰撞与受击。
    /// </summary>
    private sealed class HomingTally
    {
        public int Blocks;
        public int Used;
        public int ReachedUsed;
        public int FollowMe;
        public int FollowEnemy;
        public int FollowMonster;
        public int FollowCharacter;
        public int Smooth;
        public int Instant;
        public int StraightAim;
        public int SyncMove;
        public int SyncRotation;
        public int Timed;
        public int Kept;
        public int Directed;
        public int AccelUp;
        public int AccelDown;
        public int Turned;
        public int NarrowCone;
        public int Live;
        public int NoTemplate;
        public int Failed;
        public int NonFinite;
        public int Targeted;
        public int Checked;
        public int Steered;
        public int Approached;
        public int Ended;
        public int NeverChecked;
        public int NeverCheckedStraight;
        public int NeverCheckedOff;
        public int NeverCheckedGone;
        public int NeverCheckedOther;
        public int NotApproached;
        public int NotApproachedUnchecked;
        public int NotApproachedZeroSpeed;
        public int NotApproachedNarrow;
        public int NotApproachedDirected;
        public int NotApproachedOther;
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("被动对象追踪(E-165):带 [homing] 块的 " + Blocks + " 个,启用的(homing use 非 0)" + Used + " 个(怪物出招能放出的 " + ReachedUsed + " 个);块里的追踪种类 ME " + FollowMe
                + " / ENEMY " + FollowEnemy + " / MONSTER " + FollowMonster + " / CHARACTER " + FollowCharacter);
            Console.WriteLine("  启用的里:有 [max rotation] 的 " + Smooth + " 个,没有的 " + Instant + " 个;[straight homing] " + StraightAim + " 个,[sync target move] " + SyncMove
                + " 个,[sync animation rotation] " + SyncRotation + " 个;有 [homing time] 的 " + Timed + " 个,有 [homing keep distance] 的 " + Kept + " 个,[homing direction] 不是 -1 的 " + Directed
                + " 个;加速度为正 " + AccelUp + " 个、为负 " + AccelDown + " 个;[init rotation] 非 0 的 " + Turned + " 个,[diff rotation] 小于 180 的 " + NarrowCone + " 个");
            Console.WriteLine("  实跑(替身站在前方,每个对象最多 5 秒):对象 " + Live + " 个(没有模板 " + NoTemplate + " 个,抛异常 " + Failed + " 个,出现非数 " + NonFinite + " 个);出生时取到追踪对象的 " + Targeted
                + " 个,控制器检查过的 " + Checked + " 个,定过方向(出生之外)的 " + Steered + " 个,离替身最近距离比出发时近了的 " + Approached + " 个,5 秒内自己消失的 " + Ended + " 个"
                + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
            Console.WriteLine("  没检查过的 " + NeverChecked + " 个:[straight homing] " + NeverCheckedStraight + " 个、追踪被脚本关着 " + NeverCheckedOff + " 个、第一步前就没了 " + NeverCheckedGone + " 个、别的 " + NeverCheckedOther
                + " 个;没走近的 " + NotApproached + " 个:其中没检查过的 " + NotApproachedUnchecked + " 个、速度与加速度都不为正 " + NotApproachedZeroSpeed + " 个、追踪锥小于 180 度 " + NotApproachedNarrow + " 个、朝向码不是 -1 " + NotApproachedDirected + " 个、别的 " + NotApproachedOther + " 个");
        }

        public object ToJson() => new
        {
            Blocks, Used, ReachedUsed, FollowMe, FollowEnemy, FollowMonster, FollowCharacter, Smooth, Instant, StraightAim, SyncMove, SyncRotation, Timed, Kept, Directed,
            AccelUp, AccelDown, Turned, NarrowCone, Live, NoTemplate, Failed, NonFinite, Targeted, Checked, Steered, Approached, Ended, NeverChecked, NeverCheckedStraight, NeverCheckedOff,
            NeverCheckedGone, NeverCheckedOther, NotApproached, NotApproachedUnchecked, NotApproachedZeroSpeed, NotApproachedNarrow, NotApproachedDirected, NotApproachedOther, Errors,
        };
    }

    private static void CountHoming(Dnf.Game.PassiveObjectDef? def, bool reached, HomingTally t)
    {
        HomingDef? h = def?.Homing;
        if (h == null)
        {
            return;
        }
        t.Blocks++;
        switch (h.Follow)
        {
            case HomingFollow.Me:
                t.FollowMe++;
                break;
            case HomingFollow.Enemy:
                t.FollowEnemy++;
                break;
            case HomingFollow.Monster:
                t.FollowMonster++;
                break;
            case HomingFollow.Character:
                t.FollowCharacter++;
                break;
        }
        if (!h.Use)
        {
            return;
        }
        t.Used++;
        t.ReachedUsed += reached ? 1 : 0;
        t.Smooth += h.MaxRotation != 0f ? 1 : 0;
        t.Instant += h.MaxRotation == 0f ? 1 : 0;
        t.StraightAim += h.Straight ? 1 : 0;
        t.SyncMove += h.SyncTargetMove ? 1 : 0;
        t.SyncRotation += h.SyncAnimationRotation ? 1 : 0;
        t.Timed += h.TimeMs > 0 ? 1 : 0;
        t.Kept += h.KeepDistance != 0 ? 1 : 0;
        t.Directed += h.Direction != -1 ? 1 : 0;
        t.AccelUp += h.Acceleration > 0 ? 1 : 0;
        t.AccelDown += h.Acceleration < 0 ? 1 : 0;
        t.Turned += h.InitRotation != 0f ? 1 : 0;
        t.NarrowCone += h.DiffRotation < HomingDef.DefaultDiffRotation ? 1 : 0;
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static MonsterActor? MakeRoot(DnfAssets assets, GameData data)
    {
        foreach (int mid in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(mid);
            if (def == null)
            {
                continue;
            }
            try
            {
                MonsterModel model = MonsterModel.Read(assets, def.Path);
                return new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100, 222.5, 1500, 0);
            }
            catch (Exception)
            {
                // 读不了的怪物跳过,换下一个
            }
        }
        return null;
    }

    private static void RunLiveHoming(HomingTally tally, DnfAssets assets, GameData data, ActLibrary library, IEnumerable<int> ids)
    {
        MonsterActor? root = MakeRoot(assets, data);
        if (root == null)
        {
            return;
        }
        foreach (int id in ids)
        {
            HomingDef? h = data.GetPassiveObject(id)?.Homing;
            if (h == null || !h.Use)
            {
                continue;
            }
            PassiveTemplate? template = library.Passive(id);
            if (template == null)
            {
                tally.NoTemplate++;
                continue;
            }
            var stand = new Stand(ActEntityKind.Character, 0, 700, 160);
            var world = new LiveWorld(library, root);
            world.Heroes.Add(stand);
            world.Monsters.Add(new Stand(ActEntityKind.Monster, h.FollowParam, 700, 160));
            try
            {
                var o = new PassiveObject(template, root.ActSelf, root, world, 300, 100, 0, false);
                world.All.Add(o);
                bool targeted = o.Homing != null && o.Homing.Target != null;
                double start = Math.Sqrt((stand.X - o.X) * (stand.X - o.X) + (stand.Y - o.Y) * (stand.Y - o.Y));
                double best = start;
                bool nonFinite = false;
                bool ended = false;
                for (int t = 0; t < 5000; t += 10)
                {
                    for (int i = 0; i < world.All.Count; i++)
                    {
                        PassiveObject p = world.All[i];
                        if (!p.Finished)
                        {
                            p.Update(10);
                        }
                    }
                    world.Ms += 10;
                    nonFinite |= !Finite(o.X) || !Finite(o.Y) || !Finite(o.Z) || !Finite(o.VelocityX) || !Finite(o.VelocityY);
                    best = Math.Min(best, Math.Sqrt((stand.X - o.X) * (stand.X - o.X) + (stand.Y - o.Y) * (stand.Y - o.Y)));
                    if (o.Finished)
                    {
                        ended = true;
                        break;
                    }
                }
                tally.Live++;
                tally.Targeted += targeted ? 1 : 0;
                tally.NonFinite += nonFinite ? 1 : 0;
                tally.Checked += o.Homing != null && o.Homing.Checks > 0 ? 1 : 0;
                tally.Steered += o.Homing != null && o.Homing.Steers > 1 ? 1 : 0;
                tally.Approached += best < start - 1.0 ? 1 : 0;
                tally.Ended += ended ? 1 : 0;
                if (o.Homing == null || o.Homing.Checks == 0)
                {
                    tally.NeverChecked++;
                    if (h.Straight)
                    {
                        tally.NeverCheckedStraight++;
                    }
                    else if (o.Homing != null && !o.Homing.On)
                    {
                        tally.NeverCheckedOff++;
                    }
                    else if (ended)
                    {
                        tally.NeverCheckedGone++;
                    }
                    else
                    {
                        tally.NeverCheckedOther++;
                    }
                }
                if (!(best < start - 1.0))
                {
                    tally.NotApproached++;
                    if (o.Homing == null || o.Homing.Checks == 0)
                    {
                        tally.NotApproachedUnchecked++;
                    }
                    else if (h.Speed <= 0 && h.Acceleration <= 0)
                    {
                        tally.NotApproachedZeroSpeed++;
                    }
                    else if (h.DiffRotation < HomingDef.DefaultDiffRotation)
                    {
                        tally.NotApproachedNarrow++;
                    }
                    else if (h.Direction != -1)
                    {
                        tally.NotApproachedDirected++;
                    }
                    else
                    {
                        tally.NotApproachedOther++;
                    }
                }
            }
            catch (Exception e)
            {
                tally.Failed++;
                tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int n) ? n + 1 : 1;
            }
        }
    }

    private sealed class LiveBoardTally
    {
        public int Objects;
        public int NoTemplate;
        public int Failed;
        public int Reads;
        public int ReadMisses;
        public int Writes;
        public int WriteMisses;
        public int Spawned;
        public int Refused;
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  实跑(空场地、没有主角与目标、每个对象 10 秒;E-158):对象 " + Objects + " 个(没有模板 " + NoTemplate + " 个,抛异常 " + Failed + " 个);开关读 " + Reads + " 次(落空 " + ReadMisses
                + "),写 " + Writes + " 次(落空 " + WriteMisses + ");放出对象 " + Spawned + " 个(上限拒绝 " + Refused + " 次)" + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
        }

        public object ToJson() => new { Objects, NoTemplate, Failed, Reads, ReadMisses, Writes, WriteMisses, Spawned, Refused, Errors };
    }

    private static LiveBoardTally RunLiveBoards(DnfAssets assets, GameData data, ActLibrary library, IEnumerable<int> ids)
    {
        var tally = new LiveBoardTally();
        MonsterActor? root = null;
        foreach (int mid in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(mid);
            if (def == null)
            {
                continue;
            }
            try
            {
                MonsterModel model = MonsterModel.Read(assets, def.Path);
                root = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100, 222.5, 1500, 0);
                break;
            }
            catch (Exception)
            {
                // 读不了的怪物跳过,换下一个
            }
        }
        if (root == null)
        {
            return tally;
        }
        foreach (int id in ids)
        {
            PassiveTemplate? template = library.Passive(id);
            if (template == null)
            {
                tally.NoTemplate++;
                continue;
            }
            var world = new LiveWorld(library, root);
            try
            {
                world.Run(template, 10);
            }
            catch (Exception e)
            {
                tally.Failed++;
                tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int n) ? n + 1 : 1;
                continue;
            }
            tally.Objects++;
            foreach (PassiveObject o in world.All)
            {
                tally.Reads += o.BoardReads;
                tally.ReadMisses += o.BoardReadMisses;
                tally.Writes += o.BoardWrites;
                tally.WriteMisses += o.BoardWriteMisses;
            }
            tally.Spawned += world.All.Count - 1;
            tally.Refused += world.Refused;
        }
        return tally;
    }

    /// <summary>
    /// 怪物自定义动作的实跑(E-159;汇总数,本机,非 REA):每只写了 [etc action] 的怪物、每一项各放进一个空场地(没有主角与目标;怪物的血量给得很大),
    /// 直接让脚本的 [SET ACTION] [CUSTOM] n 那条路径把它切进去,按 10 毫秒一步最多跑 60 秒,看抛不抛异常、自己结束还是一直放着。
    /// 不是战斗模拟:没有主角,依赖主角的条件都不成立;从出招里切进来的路径(出招 → 自定义动作)由全库扫描覆盖。
    /// </summary>
    /// <summary>
    /// 带 [CASTING] 的招式实跑的统计(E-168):空场地、没有主角与目标,把每只怪物每一条带读条的招式单独放一遍,最多 <see cref="MaxSteps"/> 步(每步 10 毫秒)。
    /// 只数个数与时长档位,不输出路径与怪物编号。
    /// </summary>
    private sealed class LiveCastTally
    {
        public const int MaxSteps = 6000;

        public int Monsters;
        public int NoModel;
        public int Attacks;
        public int NotEntered;
        public int Failed;
        public int Ended;
        public int StillRunning;
        public int Spawned;
        public long Armed;
        public long Fired;
        public int Unfinished;
        public readonly SortedDictionary<string, int> EndedBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  带 [CASTING] 的招式实跑(空场地、没有主角与目标、每招最多 " + MaxSteps / 100 + " 秒;E-168):怪物 " + Monsters + " 只(模型读不出 " + NoModel + " 只),共 " + Attacks
                + " 招(没切进去 " + NotEntered + " 招,抛异常 " + Failed + " 招);自己放完 " + Ended + " 招(" + Join(EndedBy, 6) + "),到时间还在放 " + StillRunning + " 招;放出被动对象 " + Spawned + " 个;读条排上 "
                + Armed + " 次、到期执行 " + Fired + " 次、排上了却没执行的招式 " + Unfinished + " 招(出招先放完,或行为组号越界)"
                + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
        }

        public object ToJson() => new { Monsters, NoModel, Attacks, NotEntered, Failed, Ended, StillRunning, Spawned, Armed, Fired, Unfinished, EndedBy, Errors };
    }

    private static bool HasCastTag(ActProgram program)
    {
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (ActBehavior b in g.Items)
            {
                if (b.Tag == "[CASTING]")
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static LiveCastTally RunLiveCasts(DnfAssets assets, GameData data, ActLibrary library)
    {
        var tally = new LiveCastTally();
        var walk = new RectUnionArea(new List<WorldRect>());
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
            var casting = new List<int>();
            for (int i = 0; i < programs.Length; i++)
            {
                ActProgram? p = library.Program(paths[i]);
                if (p != null && p.Triggers.Count > 0)
                {
                    programs[i] = p;
                    if (HasCastTag(p))
                    {
                        casting.Add(i);
                    }
                }
            }
            if (casting.Count == 0)
            {
                continue;
            }
            tally.Monsters++;
            foreach (int a in casting)
            {
                var actor = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 300, 100, 0, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000000, 222.5, 1500, 0);
                var world = new LiveWorld(library, actor);
                actor.SetActs(programs!, world);
                tally.Attacks++;
                try
                {
                    actor.ActSelf.SetAction(ActActionKind.Attack, a, true, false);
                    if (actor.Phase != MonsterPhase.Attack)
                    {
                        tally.NotEntered++;
                        continue;
                    }
                    int steps = 0;
                    for (; steps < LiveCastTally.MaxSteps; steps++)
                    {
                        actor.Update(10, 0, 0, false, walk);
                        world.Tick(10);
                        if (actor.Phase != MonsterPhase.Attack)
                        {
                            break;
                        }
                    }
                    if (steps < LiveCastTally.MaxSteps)
                    {
                        tally.Ended++;
                        string bucket = EndedBucket(steps + 1);
                        tally.EndedBy[bucket] = tally.EndedBy.TryGetValue(bucket, out int b) ? b + 1 : 1;
                    }
                    else
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
                tally.Spawned += world.All.Count;
                tally.Armed += world.CastsArmed;
                tally.Fired += world.CastsFired;
                tally.Unfinished += world.CastsArmed > world.CastsFired ? 1 : 0;
            }
        }
        return tally;
    }

    /// <summary>
    /// 带 [MOVE TO TARGET] 的招式实跑的统计(E-169):空场地、目标替身站在怪物右边 400 像素的同一条纵深线上,把每只怪物每一条带这个行为的招式单独放一遍,最多 <see cref="MaxSteps"/> 步(每步 10 毫秒)。
    /// 数冲出去与收尾的次数、招式结束时离目标比出发时近了的招式数、位置动过的招式数;只数个数,不输出路径与怪物编号。
    /// </summary>
    private sealed class LiveMoveTally
    {
        public const int MaxSteps = 6000;

        public int Monsters;
        public int NoModel;
        public int Attacks;
        public int NotEntered;
        public int Failed;
        public int Ended;
        public int StillRunning;
        public int Pushed;
        public int Closer;
        public int Farther;
        public int Moved;
        public long Started;
        public long Stopped;
        public int Unfinished;
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  带 [MOVE TO TARGET] 的招式实跑(空场地、目标替身在右边 400 像素、每招最多 " + MaxSteps / 100 + " 秒;E-169):怪物 " + Monsters + " 只(模型读不出 " + NoModel + " 只),共 " + Attacks
                + " 招(没切进去 " + NotEntered + " 招,抛异常 " + Failed + " 招);自己放完 " + Ended + " 招,到时间还在放 " + StillRunning + " 招;真的冲出去的 " + Pushed + " 招;冲出 "
                + Started + " 次、到点收尾 " + Stopped + " 次、冲了却没收尾的招式 " + Unfinished + " 招(出招先放完,或速度被别的行为改过);结束时离目标比出发时近了 " + Closer + " 招、远了 " + Farther
                + " 招,位置动过 " + Moved + " 招" + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
        }

        public object ToJson() => new { Monsters, NoModel, Attacks, NotEntered, Failed, Ended, StillRunning, Pushed, Closer, Farther, Moved, Started, Stopped, Unfinished, Errors };
    }

    private static bool HasMoveTag(ActProgram program)
    {
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (ActBehavior b in g.Items)
            {
                if (b.Kind == ActBehaviorKind.MoveToTarget)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static LiveMoveTally RunLiveMoves(DnfAssets assets, GameData data, ActLibrary library)
    {
        var tally = new LiveMoveTally();
        var walk = new RectUnionArea(new List<WorldRect>());
        var target = new Stand(ActEntityKind.Character, 0, 700f, 100f);
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
            var moving = new List<int>();
            for (int i = 0; i < programs.Length; i++)
            {
                ActProgram? p = library.Program(paths[i]);
                if (p != null && p.Triggers.Count > 0)
                {
                    programs[i] = p;
                    if (HasMoveTag(p))
                    {
                        moving.Add(i);
                    }
                }
            }
            if (moving.Count == 0)
            {
                continue;
            }
            tally.Monsters++;
            foreach (int a in moving)
            {
                var actor = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 300, 100, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000000, 222.5, 1500, 0);
                var world = new LiveWorld(library, actor);
                actor.ActTarget = target;
                world.Heroes.Add(target);
                actor.SetActs(programs!, world);
                tally.Attacks++;
                try
                {
                    double startX = actor.X;
                    double startY = actor.Y;
                    actor.ActSelf.SetAction(ActActionKind.Attack, a, true, false);
                    if (actor.Phase != MonsterPhase.Attack)
                    {
                        tally.NotEntered++;
                        continue;
                    }
                    int steps = 0;
                    for (; steps < LiveMoveTally.MaxSteps; steps++)
                    {
                        actor.Update(10, target.X, target.Y, true, walk);
                        world.Tick(10);
                        if (actor.Phase != MonsterPhase.Attack)
                        {
                            break;
                        }
                    }
                    if (steps < LiveMoveTally.MaxSteps)
                    {
                        tally.Ended++;
                    }
                    else
                    {
                        tally.StillRunning++;
                    }
                    double before = Math.Abs(target.X - startX) + Math.Abs(target.Y - startY);
                    double after = Math.Abs(target.X - actor.X) + Math.Abs(target.Y - actor.Y);
                    tally.Closer += after < before - 1.0 ? 1 : 0;
                    tally.Farther += after > before + 1.0 ? 1 : 0;
                    tally.Moved += Math.Abs(actor.X - startX) + Math.Abs(actor.Y - startY) > 1.0 ? 1 : 0;
                }
                catch (Exception e)
                {
                    tally.Failed++;
                    tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int c) ? c + 1 : 1;
                    continue;
                }
                tally.Pushed += world.MovesStarted > 0 ? 1 : 0;
                tally.Started += world.MovesStarted;
                tally.Stopped += world.MovesStopped;
                tally.Unfinished += world.MovesStarted > world.MovesStopped ? 1 : 0;
            }
        }
        return tally;
    }

    /// <summary>程序里有没有 [SET ACTION] [STAND](怪物出招最常见的收尾写法)。</summary>
    private static bool HasStandEnd(ActProgram program)
    {
        foreach (ActBehaviorGroup group in program.Behaviors)
        {
            foreach (ActBehavior b in group.Items)
            {
                if (b.Kind == ActBehaviorKind.SetAction && b.Action == ActActionKind.Stand)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>行为组里有没有能离开出招的 [SET ACTION](站立、缺省、出招、自定义四族,与 <see cref="ActProgram.CanEndAction"/> 同一个口径)。</summary>
    private static bool GroupEndsAction(ActBehaviorGroup group)
    {
        foreach (ActBehavior b in group.Items)
        {
            if (b.Kind == ActBehaviorKind.SetAction
                && (b.Action == ActActionKind.Stand || b.Action == ActActionKind.Default || b.Action == ActActionKind.Attack || b.Action == ActActionKind.Custom))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 程序里"会执行能离开出招的行为组"的那些触发器,各条记录的原标签(去重;[DO BEHAVIOR] 本身不算):
    /// 回答"这一招靠什么条件收尾"。只数标签,不输出路径。
    /// </summary>
    private static HashSet<string> EndingTags(ActProgram program)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (ActTrigger t in program.Triggers)
        {
            bool ends = false;
            foreach (ActRecord r in t.Records)
            {
                if ((r.Kind == ActRecordKind.DoBehavior || r.Kind == ActRecordKind.DirectDoBehavior || r.Kind == ActRecordKind.DelayDoBehavior) && r.Behavior != null)
                {
                    foreach (int k in r.Behavior.Candidates())
                    {
                        if (k >= 0 && k < program.Behaviors.Count && GroupEndsAction(program.Behaviors[k]))
                        {
                            ends = true;
                        }
                    }
                }
            }
            if (!ends)
            {
                continue;
            }
            foreach (ActRecord r in t.Records)
            {
                if (r.Kind != ActRecordKind.DoBehavior && r.Kind != ActRecordKind.DirectDoBehavior && r.Kind != ActRecordKind.DelayDoBehavior)
                {
                    tags.Add(r.Tag ?? r.Kind.ToString());
                }
            }
        }
        return tags;
    }

    /// <summary>
    /// 循环动画的怪物出招(M70b,E-170)的普查与实跑。普查数出招槽的构成:动画带循环属性的有多少、其中带程序的、程序里能把出招收尾的
    /// (<see cref="ActProgram.CanEndAction"/>)、写了 [SET ACTION] [STAND] 的,与不循环的出招对比;实跑把"带循环动画、程序能收尾"的招式
    /// (出招时长按脚本收尾、不再播一遍就截断的那一类)单独放在空场地里(目标替身站在怪物右边 400 像素的同一条纵深线上)最多
    /// <see cref="MaxSteps"/> 步,数自己结束的与到时间还在放的(挂住的风险);只数个数,不输出路径与怪物编号。
    /// </summary>
    private sealed class LoopTally
    {
        public const int MaxSteps = 6000;

        public int Monsters;
        public int NoModel;
        public int Slots;
        public int LoopSlots;
        public int Programs;
        public int LoopPrograms;
        public int LoopCanEnd;
        public int LoopStandEnd;
        public int LoopNoEnd;
        public int LoopNoProgram;
        public int PlainPrograms;
        public int PlainStandEnd;
        public int Live;
        public int NotEntered;
        public int Failed;
        public int Ended;
        public int StillRunning;
        public readonly SortedDictionary<string, int> EndedBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> StillTags = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  循环动画的怪物出招(E-170)普查:出招槽 " + Slots + " 个(带程序 " + Programs + " 个);动画带循环属性的 " + LoopSlots + " 个("
                + Monsters + " 只怪物),其中带程序 " + LoopPrograms + " 个、没有程序 " + LoopNoProgram + " 个;带程序的里能由脚本收尾(有能离开出招的 [SET ACTION])的 "
                + LoopCanEnd + " 个、没有收尾路径的 " + LoopNoEnd + " 个;写了 [SET ACTION] [STAND] 的:循环 " + LoopStandEnd + "/" + LoopPrograms
                + "、不循环 " + PlainStandEnd + "/" + PlainPrograms + "(模型读不出 " + NoModel + " 只)");
            Console.WriteLine("  循环出招实跑(空场地、目标替身在右边 400 像素、每招最多 " + MaxSteps / 100 + " 秒;E-170):带循环动画而且脚本能收尾的 " + Live
                + " 招(没切进去 " + NotEntered + " 招,抛异常 " + Failed + " 招);自己结束 " + Ended + " 招(" + Join(EndedBy, 4) + "),到时间还在放 " + StillRunning + " 招"
                + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
            if (StillTags.Count > 0)
            {
                Console.WriteLine("    到时间还在放的招,收尾触发器里出现的条件标签(按招数,一招里同一标签只算一次):" + Join(StillTags, 12));
            }
        }

        public object ToJson() => new
        {
            Monsters, NoModel, Slots, LoopSlots, Programs, LoopPrograms, LoopCanEnd, LoopStandEnd, LoopNoEnd, LoopNoProgram, PlainPrograms, PlainStandEnd,
            Live, NotEntered, Failed, Ended, StillRunning, EndedBy, StillTags, Errors,
        };
    }

    private static LoopTally RunLoopCensus(DnfAssets assets, GameData data, ActLibrary library)
    {
        var tally = new LoopTally();
        var walk = new RectUnionArea(new List<WorldRect>());
        var target = new Stand(ActEntityKind.Character, 0, 700f, 100f);
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
            for (int i = 0; i < programs.Length; i++)
            {
                ActProgram? p = library.Program(paths[i]);
                if (p != null && p.Triggers.Count > 0)
                {
                    programs[i] = p;
                }
            }
            var live = new List<int>();
            bool anyLoop = false;
            for (int i = 0; i < model.Attacks.Count; i++)
            {
                AniFile? ani = model.Attacks[i];
                if (ani == null)
                {
                    continue;
                }
                tally.Slots++;
                ActProgram? program = i < programs.Length ? programs[i] : null;
                tally.Programs += program != null ? 1 : 0;
                if (!ani.Loops)
                {
                    if (program != null)
                    {
                        tally.PlainPrograms++;
                        tally.PlainStandEnd += HasStandEnd(program) ? 1 : 0;
                    }
                    continue;
                }
                anyLoop = true;
                tally.LoopSlots++;
                if (program == null)
                {
                    tally.LoopNoProgram++;
                    continue;
                }
                tally.LoopPrograms++;
                tally.LoopStandEnd += HasStandEnd(program) ? 1 : 0;
                if (program.CanEndAction)
                {
                    tally.LoopCanEnd++;
                    live.Add(i);
                }
                else
                {
                    tally.LoopNoEnd++;
                }
            }
            tally.Monsters += anyLoop ? 1 : 0;
            foreach (int a in live)
            {
                var actor = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 300, 100, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000000, 222.5, 1500, 0);
                var world = new LiveWorld(library, actor);
                actor.ActTarget = target;
                world.Heroes.Add(target);
                actor.SetActs(programs!, world);
                tally.Live++;
                try
                {
                    actor.ActSelf.SetAction(ActActionKind.Attack, a, true, false);
                    if (actor.Phase != MonsterPhase.Attack)
                    {
                        tally.NotEntered++;
                        continue;
                    }
                    int steps = 0;
                    for (; steps < LoopTally.MaxSteps; steps++)
                    {
                        actor.Update(10, target.X, target.Y, true, walk);
                        world.Tick(10);
                        if (actor.Phase != MonsterPhase.Attack)
                        {
                            break;
                        }
                    }
                    if (steps < LoopTally.MaxSteps)
                    {
                        tally.Ended++;
                        string bucket = EndedBucket(steps + 1);
                        tally.EndedBy[bucket] = tally.EndedBy.TryGetValue(bucket, out int b) ? b + 1 : 1;
                    }
                    else
                    {
                        tally.StillRunning++;
                        foreach (string tag in EndingTags(programs[a]!))
                        {
                            tally.StillTags[tag] = tally.StillTags.TryGetValue(tag, out int t) ? t + 1 : 1;
                        }
                    }
                }
                catch (Exception e)
                {
                    tally.Failed++;
                    tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int c) ? c + 1 : 1;
                }
            }
        }
        return tally;
    }

    private sealed class LiveCustomTally
    {
        public const int MaxSteps = 6000;

        public int Monsters;
        public int NoModel;
        public int Entries;
        public int NotEntered;
        public int Failed;
        public int Ended;
        public int StillRunning;
        public int StillLooping;
        public int StillSingle;
        public int StillSingleLooping;
        public int Started;
        public int Finished;
        public int Rejected;
        public int Spawned;
        public int Refused;
        public int Summons;
        public readonly SortedDictionary<string, int> EndedBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Cycles = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  实跑(空场地、没有主角与目标、每项最多 " + MaxSteps / 100 + " 秒;E-159):怪物 " + Monsters + " 只(模型读不出 " + NoModel + " 只),共 " + Entries + " 项(没切进去 "
                + NotEntered + " 项,抛异常 " + Failed + " 项);自己结束 " + Ended + " 项(" + Join(EndedBy, 6) + "),到时间还在放 " + StillRunning + " 项(其中循环动画 " + StillLooping + ",只切进过一次的 " + StillSingle + "、其中循环动画 " + StillSingleLooping
                + ");切进 " + Started + " 次、放完 " + Finished + " 次、落空 " + Rejected + " 次;放出被动对象 " + Spawned + " 个(上限拒绝 " + Refused + " 次),召唤请求 " + Summons + " 次"
                + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
            if (Cycles.Count > 0)
            {
                Console.WriteLine("    其中反复切进的(平均一轮多久):" + Join(Cycles, 6));
            }
        }

        public object ToJson() => new { Monsters, NoModel, Entries, NotEntered, Failed, Ended, StillRunning, StillLooping, StillSingle, StillSingleLooping, Started, Finished, Rejected, Spawned, Refused, Summons, EndedBy, Cycles, Errors };
    }

    private static string EndedBucket(int steps)
    {
        int ms = steps * 10;
        return ms < 1000 ? "1 秒内" : ms < 5000 ? "1 到 5 秒" : ms < 20000 ? "5 到 20 秒" : "20 秒以上";
    }

    private static LiveCustomTally RunLiveCustoms(DnfAssets assets, GameData data, ActLibrary library)
    {
        var tally = new LiveCustomTally();
        var walk = new RectUnionArea(new List<WorldRect>());
        foreach (int id in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(id);
            if (def == null || def.EtcActions.Length == 0)
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
            tally.Monsters++;
            for (int n = 0; n < def.EtcActions.Length; n++)
            {
                var actor = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 300, 100, 0, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000000, 222.5, 1500, 0);
                var world = new LiveWorld(library, actor);
                actor.SetFamilyActs(MonsterFamilyActs.Build(library, model.FamilyActs), world);
                if (!actor.CustomExists(n))
                {
                    continue;
                }
                tally.Entries++;
                try
                {
                    actor.ActSelf.SetAction(ActActionKind.Custom, n, true, false);
                    if (!actor.InCustomAction)
                    {
                        tally.NotEntered++;
                        continue;
                    }
                    int steps = 0;
                    for (; steps < LiveCustomTally.MaxSteps; steps++)
                    {
                        actor.Update(10, 0, 0, false, walk);
                        world.Tick(10);
                        if (!actor.InCustomAction)
                        {
                            break;
                        }
                    }
                    if (steps < LiveCustomTally.MaxSteps)
                    {
                        tally.Ended++;
                        string bucket = EndedBucket(steps + 1);
                        tally.EndedBy[bucket] = tally.EndedBy.TryGetValue(bucket, out int b) ? b + 1 : 1;
                    }
                    else
                    {
                        tally.StillRunning++;
                        AniFile? ani = actor.CustomAni(actor.CurrentCustomIndex);
                        bool looping = ani != null && ani.Loops;
                        if (looping)
                        {
                            tally.StillLooping++;
                        }
                        if (actor.CustomActionsStarted == 1)
                        {
                            tally.StillSingle++;
                            if (looping)
                            {
                                tally.StillSingleLooping++;
                            }
                        }
                        else
                        {
                            int cycle = LiveCustomTally.MaxSteps * 10 / Math.Max(1, actor.CustomActionsStarted);
                            string key = cycle < 20 ? "不足 20 毫秒" : cycle < 100 ? "20 到 100 毫秒" : cycle < 500 ? "100 到 500 毫秒" : "500 毫秒以上";
                            tally.Cycles[key] = tally.Cycles.TryGetValue(key, out int k) ? k + 1 : 1;
                        }
                    }
                }
                catch (Exception e)
                {
                    tally.Failed++;
                    tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int c) ? c + 1 : 1;
                    continue;
                }
                tally.Started += actor.CustomActionsStarted;
                tally.Finished += actor.CustomActionsFinished;
                tally.Rejected += actor.CustomActionsRejected;
                tally.Spawned += world.All.Count;
                tally.Refused += world.Refused;
                tally.Summons += world.Summons;
            }
        }
        return tally;
    }

    /// <summary>
    /// 被动对象各动作的触发器开关板(E-158)按对象的汇总:对象每个动作(基础动作与 [etc action] 各项)的 .act 里,读别的动作开关的 [TRIGGER CHECK] 与写别的动作开关的 [TRIGGER]
    /// (不写族的是自己这一动作,不算)。被动对象只有 [CUSTOM](.obj 的 [etc action] 第 a 项)与 [DEFAULT](基础动作)两种池;要有答案还得那个动作的 .act 读得到、装了触发器、序号在范围内,
    /// 其余落空(读侧对象被剔除、写侧跳过)。
    /// </summary>
    private sealed class BoardTally
    {
        public int Objects;
        public int Reads;
        public int Writes;
        public readonly SortedSet<int> Ids = new SortedSet<int>();
        public readonly SortedDictionary<string, int> ReadByOutcome = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> WriteByOutcome = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> ByPool = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print(string title)
        {
            Console.WriteLine("  " + title + ":带跨动作开关读写的对象 " + Objects + " 个;读 [TRIGGER CHECK] " + Reads + " 条(" + Join(ReadByOutcome, 8) + "),写 [TRIGGER] " + Writes
                + " 条(" + Join(WriteByOutcome, 8) + ");按池 " + Join(ByPool, 10));
        }

        public object ToJson() => new { Objects, Reads, Writes, ReadByOutcome, WriteByOutcome, ByPool };
    }

    private static void CountBoards(int id, Dnf.Game.PassiveObjectDef def, Func<string?, ActProgram?> program, BoardTally tally)
    {
        ActProgram? basic = program(def.BasicActionPath);
        var etc = new ActProgram?[def.EtcActionPaths.Length];
        var sources = new List<ActProgram>();
        if (basic != null)
        {
            sources.Add(basic);
        }
        for (int i = 0; i < etc.Length; i++)
        {
            etc[i] = program(def.EtcActionPaths[i]);
            if (etc[i] != null)
            {
                sources.Add(etc[i]!);
            }
        }

        string Outcome(ActTriggerPool pool, int slot, int index)
        {
            ActProgram? target;
            switch (pool)
            {
                case ActTriggerPool.Custom:
                    target = (uint)slot < (uint)etc.Length ? etc[slot] : null;
                    break;
                case ActTriggerPool.Default:
                    target = basic;
                    break;
                default:
                    return "没有这一族的池";
            }
            if (target == null)
            {
                return "没有这个动作";
            }
            if (target.Triggers.Count == 0)
            {
                return "动作没装触发器";
            }
            return (uint)index < (uint)target.Triggers.Count ? "有答案" : "序号越界";
        }

        void Add(SortedDictionary<string, int> map, string key) => map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;

        bool any = false;
        foreach (ActProgram source in sources)
        {
            foreach (ActTrigger t in source.Triggers)
            {
                foreach (ActRecord rec in t.Records)
                {
                    if (rec.Kind != ActRecordKind.TriggerCheck || rec.TriggerPool == ActTriggerPool.Own)
                    {
                        continue;
                    }
                    any = true;
                    tally.Reads++;
                    Add(tally.ReadByOutcome, Outcome(rec.TriggerPool, rec.TriggerSlot, rec.Number));
                    Add(tally.ByPool, "读 " + rec.TriggerPool);
                }
            }
            foreach (ActBehaviorGroup g in source.Behaviors)
            {
                foreach (ActBehavior b in g.Items)
                {
                    if (b.Kind != ActBehaviorKind.TriggerSwitch || b.TriggerPool == ActTriggerPool.Own)
                    {
                        continue;
                    }
                    any = true;
                    tally.Writes++;
                    Add(tally.WriteByOutcome, Outcome(b.TriggerPool, b.TriggerSlot, b.TriggerIndex));
                    Add(tally.ByPool, "写 " + b.TriggerPool);
                }
            }
        }
        tally.Objects += any ? 1 : 0;
        if (any)
        {
            tally.Ids.Add(id);
        }
    }

    /// <summary>
    /// 非出招动作族宿主(E-146)按"怪物"的汇总:一只怪物所有带触发器的 .act(出招与各族)里,指向站立、行走、坐下、倒地、过程这几族的 [TRIGGER] 开关,
    /// 目标族的 .act(每族取 .mob 里写的第一个)存在、触发器序号在范围内算落到宿主上,否则落空(客户端:池为空或个数不大于序号就跳过);[DEFAULT]、[CUSTOM] 还没有宿主。
    /// </summary>
    private sealed class FamilySwitchTally
    {
        public int FamilyMonsters;
        public int WithSwitches;
        public int Switches;
        public int Resolved;
        public int Missed;
        public int MissNoAct;
        public int MissNoTriggers;
        public int MissOutOfRange;
        public int Unhosted;
        public readonly SortedDictionary<string, int> ByPool = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  非出招动作族宿主(E-146,按怪物):带触发器的非出招族 .act 的怪物 " + FamilyMonsters + " 个;含指向这些族的 [TRIGGER] 开关的怪物 " + WithSwitches + " 个,开关 " + Switches
                + " 条(落到宿主 " + Resolved + " 条,落空 " + Missed + " 条[那一族没写 .act " + MissNoAct + "、.act 没有触发器 " + MissNoTriggers + "、序号越界 " + MissOutOfRange + "],指向没有宿主的族 " + Unhosted + " 条;按池 " + Join(ByPool, 10) + ")");
        }

        public object ToJson() => new { FamilyMonsters, WithSwitches, Switches, Resolved, Missed, MissNoAct, MissNoTriggers, MissOutOfRange, Unhosted, ByPool };
    }

    private static void CountFamilySwitches(MonsterDef def, ScriptLine[] doc, Func<string?, ActProgram?> program, FamilySwitchTally tally)
    {
        var pools = new Dictionary<ActTriggerPool, ActProgram?>();
        var sources = new List<ActProgram>();
        bool anyFamily = false;
        foreach (ScriptLine line in doc)
        {
            ActTriggerPool? pool = null;
            if (line.TagIs("[waiting action]"))
            {
                pool = ActTriggerPool.Stay;
            }
            else if (line.TagIs("[move action]"))
            {
                pool = ActTriggerPool.Move;
            }
            else if (line.TagIs("[sit action]"))
            {
                pool = ActTriggerPool.Sit;
            }
            else if (line.TagIs("[down action]"))
            {
                pool = ActTriggerPool.Down;
            }
            else if (line.TagIs("[proc action]"))
            {
                pool = ActTriggerPool.Proc;
            }
            else if (!line.TagIs("[damage action 1]") && !line.TagIs("[damage action 2]") && !line.TagIs("[overturn action]") && !line.TagIs("[last action]"))
            {
                continue;
            }
            string? path = null;
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text) && v.Text.EndsWith(".act", StringComparison.OrdinalIgnoreCase))
                {
                    path = PvfPath.ResolveRelativeTo(def.Path, v.Text);
                    break;
                }
            }
            ActProgram? p = program(path);
            if (p == null)
            {
                continue;
            }
            if (pool != null && !pools.ContainsKey(pool.Value))
            {
                pools[pool.Value] = p;
            }
            if (p.Triggers.Count > 0)
            {
                anyFamily = true;
                if (!sources.Contains(p))
                {
                    sources.Add(p);
                }
            }
        }
        foreach (string raw in def.AttackList)
        {
            ActProgram? p = string.IsNullOrEmpty(raw) ? null : program(PvfPath.ResolveRelativeTo(def.Path, raw));
            if (p != null && p.Triggers.Count > 0 && !sources.Contains(p))
            {
                sources.Add(p);
            }
        }
        if (anyFamily)
        {
            tally.FamilyMonsters++;
        }
        bool any = false;
        foreach (ActProgram source in sources)
        {
            foreach (ActBehaviorGroup g in source.Behaviors)
            {
                foreach (ActBehavior b in g.Items)
                {
                    if (b.Kind != ActBehaviorKind.TriggerSwitch || b.TriggerPool == ActTriggerPool.Own || b.TriggerPool == ActTriggerPool.Attack)
                    {
                        continue;
                    }
                    any = true;
                    tally.Switches++;
                    string name = b.TriggerPool.ToString();
                    tally.ByPool[name] = tally.ByPool.TryGetValue(name, out int n) ? n + 1 : 1;
                    if (b.TriggerPool == ActTriggerPool.Default || b.TriggerPool == ActTriggerPool.Custom)
                    {
                        tally.Unhosted++;
                    }
                    else if (pools.TryGetValue(b.TriggerPool, out ActProgram? target) && target != null && (uint)b.TriggerIndex < (uint)target.Triggers.Count)
                    {
                        tally.Resolved++;
                    }
                    else
                    {
                        tally.Missed++;
                        if (target == null)
                        {
                            tally.MissNoAct++;
                        }
                        else if (target.Triggers.Count == 0)
                        {
                            tally.MissNoTriggers++;
                        }
                        else
                        {
                            tally.MissOutOfRange++;
                        }
                    }
                }
            }
        }
        tally.WithSwitches += any ? 1 : 0;
    }

    /// <summary>
    /// 换动作文件 [CHANGE ACTION FILE](M57,E-151)按"怪物"的汇总:一只怪物的出招 .act 与有宿主的几族 .act(待机、行走、坐下、受击、倒地、翻倒、最后一幕、过程)
    /// 加上顺着换文件能到的 .act(换进来的文件里还能再换)里的记录。落到宿主上的:受击序号小于 2、出招序号小于招式个数、族在宿主表里;
    /// 越界的客户端同样跳过;后退、冲刺、冲刺攻击、跳跃、跳击、自定义这一版怪物没有对应动作,只记数。
    /// </summary>
    private sealed class ActionFileTally
    {
        public int Monsters;
        public int Records;
        public int Unreadable;
        public int Resolved;
        public int Missing;
        public int NoAnimation;
        public int IndexRejected;
        public int Unhosted;
        public int NestedPrograms;
        public readonly SortedDictionary<string, int> BySlot = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly HashSet<string> Writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Print()
        {
            Console.WriteLine("  换动作文件 [CHANGE ACTION FILE](E-151,按怪物):含换文件记录的怪物 " + Monsters + " 个,记录 " + Records + " 条(写的 .act " + Writers.Count + " 个,换进来的 .act "
                + Targets.Count + " 个;按族:" + Join(BySlot, 16) + ");换进来的文件读得到 " + Resolved + " 条、读不到 " + Missing + " 条(" + Percent(Resolved, Records) + "),其中没有主动画 "
                + NoAnimation + " 条;序号越界 " + IndexRejected + " 条,没有对应动作的族 " + Unhosted + " 条;换进来的文件里还有换文件记录的 " + NestedPrograms + " 个;写法读不通 " + Unreadable + " 条");
        }

        public object ToJson() => new
        {
            Monsters, Records, Writers = Writers.Count, Targets = Targets.Count, Resolved, Missing, NoAnimation, IndexRejected, Unhosted, NestedPrograms, Unreadable, BySlot,
        };
    }

    private static readonly string[] HostedFamilyTags =
    {
        "[waiting action]", "[move action]", "[sit action]", "[damage action 1]", "[damage action 2]", "[down action]", "[overturn action]", "[last action]", "[proc action]",
    };

    private static void CountActionFileSwaps(MonsterDef def, ScriptLine[] doc, Func<string?, ActProgram?> program, ActionFileTally tally)
    {
        var seen = new HashSet<ActProgram>();
        var targets = new HashSet<ActProgram>();
        var work = new Queue<ActProgram>();

        void Add(ActProgram? p)
        {
            if (p != null && seen.Add(p))
            {
                work.Enqueue(p);
            }
        }

        foreach (string raw in def.AttackList)
        {
            Add(string.IsNullOrEmpty(raw) ? null : program(PvfPath.ResolveRelativeTo(def.Path, raw)));
        }
        foreach (ScriptLine line in doc)
        {
            bool family = false;
            foreach (string tag in HostedFamilyTags)
            {
                family |= line.TagIs(tag);
            }
            if (!family)
            {
                continue;
            }
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v.Text) && v.Text.EndsWith(".act", StringComparison.OrdinalIgnoreCase))
                {
                    Add(program(PvfPath.ResolveRelativeTo(def.Path, v.Text)));
                }
            }
        }
        bool any = false;
        while (work.Count > 0)
        {
            ActProgram p = work.Dequeue();
            bool writes = false;
            foreach (ActBehaviorGroup g in p.Behaviors)
            {
                foreach (ActBehavior b in g.Items)
                {
                    if (b.Kind == ActBehaviorKind.Unsupported && b.Tag == "[CHANGE ACTION FILE]")
                    {
                        any = true;
                        tally.Unreadable++;
                        continue;
                    }
                    if (b.Kind != ActBehaviorKind.ChangeActionFile)
                    {
                        continue;
                    }
                    any = true;
                    writes = true;
                    tally.Records++;
                    string slot = b.FileSlot.ToString();
                    tally.BySlot[slot] = tally.BySlot.TryGetValue(slot, out int n) ? n + 1 : 1;
                    if (b.FileSlot == ActFileSlot.Damage && (uint)b.FileIndex >= 2u)
                    {
                        tally.IndexRejected++;
                    }
                    else if (b.FileSlot == ActFileSlot.Attack && (uint)b.FileIndex >= (uint)def.AttackList.Length)
                    {
                        tally.IndexRejected++;
                    }
                    else if (b.FileSlot == ActFileSlot.MoveBack || b.FileSlot == ActFileSlot.Dash || b.FileSlot == ActFileSlot.DashAttack || b.FileSlot == ActFileSlot.Jump
                        || b.FileSlot == ActFileSlot.JumpAttack || b.FileSlot == ActFileSlot.Custom)
                    {
                        tally.Unhosted++;
                    }
                    ActProgram? target = program(PvfPath.ResolveRelativeTo(p.Path, b.FilePath ?? string.Empty));
                    if (target == null)
                    {
                        tally.Missing++;
                        continue;
                    }
                    tally.Resolved++;
                    tally.Targets.Add(target.Path);
                    if (target.BaseAniPath == null)
                    {
                        tally.NoAnimation++;
                    }
                    targets.Add(target);
                    Add(target);
                }
            }
            if (writes)
            {
                tally.Writers.Add(p.Path);
                if (targets.Contains(p))
                {
                    tally.NestedPrograms++;
                }
            }
        }
        if (any)
        {
            tally.Monsters++;
        }
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

    /// <summary>把一个怪物的一份 .act 里读不懂的触发器标签与行为标签记到"这个标签波及哪些怪物"表里(按怪物去重)。</summary>
    private static void NoteMonsterTags(ActProgram program, int monster, Dictionary<string, HashSet<int>> triggerTags, Dictionary<string, HashSet<int>> behaviorTags)
    {
        foreach (ActTrigger t in program.Triggers)
        {
            if (t.Supported)
            {
                continue;
            }
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
                MarkMonster(triggerTags, tag, monster);
            }
        }
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (string tag in new SortedSet<string>(g.UnsupportedTags, StringComparer.Ordinal))
            {
                MarkMonster(behaviorTags, tag, monster);
            }
        }
    }

    private static void MarkMonster(Dictionary<string, HashSet<int>> map, string tag, int monster)
    {
        if (!map.TryGetValue(tag, out HashSet<int>? set))
        {
            set = new HashSet<int>();
            map[tag] = set;
        }
        set.Add(monster);
    }

    private static string JoinSets(Dictionary<string, HashSet<int>> map, int limit)
    {
        if (map.Count == 0)
        {
            return "无";
        }
        var items = new List<KeyValuePair<string, int>>();
        foreach (KeyValuePair<string, HashSet<int>> kv in map)
        {
            items.Add(new KeyValuePair<string, int>(kv.Key, kv.Value.Count));
        }
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
        var crossSwitch = new CrossSwitchTally();
        var monsterTriggerTags = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var monsterBehaviorTags = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
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
            var slots = new ActProgram?[list.Length];
            for (int slotIndex = 0; slotIndex < list.Length; slotIndex++)
            {
                string raw = list[slotIndex];
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
                slots[slotIndex] = program;
                anyFile = true;
                attackActs[path] = program;
                NoteMonsterTags(program, id, monsterTriggerTags, monsterBehaviorTags);
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
            CountCrossSlotSwitches(slots, crossSwitch);
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
        crossSwitch.Print();

        // ---- 第一部分之二:怪物其余动作(待机、行走、受击、倒地、etc、proc、死亡……)的 .act;怪物脚本里"待机里藏着整套首领流程"的就在这里
        var otherKinds = new (string Title, string[] Tags)[]
        {
            ("待机 [waiting action]", new[] { "[waiting action]" }),
            ("行走 [move action]", new[] { "[move action]" }),
            ("受击 [damage action 1/2]", new[] { "[damage action 1]", "[damage action 2]" }),
            ("倒地 [down action] [overturn action]", new[] { "[down action]", "[overturn action]" }),
            ("其它动作 [etc action]", new[] { "[etc action]" }),
            ("过程 [proc action]", new[] { "[proc action]" }),
            ("死亡 [last action]", new[] { "[last action]" }),
            ("坐 [sit action]", new[] { "[sit action]" }),
        };
        var familySwitch = new FamilySwitchTally();
        var fileSwap = new ActionFileTally();
        var otherTallies = new Tally[otherKinds.Length];
        var otherPrograms = new Dictionary<string, ActProgram>[otherKinds.Length];
        var otherMonsters = new int[otherKinds.Length];
        for (int k = 0; k < otherKinds.Length; k++)
        {
            otherTallies[k] = new Tally();
            otherPrograms[k] = new Dictionary<string, ActProgram>(StringComparer.OrdinalIgnoreCase);
        }
        foreach (int id in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(id);
            ScriptLine[]? doc = def == null ? null : data.ReadScript(def.Path);
            if (def == null || doc == null)
            {
                continue;
            }
            CountFamilySwitches(def, doc, Program, familySwitch);
            CountActionFileSwaps(def, doc, Program, fileSwap);
            for (int k = 0; k < otherKinds.Length; k++)
            {
                bool any = false;
                foreach (ScriptLine line in doc)
                {
                    bool match = false;
                    foreach (string tag in otherKinds[k].Tags)
                    {
                        match |= line.TagIs(tag);
                    }
                    if (!match)
                    {
                        continue;
                    }
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind != ScriptValueKind.String || string.IsNullOrEmpty(v.Text) || !v.Text.EndsWith(".act", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        string path = PvfPath.ResolveRelativeTo(def.Path, v.Text);
                        ActProgram? program = Program(path);
                        if (program != null)
                        {
                            otherPrograms[k][path] = program;
                            NoteMonsterTags(program, id, monsterTriggerTags, monsterBehaviorTags);
                            bool hasTriggers = program.Triggers.Count > 0;
                            any |= hasTriggers;
                        }
                    }
                }
                otherMonsters[k] += any ? 1 : 0;
            }
        }
        for (int k = 0; k < otherKinds.Length; k++)
        {
            foreach (ActProgram p in otherPrograms[k].Values)
            {
                otherTallies[k].Add(p);
            }
            Console.WriteLine();
            Console.WriteLine("其余动作 · " + otherKinds[k].Title + ":带触发器的怪物 " + otherMonsters[k] + " 个");
            otherTallies[k].Print("  这类 .act");
        }
        Console.WriteLine();
        Console.WriteLine("读不懂的标签波及的怪物数(出招与其余动作合计,同一怪物只算一次):");
        Console.WriteLine("  触发器标签:" + JoinSets(monsterTriggerTags, 30));
        Console.WriteLine("  行为标签:" + JoinSets(monsterBehaviorTags, 45));
        Console.WriteLine();
        familySwitch.Print();
        fileSwap.Print();

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
        var boardsAll = new BoardTally();
        var boardsReached = new BoardTally();
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
            CountBoards(id, def, Program, boardsAll);
            if (reached.Contains(id))
            {
                CountBoards(id, def, Program, boardsReached);
            }
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
        Console.WriteLine("被动对象各动作的触发器开关板(E-158,按对象):");
        boardsReached.Print("怪物出招能放出的");
        boardsAll.Print("全表");

        // ---- 检查
        var library = new ActLibrary(assets, data);
        LiveBoardTally liveBoards = RunLiveBoards(assets, data, library, boardsAll.Ids);
        Console.WriteLine();
        liveBoards.Print();
        var homing = new HomingTally();
        foreach (int id in allIds)
        {
            CountHoming(data.GetPassiveObject(id), reached.Contains(id), homing);
        }
        RunLiveHoming(homing, assets, data, library, allIds);
        Console.WriteLine();
        homing.Print();
        CustomTally customs = CountCustoms(data, library, Program);
        Console.WriteLine();
        customs.Print();
        LiveCustomTally liveCustoms = RunLiveCustoms(assets, data, library);
        liveCustoms.Print();
        LiveCastTally liveCasts = RunLiveCasts(assets, data, library);
        liveCasts.Print();
        LiveMoveTally liveMoves = RunLiveMoves(assets, data, library);
        liveMoves.Print();
        LoopTally loops = RunLoopCensus(assets, data, library);
        loops.Print();
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
        int statusOff = monsterTally.ActiveStatusSpill + objectTally.ActiveStatusSpill + monsterTally.ActiveStatusMissing + objectTally.ActiveStatusMissing;
        Report(monsterTally.ActiveStatusBroken + objectTally.ActiveStatusBroken == 0 && statusOff == 0,
            "每个 [ACTIVE STATUS] 都读得出种类,整数个数与 .atk 状态槽的读法一致(读不通 " + (monsterTally.ActiveStatusBroken + objectTally.ActiveStatusBroken)
            + " 条,多写或少写的整数 " + statusOff + " 个)");
        Report(monsterTally.Triggers > 0 && monsterTally.Supported * 100 >= monsterTally.Triggers * 95,
            "怪物出招里运行器认得的触发器占比不低于 95%(" + Percent(monsterTally.Supported, monsterTally.Triggers) + ")");
        Report(objectTally.Triggers > 0 && objectTally.Supported * 100 >= objectTally.Triggers * 90,
            "被动对象自己的 .act 里运行器认得的触发器占比不低于 90%(" + Percent(objectTally.Supported, objectTally.Triggers) + ")");
        Report(reached.Count > 0 && missingObjects * 50 <= reached.Count, "怪物出招能放出的被动对象的 .obj 读不到的不超过 2%(" + missingObjects + "/" + reached.Count + ")");
        Report(reached.Count > 0 && templates * 100 >= reached.Count * 95, "能放出的被动对象里模板(基础动画读得到)配得出的不低于 95%(" + templates + "/" + reached.Count + ")");
        Report(withRunnable > 0 && withSpawn > 0, "有怪物带运行器认得的触发器,也有怪物的招式会放被动对象");
        Report(fileSwap.Records > 0 && fileSwap.Resolved * 100 >= fileSwap.Records * 95,
            "[CHANGE ACTION FILE] 换进来的 .act 读得到的不低于 95%(" + fileSwap.Resolved + "/" + fileSwap.Records + ")");
        int boardAll = boardsAll.Reads + boardsAll.Writes;
        int boardHit = (boardsAll.ReadByOutcome.TryGetValue("有答案", out int boardReadHit) ? boardReadHit : 0)
            + (boardsAll.WriteByOutcome.TryGetValue("有答案", out int boardWriteHit) ? boardWriteHit : 0);
        Report(liveBoards.Objects > 0 && liveBoards.Failed == 0, "被动对象的开关板实跑(E-158):有跨动作开关读写的对象各跑 10 秒都不抛异常(" + liveBoards.Objects + " 个跑完,抛异常 " + liveBoards.Failed + " 个)");
        Report(homing.Used > 0 && homing.Failed == 0 && homing.NonFinite == 0 && homing.Live + homing.NoTemplate == homing.Used,
            "被动对象追踪实跑(E-165):启用了追踪的对象各跑最多 5 秒都不抛异常、没有非数(" + homing.Live + " 个跑完,没有模板 " + homing.NoTemplate + " 个,抛异常 " + homing.Failed + " 个,非数 " + homing.NonFinite + " 个)");
        Report(customs.Monsters > 0 && customs.ActsMissing == 0, "怪物的 [etc action] 各项 .act 都读得到(" + customs.Entries + " 项,读不到 " + customs.ActsMissing + " 项)");
        Report(customs.Uses > 0 && customs.UsesInRange * 100 >= customs.Uses * 95,
            "怪物自己的 .act 里 [SET ACTION] [CUSTOM] n 的 n 落在 [etc action] 列表里的不低于 95%(" + customs.UsesInRange + "/" + customs.Uses + ")");
        Report(liveCustoms.Entries > 0 && liveCustoms.Failed == 0 && liveCustoms.NotEntered == 0,
            "怪物的自定义动作实跑(E-159):每一项都切得进去、各跑最多 60 秒不抛异常(" + liveCustoms.Entries + " 项,没切进去 " + liveCustoms.NotEntered + " 项,抛异常 " + liveCustoms.Failed + " 项)");
        Report(liveCasts.Attacks > 0 && liveCasts.Failed == 0 && liveCasts.NotEntered == 0,
            "带 [CASTING] 的招式实跑(E-168):每一招都切得进去、各跑最多 60 秒不抛异常(" + liveCasts.Attacks + " 招,没切进去 " + liveCasts.NotEntered + " 招,抛异常 " + liveCasts.Failed + " 招)");
        Report(liveMoves.Attacks > 0 && liveMoves.Failed == 0 && liveMoves.NotEntered == 0 && liveMoves.Pushed > 0,
            "带 [MOVE TO TARGET] 的招式实跑(E-169):每一招都切得进去、各跑最多 60 秒不抛异常、真的冲得出去(" + liveMoves.Attacks + " 招,没切进去 " + liveMoves.NotEntered + " 招,抛异常 "
            + liveMoves.Failed + " 招,冲出去 " + liveMoves.Pushed + " 招)");
        Report(loops.Live > 0 && loops.Failed == 0 && loops.NotEntered == 0 && loops.StillRunning * 20 <= loops.Live * 3,
            "循环动画出招实跑(E-170):带循环动画而且脚本能收尾的招每一招都切得进去、各跑最多 60 秒不抛异常,到时间还在放的不超过 15%(" + loops.Live
            + " 招,没切进去 " + loops.NotEntered + " 招,抛异常 " + loops.Failed + " 招,到时间还在放 " + loops.StillRunning + " 招)");
        Report(boardAll > 0 && boardHit * 100 >= boardAll * 95,
            "被动对象跨动作的开关读写(E-158)落到自己有触发器的动作上的不低于 95%(" + boardHit + "/" + boardAll + ")");
        int forceAll = monsterTally.ForceBlocks + monsterTally.ForceBroken;
        Report(forceAll > 0 && monsterTally.ForceBlocks * 100 >= forceAll * 95,
            "怪物出招里的 [SET POS FORCE] 读通的不低于 95%(" + monsterTally.ForceBlocks + "/" + forceAll + ")");
        int motionOk = monsterTally.TargetMotionUses.Values.Sum();
        int motionAll = motionOk + monsterTally.TargetMotionBroken.Values.Sum();
        Report(motionAll > 0 && motionOk * 100 >= motionAll * 95,
            "怪物出招里朝目标的移动与转向(E-169)读通的不低于 95%(" + motionOk + "/" + motionAll + ")");

        if (opts.JsonFile != null)
        {
            var summary = new
            {
                monsters, withAttackList, withActFile, withTriggers, withRunnable, allRunnable, withSpawn, missingActs, actRefs,
                monsterActs = monsterTally.ToJson(),
                crossSlotSwitches = crossSwitch.ToJson(),
                familySwitches = familySwitch.ToJson(),
                actionFileSwaps = fileSwap.ToJson(),
                reachedObjects = reached.Count, templates, missingObjects, objectsWithTriggers, objectsRunnable, objectsAllRunnable, objectsDepth2,
                objectActs = objectTally.ToJson(),
                allObjects = allIds.Length, readable, basicAct, oldStyle, etcActs, anyTrig, allRun, timeLimit, endOfAnim, neither,
                allObjectActs = allTally.ToJson(),
                boardsReached = boardsReached.ToJson(),
                boardsAll = boardsAll.ToJson(),
                liveBoards = liveBoards.ToJson(),
                homing = homing.ToJson(),
                monsterCustoms = customs.ToJson(),
                liveCustoms = liveCustoms.ToJson(),
                liveCasts = liveCasts.ToJson(),
                liveMoves = liveMoves.ToJson(),
                loopCensus = loops.ToJson(),
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
