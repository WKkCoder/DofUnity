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

        /// <summary>读一个浮点(整数记号也算,转成浮点);不在数上返回 false 且不前进。</summary>
        public bool TryFloat(out float value)
        {
            if (AtNumber)
            {
                ScriptValue v = _t[_i].Value;
                value = v.IsNumber ? v.Int : v.Float;
                _i++;
                return true;
            }
            value = 0f;
            return false;
        }

        /// <summary>现在游标正对着的整数记号个数(到下一个标签为止;审计用,不前进)。</summary>
        public int PendingNumbers()
        {
            int n = 0;
            for (int i = _i; i < _t.Count && _t[i].Kind == ActTokenKind.Value && IsNumeric(_t[i].Value); i++)
            {
                n++;
            }
            return n;
        }

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

        /// <summary>读一项:整数、变量名(字符串值)或 [GET TIME];读不到返回 false(游标不动)。</summary>
        public bool TryTerm(out ActTerm term)
        {
            if (AtNumber)
            {
                term = ActTerm.OfNumber(Int());
                return true;
            }
            if (AtString)
            {
                term = ActTerm.OfVariable(TakeString());
                return true;
            }
            if (AtOpen("[GET TIME]"))
            {
                _i++;
                term = ActTerm.Clock;
                return true;
            }
            term = default;
            return false;
        }

        /// <summary>读表达式:一项,后面可以跟 [+] 或 [-] 再一项;读不全返回 false(游标停在读到的地方)。</summary>
        public bool TryExpression(out ActExpression expression)
        {
            expression = null;
            if (!TryTerm(out ActTerm first))
            {
                return false;
            }
            ActArith op = ActArith.None;
            ActTerm second = default;
            if (AtOpen("[+]") || AtOpen("[-]"))
            {
                op = AtOpen("[+]") ? ActArith.Add : ActArith.Subtract;
                _i++;
                if (!TryTerm(out second))
                {
                    return false;
                }
            }
            expression = new ActExpression(first, op, second);
            return true;
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
                    case "[DO PROC BEHAVIOR]":
                        records.Add(ParseDo(cur, tag, unsupported));
                        break;
                    case "[DELAY DO BEHAVIOR]":
                        records.Add(ParseDelayDo(cur, tag, unsupported));
                        break;
                    case "[FRONTSIDE]":
                        records.Add(new ActRecord { Kind = ActRecordKind.FrontSide, Tag = tag });
                        break;
                    case "[IS IN POS]":
                    {
                        int[] pos = cur.Ints();
                        if (pos.Length >= 3)
                        {
                            records.Add(new ActRecord { Kind = ActRecordKind.IsInPos, Tag = tag, Ints = pos });
                            break;
                        }
                        AddUnsupported(records, unsupported, tag);
                        cur.SkipToHead(IsTriggerHead);
                        break;
                    }
                    case "[IF]":
                    {
                        // [IF] 左表达式 比较符 右表达式 [/IF](E-132):变量与毫秒时钟的比较,全库 11 种写法都是这个形状
                        if (cur.TryExpression(out ActExpression left) && cur.AtOpen(out string cmp) && ActCompares.TryParse(cmp, out ActCompare sign))
                        {
                            cur.Skip();
                            if (cur.TryExpression(out ActExpression right))
                            {
                                cur.TakeClose(tag);
                                records.Add(new ActRecord { Kind = ActRecordKind.If, Tag = tag, Left = left, Sign = sign, Right = right });
                                break;
                            }
                        }
                        AddUnsupported(records, unsupported, tag);
                        cur.SkipToHead(IsTriggerHead);
                        break;
                    }
                    case "[HP]":
                    {
                        // [HP] 比较符 数 [%] 或 [POINT](逐对象过滤;[HP] [LOW] 之类的写法不认)
                        if (cur.TryCompare(out ActCompare hpSign, out int hpNumber))
                        {
                            bool percent = false;
                            if (cur.AtOpen("[%]"))
                            {
                                cur.Skip();
                                percent = true;
                            }
                            else if (cur.AtOpen("[POINT]"))
                            {
                                cur.Skip();
                            }
                            records.Add(new ActRecord { Kind = ActRecordKind.Hp, Tag = tag, Sign = hpSign, Number = hpNumber, Percent = percent });
                            break;
                        }
                        AddUnsupported(records, unsupported, tag);
                        cur.SkipToHead(IsTriggerHead);
                        break;
                    }
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
                    case "[IS CHILD]":
                        records.Add(new ActRecord { Kind = ActRecordKind.IsChild, Tag = tag, Number = cur.Int() });     // 全库 14 处都是一个整数 1(E-134)
                        break;
                    case "[GET TARGET]":
                        records.Add(ParseGetTarget(cur, tag, unsupported));
                        break;
                    case "[CHECK DISTANCE]":
                        records.Add(ParseCheckDistance(cur, tag, unsupported));
                        break;
                    case "[IS GRABABLE]":
                        if (cur.AtNumber)
                        {
                            records.Add(new ActRecord { Kind = ActRecordKind.IsGrabbable, Tag = tag, Number = cur.Int() });
                            break;
                        }
                        AddUnsupported(records, unsupported, tag);
                        cur.SkipToHead(IsTriggerHead);
                        break;
                    case "[TRIGGER CHECK]":
                        records.Add(ParseTriggerCheck(cur, tag, unsupported));
                        break;
                    case "[SAVE TARGET OBJECT]":
                        records.Add(ParseSaveTarget(cur, tag, unsupported));
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

        /// <summary>
        /// [GET TARGET] n 键 [HIGH]/[LOW](客户端加载函数 0x0134b27c 起的块,E-155):n 是最多留几个;键是 [RANDOM]、[DISTANCE]、[HP]、[LEVEL]、
        /// [LAST ATTACKSUCCESS]([MP] 也有,这一版不认);除 [RANDOM] 外键后面还要读一个方向标签,[HIGH] 记 0xb、[LOW] 记 0xc,
        /// 都不是的写法客户端报"不用的标签"并按"不是 [HIGH]"当 [LOW] 处理(执行块只问方向参数是不是 0xb)。
        /// 键后面的 [DISTANCE]、[HP] 同时也是触发器的条件标签:不在这里吃掉,主循环会把它们误读成条件(E-155 之前全库约 126 条触发器因此读不懂)。
        /// </summary>
        private static ActRecord ParseGetTarget(ActCursor cur, string tag, List<string> unsupported)
        {
            int max = cur.Int();
            string key = cur.TakeOpen();
            ActTargetKey kind;
            switch (key)
            {
                case "[RANDOM]":
                    kind = ActTargetKey.Random;
                    break;
                case "[DISTANCE]":
                    kind = ActTargetKey.Distance;
                    break;
                case "[HP]":
                    kind = ActTargetKey.Hp;
                    break;
                case "[LEVEL]":
                    kind = ActTargetKey.Level;
                    break;
                case "[LAST ATTACKSUCCESS]":
                    kind = ActTargetKey.LastAttackSuccess;
                    break;
                default:
                {
                    string name = tag + " " + (key ?? "?");
                    AddUnsupportedTag(unsupported, name);
                    cur.SkipToHead(IsTriggerHead);
                    return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = name };
                }
            }
            bool high = false;
            if (kind != ActTargetKey.Random)
            {
                if (cur.AtOpen("[HIGH]"))
                {
                    cur.Skip();
                    high = true;
                }
                else if (cur.AtOpen("[LOW]"))
                {
                    cur.Skip();
                }
            }
            return new ActRecord { Kind = ActRecordKind.GetTarget, Tag = tag, Number = max, TargetKey = kind, Descending = high };
        }

        /// <summary>
        /// [CHECK DISTANCE] x 范围 y 范围(客户端条件码 0x3e,加载块读两个整数,判定块在 0x010d44f4,E-156)。
        /// 后面常跟的 [ZPOS]、[BACKSIDE]、[IS TARGET STATE] 等是各自独立的条件标签,不在这里吃。
        /// </summary>
        private static ActRecord ParseCheckDistance(ActCursor cur, string tag, List<string> unsupported)
        {
            if (cur.AtNumber)
            {
                int x = cur.Int();
                if (cur.AtNumber)
                {
                    int y = cur.Int();
                    return new ActRecord { Kind = ActRecordKind.CheckDistance, Tag = tag, Ints = new[] { x, y } };
                }
            }
            AddUnsupportedTag(unsupported, tag);
            cur.SkipToHead(IsTriggerHead);
            return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
        }

        /// <summary>
        /// [TRIGGER CHECK] [种类 [动作序号]] 触发器序号 [ON]/[OFF](客户端条件码 0x2e,加载块 0x0134b3xx 起,判定块 0x010d84fe 起,E-156)。
        /// 种类的写法与 [TRIGGER] 行为一样,只是这里不认 [DEFAULT](加载块的种类表里没有它,客户端载入报错)。[ON] 记 0、[OFF] 记 1,别的写法客户端报错。
        /// </summary>
        private static ActRecord ParseTriggerCheck(ActCursor cur, string tag, List<string> unsupported)
        {
            if (TryParseTriggerPool(cur, false, out ActTriggerPool pool, out int slot, out int n))
            {
                bool on = cur.AtOpen("[ON]");
                if (on || cur.AtOpen("[OFF]"))
                {
                    cur.Skip();
                    return new ActRecord
                    {
                        Kind = ActRecordKind.TriggerCheck, Tag = tag, Number = n, Flag = on, TriggerPool = pool, TriggerSlot = slot,
                    };
                }
            }
            AddUnsupportedTag(unsupported, tag);
            cur.SkipToHead(IsTriggerHead);
            return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
        }

        /// <summary>
        /// [SAVE TARGET OBJECT] [选择器] 槽号(客户端加载块 0x0134xxxx,记录码 0x3d;E-157)。选择器只认六个标签:
        /// [TARGET]、[SUMMON MASTER]、[LAST ATTACKSUCCESS]、[CHARACTER ATTACKSUCCESS]、[LAST ATTACKER]、[LAST ACTIVE ATTACKER];
        /// 别的标签(含 [CHECKUP OBJECT] 与全库 4 处笔误 [LAST ATTACKSUUCCESS])被当作"选择器标签"吃掉并落到默认的工作集;没写标签也是工作集。
        /// 后面必须是槽号;碰上别的记录头(缺槽号)读不懂。
        /// </summary>
        private static ActRecord ParseSaveTarget(ActCursor cur, string tag, List<string> unsupported)
        {
            ActSelector selector = ActSelector.WorkingSet;
            if (cur.AtOpen(out string name) && !IsTriggerHead(name))
            {
                cur.Skip();
                switch (name)
                {
                    case "[TARGET]":
                        selector = ActSelector.Target;
                        break;
                    case "[SUMMON MASTER]":
                        selector = ActSelector.SummonMaster;
                        break;
                    case "[LAST ATTACKSUCCESS]":
                        selector = ActSelector.LastAttackSuccess;
                        break;
                    case "[CHARACTER ATTACKSUCCESS]":
                        selector = ActSelector.CharacterAttackSuccess;
                        break;
                    case "[LAST ATTACKER]":
                        selector = ActSelector.LastAttacker;
                        break;
                    case "[LAST ACTIVE ATTACKER]":
                        selector = ActSelector.LastActiveAttacker;
                        break;
                }
            }
            if (cur.TryInt(out int slot))
            {
                return new ActRecord { Kind = ActRecordKind.SaveTarget, Tag = tag, Selector = selector, Number = slot };
            }
            AddUnsupportedTag(unsupported, tag);
            cur.SkipToHead(IsTriggerHead);
            return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
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
                // [LOAD TARGET] 槽号:取拥有者槽表里存下的那个对象(客户端选择器码 0x15,取出块在 0x010d25e0 里;E-157)
                if (!cur.TryInt(out int slot))
                {
                    AddUnsupportedTag(unsupported, rec.Tag);
                    cur.SkipToHead(IsTriggerHead);
                    return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = rec.Tag };
                }
                rec.PartyIndex = slot;
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
            else if (sel != null && Selectors.TryGetValue(sel, out ActSelector s))
            {
                rec.Selector = s;
                if (s == ActSelector.LoadTarget)
                {
                    // [DO BEHAVIOR] [LOAD TARGET] 槽号 行为组号(E-157)
                    if (!cur.TryInt(out int slot))
                    {
                        AddUnsupportedTag(unsupported, tag + " [LOAD TARGET] (no slot)");
                        cur.SkipToHead(IsTriggerHead);
                        return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
                    }
                    rec.PartyIndex = slot;
                }
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

        /// <summary>[DELAY DO BEHAVIOR] 选择器 行为组号 延迟毫秒(E-132;全库 267 处,第二个数是 100 到几千的延迟)。</summary>
        private static ActRecord ParseDelayDo(ActCursor cur, string tag, List<string> unsupported)
        {
            ActRecord rec = ParseDo(cur, tag, unsupported);
            if (rec.Kind == ActRecordKind.Unsupported)
            {
                return rec;
            }
            if (!cur.TryInt(out int delay))
            {
                AddUnsupportedTag(unsupported, tag + " (no delay)");
                cur.SkipToHead(IsTriggerHead);
                return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
            }
            rec.Kind = ActRecordKind.DelayDoBehavior;
            rec.Number = delay;
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
                case "[CHANGE ACTION FILE]":
                    return ParseChangeActionFile(cur, tag);
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
                    return ParseTriggerSwitch(cur, tag);
                case "[SET FRAME]":
                    return new ActBehavior { Kind = ActBehaviorKind.SetFrame, Tag = tag, Frame = cur.Int() };
                case "[SET DAMAGE BOX]":
                {
                    // 档位是同一行里可选的 [ON]/[OFF]/[RESET];没写档位的行客户端只关盒,和 [OFF] 一样(E-133)
                    ActDamageBoxMode mode = ActDamageBoxMode.Off;
                    if (cur.AtOpen("[ON]"))
                    {
                        cur.Skip();
                        mode = ActDamageBoxMode.On;
                    }
                    else if (cur.AtOpen("[OFF]"))
                    {
                        cur.Skip();
                    }
                    else if (cur.AtOpen("[RESET]"))
                    {
                        cur.Skip();
                        mode = ActDamageBoxMode.Reset;
                    }
                    return new ActBehavior { Kind = ActBehaviorKind.SetDamageBox, Tag = tag, DamageBox = mode };
                }
                case "[CHANGE FLOATING HEIGHT]":
                    return new ActBehavior { Kind = ActBehaviorKind.SetFloatingHeight, Tag = tag, Height = cur.Int() };
                case "[SHAKING]":
                {
                    int strength = cur.Int();
                    return new ActBehavior { Kind = ActBehaviorKind.Shake, Tag = tag, ShakeStrength = strength, ShakeMs = cur.Int() };
                }
                case "[TELEPORT]":
                    return ParseTeleport(cur, tag);
                case "[SET POS FORCE]":
                    return ParseSetPosForce(cur, tag);
                case "[SUMMON MONSTER]":
                    return ParseSummon(cur, tag);
                case "[SET HP]":
                {
                    // [SET HP] 一个值表达式(全库 65 处;加载块 0x0133dcfb,E-134)
                    ActValue hp = cur.Value();
                    return hp == null
                        ? new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag }
                        : new ActBehavior { Kind = ActBehaviorKind.SetHp, Tag = tag, Hp = hp };
                }
                case "[RESTORE]":
                {
                    // [RESTORE] [HP]|[MP] 数量 [%]|[POINT](加载块 0x0133b455,执行块 0x010b8c89;E-134):种类只认 HP 与 MP,数量是值表达式(可负),
                    // 后面跟 [%] 才是百分比(客户端的符号码 7),别的符号([POINT] 等)当点数
                    string kind = cur.TakeOpen();
                    ActValue amount = kind == "[HP]" || kind == "[MP]" ? cur.Value() : null;
                    if (amount == null)
                    {
                        return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                    }
                    return new ActBehavior
                    {
                        Kind = ActBehaviorKind.Restore,
                        Tag = tag,
                        RestoreMp = kind == "[MP]",
                        RestoreAmount = amount,
                        RestorePercent = cur.AtOpen("[%]"),
                    };
                }
                case "[ACTIVE STATUS]":
                    return ParseActiveStatus(cur, tag);
                case "[DIM]":
                {
                    // [DIM] "名字" [=] 表达式 [/DIM](E-132;全库只有赋值,没有别的比较符)
                    string name = cur.TakeString();
                    if (name != null && cur.AtOpen("[=]"))
                    {
                        cur.Skip();
                        if (cur.TryExpression(out ActExpression value))
                        {
                            return new ActBehavior { Kind = ActBehaviorKind.SetVariable, Tag = tag, VariableName = name, Expression = value };
                        }
                    }
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                }
                case "[APPENDAGE]":
                    return ParseAppendage(cur, tag, false);
                case "[DELETE APPENDAGE]":
                    return ParseAppendage(cur, tag, true);
                default:
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
        }

        /// <summary>
        /// [APPENDAGE] 持续毫秒 { [属性标签] 操作符 数值 }… 与 [DELETE APPENDAGE] 同一种写法(加载块 0x013406f6 与 0x01341cea,E-161):
        /// 开头一个数(持续毫秒),然后一串项,每项三个记号:属性标签(字典 0x0125aa00;不认得的标签客户端也造一项,类型 −1)、操作符(是 [%] 才按比例,
        /// 别的都按点数)、数值;一项后面还可能多跟几个数(全库有 12 项,都是防御类的点数写法多带一个数,客户端加载块不读它,这里读掉不用)。
        /// 缺操作符或数值的写法(全库没有)读到此为止,已读的项照用。
        /// </summary>
        private static ActBehavior ParseAppendage(ActCursor cur, string tag, bool delete)
        {
            int duration = cur.Int();
            var changes = new List<StatChange>();
            while (cur.AtOpen(out string stat) && !IsBehaviorHead(stat))
            {
                cur.Skip();
                if (!cur.AtOpen(out string op) || IsBehaviorHead(op))
                {
                    break;
                }
                cur.Skip();
                if (!cur.TryInt(out int value))
                {
                    break;
                }
                cur.Ints();
                changes.Add(new StatChange(StatChangeTypes.FromTag(stat), op == "[%]", value));
            }
            return new ActBehavior
            {
                Kind = delete ? ActBehaviorKind.DeleteAppendage : ActBehaviorKind.Appendage,
                Tag = tag,
                StatChanges = changes,
                StatDurationMs = duration,
            };
        }

        /// <summary>
        /// [TRIGGER] [种类 [动作序号]] 触发器序号 [ON]/[OFF]/[RESET](客户端加载分支 0x0133b043 起,E-145)。
        /// 没写种类:当前 .act 自己;[ATTACK]、[CUSTOM] 先写动作序号,其余种类每族只有一个动作。
        /// 读不全(缺序号、不认得的种类或命令)的行当读不懂:客户端那边是载入报错。
        /// </summary>
        private static ActBehavior ParseTriggerSwitch(ActCursor cur, string tag)
        {
            var unsupported = new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            if (!TryParseTriggerPool(cur, true, out ActTriggerPool pool, out int slot, out int n))
            {
                return unsupported;
            }
            ActTriggerCommand command;
            switch (cur.TakeOpen())
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
                    return unsupported;
            }
            return new ActBehavior
            {
                Kind = ActBehaviorKind.TriggerSwitch, Tag = tag, TriggerIndex = n, Command = command, TriggerPool = pool, TriggerSlot = slot,
            };
        }

        /// <summary>
        /// [TRIGGER] 与 [TRIGGER CHECK] 共用的"哪一族动作的第几个触发器"写法:[种类 [动作序号]] 触发器序号(E-145、E-156)。
        /// 没写种类:当前 .act 自己;[ATTACK]、[CUSTOM] 先写动作序号,其余种类每族只有一个动作;<paramref name="allowDefault"/> 控制认不认 [DEFAULT]。
        /// 缺序号、不认得的种类返回 false。
        /// </summary>
        private static bool TryParseTriggerPool(ActCursor cur, bool allowDefault, out ActTriggerPool pool, out int slot, out int index)
        {
            pool = ActTriggerPool.Own;
            slot = -1;
            index = 0;
            if (!cur.AtNumber)
            {
                switch (cur.TakeOpen())
                {
                    case "[MOVE]":
                        pool = ActTriggerPool.Move;
                        break;
                    case "[STAY]":
                        pool = ActTriggerPool.Stay;
                        break;
                    case "[SIT]":
                        pool = ActTriggerPool.Sit;
                        break;
                    case "[DOWN]":
                        pool = ActTriggerPool.Down;
                        break;
                    case "[DEFAULT]":
                        if (!allowDefault)
                        {
                            return false;
                        }
                        pool = ActTriggerPool.Default;
                        break;
                    case "[PROC]":
                        pool = ActTriggerPool.Proc;
                        break;
                    case "[ATTACK]":
                        pool = ActTriggerPool.Attack;
                        break;
                    case "[CUSTOM]":
                        pool = ActTriggerPool.Custom;
                        break;
                    default:
                        return false;
                }
                if (pool == ActTriggerPool.Attack || pool == ActTriggerPool.Custom)
                {
                    if (!cur.AtNumber)
                    {
                        return false;
                    }
                    slot = cur.Int();
                }
                if (!cur.AtNumber)
                {
                    return false;
                }
            }
            index = cur.Int();
            return true;
        }

        /// <summary>
        /// [ACTIVE STATUS] [种类] 概率 等级 持续毫秒 附加整数…(客户端执行块 0x010bbec6,E-144):种类是一个小写开标签(与 .atk 的状态槽同名),
        /// 也可以写成带引号的字符串(客户端读的是"类型记号或字符串",真实数据里有一处这样写),
        /// 概率读成浮点,等级与持续时间是整数,附加整数的个数按种类定(与 <see cref="AttackInfoLoader.ExtraIntegers"/> 一致,执行块里每个种类分支取的 ints 个数同样如此)。
        /// 种类认不出来就记成不支持(客户端的种类分支只到 17);数不够当缺省 0。
        /// </summary>
        private static ActBehavior ParseActiveStatus(ActCursor cur, string tag)
        {
            string name = cur.TakeOpen() ?? cur.TakeString();
            int kind = name == null ? -1 : Array.IndexOf(AttackInfoLoader.StatusNames, name);
            if (kind < 0)
            {
                return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            var extra = new int[AttackInfoLoader.ExtraIntegers(name)];
            int expected = 3 + extra.Length;
            int pending = cur.PendingNumbers();
            cur.TryFloat(out float probability);
            int level = cur.Int();
            int duration = cur.Int();
            for (int i = 0; i < extra.Length; i++)
            {
                extra[i] = cur.Int();
            }
            return new ActBehavior
            {
                Kind = ActBehaviorKind.ActiveStatus,
                Tag = tag,
                Status = ActiveStatusEntry.Create((ActiveStatusKind)kind, probability, level, duration, extra),
                StatusSpill = Math.Max(0, pending - expected),
                StatusMissing = Math.Max(0, expected - pending),
            };
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

        /// <summary>
        /// [TELEPORT] [POS] x y z 标志… [/TELEPORT](E-133):位置三元组是记录的前三个参数,其后的标志逐个读,后写的算(见 <see cref="ActBehavior.MoveMe"/>)。
        /// 没写 [POS] 的当 0(推断:客户端的执行块不管有没有 [POS] 都读三个值,缺的读到什么没有核对;全库样本里每条都写了)。
        /// </summary>
        private static ActBehavior ParseTeleport(ActCursor cur, string tag)
        {
            var b = new ActBehavior { Kind = ActBehaviorKind.Teleport, Tag = tag };
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
                    case "[POS]":
                        b.PosX = cur.Value();
                        b.PosY = cur.Value();
                        b.PosZ = cur.Value();
                        break;
                    case "[USE MY DIRECTION]":
                        b.UseMyDirection = true;
                        break;
                    case "[USE MY BASEPOS]":
                        b.UseMyBasePos = true;
                        break;
                    case "[NOT USE OBJECT DIRECTION]":
                        b.NotUseDirection = true;
                        break;
                    case "[NOT USE OBJECT ZPOS]":
                        b.NotUseZ = true;
                        break;
                    case "[USE MAP POS]":
                        b.UseMapPos = true;
                        break;
                    case "[MOVE ME]":
                        b.MoveMe = true;
                        break;
                    case "[MOVE TARGET]":
                        b.MoveMe = false;
                        break;
                    case "[ALLOW FIXTURE]":
                        b.AllowFixture = true;
                        break;
                    case "[USE EFFECT]":
                        b.UseEffect = true;
                        break;
                }
            }
            b.PosX = b.PosX ?? ActValue.Literal(0);
            b.PosY = b.PosY ?? ActValue.Literal(0);
            b.PosZ = b.PosZ ?? ActValue.Literal(0);
            return b;
        }

        private static readonly Dictionary<string, ActForceCode> ForceCodes = new Dictionary<string, ActForceCode>(StringComparer.Ordinal)
        {
            { "[X START]", ActForceCode.XStart },
            { "[Y START]", ActForceCode.YStart },
            { "[Z START]", ActForceCode.ZStart },
            { "[X END]", ActForceCode.XEnd },
            { "[Y END]", ActForceCode.YEnd },
            { "[Z END]", ActForceCode.ZEnd },
            { "[USE MY DIRECTION]", ActForceCode.UseMyDirection },
            { "[NOT USE OBJECT DIRECTION]", ActForceCode.NotUseObjectDirection },
            { "[USE TARGET POS]", ActForceCode.UseTargetPos },
            { "[USE MY BASEPOS]", ActForceCode.UseMyBasePos },
            { "[USE MAP POS]", ActForceCode.UseMapPos },
            { "[MOVE UNIFORM]", ActForceCode.MoveUniform },
            { "[MOVE ACCELATE]", ActForceCode.MoveAccelate },
            { "[MOVE REDUCE]", ActForceCode.MoveReduce },
            { "[MOVE TARGET]", ActForceCode.MoveTarget },
            { "[MOVE ME]", ActForceCode.MoveMe },
        };

        /// <summary>
        /// [SET POS FORCE] 记号… [/SET POS FORCE](装载函数 0x01340140 逐个比标签、把码推进向量,执行块 0x010bfa89 按顺序读,E-152):
        /// 记号按出现顺序全部留下(<see cref="ActBehavior.ForceTokens"/>),语义在运行器里按客户端的"后写的算、计数累加"规则读。
        /// 0 到 5 号后面是一个值表达式;读不出值(全库 3 个块写成 [LASTATTACK POSITION],那是客户端值读取函数 0x01330240 的另一种写法,这一版没做)
        /// 或带 [MOVE ACCELATE](插值函数没读)的块整个记成不支持。
        /// </summary>
        private static ActBehavior ParseSetPosForce(ActCursor cur, string tag)
        {
            var tokens = new List<ActForceToken>();
            bool unsupported = false;
            while (!cur.End && !cur.TakeClose(tag))
            {
                string t = cur.TakeOpen();
                if (t == null)
                {
                    cur.Skip();
                    continue;
                }
                if (!ForceCodes.TryGetValue(t, out ActForceCode code))
                {
                    if (t == "[LASTATTACK POSITION]")
                    {
                        unsupported = true;
                    }
                    continue;
                }
                if (code == ActForceCode.MoveAccelate)
                {
                    unsupported = true;
                    continue;
                }
                if (code <= ActForceCode.ZEnd)
                {
                    ActValue value = cur.Value();
                    if (value == null)
                    {
                        unsupported = true;
                        continue;
                    }
                    tokens.Add(new ActForceToken(code, value));
                }
                else
                {
                    tokens.Add(new ActForceToken(code, null));
                }
            }
            if (unsupported)
            {
                return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            return new ActBehavior { Kind = ActBehaviorKind.SetPosForce, Tag = tag, ForceTokens = tokens.ToArray() };
        }

        /// <summary>
        /// [SUMMON MONSTER] [INDEX] 编号 [LEVEL] 等级 [POS] x y z 标志… [/SUMMON MONSTER](加载块的子标签表见 E-134):
        /// 位置三元组与创建被动对象的写法相同,缺的当 0;其余是标志与带数的标签。[JUMP OUT] 后面跟的三个值与方向标签这一版不读(效果只记下),
        /// 它们落在循环里被当作游离标签跳过。没有 [INDEX] 的放不出任何东西,记成不支持。
        /// </summary>
        private static ActBehavior ParseSummon(ActCursor cur, string tag)
        {
            var b = new ActBehavior { Kind = ActBehaviorKind.Summon, Tag = tag };
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
                    case "[USE TARGET TEAM]":
                        b.UseTargetTeam = true;
                        break;
                    case "[USE MY DIRECTION]":
                        b.UseMyDirection = true;
                        break;
                    case "[USE MY BASEPOS]":
                        b.UseMyBasePos = true;
                        break;
                    case "[NOT USE OBJECT DIRECTION]":
                        b.NotUseDirection = true;
                        break;
                    case "[USE MAP POS]":
                        b.UseMapPos = true;
                        break;
                    case "[USE OBJECT ZPOS]":
                        b.UseObjectZ = true;
                        break;
                    case "[TEAM]":
                        b.Team = cur.Int(-1);
                        break;
                    case "[SUMMON TIME]":
                        b.SummonTimeMs = Math.Max(0, cur.Int());
                        break;
                    case "[SUMMON MARK]":
                    case "[COLLISION GROUP]":
                        cur.Ints();     // 标记与碰撞组:读掉后面的整数,这一版不用
                        break;
                    case "[NO EFFECT]":
                        b.SummonEffect = ActSummonEffect.None;
                        break;
                    case "[JUMP OUT]":
                        b.SummonEffect = ActSummonEffect.JumpOut;
                        break;
                    case "[MAGIC CIRCLE]":
                        b.SummonEffect = ActSummonEffect.MagicCircle;
                        break;
                    default:
                        break;
                }
            }
            if (b.Index == null)
            {
                b.Kind = ActBehaviorKind.Unsupported;
            }
            else
            {
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

        /// <summary>
        /// [CHANGE ACTION FILE]:紧跟一个族关键字(与 [SET ACTION] 同一套写法:[STAY] [MOVE] [DAMAGE] n [ATTACK] n [DOWN] [SIT] [OVERTURN] [MOVEBACK] [DASH] [DASHATTACK] [JUMP] [JUMPATTACK] [CUSTOM] n),
        /// 带序号的族可以不写序号(客户端读不到就是 0),最后是新 .act 的路径字符串(E-151)。族关键字或路径缺失的写法按没做处理。
        /// </summary>
        private static ActBehavior ParseChangeActionFile(ActCursor cur, string tag)
        {
            ActFileSlot slot;
            bool indexed = false;
            switch (cur.TakeOpen())
            {
                case "[STAY]":
                    slot = ActFileSlot.Stay;
                    break;
                case "[MOVE]":
                    slot = ActFileSlot.Move;
                    break;
                case "[MOVEBACK]":
                    slot = ActFileSlot.MoveBack;
                    break;
                case "[DASH]":
                    slot = ActFileSlot.Dash;
                    break;
                case "[DASHATTACK]":
                    slot = ActFileSlot.DashAttack;
                    break;
                case "[SIT]":
                    slot = ActFileSlot.Sit;
                    break;
                case "[DAMAGE]":
                    slot = ActFileSlot.Damage;
                    indexed = true;
                    break;
                case "[DOWN]":
                    slot = ActFileSlot.Down;
                    break;
                case "[OVERTURN]":
                    slot = ActFileSlot.Overturn;
                    break;
                case "[JUMP]":
                    slot = ActFileSlot.Jump;
                    break;
                case "[JUMPATTACK]":
                    slot = ActFileSlot.JumpAttack;
                    break;
                case "[ATTACK]":
                    slot = ActFileSlot.Attack;
                    indexed = true;
                    break;
                case "[CUSTOM]":
                    slot = ActFileSlot.Custom;
                    indexed = true;
                    break;
                default:
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };     // [LAST] 等没有族码的写法:客户端的族码表里没有它们
            }
            int index = indexed ? cur.Int() : 0;
            string path = cur.TakeString();
            if (string.IsNullOrEmpty(path))
            {
                return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            return new ActBehavior { Kind = ActBehaviorKind.ChangeActionFile, Tag = tag, FileSlot = slot, FileIndex = index, FilePath = path };
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
