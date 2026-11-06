using System;
using System.Collections.Generic;

namespace Dnf.Game.Act
{
    /// <summary>
    /// M107 的编译侧:符号、队伍、区域、受击开关一族条件与行为。
    /// 写法与客户端加载块逐项对齐;读不全、多写、不认得的子标签一律记成"读不懂"(整条触发器不触发 / 行为按空操作),
    /// 不替客户端猜没读通的路径。
    /// </summary>
    internal static partial class ActCompiler
    {
        /// <summary>[CHECK PARTYMEMBERS STATE] 的子码(客户端加载块把 [ALL ALIVE]、[ALL DEATH]、[EVEN ONE DEATH] 记成 0x4e、0x4f、0x50)。</summary>
        internal const int PartyAllAlive = 0x4e;

        internal const int PartyAllDeath = 0x4f;

        internal const int PartyEvenOneDeath = 0x50;

        /// <summary>[IS IN AREA] 的坐标写法:绝对。</summary>
        internal const int CoordinateAbsolute = 0;

        /// <summary>[IS IN AREA] 的坐标写法:相对拥有者(加上拥有者的整数坐标)。</summary>
        internal const int CoordinateRelative = 1;

        /// <summary>[IS IN AREA] 的坐标写法:写了别的字符串(客户端执行块直接判整条条件为假)。</summary>
        internal const int CoordinateInvalid = 2;

        /// <summary>条件标签入口:调用方已经吃掉标签本身;读不懂时记 Unsupported 并跳到下一个记录头。</summary>
        private static void ParseSymbolRecord(ActCursor cur, string tag, List<ActRecord> records, List<string> unsupported)
        {
            ActRecord rec;
            switch (tag)
            {
                case "[CHECK SYMBOL]":
                    rec = ParseSymbolPair(cur, tag, ActRecordKind.CheckSymbol);
                    break;
                case "[CHECK PARTY SYMBOL]":
                    rec = ParseSymbolPair(cur, tag, ActRecordKind.CheckPartySymbol);
                    break;
                case "[CHECK PARTY SLOT]":
                    rec = ParseSymbolPair(cur, tag, ActRecordKind.CheckPartySlot);      // A = 槽,B = 标志(全库一处,写法正是两个整数)
                    break;
                case "[CHECK PLAYER NUM]":
                    rec = ParseCheckPlayerNum(cur, tag);
                    break;
                case "[CHECK PARTYMEMBERS STATE]":
                    rec = ParseCheckPartyState(cur, tag);
                    break;
                case "[AREA]":
                    rec = ParseArea(cur, tag);
                    break;
                case "[IS IN AREA]":
                    rec = ParseIsInArea(cur, tag);
                    break;
                default:
                    // [IS DAMAGE ENABLE]:客户端执行块不读参数(0x010d5c09)
                    rec = new ActRecord { Kind = ActRecordKind.IsDamageEnable, Tag = tag };
                    break;
            }
            if (rec == null)
            {
                AddUnsupported(records, unsupported, tag);
                cur.SkipToHead(IsTriggerHead);
                return;
            }
            records.Add(rec);
        }

        /// <summary>[CHECK SYMBOL] / [CHECK PARTY SYMBOL] 模式 值:恰好两个整数(全库 262 处都是);少写或多写读不懂。</summary>
        private static ActRecord ParseSymbolPair(ActCursor cur, string tag, ActRecordKind kind)
        {
            if (!cur.TryInt(out int mode) || !cur.TryInt(out int value) || cur.AtNumber)
            {
                return null;
            }
            return new ActRecord { Kind = kind, Tag = tag, A = mode, B = value };
        }

        /// <summary>
        /// [CHECK PLAYER NUM] 块(加载块 0x0134bc83):子标签 [COUNT] n、[INCLUDE DEAD] 0/1,次序随意,后写的盖前写的;
        /// 没写的取缺省(人数 0、不含死者)。一个子标签也没有时客户端报错并判假(执行块 0x010d8cbb),这里记成空的 Ints(运行时恒假)。
        /// 不认得的子标签客户端走报错路径、后果没读通,整条读不懂。
        /// </summary>
        private static ActRecord ParseCheckPlayerNum(ActCursor cur, string tag)
        {
            int count = 0;
            bool includeDead = false;
            int subTags = 0;
            while (cur.AtOpen(out string sub) && !IsTriggerHead(sub))
            {
                cur.Skip();
                if (sub == "[COUNT]")
                {
                    if (!cur.TryInt(out count))
                    {
                        return null;
                    }
                }
                else if (sub == "[INCLUDE DEAD]")
                {
                    if (!cur.TryInt(out int flag) || (flag != 0 && flag != 1))
                    {
                        return null;
                    }
                    includeDead = flag == 1;
                }
                else
                {
                    return null;
                }
                subTags++;
            }
            cur.TakeClose(tag);
            return new ActRecord
            {
                Kind = ActRecordKind.CheckPlayerNum,
                Tag = tag,
                Ints = subTags == 0 ? Array.Empty<int>() : new[] { count, includeDead ? 1 : 0 },
            };
        }

        /// <summary>[CHECK PARTYMEMBERS STATE] 块(加载块 0x0134a8b0):恰好一个子标签;零个或多个、不认得的都读不懂。</summary>
        private static ActRecord ParseCheckPartyState(ActCursor cur, string tag)
        {
            int code = 0;
            while (cur.AtOpen(out string sub) && !IsTriggerHead(sub))
            {
                int c;
                switch (sub)
                {
                    case "[ALL ALIVE]":
                        c = PartyAllAlive;
                        break;
                    case "[ALL DEATH]":
                        c = PartyAllDeath;
                        break;
                    case "[EVEN ONE DEATH]":
                        c = PartyEvenOneDeath;
                        break;
                    default:
                        return null;
                }
                if (code != 0)
                {
                    return null;
                }
                code = c;
                cur.Skip();
            }
            if (code == 0)
            {
                return null;
            }
            cur.TakeClose(tag);
            return new ActRecord { Kind = ActRecordKind.CheckPartyMembersState, Tag = tag, Number = code };
        }

        /// <summary>
        /// [AREA] a b c d:客户端加载分支(0x01332dd2 起)恰好读四个整数;多写的整数这个分支不读,处理方式没读通,这一版不认(记读不懂)。
        /// </summary>
        private static ActRecord ParseArea(ActCursor cur, string tag)
        {
            int[] v = cur.Ints();
            if (v.Length != 4)
            {
                return null;
            }
            return new ActRecord { Kind = ActRecordKind.Area, Tag = tag, Ints = v };
        }

        /// <summary>
        /// [IS IN AREA] 块(加载块 0x0134a0c5):[POS] x y z、[RANGE] x y z、[COORDINATE] "RELATIVE"/"ABSOLUTE",次序随意,后写的盖前写的;
        /// 没写的取缺省(中心 0、半宽 0、绝对)。坐标写成别的字符串时执行块判整条条件为假(<see cref="CoordinateInvalid"/>);
        /// 只是大小写不同的写法客户端查表是否区分大小写没读通,记读不懂。一个子标签也没有的写法没读通,记读不懂。
        /// </summary>
        private static ActRecord ParseIsInArea(ActCursor cur, string tag)
        {
            var v = new int[6];
            int coordinate = CoordinateAbsolute;
            int subTags = 0;
            while (cur.AtOpen(out string sub) && !IsTriggerHead(sub))
            {
                cur.Skip();
                switch (sub)
                {
                    case "[POS]":
                    case "[RANGE]":
                    {
                        int[] xyz = cur.Ints();
                        if (xyz.Length != 3)
                        {
                            return null;
                        }
                        Array.Copy(xyz, 0, v, sub == "[POS]" ? 0 : 3, 3);
                        break;
                    }
                    case "[COORDINATE]":
                    {
                        string text = cur.TakeString();
                        if (text == null)
                        {
                            return null;
                        }
                        if (text == "RELATIVE")
                        {
                            coordinate = CoordinateRelative;
                        }
                        else if (text == "ABSOLUTE")
                        {
                            coordinate = CoordinateAbsolute;
                        }
                        else if (string.Equals(text, "RELATIVE", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(text, "ABSOLUTE", StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }
                        else
                        {
                            coordinate = CoordinateInvalid;
                        }
                        break;
                    }
                    default:
                        return null;
                }
                subTags++;
            }
            if (subTags == 0)
            {
                return null;
            }
            cur.TakeClose(tag);
            return new ActRecord { Kind = ActRecordKind.IsInArea, Tag = tag, Ints = v, Number = coordinate };
        }

        /// <summary>行为标签入口:[SET SYMBOL]、[SET SYMBOL OPTION]、[SET PARTY SYMBOL] 恰好一个整数;[ABS CASTING] 至多两个整数(少写按 -1),多写读不懂。</summary>
        private static ActBehavior ParseSymbolBehavior(ActCursor cur, string tag)
        {
            switch (tag)
            {
                case "[SET SYMBOL]":
                case "[SET SYMBOL OPTION]":
                case "[SET PARTY SYMBOL]":
                    if (cur.TryInt(out int value) && !cur.AtNumber)
                    {
                        ActBehaviorKind kind = tag == "[SET SYMBOL]"
                            ? ActBehaviorKind.SetSymbol
                            : tag == "[SET SYMBOL OPTION]" ? ActBehaviorKind.SetSymbolOption : ActBehaviorKind.SetPartySymbol;
                        return new ActBehavior { Kind = kind, Tag = tag, SymbolValue = value };
                    }
                    break;
                case "[ABS CASTING]":
                {
                    // 至多两个整数;少写的按 -1 读(参数读取 0x0107b2e0 越界返回 -1,执行块对负数不起读条条,所以少写 = 什么都不做)
                    int[] v = cur.Ints();
                    if (v.Length <= 2)
                    {
                        return new ActBehavior
                        {
                            Kind = ActBehaviorKind.AbsCasting,
                            Tag = tag,
                            AbsCastFirst = v.Length > 0 ? v[0] : -1,
                            AbsCastSecond = v.Length > 1 ? v[1] : -1,
                        };
                    }
                    break;
                }
            }
            return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
        }
    }
}
