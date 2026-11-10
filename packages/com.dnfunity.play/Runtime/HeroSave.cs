using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 本项目自己的设计(M26):原版的角色数据在服务器数据库里,客户端不存,这里没有原版证据可依。
// 存档里只有数字与物品编号(equipment.lst、stackable.lst 里的号),不含任何原版内容;物品的名字、图标、数值每次从用户自己的数据里按编号读。
namespace Dnf.Play
{
    /// <summary>存档里背包的一格:物品编号与数量。</summary>
    public readonly struct SavedStack
    {
        public SavedStack(int code, int count)
        {
            Code = code;
            Count = count;
        }

        public int Code { get; }

        public int Count { get; }
    }

    /// <summary>存档里装备栏的一格:部位号与物品编号。</summary>
    public readonly struct SavedWorn
    {
        public SavedWorn(int part, int code)
        {
            Part = part;
            Code = code;
        }

        public int Part { get; }

        public int Code { get; }
    }

    /// <summary>存档里快捷栏的一格:格号(0 到 5)与物品编号。</summary>
    public readonly struct SavedQuick
    {
        public SavedQuick(int slot, int code)
        {
            Slot = slot;
            Code = code;
        }

        public int Slot { get; }

        public int Code { get; }
    }

    /// <summary>存档里一个进行中的任务(M31):任务编号与各项剩余计数(收集类没有计数,是空表)。</summary>
    public readonly struct SavedQuest
    {
        public SavedQuest(int id, int[] remaining)
        {
            Id = id;
            Remaining = remaining ?? new int[0];
        }

        public int Id { get; }

        public int[] Remaining { get; }
    }

    /// <summary>存档里一个副本的通关记录(M33):副本编号与记录值(<see cref="Dnf.Game.DungeonClears"/>:值 v 表示难度 1 到 v 已开放)。</summary>
    public readonly struct SavedClear
    {
        public SavedClear(int dungeonId, int opened)
        {
            DungeonId = dungeonId;
            Opened = opened;
        }

        public int DungeonId { get; }

        public int Opened { get; }
    }

    /// <summary>存档里学会的一个技能(M35):技能编号与等级(等级不小于 1)。</summary>
    public readonly struct SavedSkill
    {
        public SavedSkill(int id, int level)
        {
            Id = id;
            Level = level;
        }

        public int Id { get; }

        public int Level { get; }
    }

    /// <summary>存档里技能快捷栏的一格(M35):格号(0 到 5,键 A S D F G H)与技能编号。</summary>
    public readonly struct SavedSkillSlot
    {
        public SavedSkillSlot(int slot, int skillId)
        {
            Slot = slot;
            SkillId = skillId;
        }

        public int Slot { get; }

        public int SkillId { get; }
    }

    /// <summary>还原存档时发生了什么(宿主写进日志)。</summary>
    public sealed class SaveApplyReport
    {
        public SaveApplyReport(int bagStacks, int wornItems, int moved, int dropped)
        {
            BagStacks = bagStacks;
            WornItems = wornItems;
            Moved = moved;
            Dropped = dropped;
        }

        /// <summary>还原的背包格数。</summary>
        public int BagStacks { get; }

        /// <summary>还原的穿着装备件数。</summary>
        public int WornItems { get; }

        /// <summary>存档里穿着、但现在穿不上(数据变了)所以放回背包的件数。</summary>
        public int Moved { get; }

        /// <summary>找不到物品(编号不在用户的数据里)或背包放不下而丢掉的条数。</summary>
        public int Dropped { get; }
    }

    /// <summary>
    /// 角色进度的存档快照(M26):等级、经验、点数、金币、背包的每一格、装备栏的每一格。纯数据;文本格式 <see cref="ToText"/> 与 <see cref="TryParse"/>:
    /// 第一行 <c>DNFUNITY-SAVE 版本号</c>,之后每行 <c>键 值…</c>(<c>bag 编号 数量</c>、<c>worn 部位 编号</c>、<c>quick 格号 编号</c> 可以有很多行),空行与 # 开头的行忽略,
    /// 不认得的键忽略(以后加字段旧版本还能读),认得的键值写坏了整个存档当作读不懂(不读一半)。
    /// </summary>
    public sealed partial class HeroSave
    {
        public const string Magic = "DNFUNITY-SAVE";

        /// <summary>这个程序写出与能读的最高格式版本。</summary>
        public const int CurrentVersion = 1;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>职业下标(<see cref="Dnf.Game.EquipRules.JobOf"/>)。</summary>
        public int Job { get; set; }

        public int Level { get; set; } = 1;

        /// <summary>累计经验(<see cref="HeroProgress.Exp"/>)。</summary>
        public int Exp { get; set; }

        /// <summary>剩下的技能点(M35 起是花掉以后的余额;更早的存档没有花过,余额就是累计值)。</summary>
        public int SkillPoints { get; set; }

        /// <summary>剩下的觉醒点(特殊技能花它)。</summary>
        public int AwakeningPoints { get; set; }

        /// <summary>成长类型(M35 加的键 <c>growtype 下标</c>,0 或没有这一行是转职前)。</summary>
        public int Growtype { get; set; }

        public int LevelUps { get; set; }

        public long TotalGained { get; set; }

        public long Gold { get; set; }

        public long GoldGained { get; set; }

        public int ItemsGained { get; set; }

        /// <summary>背包的每一格(页签从小到大、每页按格子顺序)。</summary>
        public List<SavedStack> Bag { get; } = new List<SavedStack>();

        /// <summary>装备栏穿着的每一件(部位从小到大)。</summary>
        public List<SavedWorn> Worn { get; } = new List<SavedWorn>();

        /// <summary>快捷栏放了什么(格号从小到大,只写有物品的格;M28 加的键,旧存档没有这些行)。</summary>
        public List<SavedQuick> Quick { get; } = new List<SavedQuick>();

        /// <summary>进行中的任务(M31 加的键 <c>quest 编号 计数…</c>,旧存档没有这些行;接取次序)。</summary>
        public List<SavedQuest> Quests { get; } = new List<SavedQuest>();

        /// <summary>已完成的任务编号(M31 加的键 <c>cleared 编号</c>,从小到大)。</summary>
        public List<int> ClearedQuests { get; } = new List<int>();

        /// <summary>各副本的通关记录(M33 加的键 <c>dclear 副本编号 值</c>,旧存档没有这些行;副本编号从小到大)。</summary>
        public List<SavedClear> Clears { get; } = new List<SavedClear>();

        /// <summary>学会的技能(M35 加的键 <c>skill 编号 等级</c>,旧存档没有这些行;编号从小到大)。</summary>
        public List<SavedSkill> Skills { get; } = new List<SavedSkill>();

        /// <summary>技能快捷栏放了什么(M35 加的键 <c>skquick 格号 编号</c>,格号从小到大,只写有技能的格)。</summary>
        public List<SavedSkillSlot> SkillQuick { get; } = new List<SavedSkillSlot>();

        /// <summary>写成文本(换行用 \n,键的次序固定,所以内容没变文本也不变)。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append(' ').Append(Version.ToString(Inv)).Append('\n');
            Line(sb, "job", Job);
            if (Growtype != 0)
            {
                Line(sb, "growtype", Growtype);
            }
            Line(sb, "level", Level);
            Line(sb, "exp", Exp);
            Line(sb, "sp", SkillPoints);
            Line(sb, "ap", AwakeningPoints);
            Line(sb, "levelups", LevelUps);
            Line(sb, "gained", TotalGained);
            Line(sb, "gold", Gold);
            Line(sb, "goldgained", GoldGained);
            Line(sb, "itemsgained", ItemsGained);
            foreach (SavedStack stack in Bag)
            {
                sb.Append("bag ").Append(stack.Code.ToString(Inv)).Append(' ').Append(stack.Count.ToString(Inv)).Append('\n');
            }
            AvatarSaveFormat.Write(this, sb);
            foreach (SavedWorn worn in Worn)
            {
                sb.Append("worn ").Append(worn.Part.ToString(Inv)).Append(' ').Append(worn.Code.ToString(Inv)).Append('\n');
            }
            foreach (SavedQuick quick in Quick)
            {
                sb.Append("quick ").Append(quick.Slot.ToString(Inv)).Append(' ').Append(quick.Code.ToString(Inv)).Append('\n');
            }
            PetSaveFormat.Write(this, sb);
            foreach (SavedQuest quest in Quests)
            {
                sb.Append("quest ").Append(quest.Id.ToString(Inv));
                foreach (int remaining in quest.Remaining)
                {
                    sb.Append(' ').Append(remaining.ToString(Inv));
                }
                sb.Append('\n');
            }
            foreach (int id in ClearedQuests)
            {
                sb.Append("cleared ").Append(id.ToString(Inv)).Append('\n');
            }
            foreach (SavedClear clear in Clears)
            {
                sb.Append("dclear ").Append(clear.DungeonId.ToString(Inv)).Append(' ').Append(clear.Opened.ToString(Inv)).Append('\n');
            }
            foreach (SavedSkill skill in Skills)
            {
                sb.Append("skill ").Append(skill.Id.ToString(Inv)).Append(' ').Append(skill.Level.ToString(Inv)).Append('\n');
            }
            foreach (SavedSkillSlot slot in SkillQuick)
            {
                sb.Append("skquick ").Append(slot.Slot.ToString(Inv)).Append(' ').Append(slot.SkillId.ToString(Inv)).Append('\n');
            }
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string key, long value)
        {
            sb.Append(key).Append(' ').Append(value.ToString(Inv)).Append('\n');
        }

        /// <summary>读文本;读不懂时返回 false、<paramref name="error"/> 写原因(带行号)、<paramref name="save"/> 为 null。</summary>
        public static bool TryParse(string text, out HeroSave save, out string error)
        {
            save = null;
            error = null;
            if (text == null)
            {
                error = "没有内容";
                return false;
            }
            var result = new HeroSave();
            bool header = false;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int no = i + 1;
                if (!header)
                {
                    if (f[0] != Magic)
                    {
                        error = "第 " + no + " 行:不是存档(开头应是 " + Magic + ")";
                        return false;
                    }
                    if (f.Length != 2 || !int.TryParse(f[1], NumberStyles.Integer, Inv, out int version) || version < 1)
                    {
                        error = "第 " + no + " 行:版本号写坏了";
                        return false;
                    }
                    if (version > CurrentVersion)
                    {
                        error = "存档的版本 " + version + " 比这个程序认得的 " + CurrentVersion + " 新";
                        return false;
                    }
                    result.Version = version;
                    header = true;
                    continue;
                }
                if (!ParseLine(result, f, no, out error))
                {
                    return false;
                }
            }
            if (!header)
            {
                error = "没有内容";
                return false;
            }
            save = result;
            return true;
        }

        private static bool ParseLine(HeroSave r, string[] f, int no, out string error)
        {
            error = null;
            string key = f[0];
            if (key == "quest")
            {
                return ParseQuest(r, f, no, out error);
            }
            if (PetSaveFormat.Handles(key))
            {
                return PetSaveFormat.Parse(r, key, f, no, out error);
            }
            int arity;
            if (AvatarSaveFormat.Handles(key))
            {
                return AvatarSaveFormat.Parse(r, key, f, no, out error);
            }
            switch (key)
            {
                case "cleared":
                case "growtype":
                case "job":
                case "level":
                case "exp":
                case "sp":
                case "ap":
                case "levelups":
                case "itemsgained":
                case "gained":
                case "gold":
                case "goldgained":
                    arity = 2;
                    break;
                case "bag":
                case "worn":
                case "quick":
                case "dclear":
                case "skill":
                case "skquick":
                    arity = 3;
                    break;
                default:
                    return true;              // 不认得的键忽略(以后加字段旧版本还能读)
            }
            if (f.Length != arity)
            {
                error = "第 " + no + " 行:" + key + " 需要 " + (arity - 1) + " 个值";
                return false;
            }
            long a;
            long b = 0;
            if (!long.TryParse(f[1], NumberStyles.Integer, Inv, out a) || arity == 3 && !long.TryParse(f[2], NumberStyles.Integer, Inv, out b))
            {
                error = "第 " + no + " 行:" + key + " 的值不是整数";
                return false;
            }
            bool ok;
            switch (key)
            {
                case "job":
                    ok = InRange(a, 0, int.MaxValue);
                    r.Job = (int)a;
                    break;
                case "level":
                    ok = InRange(a, 1, int.MaxValue);
                    r.Level = (int)a;
                    break;
                case "exp":
                    ok = InRange(a, 0, int.MaxValue);
                    r.Exp = (int)a;
                    break;
                case "sp":
                    ok = InRange(a, 0, int.MaxValue);
                    r.SkillPoints = (int)a;
                    break;
                case "ap":
                    ok = InRange(a, 0, int.MaxValue);
                    r.AwakeningPoints = (int)a;
                    break;
                case "levelups":
                    ok = InRange(a, 0, int.MaxValue);
                    r.LevelUps = (int)a;
                    break;
                case "itemsgained":
                    ok = InRange(a, 0, int.MaxValue);
                    r.ItemsGained = (int)a;
                    break;
                case "gained":
                    ok = InRange(a, 0, long.MaxValue);
                    r.TotalGained = a;
                    break;
                case "gold":
                    ok = InRange(a, 0, long.MaxValue);
                    r.Gold = a;
                    break;
                case "goldgained":
                    ok = InRange(a, 0, long.MaxValue);
                    r.GoldGained = a;
                    break;
                case "cleared":
                    ok = InRange(a, 1, int.MaxValue);
                    if (ok)
                    {
                        r.ClearedQuests.Add((int)a);
                    }
                    break;
                case "bag":
                    ok = InRange(a, 0, int.MaxValue) && InRange(b, 1, int.MaxValue);
                    if (ok)
                    {
                        r.Bag.Add(new SavedStack((int)a, (int)b));
                    }
                    break;
                case "dclear":
                    ok = InRange(a, 0, int.MaxValue) && InRange(b, 1, Dnf.Game.DungeonClears.Difficulties - 1);
                    if (ok)
                    {
                        r.Clears.Add(new SavedClear((int)a, (int)b));
                    }
                    break;
                case "growtype":
                    ok = InRange(a, 0, Dnf.Game.SkillRules.GrowtypeCount - 1);
                    r.Growtype = (int)a;
                    break;
                case "skill":
                    ok = InRange(a, 1, int.MaxValue) && InRange(b, 1, MaxSkillLevel);
                    if (ok)
                    {
                        r.Skills.Add(new SavedSkill((int)a, (int)b));
                    }
                    break;
                case "skquick":
                    ok = InRange(a, 0, Dnf.Game.SkillBook.QuickSlots - 1) && InRange(b, 1, int.MaxValue);
                    if (ok)
                    {
                        r.SkillQuick.Add(new SavedSkillSlot((int)a, (int)b));
                    }
                    break;
                case "quick":
                    ok = InRange(a, 0, Dnf.Game.QuickSlots.Count - 1) && InRange(b, 1, int.MaxValue);
                    if (ok)
                    {
                        r.Quick.Add(new SavedQuick((int)a, (int)b));
                    }
                    break;
                default:                      // worn
                    ok = InRange(a, 0, int.MaxValue) && InRange(b, 0, int.MaxValue);
                    if (ok)
                    {
                        r.Worn.Add(new SavedWorn((int)a, (int)b));
                    }
                    break;
            }
            if (!ok)
            {
                error = "第 " + no + " 行:" + key + " 的值超出范围";
            }
            return ok;
        }

        /// <summary>存档里一个技能的等级上限(防写坏的文件;原版技能最高一百级出头)。</summary>
        public const int MaxSkillLevel = 1000;

        /// <summary>每个任务最多存多少项计数(猎杀目标组数的上限,防写坏的文件占内存)。</summary>
        public const int MaxQuestCounters = 32;

        private static bool ParseQuest(HeroSave r, string[] f, int no, out string error)
        {
            error = null;
            if (f.Length < 2 || f.Length - 2 > MaxQuestCounters)
            {
                error = "第 " + no + " 行:quest 需要任务编号和 0 到 " + MaxQuestCounters + " 项计数";
                return false;
            }
            var values = new long[f.Length - 1];
            for (int i = 1; i < f.Length; i++)
            {
                if (!long.TryParse(f[i], NumberStyles.Integer, Inv, out values[i - 1]))
                {
                    error = "第 " + no + " 行:quest 的值不是整数";
                    return false;
                }
            }
            if (!InRange(values[0], 1, int.MaxValue))
            {
                error = "第 " + no + " 行:quest 的值超出范围";
                return false;
            }
            var remaining = new int[values.Length - 1];
            for (int i = 1; i < values.Length; i++)
            {
                if (!InRange(values[i], 0, int.MaxValue))
                {
                    error = "第 " + no + " 行:quest 的值超出范围";
                    return false;
                }
                remaining[i - 1] = (int)values[i];
            }
            r.Quests.Add(new SavedQuest((int)values[0], remaining));
            return true;
        }

        private static bool InRange(long value, long min, long max)
        {
            return value >= min && value <= max;
        }
    }
}
