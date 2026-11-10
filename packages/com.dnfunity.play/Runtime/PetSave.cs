using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 本项目自己的设计:宠物的存档字段。原版的宠物数据在服务器数据库里,客户端不存;这里只存数字(实例号、物品编号、累计经验、整数饱食、宠物格里放了什么),
// 不含任何原版内容。存什么按服务端存盘字段的取舍(静态读码):经验与整数饱食照存,等级不存、读档时由经验重算。
namespace Dnf.Play
{
    /// <summary>存档里的一只宠物:实例号、宠物物品编号、累计经验、整数饱食(0 到 100)。</summary>
    public readonly struct SavedPet
    {
        public SavedPet(int uid, int itemCode, int exp, int stomach)
        {
            Uid = uid;
            ItemCode = itemCode;
            Exp = exp;
            Stomach = stomach;
        }

        public int Uid { get; }

        public int ItemCode { get; }

        public int Exp { get; }

        public int Stomach { get; }
    }

    /// <summary>存档里宠物格的一格:部位号(22 到 25)与值——22 号存装备着的宠物的实例号,23 到 25 号存神器的物品编号。</summary>
    public readonly struct SavedPetSlot
    {
        public SavedPetSlot(int part, int value)
        {
            Part = part;
            Value = value;
        }

        public int Part { get; }

        public int Value { get; }
    }

    public sealed partial class HeroSave
    {
        /// <summary>宠物名册(键 <c>pet 实例号 物品编号 经验 饱食</c>,每只一行,名册次序;旧存档没有这些行)。</summary>
        public List<SavedPet> Pets { get; } = new List<SavedPet>();

        /// <summary>宠物格里放了什么(键 <c>petslot 部位号 值</c>,部位号从小到大,只写有东西的格;旧存档没有这些行)。</summary>
        public List<SavedPetSlot> PetSlots { get; } = new List<SavedPetSlot>();
    }

    /// <summary>宠物存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上;键不认得的旧程序会忽略它们)。</summary>
    internal static class PetSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string PetKey = "pet";
        public const string SlotKey = "petslot";

        public static bool Handles(string key)
        {
            return key == PetKey || key == SlotKey;
        }

        public static void Write(HeroSave save, StringBuilder sb)
        {
            foreach (SavedPet pet in save.Pets)
            {
                sb.Append(PetKey).Append(' ').Append(pet.Uid.ToString(Inv)).Append(' ').Append(pet.ItemCode.ToString(Inv)).Append(' ')
                    .Append(pet.Exp.ToString(Inv)).Append(' ').Append(pet.Stomach.ToString(Inv)).Append('\n');
            }
            foreach (SavedPetSlot slot in save.PetSlots)
            {
                sb.Append(SlotKey).Append(' ').Append(slot.Part.ToString(Inv)).Append(' ').Append(slot.Value.ToString(Inv)).Append('\n');
            }
        }

        public static bool Parse(HeroSave r, string key, string[] f, int no, out string error)
        {
            error = null;
            int arity = key == PetKey ? 5 : 3;
            if (f.Length != arity)
            {
                error = "第 " + no + " 行:" + key + " 需要 " + (arity - 1) + " 个值";
                return false;
            }
            var v = new long[arity - 1];
            for (int i = 0; i < v.Length; i++)
            {
                if (!long.TryParse(f[i + 1], NumberStyles.Integer, Inv, out v[i]))
                {
                    error = "第 " + no + " 行:" + key + " 的值不是整数";
                    return false;
                }
            }
            bool ok;
            if (key == PetKey)
            {
                ok = InRange(v[0], 1, int.MaxValue) && InRange(v[1], 1, int.MaxValue) && InRange(v[2], 0, int.MaxValue)
                    && InRange(v[3], 0, (long)Dnf.Game.PetRules.MaxStomach);
                if (ok)
                {
                    r.Pets.Add(new SavedPet((int)v[0], (int)v[1], (int)v[2], (int)v[3]));
                }
            }
            else
            {
                ok = InRange(v[0], Dnf.Game.PetEquipment.PetPart, Dnf.Game.PetEquipment.LastArtifact) && InRange(v[1], 1, int.MaxValue);
                if (ok)
                {
                    r.PetSlots.Add(new SavedPetSlot((int)v[0], (int)v[1]));
                }
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
