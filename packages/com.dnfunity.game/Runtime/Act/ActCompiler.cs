using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:触发器与行为的"记录头 + 尾部"文法取自客户端的加载函数(0x01333530、0x013322f0)与 docs/act-tags.tsv 的标签表(E-050);
// 逐类型的参数布局来自 E-130 的静态读码。加载函数是嵌套的 if 链,这里改成"摊平记号流 + 按记录头切段"的等价写法。
namespace Dnf.Game.Act
{
    internal enum ActTokenKind : byte
    {
        Open,
        Close,
        Value,
    }

    /// <summary>摊平后的记号:开标签、闭标签或一个值。</summary>
    internal readonly struct ActToken
    {
        public ActToken(ActTokenKind kind, string tag, ScriptValue value)
        {
            Kind = kind;
            Tag = tag;
            Value = value;
        }

        public ActTokenKind Kind { get; }
        public string Tag { get; }
        public ScriptValue Value { get; }
    }

    /// <summary>记号流上的游标与基本读取(整数、值表达式、"一个比较符加一个数")。</summary>
    internal sealed class ActCursor
    {
        private readonly List<ActToken> _t;
        private int _i;

        public ActCursor(List<ActToken> tokens)
        {
            _t = tokens;
        }

        public bool End => _i >= _t.Count;

        public static List<ActToken> Flatten(IReadOnlyList<ActNode> nodes)
        {
            var list = new List<ActToken>();
            Add(nodes, list);
            return list;
        }

        private static void Add(IReadOnlyList<ActNode> nodes, List<ActToken> into)
        {
            foreach (ActNode n in nodes)
            {
                into.Add(new ActToken(ActTokenKind.Open, n.Tag, default));
                foreach (ScriptValue v in n.Values)
                {
                    into.Add(new ActToken(ActTokenKind.Value, null, v));
                }
                if (n.IsBlock)
                {
                    Add(n.Children, into);
                    into.Add(new ActToken(ActTokenKind.Close, n.Tag, default));
                }
            }
        }

        public bool AtOpen(out string tag)
        {
            if (_i < _t.Count && _t[_i].Kind == ActTokenKind.Open)
            {
                tag = _t[_i].Tag;
                return true;
            }
            tag = null;
            return false;
        }

        public bool AtOpen(string tag) => _i < _t.Count && _t[_i].Kind == ActTokenKind.Open && _t[_i].Tag == tag;

        public bool AtClose(string tag) => _i < _t.Count && _t[_i].Kind == ActTokenKind.Close && _t[_i].Tag == tag;

        public bool AtNumber => _i < _t.Count && _t[_i].Kind == ActTokenKind.Value && IsNumeric(_t[_i].Value);

        public bool AtString => _i < _t.Count && _t[_i].Kind == ActTokenKind.Value && _t[_i].Value.Kind == ScriptValueKind.String;

        public void Skip()
        {
            if (_i < _t.Count)
            {
                _i++;
            }
        }

        /// <summary>读一个开标签并前进;不在开标签上返回 null。</summary>
        public string TakeOpen()
        {
            if (AtOpen(out string tag))
            {
                _i++;
                return tag;
            }
            return null;
        }

        public bool TakeClose(string tag)
        {
            if (AtClose(tag))
            {
                _i++;
                return true;
            }
            return false;
        }

        private static bool IsNumeric(ScriptValue v) => v.IsNumber || v.Kind == ScriptValueKind.Float;

        private static int ToInt(ScriptValue v) => v.IsNumber ? v.Int : ScriptCursor.TruncateToInt(v.Float);

        /// <summary>读一个整数(浮点按向零截断);不在数上返回 false 且不前进。</summary>
        public bool TryInt(out int value)
        {
            if (AtNumber)
            {
                value = ToInt(_t[_i].Value);
                _i++;
                return true;
            }
            value = 0;
            return false;
        }

        public int Int(int fallback = 0) => TryInt(out int v) ? v : fallback;

        /// <summary>读掉所有紧跟着的整数。</summary>
        public int[] Ints()
        {
            var list = new List<int>();
            while (TryInt(out int v))
            {
                list.Add(v);
            }
            return list.ToArray();
        }

        /// <summary>读一个字符串值;不在字符串上返回 null。</summary>
        public string TakeString()
        {
            if (AtString)
            {
                string s = _t[_i].Value.Text;
                _i++;
                return s ?? string.Empty;
            }
            return null;
        }

        /// <summary>
        /// 读一个值表达式(客户端的 0x01330240):字面整数、[RANDOM] a b、[RANDOM SELECT] v1…vn [/RANDOM SELECT];读不到返回 null。
        /// </summary>
        public ActValue Value()
        {
            if (AtNumber)
            {
                return ActValue.Literal(Int());
            }
            if (AtOpen("[RANDOM]"))
            {
                _i++;
                int a = Int();
                int b = Int();
                TakeClose("[RANDOM]");
                return ActValue.Range(a, b);
            }
            if (AtOpen("[RANDOM SELECT]"))
            {
                _i++;
                var list = new List<int>();
                while (!End && !AtClose("[RANDOM SELECT]"))
                {
                    if (!TryInt(out int v))
                    {
                        break;
                    }
                    list.Add(v);
                }
                TakeClose("[RANDOM SELECT]");
                return ActValue.Choice(list);
            }
            return null;
        }

        /// <summary>读 "[比较符] 数":比较符是一个开标签;读不到返回 false(游标不动)。</summary>
        public bool TryCompare(out ActCompare op, out int number)
        {
            op = ActCompare.Equal;
            number = 0;
            if (AtOpen(out string tag) && ActCompares.TryParse(tag, out op))
            {
                _i++;
                number = Int();
                return true;
            }
            return false;
        }

        /// <summary>跳到下一个满足 <paramref name="isHead"/> 的开标签(不消耗它)或流尾。</summary>
        public void SkipToHead(Func<string, bool> isHead)
        {
            while (!End)
            {
                if (AtOpen(out string tag) && isHead(tag))
                {
                    return;
                }
                _i++;
            }
        }
    }

    /// <summary>触发器与行为组的编译(见 <see cref="ActProgram.Compile"/>)。</summary>
    internal static class ActCompiler
    {
        // ------------------------------------------------------------------ 触发器

        /// <summary>触发器里开始一条新记录的标签:docs/act-tags.tsv 的 trigger 一节,加上 [CHECKUP]、[ENABLE]、[LIMIT]、[DO BEHAVIOR] 等块内语法。</summary>
        private static readonly HashSet<string> TriggerHeads = new HashSet<string>(StringComparer.Ordinal)
        {
            "[FRAME]", "[XSPEED]", "[YSPEED]", "[ZSPEED]", "[ZPOS]", "[CHARGED TIME]", "[ON DAMAGE]", "[CHECK STUCK]",
            "[ON ATTACKSUCCESS]", "[ON CREATE OBJECT]", "[ON DESTROY OBJECT]", "[ON DESTROY ME]", "[ON SET ACTION]",
            "[ON END ACTION]", "[ON SHAKE INPUT]", "[ON MOVE COLLISION]", "[ON CHANGE LIFE]", "[IS MEET PROB]", "[IS NAME]",
            "[IS CATEGORY]", "[IS OBJECT TYPE]", "[IS LEFT]", "[IS RIGHT]", "[IS INDEX]", "[IS NOT INDEX]", "[CHECK PARTY SLOT]",
            "[IS TEAM]", "[HP]", "[MP]", "[WHICH]", "[DISTANCE]", "[IS BACKPOS]", "[IS FRONTPOS]", "[CHECK GHOST]", "[CHECK BUFF]",
            "[CHECK SPECIFIC BUFF]", "[CHECK THREAT]", "[CHECKED NO]", "[IF]", "[IS AI TARGET]", "[DIRECT DO BEHAVIOR]",
            "[GET TARGET]", "[IS GRABABLE]", "[TRIGGER CHECK]", "[IS MISSILE DAMAGE]", "[IS FIRE DAMAGE]", "[IS WATER DAMAGE]",
            "[IS DARK DAMAGE]", "[IS LIGHT DAMAGE]", "[IS NONE DAMAGE]", "[IS PHYSICAL DAMAGE]", "[IS MAGICAL DAMAGE]",
            "[IS ABSOLUTE DAMAGE]", "[IS END MAP]", "[IS END MAP VERTICALLY]", "[IS DAMAGEBOX ENABLE]", "[IS VISIBLE]",
            "[AXIS INDEX]", "[SAVE TARGET OBJECT]", "[CHECK DISTANCE]", "[CHECK AREA]", "[FRONTSIDE]", "[BACKSIDE]",
            "[FACE TO FACE]", "[WITHIN SIGHT]", "[OUT OF SIGHT]", "[IS OURTEAMATTACKED]", "[IS TARGETACTIVESTATUS]",
            "[RANDOM CHECK]", "[IS TARGET STATE]", "[IS NOT TARGET STATE]", "[IS IN POS]", "[IS IN AREA]",
            "[CHECK PARTYMEMBERS STATE]", "[CHECK SYMBOL]", "[CHECK PARTY SYMBOL]", "[CHECK TIME]", "[NOTHING BREAKABLEOBJ]",
            "[IS PROPERTY CAPACITY OVER]", "[LAST ATTACK PRIORITY]", "[LAST TARGET PRIORITY]", "[ON END BEHAVIOR]",
            "[IS DAMAGE ENABLE]", "[REALDUNGEON CHECKUP]", "[AREA]", "[DELAY DO BEHAVIOR]", "[IS MOUNT]", "[IS DIRECTION LEFT]",
            "[IS DIRECTION RIGHT]", "[IS CHILD]", "[CHECK PLAYER NUM]",
            "[CHECKUP]", "[ENABLE]", "[ENABLE OFF]", "[LIMIT]", "[DO BEHAVIOR]", "[DO PROC BEHAVIOR]", "[ON TIMING]",
            "[ON CLICK CONTROL]", "[ON LEAVE MOUSE]", "[ON ENTER MOUSE]", "[ON LBDOWN CONTROL]", "[ON REMOVE ACTION]",
            "[IS MOUSE LBCLICK]", "[CHECK FUNC]", "[CALL FUNC]",
        };

        private static readonly Dictionary<string, ActSelector> Selectors = new Dictionary<string, ActSelector>(StringComparer.Ordinal)
        {
            { "[ME]", ActSelector.Me },
            { "[LAST ATTACKER]", ActSelector.LastAttacker },
            { "[LAST ACTIVE ATTACKER]", ActSelector.LastActiveAttacker },
            { "[LAST ATTACKSUCCESS]", ActSelector.LastAttackSuccess },
            { "[LAST ATTACKSUUCCESS]", ActSelector.LastAttackSuccess },   // 全库里有 4 处笔误
            { "[CHARACTER ATTACKSUCCESS]", ActSelector.CharacterAttackSuccess },
            { "[LAST ATTACKSUCCESSES]", ActSelector.LastAttackSuccesses },
            { "[LAST ACTIVE ATTACKSUCCESS]", ActSelector.LastActiveAttackSuccess },
            { "[LAST ATTACKERS]", ActSelector.LastAttackers },
            { "[TARGET]", ActSelector.Target },
            { "[SUMMON MASTER]", ActSelector.SummonMaster },
            { "[ALL OUR TEAM]", ActSelector.AllOurTeam },
            { "[ALL ENEMY]", ActSelector.AllEnemy },
            { "[ALL CHARACTER TEAM]", ActSelector.AllCharacterTeam },
            { "[ALL MONSTER TEAM]", ActSelector.AllMonsterTeam },
            { "[CHARACTER]", ActSelector.Character },
            { "[MONSTER]", ActSelector.Monster },
            { "[AI CHARACTER]", ActSelector.AiCharacter },
            { "[CREATE OBJECT]", ActSelector.CreateObject },
            { "[DESTROY OBJECT]", ActSelector.DestroyObject },
            { "[PASSIVE]", ActSelector.Passive },
            { "[LOAD TARGET]", ActSelector.LoadTarget },
            { "[PARTY TARGET]", ActSelector.PartyTarget },
            { "[PLAYER]", ActSelector.Player },
            { "[SUMMON OBJECT]", ActSelector.SummonObject },
        };

        public static ActTrigger Trigger(int index, ActNode node)
        {
            var cur = new ActCursor(ActCursor.Flatten(node.Children));
            var records = new List<ActRecord>();
            var unsupported = new List<string>();
            bool enabled = true;
            int limit = 0;
            ParseRecords(cur, null, records, unsupported, ref enabled, ref limit);
            return new ActTrigger(index, enabled, limit, records, unsupported);
        }

        /// <summary>读记录直到流尾或 <paramref name="until"/> 的闭标签([CHECKUP] 块里用,内容摊进同一个记录表)。</summary>
        private static void ParseRecords(ActCursor cur, string until, List<ActRecord> records, List<string> unsupported, ref bool enabled, ref int limit)
        {
            while (!cur.End)
            {
                if (until != null && cur.TakeClose(until))
                {
                    return;
                }
                if (!cur.AtOpen(out string tag))
                {
                    cur.Skip();
                    continue;
                }
                if (!TriggerHeads.Contains(tag))
                {
                    cur.Skip();    // 记录头之外的游离标签(上一条记录读剩的子标签)
                    continue;
                }
                cur.Skip();
                switch (tag)
                {
                    case "[ENABLE]":
                    case "[ENABLE OFF]":
                        if (tag == "[ENABLE OFF]")
                        {
                            enabled = false;
                        }
                        else if (cur.AtOpen("[ON]"))
                        {
                            cur.Skip();
                            enabled = true;
                        }
                        else if (cur.AtOpen("[OFF]"))
                        {
                            cur.Skip();
                            enabled = false;
                        }
                        break;
                    case "[LIMIT]":
                        limit = cur.Int();
                        break;
                    case "[CHECKUP]":
                        ParseRecords(cur, "[CHECKUP]", records, unsupported, ref enabled, ref limit);
                        break;
                    case "[FRAME]":
                    {
                        int a = cur.Int();
                        int b = cur.Int(a);
                        records.Add(new ActRecord { Kind = ActRecordKind.Frame, Tag = tag, A = a, B = b });
                        break;
                    }
                    case "[CHECK TIME]":
                        records.Add(new ActRecord { Kind = ActRecordKind.CheckTime, Tag = tag, Number = cur.Int() });
                        break;
                    case "[WHICH]":
                        records.Add(ParseWhich(cur, unsupported));
                        break;
                    case "[CHECKED NO]":
                        AddCompare(cur, records, unsupported, tag, ActRecordKind.CheckedNo);
                        break;
                    case "[ZPOS]":
                        AddCompare(cur, records, unsupported, tag, ActRecordKind.ZPos);
                        break;
                    case "[DISTANCE]":
                        AddCompare(cur, records, unsupported, tag, ActRecordKind.Distance);
                        break;
                    case "[DO BEHAVIOR]":
                    case "[DIRECT DO BEHAVIOR]":
                        records.Add(ParseDo(cur, tag, unsupported));
                        break;
                    case "[ON SET ACTION]":
                        records.Add(new ActRecord { Kind = ActRecordKind.OnSetAction, Tag = tag });
                        break;
                    case "[ON ATTACKSUCCESS]":
                        records.Add(new ActRecord { Kind = ActRecordKind.OnAttackSuccess, Tag = tag });
                        break;
                    case "[ON DAMAGE]":
                        records.Add(new ActRecord { Kind = ActRecordKind.OnDamage, Tag = tag });
                        break;
                    case "[ON CREATE OBJECT]":
                    case "[ON DESTROY OBJECT]":
                    case "[ON DESTROY ME]":
                    case "[ON END ACTION]":
                    case "[ON SHAKE INPUT]":
                    case "[ON MOVE COLLISION]":
                    case "[ON CHANGE LIFE]":
                    case "[ON END BEHAVIOR]":
                        records.Add(new ActRecord { Kind = ActRecordKind.OnOtherEvent, Tag = tag });
                        break;
                    case "[IS INDEX]":
                    case "[IS NOT INDEX]":
                    {
                        // 块 [IS INDEX] 1 2 3 [/IS INDEX];笔误或叶子写法下数字直接跟在标签后
                        int[] ints = cur.Ints();
                        cur.TakeClose(tag);
                        records.Add(new ActRecord
                        {
                            Kind = tag == "[IS INDEX]" ? ActRecordKind.IsIndex : ActRecordKind.IsNotIndex,
                            Tag = tag,
                            Ints = ints,
                        });
                        break;
                    }
                    case "[IS OBJECT TYPE]":
                        records.Add(ParseObjectTypes(cur, tag));
                        break;
                    case "[IS LEFT]":
                        records.Add(new ActRecord { Kind = ActRecordKind.IsLeft, Tag = tag });
                        break;
                    case "[IS RIGHT]":
                        records.Add(new ActRecord { Kind = ActRecordKind.IsRight, Tag = tag });
                        break;
                    case "[IS DIRECTION LEFT]":
                        records.Add(new ActRecord { Kind = ActRecordKind.IsDirectionLeft, Tag = tag });
                        break;
                    case "[IS DIRECTION RIGHT]":
                        records.Add(new ActRecord { Kind = ActRecordKind.IsDirectionRight, Tag = tag });
                        break;
                    case "[GET TARGET]":
                        // 在已有目标上排序、随机取、按距离取并截断到最多 N 个(E-130 读码);单人房间里目标最多一个,不改集合
                        cur.SkipToHead(IsTriggerHead);
                        records.Add(new ActRecord { Kind = ActRecordKind.GetTarget, Tag = tag });
                        break;
                    default:
                        AddUnsupported(records, unsupported, tag);
                        cur.SkipToHead(IsTriggerHead);
                        break;
                }
            }
        }

        private static bool IsTriggerHead(string tag) => TriggerHeads.Contains(tag);

        private static void AddUnsupported(List<ActRecord> records, List<string> unsupported, string tag)
        {
            records.Add(new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag });
            if (!unsupported.Contains(tag))
            {
                unsupported.Add(tag);
            }
        }

        private static void AddCompare(ActCursor cur, List<ActRecord> records, List<string> unsupported, string tag, ActRecordKind kind)
        {
            if (cur.TryCompare(out ActCompare op, out int n))
            {
                records.Add(new ActRecord { Kind = kind, Tag = tag, Sign = op, Number = n });
                return;
            }
            AddUnsupported(records, unsupported, tag);
            cur.SkipToHead(IsTriggerHead);
        }

        private static ActRecord ParseWhich(ActCursor cur, List<string> unsupported)
        {
            string sel = cur.TakeOpen();
            if (sel == null || !Selectors.TryGetValue(sel, out ActSelector selector))
            {
                string name = "[WHICH] " + (sel ?? "?");
                if (!unsupported.Contains(name))
                {
                    unsupported.Add(name);
                }
                cur.SkipToHead(IsTriggerHead);
                return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = name };
            }
            var rec = new ActRecord { Kind = ActRecordKind.Which, Tag = "[WHICH] " + sel, Selector = selector };
            if (selector == ActSelector.PartyTarget)
            {
                // [PARTY TARGET] [INDEX] n [INCLUDE DEAD] m … [/PARTY TARGET]:队伍里第 n 位
                while (!cur.End && !cur.TakeClose("[PARTY TARGET]"))
                {
                    if (cur.AtOpen("[INDEX]"))
                    {
                        cur.Skip();
                        rec.PartyIndex = cur.Int();
                    }
                    else
                    {
                        cur.Skip();
                    }
                }
            }
            else if (selector == ActSelector.LoadTarget)
            {
                cur.Ints();
                rec.Kind = ActRecordKind.Unsupported;     // 读取脚本槽里存下的目标:要 [SAVE TARGET OBJECT] 的状态,这一版不做
                if (!unsupported.Contains(rec.Tag))
                {
                    unsupported.Add(rec.Tag);
                }
            }
            return rec;
        }

        private static ActRecord ParseDo(ActCursor cur, string tag, List<string> unsupported)
        {
            var rec = new ActRecord
            {
                Kind = tag == "[DO BEHAVIOR]" ? ActRecordKind.DoBehavior : ActRecordKind.DirectDoBehavior,
                Tag = tag,
            };
            string sel = cur.TakeOpen();
            if (sel == "[CHECKUP OBJECT]")
            {
                rec.Selector = ActSelector.WorkingSet;
            }
            else if (sel != null && Selectors.TryGetValue(sel, out ActSelector s) && s != ActSelector.LoadTarget)
            {
                rec.Selector = s;
            }
            else
            {
                string name = tag + " " + (sel ?? "?");
                if (!unsupported.Contains(name))
                {
                    unsupported.Add(name);
                }
                cur.SkipToHead(IsTriggerHead);
                return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = name };
            }
            rec.Behavior = cur.Value();
            if (rec.Behavior == null)
            {
                AddUnsupportedTag(unsupported, tag + " (no group)");
                cur.SkipToHead(IsTriggerHead);
                return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
            }
            return rec;
        }

        private static void AddUnsupportedTag(List<string> unsupported, string name)
        {
            if (!unsupported.Contains(name))
            {
                unsupported.Add(name);
            }
        }

        private static ActRecord ParseObjectTypes(ActCursor cur, string tag)
        {
            var kinds = new List<int>();
            while (!cur.End && !cur.TakeClose(tag))
            {
                string t = cur.TakeOpen();
                if (t == null)
                {
                    cur.Skip();
                    continue;
                }
                switch (t)
                {
                    case "[CHARACTER]":
                    case "[AI CHARACTER]":
                        kinds.Add((int)ActEntityKind.Character);
                        break;
                    case "[MONSTER]":
                        kinds.Add((int)ActEntityKind.Monster);
                        break;
                    case "[PASSIVE OBJECT]":
                        kinds.Add((int)ActEntityKind.Passive);
                        break;
                    default:
                        break;
                }
            }
            return new ActRecord { Kind = ActRecordKind.IsObjectType, Tag = tag, Ints = kinds.ToArray() };
        }

        // ------------------------------------------------------------------ 行为

        /// <summary>行为组里开始一条新行为的标签:docs/act-tags.tsv 的 behavior 一节(加载函数里行为字典的全部键)。</summary>
        private static readonly HashSet<string> BehaviorHeads = new HashSet<string>(StringComparer.Ordinal)
        {
            "[CREATE PASSIVEOBJECT]", "[SUMMON MONSTER]", "[TELEPORT]", "[DEBUFF]", "[DEBUFF GHOST]", "[CHANGE ATTACKINFO]",
            "[SET X SPEED]", "[SET Y SPEED]", "[SET Z SPEED]", "[SET SPEED]", "[SET MOVEVELS]", "[TRIGGER]", "[RESTORE]",
            "[PLAY SOUND]", "[SEND DO BEHAVIOR]", "[DESTROY BY FORCE]", "[DESTROY]", "[SET HP]", "[ATTACKRECT]", "[SET DIRECTION]",
            "[SET ACTION]", "[SEND MESSAGE]", "[REMOVE CATEGORY]", "[ACTIVE STATUS]", "[SET FRAME]", "[CASTING]", "[ABS CASTING]",
            "[CASTING EX]", "[DIM]", "[APPEND HIT]", "[SET POS FORCE]", "[MASTER APPENDAGE]", "[FLIP HOLD ANIMATION]",
            "[SHAKE INPUT]", "[SHAKE HOLD POS]", "[SET COLOR]", "[ICE APPENDAGE]", "[THORNS APPENDAGE]", "[REFLECTBUFF APPENDAGE]",
            "[DARKSCALP APPENDAGE]", "[DESOLATEPAIN APPENDAGE]", "[TELEKINESIS APPENDAGE]", "[VIRTUALHP APPENDAGE]",
            "[REMOVE VIRTUALHP]", "[TERROR APPENDAGE]", "[DESPAIR APPENDAGE]", "[CHANGE TEAM]", "[DEFAULT TEAM]", "[CHANGE SIGHT]",
            "[DEFAULT SIGHT]", "[SHAKING GAGE]", "[CHANGE DISGUISER]", "[DEFALUT DISGUISER]", "[RHYTHM ACTION]", "[STRAIGHT MOVE]",
            "[DRAIN APPENDAGE]", "[CUT SCENE]", "[INFECTION APPENDAGE]", "[SET CASTING GAUGEPOS]", "[SET RESPAWN POS]",
            "[RESTORE TARGET]", "[CHANGE RIDABLESKILL PATTERN]", "[ANGER APPENDAGE]", "[ATTACKABLEAREA APPENDAGE]",
            "[REMOVE ACTIVE STATUS]", "[REMOVE APPENDAGE]", "[CURSE APPENDAGE]", "[SHAKING]", "[PARTICLE]",
            "[CHANGE DIRECTION BY TARGET]", "[SAY SPEECH]", "[DELETE APPENDAGE]", "[CAMOUFLAGE]", "[SET ALPHA]", "[SET OVERTURN]",
            "[SUMMON APC]", "[SET HOLD DOWNANI]", "[SET HOLD DAMAGEANI]", "[CHANGE ACTION FILE]", "[COLOR CHANGE EFFECT]",
            "[WIND APPENDAGE]", "[HP APPENDAGE]", "[FLASH SCREEN EX]", "[FLASH SCREEN]", "[SET SPEED CONTROL]", "[ADD LAYER]",
            "[REMOVE LAYER]", "[SET GRABABLE]", "[SET DAMAGE CHECK]", "[SET COME IN ACTION]", "[SET OUTLINE]", "[IS CHANGE TARGET]",
            "[SET TARGET]", "[SET DAMAGE BOX]", "[SET TEAM]", "[IS DIRECTION TO MOVE]", "[SET WIDTH]", "[SET PASSTYPE]",
            "[PULL APPENDAGE]", "[PULL APPENDAGE EX]", "[RESTART COOLTIME]", "[SET RANDOM TARGET]", "[MIND CONTROL APPENDAGE]",
            "[SUFFOCATION APPENDAGE]", "[MUCU LIMIT CONTROL]", "[SET PARTY TARGET]", "[ADD CATEGORY]", "[DISEASE APPENDAGE]",
            "[HELLOFHEAL APPENDAGE]", "[CHARACTER COOLTIME RESET]", "[SAY SPEECH WITH POSITION]", "[SAVE POSITION]",
            "[CHANGE FLOATING HEIGHT]", "[CHANGE GRAVITY]", "[ZOOM IT]", "[CHANGE MY TARGET]", "[JUMP TO TARGET]", "[TAKE IT]",
            "[THROW ITS OWN]", "[BOUNCE FROM THE WALL]", "[SET USE HOMING]", "[SET VISIBLITY]", "[MOVE TO]", "[SET INTERPOLATION]",
            "[MAKE VIBRATION]", "[CENTER MSG]", "[START KEYSTROKE MODE]", "[END KEYSTROKE MODE]", "[FOLLOWING TYPE]",
            "[SET WHOLE ALPHA]", "[SET WHOLE DAMAGETYPE]", "[REFRESH MOVEINFO]", "[FOLLOWING TARGET AND DIRECTION]", "[CHANGE AI]",
            "[FIX TARGET ME]", "[NAME HIDE ON]", "[NAME HIDE OFF]", "[AURA]", "[TRIGGER QUESTEVENT]", "[LOCK QUEST UNTIL]",
            "[UNLOCK QUEST]", "[SET SAVED TARGET]", "[MOVE TO TARGET]", "[HIT APPENDAGE]", "[SET SYMBOL]", "[SET SYMBOL OPTION]",
            "[SET PARTY SYMBOL]", "[LANDTYPE APPENDAGE]", "[DAMAGE APPENDAGE]", "[RESOLUTION APPENDAGE]", "[FORCE JUMP TO]",
            "[SET SHADOW]", "[SET AS PASSABLE OBJECT]", "[BOOSTER APPENDAGE]", "[CAMERA TRACE]", "[MOVE APPENDAGE]",
            "[FREEZE APPENDAGE]", "[TRAP APPENDAGE]", "[RANDOM GENERATE PASSIVE OBJECT]", "[MOVE PATTERN]", "[MOVE PATTERN OFF]",
            "[DISABLE KEYINPUT]", "[EXCHANGE HP RATE]", "[SET MOVABLE AREA]", "[SHOW HITGAUGE]", "[HIDE HITGAUGE]",
            "[GUARD APPENDAGE]", "[EVENT RHYTHM ACTION]", "[SET POSITION HP GAUGE]", "[SET POSITION CASTING GAUGE]", "[SET UNBEATABLE]",
            "[APPENDAGE]", "[NO BUFF APPENDAGE]", "[SET DEFAULT ACTION]", "[ETC_COMMAND]", "[SET VISIBLE]", "[SET INVISIBLE]",
        };

        public static ActBehaviorGroup Behavior(ActNode node)
        {
            var cur = new ActCursor(ActCursor.Flatten(node.Children));
            var items = new List<ActBehavior>();
            var unsupported = new List<string>();
            while (!cur.End)
            {
                if (!cur.AtOpen(out string tag) || !BehaviorHeads.Contains(tag))
                {
                    cur.Skip();
                    continue;
                }
                cur.Skip();
                ActBehavior b = ParseBehavior(cur, tag);
                if (b.Kind == ActBehaviorKind.Unsupported && !unsupported.Contains(tag))
                {
                    unsupported.Add(tag);
                }
                items.Add(b);
                cur.SkipToHead(IsBehaviorHead);     // 这条行为读剩的子标签与值
            }
            return new ActBehaviorGroup(items, unsupported);
        }

        private static bool IsBehaviorHead(string tag) => BehaviorHeads.Contains(tag);

        private static ActBehavior ParseBehavior(ActCursor cur, string tag)
        {
            switch (tag)
            {
                case "[CREATE PASSIVEOBJECT]":
                    return ParseCreate(cur, tag);
                case "[SET SPEED]":
                    return ParseSpeed(cur, tag);
                case "[SET ACTION]":
                    return ParseSetAction(cur, tag);
                case "[DESTROY]":
                case "[DESTROY BY FORCE]":
                    return new ActBehavior { Kind = ActBehaviorKind.Destroy, Tag = tag, Force = tag == "[DESTROY BY FORCE]" };
                case "[ATTACKRECT]":
                {
                    string mode = cur.TakeOpen();
                    switch (mode)
                    {
                        case "[ON]":
                            return new ActBehavior { Kind = ActBehaviorKind.AttackRect, Tag = tag, Rect = ActAttackRectMode.On };
                        case "[OFF]":
                            return new ActBehavior { Kind = ActBehaviorKind.AttackRect, Tag = tag, Rect = ActAttackRectMode.Off };
                        case "[RESET]":
                            return new ActBehavior { Kind = ActBehaviorKind.AttackRect, Tag = tag, Rect = ActAttackRectMode.Reset };
                        default:
                            return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };    // 没写子行:客户端也什么都不做
                    }
                }
                case "[SET DIRECTION]":
                    return ParseDirection(cur, tag);
                case "[TRIGGER]":
                {
                    if (!cur.AtNumber)
                    {
                        return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };    // 按动作槽选容器的写法([MOVE]、[ATTACK] n 等),这一版不做
                    }
                    int n = cur.Int();
                    string cmd = cur.TakeOpen();
                    ActTriggerCommand command;
                    switch (cmd)
                    {
                        case "[ON]":
                            command = ActTriggerCommand.On;
                            break;
                        case "[OFF]":
                            command = ActTriggerCommand.Off;
                            break;
                        case "[RESET]":
                            command = ActTriggerCommand.Reset;
                            break;
                        default:
                            return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                    }
                    return new ActBehavior { Kind = ActBehaviorKind.TriggerSwitch, Tag = tag, TriggerIndex = n, Command = command };
                }
                case "[SET FRAME]":
                    return new ActBehavior { Kind = ActBehaviorKind.SetFrame, Tag = tag, Frame = cur.Int() };
                default:
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
        }

        private static ActBehavior ParseCreate(ActCursor cur, string tag)
        {
            var b = new ActBehavior { Kind = ActBehaviorKind.CreatePassive, Tag = tag };
            while (!cur.End && !cur.TakeClose(tag))
            {
                string t = cur.TakeOpen();
                if (t == null)
                {
                    cur.Skip();
                    continue;
                }
                switch (t)
                {
                    case "[INDEX]":
                        b.Index = cur.Value();
                        break;
                    case "[LEVEL]":
                        b.Level = cur.Value();
                        break;
                    case "[POS]":
                        b.PosX = cur.Value();
                        b.PosY = cur.Value();
                        b.PosZ = cur.Value();
                        break;
                    case "[USE MY DIRECTION]":
                        b.UseMyDirection = true;
                        break;
                    case "[USE MAP POS]":
                        b.UseMapPos = true;
                        break;
                    case "[USE MY BASEPOS]":
                        b.UseMyBasePos = true;
                        break;
                    case "[USE OBJECT ZPOS]":
                        b.UseObjectZ = true;
                        break;
                    case "[FIX DIRECTION]":
                    {
                        string d = cur.TakeOpen();
                        b.FixDirection = d == "[LEFT]" ? 0 : d == "[RIGHT]" ? 1 : d == "[NEUTRAL]" ? 4 : d == "[RANDOM]" ? 5 : -1;
                        break;
                    }
                    default:
                        break;     // [PARTICLE FILENAME]、[WARNING MARK]、队伍、碰撞组等:这一版不用(它们的值被上面的循环当游离值跳过)
                }
            }
            if (b.Index == null)
            {
                b.Kind = ActBehaviorKind.Unsupported;     // 没有对象编号:放不出任何东西
            }
            else
            {
                // [POS] 写得不足三个值(全库有少数写一个、两个值,或干脆没有 [POS]):缺的当 0(推断,没有逐条核对客户端遇到标签时的读法;
                // 整条丢掉会让这些招式什么也放不出来,当 0 至少落在基准点上)
                b.PosX = b.PosX ?? ActValue.Literal(0);
                b.PosY = b.PosY ?? ActValue.Literal(0);
                b.PosZ = b.PosZ ?? ActValue.Literal(0);
            }
            return b;
        }

        private static ActBehavior ParseSpeed(ActCursor cur, string tag)
        {
            var b = new ActBehavior { Kind = ActBehaviorKind.SetSpeed, Tag = tag };
            while (!cur.End && !cur.TakeClose(tag))
            {
                string t = cur.TakeOpen();
                switch (t)
                {
                    case "[X AXIS]":
                        b.SpeedX = cur.Value();
                        break;
                    case "[Y AXIS]":
                        b.SpeedY = cur.Value();
                        break;
                    case "[Z AXIS]":
                        b.SpeedZ = cur.Value();
                        break;
                    case null:
                        cur.Skip();
                        break;
                    default:
                        break;     // [USE MY DIRECTION] / [NOT USE DIRECTION]:执行块里没有分支(E-130),不影响速度符号
                }
            }
            return b;
        }

        private static ActBehavior ParseSetAction(ActCursor cur, string tag)
        {
            var b = new ActBehavior { Kind = ActBehaviorKind.SetAction, Tag = tag, Action = ActActionKind.Unknown, ActionIndex = ActValue.Literal(0) };
            bool first = true;
            while (!cur.End && cur.AtOpen(out string t) && !BehaviorHeads.Contains(t))
            {
                cur.Skip();
                switch (t)
                {
                    case "[NOW]":
                        b.Now = true;
                        continue;
                    case "[END]":
                        b.AtEnd = true;
                        continue;
                    case "[RESET]":
                        continue;
                }
                if (!first)
                {
                    continue;     // 第一个关键字之后的方向、速度等修饰
                }
                first = false;
                switch (t)
                {
                    case "[STAND]":
                        b.Action = ActActionKind.Stand;
                        break;
                    case "[MOVE]":
                        b.Action = ActActionKind.Move;
                        break;
                    case "[ATTACK]":
                        b.Action = ActActionKind.Attack;
                        b.ActionIndex = cur.Value() ?? ActValue.Literal(0);
                        break;
                    case "[CUSTOM]":
                        b.Action = ActActionKind.Custom;
                        b.ActionIndex = cur.Value() ?? ActValue.Literal(0);
                        break;
                    case "[DAMAGE]":
                        b.Action = ActActionKind.Damage;
                        break;
                    case "[DOWN]":
                        b.Action = ActActionKind.Down;
                        break;
                    case "[SIT]":
                        b.Action = ActActionKind.Sit;
                        break;
                    case "[DEFAULT]":
                        b.Action = ActActionKind.Default;
                        break;
                    case "[JUMP]":
                        b.Action = ActActionKind.Jump;
                        break;
                    case "[HOLD]":
                        b.Action = ActActionKind.Hold;
                        break;
                    case "[GUARD]":
                        b.Action = ActActionKind.Guard;
                        break;
                    default:
                        break;
                }
            }
            return b;
        }

        private static ActBehavior ParseDirection(ActCursor cur, string tag)
        {
            string mode = cur.TakeOpen();
            ActDirectionMode m;
            switch (mode)
            {
                case "[LEFT]":
                    m = ActDirectionMode.Left;
                    break;
                case "[RIGHT]":
                    m = ActDirectionMode.Right;
                    break;
                case "[TO TARGET]":
                    m = ActDirectionMode.ToTarget;
                    break;
                case "[MY DIRECTION]":
                    m = ActDirectionMode.MyDirection;
                    break;
                case "[RANDOM]":
                    m = ActDirectionMode.Random;
                    break;
                case "[REVERSE]":
                    m = ActDirectionMode.Reverse;
                    break;
                case "[MY OPPOSITE DIRECTION]":
                    m = ActDirectionMode.MyOppositeDirection;
                    break;
                default:
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            return new ActBehavior { Kind = ActBehaviorKind.SetDirection, Tag = tag, Direction = m };
        }
    }
}
