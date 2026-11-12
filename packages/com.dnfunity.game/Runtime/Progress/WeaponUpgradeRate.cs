using System;
using System.Collections.Generic;
using System.Globalization;

// 证据(客户端静态读码 + 服务端二进制磁盘字节的只读比对;原版数值不入库):
//  · 武器强化带来的攻击不是"强化表第一格"原样加上,而是再乘一个按"职业 × 武器种类 × (物理, 魔法)"取的倍率。
//    客户端生成装备属性集的函数(0x0078d4a0)先在装备的"可用职业"标志里从第 0 位起找第一个为 1 的位(职业下标 X),
//    再用装备脚本 [sub type] 的整数(武器种类 Y,不小于 6 时倍率按 0)查表(0x01292540):下标 = ((X × 6) + Y) × 2 + (0 物理 | 1 魔法)。
//  · 表是 10 行 × 12 个浮点,启动时由 0x0153fe30 逐个写入;服务端 df_game_r 里有同一张表(符号 upgradeWeaponAttackPowerRate_,528 字节,
//    比客户端多一行全 0),没有任何调用者(服务端不算强化属性)。本机核对:两边 120 个值逐项一致。
//  · 客户端的职业下标次序与服务端 EquipRules 的不同:觉醒职业共用本职业的行(行相等的分组是 {剑魂,暗黑骑士}、{格斗,男格斗}、{神枪手,女神枪手}、
//    {魔法师,男魔法师,创造者}、{圣职者}),推出次序是 剑魂、格斗、神枪手、魔法师、圣职者、女神枪手、男格斗、男魔法师、暗黑骑士、创造者、暗夜使者(推断,行分组一一对得上)。
//  · 第 11 个职业(暗夜使者)在客户端没有写值,客户端读到什么不知道(未知);没有给第 11 行时这里按倍率 1 处理。
//  · 表的数值是原版的平衡数据,不入库:宿主有就用 WeaponUpgradeRateTable 载入(tools/py/weapon_rate_dump.py 从服务端二进制在本机生成文本),没有就按 1。
namespace Dnf.Game
{
    /// <summary>武器强化攻击的倍率来源(按武器的职业与种类分档)。</summary>
    public interface IWeaponUpgradeRate
    {
        /// <summary>
        /// 武器 <paramref name="weapon"/> 的强化攻击倍率:<paramref name="physical"/> 为真取物理一栏,否则取魔法一栏。
        /// 返回 0 表示这件武器没有强化攻击(没写可用职业、武器种类越界)。
        /// </summary>
        float RateOf(ItemInfo weapon, bool physical);
    }

    /// <summary>不分职业与武器种类,一律同一个倍率(没有载入倍率表时的默认:1)。</summary>
    public sealed class FlatWeaponUpgradeRate : IWeaponUpgradeRate
    {
        /// <summary>倍率 1:强化表算出来的数原样用。</summary>
        public static readonly FlatWeaponUpgradeRate One = new FlatWeaponUpgradeRate(1f);

        private readonly float _rate;

        public FlatWeaponUpgradeRate(float rate)
        {
            _rate = rate;
        }

        public float RateOf(ItemInfo weapon, bool physical) => _rate;
    }

    /// <summary>
    /// 倍率表:10 个职业 × 6 种武器 × (物理, 魔法) = 120 个浮点(可再带第 11 个职业的一行共 132 个)。
    /// 文本格式:数以空白或逗号分隔,<c>#</c> 开头的行是注释;次序是 职业 0 的(种类 0 物理、魔法,种类 1 物理、魔法……)接职业 1 ……
    /// </summary>
    public sealed class WeaponUpgradeRateTable : IWeaponUpgradeRate
    {
        /// <summary>客户端写了值的职业数。</summary>
        public const int Jobs = 10;

        /// <summary>每个职业的武器种类数。</summary>
        public const int SubTypes = 6;

        /// <summary>写了值的格子数。</summary>
        public const int Cells = Jobs * SubTypes * 2;

        /// <summary>带第 11 个职业一行的格子数。</summary>
        public const int CellsWithLastJob = (Jobs + 1) * SubTypes * 2;

        /// <summary>客户端职业标志的次序(推断,见文件头),<c>[usable job]</c> 的字符串按它排第几位。</summary>
        private static readonly string[] ClientJobOrder =
        {
            "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[at gunner]", "[at fighter]", "[at mage]", "[demonic swordman]",
            "[creator mage]", "[thief]",
        };

        private readonly float[] _cells;

        /// <summary>按次序给格子(120 个,或 132 个带第 11 个职业)。</summary>
        public WeaponUpgradeRateTable(IReadOnlyList<float> cells)
        {
            if (cells == null)
            {
                throw new ArgumentNullException(nameof(cells));
            }
            if (cells.Count != Cells && cells.Count != CellsWithLastJob)
            {
                throw new ArgumentException("倍率表要 " + Cells + " 或 " + CellsWithLastJob + " 个数,给了 " + cells.Count);
            }
            _cells = new float[cells.Count];
            for (int i = 0; i < _cells.Length; i++)
            {
                _cells[i] = cells[i];
            }
        }

        /// <summary>有没有第 11 个职业(暗夜使者)的一行。</summary>
        public bool HasLastJob => _cells.Length == CellsWithLastJob;

        /// <summary>
        /// 读文本(格式见类说明)。数不是 120 或 132 个、或有读不成浮点数(含 NaN、无穷)的记号时返回 false。
        /// </summary>
        public static bool TryParse(IEnumerable<string> lines, out WeaponUpgradeRateTable table)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }
            table = null;
            var cells = new List<float>();
            foreach (string line in lines)
            {
                if (line == null)
                {
                    continue;
                }
                string text = line.Trim();
                if (text.Length == 0 || text[0] == '#')
                {
                    continue;
                }
                foreach (string token in text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
                    {
                        return false;
                    }
                    cells.Add(value);
                }
            }
            if (cells.Count != Cells && cells.Count != CellsWithLastJob)
            {
                return false;
            }
            table = new WeaponUpgradeRateTable(cells);
            return true;
        }

        /// <summary>
        /// 装备的职业下标:可用职业里按客户端次序最靠前的那一个(不是职业名的字符串,例如 [all],等于全部职业都可用,取 0);
        /// 没写 <c>[usable job]</c> 返回 −1(客户端的标志位全是 0,找不到)。
        /// </summary>
        public static int FirstJob(ItemInfo item)
        {
            if (item == null || item.UsableJobs.Count == 0)
            {
                return -1;
            }
            int best = int.MaxValue;
            foreach (string tag in item.UsableJobs)
            {
                int at = Array.FindIndex(ClientJobOrder, t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                best = Math.Min(best, at < 0 ? 0 : at);
            }
            return best;
        }

        /// <summary>表里一格;下标越界(职业、种类)返回 0,第 11 个职业没有行时返回 0。</summary>
        public float Cell(int job, int subType, bool physical)
        {
            if ((uint)job >= (uint)(_cells.Length / (SubTypes * 2)) || (uint)subType >= SubTypes)
            {
                return 0f;
            }
            return _cells[((((job * SubTypes) + subType) * 2)) + (physical ? 0 : 1)];
        }

        /// <summary>
        /// 武器的倍率:没写可用职业或武器种类(<see cref="ItemInfo.SubType"/>)不在 0 到 5 为 0;职业是第 11 个而表里没有那一行时按 1。
        /// </summary>
        public float RateOf(ItemInfo weapon, bool physical)
        {
            int job = FirstJob(weapon);
            if (job < 0)
            {
                return 0f;
            }
            int subType = weapon.SubType;
            if ((uint)subType >= SubTypes)
            {
                return 0f;
            }
            if (job >= Jobs && !HasLastJob)
            {
                return 1f;
            }
            return Cell(job, subType, physical);
        }
    }
}
