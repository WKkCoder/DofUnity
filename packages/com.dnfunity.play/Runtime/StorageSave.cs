using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 本项目自己的设计(r15-storage):原版的仓库在服务器数据库里(个人仓库按角色存、账号金库按账号存),客户端不存,这里没有原版证据可依;
// 存档里只有数字与物品编号,不含任何原版内容。容量、金币上限、价钱这些数据每次从用户自己的数据(账号金库脚本)里取。
// 布局(账号级,一个账号一个文件,与角色存档 HeroSave 分开):
//   DNFUNITY-STORAGE 1
//   acap <容量值>                  账号金库的容量值(档位表里的键);没有这一行或为 0 就是还没开通
//   agold <金币>                   账号金库里的金币
//   aslot <格号> <编号> <数量>      账号金库的一格(可以有很多行,格号从小到大)
//   pcap <角色键> <容量>           某个角色的个人仓库容量(角色键是宿主起的、不含空白的名字)
//   pslot <角色键> <格号> <编号> <数量>   个人仓库的一格(格号从小到大;角色键按字典序成组;只有 pslot 没有 pcap 的角色按初始容量算)
// 约定同 HeroSave:第一行是魔数与版本;空行与 # 开头的行忽略;不认得的键忽略;认得的键值写坏了整个存档当作读不懂;版本比程序新的拒绝。
namespace Dnf.Play
{
    /// <summary>存档里仓库的一格:格号、物品编号、数量。</summary>
    public readonly struct SavedVaultSlot
    {
        public SavedVaultSlot(int slot, int code, int count)
        {
            Slot = slot;
            Code = code;
            Count = count;
        }

        public int Slot { get; }

        public int Code { get; }

        public int Count { get; }
    }

    /// <summary>存档里一个角色的个人仓库:角色键、容量与有东西的格。</summary>
    public sealed class SavedPersonalVault
    {
        public SavedPersonalVault(string key, int capacity)
        {
            Key = key;
            Capacity = capacity;
        }

        public string Key { get; }

        public int Capacity { get; }

        public List<SavedVaultSlot> Slots { get; } = new List<SavedVaultSlot>();
    }

    /// <summary>
    /// 仓库的存档快照(账号级):账号金库的容量值、金币与每一格,以及每个角色的个人仓库。纯数据;文本格式见文件头的注释。
    /// </summary>
    public sealed class StorageSave
    {
        public const string Magic = "DNFUNITY-STORAGE";

        /// <summary>这个程序写出与能读的最高格式版本。</summary>
        public const int CurrentVersion = 1;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>账号金库的容量值(档位表里的键);0 是还没开通。</summary>
        public int AccountCapacity { get; set; }

        /// <summary>账号金库里的金币。</summary>
        public long AccountGold { get; set; }

        /// <summary>账号金库有东西的格(格号从小到大)。</summary>
        public List<SavedVaultSlot> AccountSlots { get; } = new List<SavedVaultSlot>();

        /// <summary>各角色的个人仓库(角色键按字典序)。</summary>
        public List<SavedPersonalVault> Personal { get; } = new List<SavedPersonalVault>();

        /// <summary>写成文本(换行用 \n,键的次序固定,所以内容没变文本也不变)。没开通账号金库就不写账号那几行。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append(' ').Append(Version.ToString(Inv)).Append('\n');
            if (AccountCapacity > 0)
            {
                sb.Append("acap ").Append(AccountCapacity.ToString(Inv)).Append('\n');
                sb.Append("agold ").Append(AccountGold.ToString(Inv)).Append('\n');
                foreach (SavedVaultSlot slot in AccountSlots)
                {
                    sb.Append("aslot ").Append(slot.Slot.ToString(Inv)).Append(' ').Append(slot.Code.ToString(Inv)).Append(' ')
                        .Append(slot.Count.ToString(Inv)).Append('\n');
                }
            }
            foreach (SavedPersonalVault vault in Personal)
            {
                sb.Append("pcap ").Append(vault.Key).Append(' ').Append(vault.Capacity.ToString(Inv)).Append('\n');
                foreach (SavedVaultSlot slot in vault.Slots)
                {
                    sb.Append("pslot ").Append(vault.Key).Append(' ').Append(slot.Slot.ToString(Inv)).Append(' ')
                        .Append(slot.Code.ToString(Inv)).Append(' ').Append(slot.Count.ToString(Inv)).Append('\n');
                }
            }
            return sb.ToString();
        }

        /// <summary>读文本;读不懂时返回 false、<paramref name="error"/> 写原因(带行号)、<paramref name="save"/> 为 null。</summary>
        public static bool TryParse(string text, out StorageSave save, out string error)
        {
            save = null;
            error = null;
            if (text == null)
            {
                error = "没有内容";
                return false;
            }
            var result = new StorageSave();
            var capacities = new Dictionary<string, int>(StringComparer.Ordinal);
            var slots = new Dictionary<string, List<SavedVaultSlot>>(StringComparer.Ordinal);
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
                        error = "第 " + no + " 行:不是仓库存档(开头应是 " + Magic + ")";
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
                if (!ParseLine(result, capacities, slots, f, no, out error))
                {
                    return false;
                }
            }
            if (!header)
            {
                error = "没有内容";
                return false;
            }
            var keys = new List<string>(capacities.Keys);
            foreach (string key in slots.Keys)
            {
                if (!capacities.ContainsKey(key))
                {
                    keys.Add(key);
                }
            }
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                int capacity = capacities.TryGetValue(key, out int c) ? c : Dnf.Game.StorageRules.PersonalInitialCapacity;
                var vault = new SavedPersonalVault(key, capacity);
                if (slots.TryGetValue(key, out List<SavedVaultSlot> list))
                {
                    vault.Slots.AddRange(list);
                }
                result.Personal.Add(vault);
            }
            save = result;
            return true;
        }

        private static bool ParseLine(StorageSave r, Dictionary<string, int> capacities, Dictionary<string, List<SavedVaultSlot>> slots,
            string[] f, int no, out string error)
        {
            error = null;
            string key = f[0];
            int arity;
            switch (key)
            {
                case "acap":
                case "agold":
                    arity = 2;
                    break;
                case "pcap":
                    arity = 3;
                    break;
                case "aslot":
                    arity = 4;
                    break;
                case "pslot":
                    arity = 5;
                    break;
                default:
                    return true;              // 不认得的键忽略(以后加字段旧版本还能读)
            }
            if (f.Length != arity)
            {
                error = "第 " + no + " 行:" + key + " 需要 " + (arity - 1) + " 个值";
                return false;
            }
            bool personal = key[0] == 'p';
            int first = personal ? 2 : 1;                 // 数字从第几个字段开始(个人仓库的第一个字段是角色键)
            var n = new long[arity - first];
            for (int i = 0; i < n.Length; i++)
            {
                if (!long.TryParse(f[first + i], NumberStyles.Integer, Inv, out n[i]))
                {
                    error = "第 " + no + " 行:" + key + " 的值不是整数";
                    return false;
                }
            }
            if (personal && !Dnf.Game.AccountStorage.IsValidHeroKey(f[1]))
            {
                error = "第 " + no + " 行:" + key + " 的角色键不合法";
                return false;
            }
            bool ok;
            switch (key)
            {
                case "acap":
                    ok = InRange(n[0], 0, int.MaxValue);
                    r.AccountCapacity = (int)n[0];
                    break;
                case "agold":
                    ok = InRange(n[0], 0, long.MaxValue);
                    r.AccountGold = n[0];
                    break;
                case "aslot":
                    ok = InRange(n[0], 0, Dnf.Game.StorageRules.AccountMaxSlots - 1) && InRange(n[1], 1, int.MaxValue) && InRange(n[2], 1, int.MaxValue);
                    if (ok)
                    {
                        r.AccountSlots.Add(new SavedVaultSlot((int)n[0], (int)n[1], (int)n[2]));
                    }
                    break;
                case "pcap":
                    ok = InRange(n[0], 1, Dnf.Game.StorageRules.PersonalMaxSlots);
                    if (ok)
                    {
                        capacities[f[1]] = (int)n[0];
                    }
                    break;
                default:                      // pslot
                    ok = InRange(n[0], 0, Dnf.Game.StorageRules.PersonalMaxSlots - 1) && InRange(n[1], 1, int.MaxValue) && InRange(n[2], 1, int.MaxValue);
                    if (ok)
                    {
                        if (!slots.TryGetValue(f[1], out List<SavedVaultSlot> list))
                        {
                            list = new List<SavedVaultSlot>();
                            slots[f[1]] = list;
                        }
                        list.Add(new SavedVaultSlot((int)n[0], (int)n[1], (int)n[2]));
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
