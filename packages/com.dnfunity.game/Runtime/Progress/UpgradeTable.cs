using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据(服务端静态读码,没有运行;地址是 df_game_r.orig 的虚拟地址,表里的数值不入库):
// 读表 importUpgradeTable 0x08900c26(upgrade_table_t 的构造 0x089000ca 给出默认值:权重都是 1.0、[cost] 都是 1、各档最大与销毁等级都是 -1);
// 取一行并算数值 upgrade_table_t::get_upgrade_info 0x08900436(浮点走 x87,中间结果留在 80 位寄存器里,落到 float 才舍入;
// 取整是 FISTP 并把舍入控制改成截断——所以用 X87 模型复现);惩罚类型 GetPenaltyType 0x089003ee;稀有度名字映射 getItemRarity 0x088bfd97。
// 数据普查(2026-11,本机全库,只记计数):强化表 255 行、增幅强化表 255 行(都是 17 个值一行);[cost] 与 [repair cost rate by upgrade level] 各 100 项;
//   [destroy level by rarity] 与 [max upgrade level by rarity] 都只写了普通与优秀两档(其余是默认 -1);[cost weight by upgrade level] 19 对。
namespace Dnf.Game
{
    /// <summary>一档强化表里一行的原始内容(第 N 行 = 当前强化等级 N 的装备再强化一次要面对的数据)。</summary>
    public sealed class UpgradeRow
    {
        internal UpgradeRow()
        {
        }

        /// <summary>
        /// 属性项第一组,服务端行结构的第 0 到 7 个 dword:0、1、2、3 是随等级放大的四项(文件里的 A、f1、f3、f5),
        /// 5、6 是不放大的两项(f2、f4),4 与 7 恒为 0。含义(哪一项对应哪种属性)客户端定义,服务端不读;客户端读码的结果见 <see cref="EquipUpgradeEffect"/> 的文件头(武器用 [0]、防具 [1] 与 [5]、首饰 [2] 与 [6]、辅助与魔法石 [3])。
        /// </summary>
        public float[] StatsA { get; } = new float[8];

        /// <summary>属性项第二组,行结构的第 0x13 到 0x1a 个 dword,排法同 <see cref="StatsA"/>(文件里的 B、g1 到 g5)。</summary>
        public float[] StatsB { get; } = new float[8];

        /// <summary>失败概率分子(第 8 个 dword;分母见 <see cref="ItemUpgrade.ProbabilityDenominator"/>)。</summary>
        public int FailChance { get; internal set; }

        /// <summary>失败后的惩罚类型:1 无惩罚、2 降级、3 销毁(第 9 个 dword)。</summary>
        public int PenaltyType { get; internal set; }

        /// <summary>类型 2(降级)时降几级(第 10 个 dword)。</summary>
        public int PenaltyLevels { get; internal set; }

        /// <summary>这一次强化要的材料物品编号(第 11 个 dword)。</summary>
        public int MaterialCode { get; internal set; }

        /// <summary>材料数量(第 12 个 dword)。</summary>
        public int MaterialCount { get; internal set; }

        /// <summary>增幅表的 <c>[amplification const]</c>:每行 4 个整数(第 15 到 18 个 dword);普通表全 0。</summary>
        public int[] Amplification { get; } = new int[4];
    }

    /// <summary>
    /// 取一行之后算出来的结果(<c>upgrade_info_t</c>):属性项按稀有度权重与装备等级放大,金币费用按部位、稀有度、强化等级的权重折过。
    /// 材料、概率、惩罚直接来自表行,<see cref="ItemUpgrade"/> 用的就是这几项加上金币费用。
    /// </summary>
    public sealed class UpgradeInfo
    {
        internal UpgradeInfo()
        {
        }

        /// <summary>第一组属性项(8 个,含义同 <see cref="UpgradeRow.StatsA"/>),已按"稀有度权重 × 项 × (装备等级 ÷ 档位 + 1)"放大。</summary>
        public float[] StatsA { get; } = new float[8];

        /// <summary>第二组属性项。</summary>
        public float[] StatsB { get; } = new float[8];

        public int FailChance { get; internal set; }

        public int PenaltyType { get; internal set; }

        public int PenaltyLevels { get; internal set; }

        public int MaterialCode { get; internal set; }

        public int MaterialCount { get; internal set; }

        /// <summary>金币费用(整数,三步取整之后)。</summary>
        public int GoldCost { get; internal set; }

        public int[] Amplification { get; } = new int[4];
    }

    /// <summary>
    /// 强化表(<c>upgrade.etc</c> 与增幅表 <c>amplifyupgrade.etc</c> 同一种格式):一张"当前强化等级 → 一行"的表,
    /// 加上稀有度权重、部位权重、金币基数、修理系数、销毁等级与封顶等级。数据由用户自己的游戏目录读出;库里的测试用合成的小表。
    /// </summary>
    public sealed class UpgradeTable
    {
        /// <summary>稀有度的个数(0 到 5:普通、优秀、稀有、神器、史诗、传说)。</summary>
        public const int Rarities = 6;

        /// <summary>装备等级的上限(含):<c>[cost]</c> 有这么多项。</summary>
        public const int CostSlots = 100;

        /// <summary>服务端 <c>get_upgrade_info</c> 的部位范围:10 到 21(<see cref="EquipmentParts.Weapon"/> 到 <see cref="EquipmentParts.MagicStone"/>)。</summary>
        public const int FirstPart = EquipmentParts.Weapon;

        public const int LastPart = EquipmentParts.MagicStone;

        private readonly List<UpgradeRow> _rows = new List<UpgradeRow>();
        private readonly float[] _level = { 1f, 1f, 1f, 1f };
        private readonly float[] _typeWeight = new float[22];
        private readonly float[] _costRarityWeight = { 1f, 1f, 1f, 1f, 1f, 1f };
        private readonly float[] _statRarityWeight = { 1f, 1f, 1f, 1f, 1f, 1f };
        private readonly int[] _cost = new int[CostSlots];
        private readonly float[] _repair = new float[CostSlots];
        private readonly int[] _destroy = { -1, -1, -1, -1, -1, -1 };
        private readonly int[] _max = { -1, -1, -1, -1, -1, -1 };
        private readonly SortedDictionary<int, float> _levelCostWeight = new SortedDictionary<int, float>();
        private readonly List<string> _problems = new List<string>();

        private UpgradeTable()
        {
            for (int i = 0; i < _typeWeight.Length; i++)
            {
                _typeWeight[i] = 1f;
            }
            for (int i = 0; i < CostSlots; i++)
            {
                _cost[i] = 1;
                _repair[i] = 1f;
            }
        }

        /// <summary>表有几行(强化等级不小于它就没有下一档了)。</summary>
        public int RowCount => _rows.Count;

        /// <summary><c>[notice]</c>:升到这个强化等级时全服通知(服务端读进来,通知本身这一步不做;没写为 0)。</summary>
        public int NoticeLevel { get; private set; }

        /// <summary>读表时发现的问题(服务端是写日志并继续;个别问题让整张表读不进来,见 <see cref="TryParse"/>)。</summary>
        public IReadOnlyList<string> Problems => _problems;

        public UpgradeRow Row(int level)
        {
            return (uint)level < (uint)_rows.Count ? _rows[level] : null;
        }

        /// <summary>某稀有度的"销毁等级":强化等级不小于它,失败就是销毁档(-1 表示没有这一说,按表行自己的惩罚类型)。稀有度不在 0 到 5 时返回 -1。</summary>
        public int DestroyLevel(int rarity) => (uint)rarity < Rarities ? _destroy[rarity] : -1;

        /// <summary>某稀有度的最大强化等级(-1 表示不限;稀有度不在 0 到 5 时返回 -1)。</summary>
        public int MaxLevel(int rarity) => (uint)rarity < Rarities ? _max[rarity] : -1;

        /// <summary>修理费按强化等级的倍率(<c>getUpgradeItemRepairCostRate</c> 0x08549148 读的表);等级不在 0 到 99 时按 1。</summary>
        public float RepairCostRate(int upgradeLevel) => (uint)upgradeLevel < CostSlots ? _repair[upgradeLevel] : 1f;

        /// <summary>
        /// 失败之后实际走哪一档惩罚(<c>GetPenaltyType</c>):没有销毁等级的稀有度,或强化等级还没到销毁等级,用表行写的类型;到了就是销毁(3)。
        /// 强化等级越过表的行数时按 1(这种情形强化早就被拒绝了,不会走到这里)。
        /// </summary>
        public int PenaltyTypeAt(int upgradeLevel, int rarity)
        {
            UpgradeRow row = Row(upgradeLevel);
            int destroy = DestroyLevel(rarity);
            if (destroy == -1 || upgradeLevel < destroy)
            {
                return row == null ? 1 : row.PenaltyType;
            }
            return 3;
        }

        /// <summary>
        /// 取一行并算数值(服务端 <c>get_upgrade_info</c>)。<paramref name="growthGrade"/> 是装备的"成长等级":普通装备就是 <see cref="ItemInfo.Grade"/>
        /// (<c>CEquipItem::getGrowthGrade</c> 0x085137b8 在不是成长型装备时返回物品等级;成长型装备按角色等级线性换算,这一步不做)。
        /// 稀有度不在 0 到 5、装备等级不在 0 到 99、强化等级不在表里、部位不在 10 到 21 时返回 null(服务端返回 0)。
        /// </summary>
        public UpgradeInfo GetInfo(int rarity, int growthGrade, int upgradeLevel, int part)
        {
            if (upgradeLevel < 0 || upgradeLevel >= _rows.Count || rarity < 0 || rarity >= Rarities || part < FirstPart || part > LastPart
                || growthGrade < 0 || growthGrade >= CostSlots)
            {
                return null;
            }
            UpgradeRow row = _rows[upgradeLevel];
            var info = new UpgradeInfo
            {
                FailChance = row.FailChance,
                PenaltyType = row.PenaltyType,
                PenaltyLevels = row.PenaltyLevels,
                MaterialCode = row.MaterialCode,
                MaterialCount = row.MaterialCount,
            };
            Array.Copy(row.Amplification, info.Amplification, 4);

            // 属性项:稀有度权重 × 项 × (装备等级 ÷ 档位 + 1.0)(前四项),稀有度权重 × 项(后几项);一次舍入到 float(x87 扩展精度)
            X87Value weight = X87.FromSingle(_statRarityWeight[rarity]);
            X87Value one = X87.FromSingle(1f);
            X87Value grade = X87.FromInt(growthGrade);
            for (int k = 0; k < 8; k++)
            {
                info.StatsA[k] = Scale(weight, row.StatsA[k], k, grade, one);
                info.StatsB[k] = Scale(weight, row.StatsB[k], k, grade, one);
            }

            // 金币:三步取整。每一步都是 FILD → 乘权重 →(前两步加 0.5)→ FISTP 截断;第三步先把整数落成 float 再乘
            X87Value half = X87.FromSingle(0.5f);
            int cost = _cost[growthGrade];
            cost = X87.TruncateToInt(X87.Add(half, X87.Mul(X87.FromInt(cost), X87.FromSingle(_typeWeight[part]))));
            cost = X87.TruncateToInt(X87.Add(half, X87.Mul(X87.FromInt(cost), X87.FromSingle(_costRarityWeight[rarity]))));
            if (_levelCostWeight.TryGetValue(Math.Min(upgradeLevel, 18), out float levelWeight))
            {
                cost = X87.TruncateToInt(X87.Mul(X87.FromSingle(levelWeight), X87.FromSingle((float)cost)));
            }
            info.GoldCost = cost;
            return info;
        }

        private float Scale(X87Value weight, float field, int k, X87Value grade, X87Value one)
        {
            X87Value product = X87.Mul(weight, X87.FromSingle(field));
            if (k < 4)
            {
                // 随等级放大的四项:(float)装备等级 ÷ 档位 + 1.0,再乘进去
                X87Value factor = X87.Add(X87.Div(grade, X87.FromSingle(_level[k])), one);
                product = X87.Mul(product, factor);
            }
            else if (k == 4 || k == 7)
            {
                // 恒为 0 的两格:0 乘任何权重还是 0
                return 0f;
            }
            return X87.ToSingle(product);
        }

        // ------------------------------------------------------------ 读表

        /// <summary>
        /// 读表(服务端 <c>importUpgradeTable</c>)。<paramref name="lines"/> 是 .etc 脚本按"一个标签一行"解出来的结构。
        /// 读不出的数值段只写日志不中止(缺的值按 0,和服务端读失败时留下的值一致),<see cref="Problems"/> 里记着;
        /// 稀有度名字不认得、或"按稀有度"的段里读不出整数,服务端整张表作废——这里返回 false。
        /// </summary>
        public static bool TryParse(ScriptLine[] lines, out UpgradeTable table)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }
            var t = new UpgradeTable();
            table = t;
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    continue;
                }
                if (line.TagIs("[table]"))
                {
                    t.ReadRows(line);
                }
                else if (line.TagIs("[amplification const]"))
                {
                    t.ReadAmplification(line);
                }
                else if (line.TagIs("[cost weights by rarity]"))
                {
                    t.ReadFloats(line, "cost rarity", t._costRarityWeight, 0, Rarities);
                }
                else if (line.TagIs("[stat weights by rarity]"))
                {
                    t.ReadFloats(line, "stat rarity", t._statRarityWeight, 0, Rarities);
                }
                else if (line.TagIs("[type]"))
                {
                    t.ReadFloats(line, "type", t._typeWeight, FirstPart, LastPart - FirstPart + 1);
                }
                else if (line.TagIs("[level]"))
                {
                    t.ReadFloats(line, "level", t._level, 0, 4);
                }
                else if (line.TagIs("[cost]"))
                {
                    t.ReadInts(line, "gold", t._cost, CostSlots);
                }
                else if (line.TagIs("[notice]"))
                {
                    List<int> v = Numbers(line);
                    if (v.Count == 0)
                    {
                        t._problems.Add("notice tag");
                    }
                    else
                    {
                        t.NoticeLevel = v[0];
                    }
                }
                else if (line.TagIs("[repair cost rate by upgrade level]"))
                {
                    List<float> v = Floats(line);
                    for (int i = 0; i < CostSlots; i++)
                    {
                        t._repair[i] = i < v.Count ? v[i] : 0f;
                    }
                    if (v.Count != CostSlots)
                    {
                        t._problems.Add("repair cost rate size " + v.Count + " / " + CostSlots);
                    }
                }
                else if (line.TagIs("[destroy level by rarity]"))
                {
                    if (!t.ReadByRarity(line, "destroy level", t._destroy))
                    {
                        table = null;
                        return false;
                    }
                }
                else if (line.TagIs("[max upgrade level by rarity]"))
                {
                    if (!t.ReadByRarity(line, "max upgrade level", t._max))
                    {
                        table = null;
                        return false;
                    }
                }
                else if (line.TagIs("[cost weight by upgrade level]"))
                {
                    t.ReadLevelCostWeights(line);
                }
            }
            return true;
        }

        private void ReadRows(ScriptLine line)
        {
            _rows.Clear();
            var v = new List<ScriptValue>(line.Values.Length);
            foreach (ScriptValue value in line.Values)
            {
                if (value.IsNumber || value.Kind == ScriptValueKind.Float)
                {
                    v.Add(value);
                }
            }
            const int Width = 17;
            for (int at = 0; at + Width <= v.Count; at += Width)
            {
                var row = new UpgradeRow();
                // 文件里一行:A f1 f2 f3 f4 f5 | i1 i2 i3 i4 i5 | B g1 g2 g3 g4 g5(A、B 与 i 是整数,f、g 是浮点;服务端 ScanInt / ScanFloat 分别读);
                // 行结构里的次序:[0]=A [1]=f1 [2]=f3 [3]=f5 [5]=f2 [6]=f4,第二组同样排在 [0x13] 起(B g1 g3 g5 在前,g2 g4 在后)
                row.StatsA[0] = AsInt(v[at]);
                row.StatsA[1] = AsFloat(v[at + 1]);
                row.StatsA[5] = AsFloat(v[at + 2]);
                row.StatsA[2] = AsFloat(v[at + 3]);
                row.StatsA[6] = AsFloat(v[at + 4]);
                row.StatsA[3] = AsFloat(v[at + 5]);
                row.FailChance = AsInt(v[at + 6]);
                row.PenaltyType = AsInt(v[at + 7]);
                row.PenaltyLevels = AsInt(v[at + 8]);
                row.MaterialCode = AsInt(v[at + 9]);
                row.MaterialCount = AsInt(v[at + 10]);
                row.StatsB[0] = AsInt(v[at + 11]);
                row.StatsB[1] = AsFloat(v[at + 12]);
                row.StatsB[5] = AsFloat(v[at + 13]);
                row.StatsB[2] = AsFloat(v[at + 14]);
                row.StatsB[6] = AsFloat(v[at + 15]);
                row.StatsB[3] = AsFloat(v[at + 16]);
                _rows.Add(row);
            }
        }

        private static float AsFloat(ScriptValue v) => v.Kind == ScriptValueKind.Float ? v.Float : v.Int;

        private static int AsInt(ScriptValue v) => v.Kind == ScriptValueKind.Float ? (int)v.Float : v.Int;

        private void ReadAmplification(ScriptLine line)
        {
            List<int> v = Numbers(line);
            for (int i = 0; i < _rows.Count; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int at = i * 4 + k;
                    if (at < v.Count)
                    {
                        _rows[i].Amplification[k] = v[at];
                    }
                }
            }
            if (v.Count < _rows.Count * 4)
            {
                _problems.Add("amplification const count " + v.Count);
            }
        }

        private void ReadFloats(ScriptLine line, string what, float[] into, int from, int count)
        {
            List<float> v = Floats(line);
            for (int i = 0; i < count; i++)
            {
                into[from + i] = i < v.Count ? v[i] : 0f;
            }
            if (v.Count < count)
            {
                _problems.Add("invalid " + what + " table");
            }
        }

        private void ReadInts(ScriptLine line, string what, int[] into, int count)
        {
            List<int> v = Numbers(line);
            for (int i = 0; i < count; i++)
            {
                into[i] = i < v.Count ? v[i] : 0;
            }
            if (v.Count < count)
            {
                _problems.Add("invalid " + what + " table");
            }
        }

        private bool ReadByRarity(ScriptLine line, string what, int[] into)
        {
            ScriptValue[] values = line.Values;
            int i = 0;
            while (i < values.Length)
            {
                if (values[i].Kind != ScriptValueKind.String)
                {
                    i++;
                    continue;
                }
                int rarity = RarityOf(values[i].Text);
                if (i + 1 >= values.Length || !values[i + 1].IsNumber)
                {
                    _problems.Add(what + ": 缺整数");
                    return false;
                }
                if (rarity >= Rarities)
                {
                    _problems.Add(what + ": 稀有度名字不认得");
                    return false;
                }
                into[rarity] = values[i + 1].Int;
                i += 2;
            }
            return true;
        }

        private void ReadLevelCostWeights(ScriptLine line)
        {
            _levelCostWeight.Clear();
            List<float> v = Floats(line);
            for (int i = 0; i + 1 < v.Count; i += 2)
            {
                _levelCostWeight[IntOf(v[i])] = v[i + 1];
            }
        }

        /// <summary>稀有度名字到编号(<c>getItemRarity</c> 0x088bfd97);不认得返回 6。</summary>
        public static int RarityOf(string name)
        {
            switch (name)
            {
                case "common": return 0;
                case "uncommon": return 1;
                case "rare": return 2;
                case "unique": return 3;
                case "epic": return 4;
                case "chronicle": return 5;
                default: return Rarities;
            }
        }

        private static List<float> Floats(ScriptLine line)
        {
            var list = new List<float>(line.Values.Length);
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Float)
                {
                    list.Add(v.Float);
                }
                else if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
            }
            return list;
        }

        private static List<int> Numbers(ScriptLine line)
        {
            var list = new List<int>(line.Values.Length);
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Float)
                {
                    list.Add((int)v.Float);
                }
                else if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
            }
            return list;
        }

        private static int IntOf(float value) => (int)value;
    }
}
