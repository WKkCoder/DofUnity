using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dnf.Game;

// 本项目自己的设计:装备个体状态(强化等级、增幅标志、附魔卡编号、品级值)的存档字段。原版的这些数据在服务器数据库里,客户端不存;
// 这里只存数字与物品编号,不含任何原版内容。存档里的背包与装备栏行(bag / worn)仍然只存物品编号,个体状态另起一种 equipstate 行,
// 旧存档没有这些行(装备都是没强化的原样),旧程序读到新存档会忽略不认得的键,所以两个方向都兼容。
namespace Dnf.Play
{
    /// <summary>
    /// 存档里一件装备的个体状态。位置:<see cref="Place"/> 为 0 表示背包装备页,<see cref="Index"/> 是"同编号的第几件(从 0 起,按背包装备页次序)";
    /// 为 1 表示穿在身上,<see cref="Index"/> 是部位号。<see cref="Code"/> 是装备的物品编号,还原时必须对得上。
    /// </summary>
    public readonly struct SavedEquipState
    {
        /// <summary>标志位:有增幅属性。</summary>
        public const int FlagAmplified = 1;

        /// <summary>标志位:增幅属性没鉴定。</summary>
        public const int FlagUnidentified = 2;

        /// <summary>标志位:耐久没满。</summary>
        public const int FlagDamaged = 4;

        public SavedEquipState(int place, int index, int code, int upgrade, int flags, int enchant, int grade)
        {
            Place = place;
            Index = index;
            Code = code;
            Upgrade = upgrade;
            Flags = flags;
            Enchant = enchant;
            Grade = grade;
        }

        public int Place { get; }

        public int Index { get; }

        public int Code { get; }

        public int Upgrade { get; }

        public int Flags { get; }

        public int Enchant { get; }

        public int Grade { get; }

        internal static SavedEquipState From(EnhancePlace place, int index, int code, EquipInstance state)
        {
            int flags = (state.Amplified ? FlagAmplified : 0) | (state.Identified ? 0 : FlagUnidentified) | (state.Damaged ? FlagDamaged : 0);
            return new SavedEquipState((int)place, index, code, state.Upgrade, flags, state.EnchantCode, state.GradeValue);
        }

        internal EquipInstance ToInstance()
        {
            return new EquipInstance(Upgrade, (Flags & FlagAmplified) != 0, (Flags & FlagUnidentified) == 0, Enchant, Grade, (Flags & FlagDamaged) != 0);
        }
    }

    public sealed partial class HeroSave
    {
        /// <summary>装备的个体状态(键 <c>equipstate 位置 序号 物品编号 强化等级 标志 附魔卡编号 品级值</c>,每件一行,只写有状态的装备;旧存档没有这些行)。</summary>
        public List<SavedEquipState> EquipStates { get; } = new List<SavedEquipState>();
    }

    /// <summary>装备个体状态存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上)。</summary>
    internal static class EnhanceSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string Key = "equipstate";

        public static bool Handles(string key)
        {
            return key == Key;
        }

        public static void Write(HeroSave save, StringBuilder sb)
        {
            foreach (SavedEquipState s in save.EquipStates)
            {
                sb.Append(Key).Append(' ').Append(s.Place.ToString(Inv)).Append(' ').Append(s.Index.ToString(Inv)).Append(' ').Append(s.Code.ToString(Inv))
                    .Append(' ').Append(s.Upgrade.ToString(Inv)).Append(' ').Append(s.Flags.ToString(Inv)).Append(' ').Append(s.Enchant.ToString(Inv))
                    .Append(' ').Append(s.Grade.ToString(Inv)).Append('\n');
            }
        }

        public static bool Parse(HeroSave r, string key, string[] f, int no, out string error)
        {
            error = null;
            const int Arity = 8;
            if (f.Length != Arity)
            {
                error = "第 " + no + " 行:" + key + " 需要 " + (Arity - 1) + " 个值";
                return false;
            }
            var v = new long[Arity - 1];
            for (int i = 0; i < v.Length; i++)
            {
                if (!long.TryParse(f[i + 1], NumberStyles.Integer, Inv, out v[i]))
                {
                    error = "第 " + no + " 行:" + key + " 的值不是整数";
                    return false;
                }
            }
            bool ok = InRange(v[0], 0, 1)
                && (v[0] == 0 ? InRange(v[1], 0, int.MaxValue) : InRange(v[1], EquipmentSlots.First, EquipmentSlots.Last))
                && InRange(v[2], 1, int.MaxValue)
                && InRange(v[3], 0, 255)
                && InRange(v[4], 0, SavedEquipState.FlagAmplified | SavedEquipState.FlagUnidentified | SavedEquipState.FlagDamaged)
                && InRange(v[5], 0, int.MaxValue)
                && InRange(v[6], 0, int.MaxValue);
            if (!ok)
            {
                error = "第 " + no + " 行:" + key + " 的值超出范围";
                return false;
            }
            r.EquipStates.Add(new SavedEquipState((int)v[0], (int)v[1], (int)v[2], (int)v[3], (int)v[4], (int)v[5], (int)v[6]));
            return true;
        }

        private static bool InRange(long value, long min, long max)
        {
            return value >= min && value <= max;
        }
    }
}
