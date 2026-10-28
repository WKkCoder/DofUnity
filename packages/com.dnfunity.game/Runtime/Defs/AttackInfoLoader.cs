using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>读一个 .atk 时遇到的不规整之处的统计(原版对这些情况都不报错,而是按各自的宽容规则继续或停止)。</summary>
    public sealed class AttackInfoLoadReport
    {
        /// <summary>顶层遇到不认识的标签(整个标签被忽略,它后面的值会成为游离标记)。</summary>
        public int UnknownTags { get; internal set; }

        /// <summary>第一个出现的不认识的标签名,便于排查。</summary>
        public string FirstUnknownTag { get; internal set; }

        /// <summary>关键字位置上的词不在认识的集合里(字段保持默认值)。</summary>
        public int UnknownKeywords { get; internal set; }

        /// <summary>整数/浮点/字符串位置读到了别的标记(那个标记已被消耗,字段取 0 或保持原值)。</summary>
        public int BadValues { get; internal set; }

        /// <summary>被丢弃的游离标记个数。</summary>
        public int DiscardedTokens { get; internal set; }

        /// <summary>顶层读到游离标记、而它后面还有标签:原版在这里停止加载,后面的内容全部读不到。</summary>
        public bool StoppedAtStrayToken { get; internal set; }

        /// <summary>被整节跳过的模块节([pvp] 等,副本模块下不生效)个数。</summary>
        public int SkippedModuleSections { get; internal set; }

        /// <summary>模块节没有遇到结束标签就到了文件末尾。</summary>
        public bool UnterminatedModuleSection { get; internal set; }

        /// <summary>第 4 个及以后的 [active status](只有三个槽;原版在这里写日志,具体后续行为未读清,这里把它的类型与数读完以保持标记流同步,值扔掉并计数)。</summary>
        public int StatusOverflow { get; internal set; }

        /// <summary>整个文件读下来没有任何不规整之处。</summary>
        public bool Clean =>
            UnknownTags == 0 && UnknownKeywords == 0 && BadValues == 0 && DiscardedTokens == 0 && !StoppedAtStrayToken
            && !UnterminatedModuleSection && StatusOverflow == 0;
    }

    /// <summary>
    /// .atk 加载器:逐标记地复现原版加载函数的读法(证据 E-047)——先填默认值,再循环"读标签 → 处理模块节 → 按标签读值"。
    /// 这里只实现"副本"模块:[pvp]、[warroom]、[assault]、[death tower]、[boss tower] 到各自结束标签之间的内容整节跳过,
    /// [dungeon] 自身是空操作。(原版的模块号在这份二进制里是写死的 3,即副本;PVP 等模式以后需要时再扩展。)
    /// </summary>
    public static class AttackInfoLoader
    {
        private static readonly Dictionary<string, string> SkippedModules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[pvp]"] = "[/pvp]",
            ["[warroom]"] = "[/warroom]",
            ["[assault]"] = "[/assault]",
            ["[death tower]"] = "[/death tower]",
            ["[boss tower]"] = "[/boss tower]",
        };

        private static readonly string[] StatusNames =
        {
            "[slow]", "[freeze]", "[poison]", "[stun]", "[curse]", "[blind]", "[lightning]", "[stone]", "[sleep]",
            "[burn]", "[weapon break]", "[bleeding]", "[haste]", "[bless]", "[element]", "[confuse]", "[hold]", "[armor break]",
        };

        /// <summary>读完类型后还要再读的整数个数(其余类型没有附加整数)。</summary>
        private static int ExtraIntegers(string status)
        {
            switch (status)
            {
                case "[blind]":
                case "[slow]":
                case "[haste]":
                    return 2;
                case "[stone]":
                case "[confuse]":
                case "[armor break]":
                case "[weapon break]":
                case "[poison]":
                case "[lightning]":
                case "[bleeding]":
                    return 1;
                case "[burn]":
                case "[bless]":
                case "[curse]":
                    return 4;
                default:
                    return 0;
            }
        }

        public static AttackInfoData Load(ScriptCursor cursor, out AttackInfoLoadReport report)
        {
            if (cursor == null)
            {
                throw new ArgumentNullException(nameof(cursor));
            }
            var data = new AttackInfoData();
            var r = new AttackInfoLoadReport();
            int statusCount = 0;

            while (true)
            {
                if (!cursor.ScanType(out string name))
                {
                    r.StoppedAtStrayToken = name != null;
                    break;
                }
                if (name == "[dungeon]" || name == "[/dungeon]")
                {
                    continue;
                }
                if (SkippedModules.TryGetValue(name ?? string.Empty, out string close))
                {
                    r.SkippedModuleSections++;
                    if (!SkipSection(cursor, close))
                    {
                        r.UnterminatedModuleSection = true;
                        break;
                    }
                    continue;
                }
                if (!Dispatch(cursor, data, r, name, ref statusCount))
                {
                    r.UnknownTags++;
                    if (r.FirstUnknownTag == null)
                    {
                        r.FirstUnknownTag = name;
                    }
                }
            }
            r.DiscardedTokens = cursor.DiscardedTokens;
            report = r;
            return data;
        }

        /// <summary>丢弃标记直到遇到结束标签;没遇到就到了文件末尾时返回 false。</summary>
        private static bool SkipSection(ScriptCursor cursor, string close)
        {
            while (true)
            {
                bool clean = cursor.ScanType(out string name, report: false);
                if (name == close)
                {
                    return true;
                }
                if (name == null && !clean)
                {
                    return false;
                }
            }
        }

        // ---- 读值的小工具(失败只计数) ----

        private static int ReadInt(ScriptCursor c, AttackInfoLoadReport r)
        {
            int v = c.ScanInt(out bool ok);
            if (!ok)
            {
                r.BadValues++;
            }
            return v;
        }

        private static float ReadFloat(ScriptCursor c, AttackInfoLoadReport r)
        {
            float v = c.ScanFloat(out bool ok);
            if (!ok)
            {
                r.BadValues++;
            }
            return v;
        }

        private static bool ReadBool(ScriptCursor c, AttackInfoLoadReport r) => ReadInt(c, r) != 0;

        /// <summary>读一个关键字并换成数值;不在表里时返回 false(调用方保持默认值)。</summary>
        private static bool ReadKeyword(ScriptCursor c, AttackInfoLoadReport r, string[] names, out int index)
        {
            string text = c.ScanTypeOrStr();
            index = Array.IndexOf(names, text);
            if (index < 0)
            {
                r.UnknownKeywords++;
                return false;
            }
            return true;
        }

        private static readonly string[] AttackTypes = { "[physic]", "[magic]", "[ignore defense]" };
        private static readonly string[] Reactions = { "[none]", "[damage]", "[down]", "[damage except human]" };
        private static readonly string[] Directions = { "[hit lift up]", "[hit horizon]", "[hit down]" };
        private static readonly string[] HitKinds = { "[blow]", "[cut]", "[burn]", "[etc]", "[mana burn]" };
        private static readonly string[] Bloods = { "[no blood]", "[blood]" };
        private static readonly string[] HitSides = { "[auto]", "[front]", "[back]", "[outer]", "[inner]" };

        /// <summary>按标签读值并写进字段;不认识的标签返回 false(什么也不读)。</summary>
        private static bool Dispatch(ScriptCursor c, AttackInfoData d, AttackInfoLoadReport r, string name, ref int statusCount)
        {
            int index;
            switch (name)
            {
                case "[damage]":
                    d.Damage = ReadFloat(c, r);
                    return true;
                case "[damage increase rate]":
                    d.DamageIncreaseRate = ReadFloat(c, r);
                    return true;
                case "[absolute damage]":
                    d.AbsoluteDamage = ReadInt(c, r);
                    return true;
                case "[damage bonus]":
                    d.DamageBonus = ReadInt(c, r);
                    return true;
                case "[attack type]":
                    if (ReadKeyword(c, r, AttackTypes, out index))
                    {
                        d.AttackType = (AttackKind)index;
                    }
                    return true;
                case "[weapon damage apply]":
                    d.WeaponDamageApply = ReadBool(c, r);
                    return true;
                case "[attack myself]":
                    d.AttackMyself = ReadBool(c, r);
                    return true;
                case "[attack friend]":
                    d.AttackFriend = ReadBool(c, r);
                    return true;
                case "[attack enemy]":
                    d.AttackEnemy = ReadBool(c, r);
                    return true;
                case "[elemental property]":
                    ReadElement(c, d);
                    return true;
                case "[active status]":
                    ReadStatus(c, d, r, ref statusCount);
                    return true;
                case "[damage reaction]":
                    if (ReadKeyword(c, r, Reactions, out index))
                    {
                        d.Reaction = (DamageReaction)index;
                    }
                    return true;
                case "[push aside]":
                    d.PushAside = ReadInt(c, r);
                    return true;
                case "[lift up]":
                    d.LiftUp = ReadInt(c, r);
                    return true;
                case "[critical hit]":
                    d.CriticalHit = ReadFloat(c, r);
                    return true;
                case "[stuck]":
                    d.Stuck = ReadFloat(c, r);
                    return true;
                case "[stuck prob increase]":
                    d.StuckProbIncrease = ReadBool(c, r);
                    return true;
                case "[knuck back]":
                    d.KnockBackType = ReadInt(c, r);
                    if (d.KnockBackType == 3)
                    {
                        d.KnockBackPixels = ScriptCursor.TruncateToInt(ReadFloat(c, r));
                    }
                    return true;
                case "[weakness]":
                    d.WeaknessFirst = ReadFloat(c, r);
                    d.WeaknessSecond = ReadFloat(c, r);
                    return true;
                case "[bounce]":
                    d.Bounce = ReadInt(c, r);
                    return true;
                case "[ignore defense]":
                    d.IgnoreDefense = ReadBool(c, r);
                    return true;
                case "[human damage rate]":
                    d.HumanDamageRate = ReadFloat(c, r);
                    return true;
                case "[fort damage rate]":
                    d.FortDamageRate = ReadFloat(c, r);
                    return true;
                case "[monster damage rate]":
                    d.MonsterDamageRate = ReadFloat(c, r);
                    return true;
                case "[HP drain]":
                    d.HpDrain = ReadBool(c, r);
                    return true;
                case "[MP drain]":
                    d.MpDrain = ReadBool(c, r);
                    return true;
                case "[hit wav]":
                    if (c.ScanStr(out string wav))
                    {
                        d.HitWav = wav;
                    }
                    else
                    {
                        r.BadValues++;
                    }
                    return true;
                case "[attack direction]":
                    if (ReadKeyword(c, r, Directions, out index))
                    {
                        d.Direction = (AttackDirection)index;
                    }
                    return true;
                case "[hit info]":
                    if (ReadKeyword(c, r, HitKinds, out index))
                    {
                        d.HitType = (HitKind)index;
                    }
                    if (ReadKeyword(c, r, Bloods, out index))
                    {
                        d.HitBlood = index == 1;
                    }
                    d.HitValue = ReadInt(c, r);
                    d.HitScale = ReadFloat(c, r);
                    return true;
                case "[is use weapon hit delay rate]":
                    d.UseWeaponHitDelayRate = ReadBool(c, r);
                    return true;
                case "[force hit stun time]":
                    d.ForceHitStunTime = ReadInt(c, r);
                    return true;
                case "[hit direction]":
                    if (ReadKeyword(c, r, HitSides, out index))
                    {
                        d.HitSide = (HitDirection)index;
                    }
                    return true;
                case "[ignore super armor]":
                    d.IgnoreSuperArmor = ReadInt(c, r);
                    return true;
                case "[ignore weight]":
                    d.IgnoreWeight = ReadInt(c, r);
                    return true;
                case "[human active status rate]":
                    d.HumanActiveStatusRate = ReadFloat(c, r) / 100f;
                    return true;
                case "[is hit number increasing]":
                    d.HitNumberIncreasing = ReadBool(c, r);
                    return true;
                case "[catch object]":
                    ReadCatchObjects(c, d, r);
                    return true;
                case "[max target hit number]":
                    d.MaxTargetHitNumber = ReadInt(c, r);
                    return true;
                case "[ignore diehard]":
                    d.IgnoreDiehard = ReadBool(c, r);
                    return true;
                case "[hit check by screen pos]":
                    d.HitCheckByScreenPos = ReadBool(c, r);
                    return true;
                default:
                    return false;
            }
        }

        private static void ReadElement(ScriptCursor c, AttackInfoData d)
        {
            string text = c.ScanTypeOrStr();
            ElementKind element;
            switch (text)
            {
                case "[fire element]":
                    element = ElementKind.Fire;
                    break;
                case "[water element]":
                    element = ElementKind.Water;
                    break;
                case "[dark element]":
                    element = ElementKind.Dark;
                    break;
                case "[light element]":
                    element = ElementKind.Light;
                    break;
                default:
                    element = ElementKind.None;
                    break;
            }
            if (element == ElementKind.None)
            {
                d.Elements.Clear();
            }
            else
            {
                d.Elements.Remove(ElementKind.None);
                d.Elements.Add(element);
            }
        }

        private static void ReadStatus(ScriptCursor c, AttackInfoData d, AttackInfoLoadReport r, ref int statusCount)
        {
            // 超出三个槽的:照样把类型与数读完(标记流不能错位,否则后面的标签全被当成游离标记),读到的值扔掉(真实数据里 0 次)
            bool overflow = statusCount >= AttackInfoData.StatusSlots;
            if (overflow)
            {
                r.StatusOverflow++;
            }
            ActiveStatusEntry slot = overflow ? new ActiveStatusEntry() : d.Statuses[statusCount];
            string text = c.ScanTypeOrStr();
            int kind = Array.IndexOf(StatusNames, text);
            slot.Kind = kind < 0 ? ActiveStatusKind.None : (ActiveStatusKind)kind;
            slot.Probability = ReadFloat(c, r);
            slot.Level = ReadInt(c, r);
            slot.Duration = ReadInt(c, r);
            int extras = ExtraIntegers(text);
            var values = new int[extras];
            for (int i = 0; i < extras; i++)
            {
                values[i] = ReadInt(c, r);
            }
            slot.Extra = values;
            if (!overflow)
            {
                statusCount++;
            }
        }

        private static void ReadCatchObjects(ScriptCursor c, AttackInfoData d, AttackInfoLoadReport r)
        {
            d.CatchObjects.Clear();
            while (true)
            {
                int first = c.ScanInt(out bool ok);
                if (!ok)
                {
                    break;
                }
                int second = ReadInt(c, r);
                float third = ReadFloat(c, r);
                d.CatchObjects.Add(new CatchObjectEntry(first, second, third));
            }
        }
    }
}
