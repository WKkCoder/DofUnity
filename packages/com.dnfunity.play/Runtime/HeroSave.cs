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
    public sealed class HeroSave
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

        public int SkillPoints { get; set; }

        public int AwakeningPoints { get; set; }

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

        /// <summary>写成文本(换行用 \n,键的次序固定,所以内容没变文本也不变)。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append(' ').Append(Version.ToString(Inv)).Append('\n');
            Line(sb, "job", Job);
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
            foreach (SavedWorn worn in Worn)
            {
                sb.Append("worn ").Append(worn.Part.ToString(Inv)).Append(' ').Append(worn.Code.ToString(Inv)).Append('\n');
            }
            foreach (SavedQuick quick in Quick)
            {
                sb.Append("quick ").Append(quick.Slot.ToString(Inv)).Append(' ').Append(quick.Code.ToString(Inv)).Append('\n');
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
            int arity;
            switch (key)
            {
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
                case "bag":
                    ok = InRange(a, 0, int.MaxValue) && InRange(b, 1, int.MaxValue);
                    if (ok)
                    {
                        r.Bag.Add(new SavedStack((int)a, (int)b));
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

        private static bool InRange(long value, long min, long max)
        {
            return value >= min && value <= max;
        }
    }
}
