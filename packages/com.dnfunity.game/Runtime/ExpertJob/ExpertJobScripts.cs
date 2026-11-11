using System;
using System.Collections.Generic;
using Dnf.Data;

// 服务端静态读码(df_game_r.orig)得到的副职业脚本字段。
//   装载:initExpertJobScript 0x0896d830 → CExpertJobList::init 0x0896d8ec;
//         等级与上限表 .etc = ImportExpertJobEtcScript 0x0896ae21;各副职业脚本 .exj = ImportExpertJobScript 0x0896b0e4(每项 STExpertJobScript 0x290 字节)。
//   读值的方式:标签后面的值按"一个值一个值读到下一个标签为止",读不出来的位置在一行之首就当"这个标签读完了",行中间读不出来就是装载失败。
//   这里只有自己命名的字段,不含原版数据;全部测试用合成数据。
// 观察与推断:
//   · 观察:四个 .exj 都用同一个装载函数;分解师以外的三条线的 [items] [compound rate] [potion info] 与各种 extraction / monstercard / shop doll 标签服务端也读,
//     这里只记下"出现过"(RegisteredTags),不建模(制作、附魔、人偶的执行不在这一线)。
//   · 观察:[disjoint result] 的每一行 8 个值:机器档、稀有度、未鉴定标志(都截成一个字节)、主产物物品、系数(浮点)、普通附加组号、大成功组号、大成功百分数;
//     组号非 0 时必须在前面已读出的 [additional result] / [big win result] 里有同号条目,否则装载失败(所以这两个标签要写在 [disjoint result] 之前);
//     同键的第二行不会覆盖第一行(std::map::insert)。
namespace Dnf.Game
{
    /// <summary>经验表的一项:<c>[expertness exp]</c> 里一组 "阈值 数 (0,"名字")"。</summary>
    public readonly struct ExpertLevelEntry
    {
        public ExpertLevelEntry(int threshold, int extra, string name)
        {
            Threshold = threshold;
            Extra = extra;
            Name = name;
        }

        /// <summary>这一级的经验阈值(与 <c>GetLevel</c> 比较时当无符号数)。</summary>
        public int Threshold { get; }

        /// <summary>第二个整数(服务端读进结构,没有读到用处;全库都是 0)。</summary>
        public int Extra { get; }

        /// <summary>等级名的字符串引用(键);可能为 null。</summary>
        public string Name { get; }
    }

    /// <summary><c>[skill]</c> 里一对 "技能号 初始等级":就职时授予(技能号, 第二个数),升级时按新的副职业等级重新授予。</summary>
    public readonly struct ExpertSkillGrant
    {
        public ExpertSkillGrant(int skillId, int level)
        {
            SkillId = skillId;
            Level = level;
        }

        public int SkillId { get; }

        public int Level { get; }
    }

    /// <summary><c>[endurance repair cost]</c> 里一对 "整修全价 本档耐久上限":第 n 对对应机器档 n−1。</summary>
    public readonly struct EnduranceRepairCost
    {
        public EnduranceRepairCost(int cost, int maxEndurance)
        {
            Cost = cost;
            MaxEndurance = maxEndurance;
        }

        /// <summary>耐久从 0 修满的金币全价(服务端 +0)。</summary>
        public int Cost { get; }

        /// <summary>这一档的耐久上限(服务端 +4;就职与升档时耐久被设成它)。</summary>
        public int MaxEndurance { get; }
    }

    /// <summary><c>[additional result]</c> / <c>[big win result]</c> 的一行:同组号的行按书写次序排。</summary>
    public readonly struct SelectRate
    {
        public SelectRate(int item, int weight, float divisor, int minGrade, int maxGrade)
        {
            Item = item;
            Weight = weight;
            Divisor = divisor;
            MinGrade = minGrade;
            MaxGrade = maxGrade;
        }

        /// <summary>产物物品编号。</summary>
        public int Item { get; }

        /// <summary>权重(这一组是按 0 到 9999 的随机数逐项减权重选的,所以一组的权重和通常是 10000)。</summary>
        public int Weight { get; }

        /// <summary>数量除数:产物个数 = max(1, 向零取整(被分解物品的等级 ÷ 除数))。</summary>
        public float Divisor { get; }

        /// <summary>适用的最低等级(含)。</summary>
        public int MinGrade { get; }

        /// <summary>适用的最高等级(含)。</summary>
        public int MaxGrade { get; }
    }

    /// <summary>
    /// <c>[disjoint result]</c> 的键:(机器档, 稀有度, 未鉴定)。服务端的 <c>DisjointMachineGrade</c>(0x085d3b9e)三个成员各占一个字节,
    /// 读取时整数被截成一个字节,查找时也截——所以 256 与 0 同键(观察;全库取值都远小于 256)。
    /// </summary>
    public readonly struct DisjointKey : IEquatable<DisjointKey>
    {
        public DisjointKey(int machineGrade, int rarity, bool unidentified)
        {
            MachineGrade = (byte)machineGrade;
            Rarity = (byte)rarity;
            Unidentified = unidentified;
        }

        public byte MachineGrade { get; }

        public byte Rarity { get; }

        public bool Unidentified { get; }

        public bool Equals(DisjointKey other)
        {
            return MachineGrade == other.MachineGrade && Rarity == other.Rarity && Unidentified == other.Unidentified;
        }

        public override bool Equals(object obj) => obj is DisjointKey other && Equals(other);

        public override int GetHashCode() => (MachineGrade << 9) | (Rarity << 1) | (Unidentified ? 1 : 0);
    }

    /// <summary><c>[disjoint result]</c> 的一行(键之外的部分)。</summary>
    public sealed class DisjointRow
    {
        public DisjointRow(int item, float factor, IReadOnlyList<SelectRate> additional, IReadOnlyList<SelectRate> bigWin, int bigWinPercent)
        {
            Item = item;
            Factor = factor;
            Additional = additional ?? Array.Empty<SelectRate>();
            BigWin = bigWin ?? Array.Empty<SelectRate>();
            BigWinPercent = bigWinPercent;
        }

        /// <summary>主产物物品(全库每行都是同一种通用材料)。</summary>
        public int Item { get; }

        /// <summary>主产物个数的系数(千分之几来自 1000 倍之后的整除,见 <see cref="DisjointRules"/>)。</summary>
        public float Factor { get; }

        /// <summary>普通附加产物组(组号为 0 时为空)。</summary>
        public IReadOnlyList<SelectRate> Additional { get; }

        /// <summary>大成功时的附加产物组(组号为 0 时为空)。</summary>
        public IReadOnlyList<SelectRate> BigWin { get; }

        /// <summary>这一行的大成功基础百分数(乘 100 变万分比)。</summary>
        public int BigWinPercent { get; }
    }

    /// <summary><c>[disjoint self service]</c>:稀有度不低于 2 时,按 <see cref="Chance"/>% 自用额外得到 <see cref="Count"/> 个 <see cref="Item"/>(只有自己的分解机才有)。</summary>
    public readonly struct DisjointSelfService
    {
        public DisjointSelfService(int chance, int item, int count)
        {
            Chance = chance;
            Item = item;
            Count = count;
        }

        public int Chance { get; }

        public int Item { get; }

        public int Count { get; }
    }

    /// <summary>
    /// 副职业杂项脚本 expertjob.etc(character 目录下):<c>[expertjob level limit]</c>(副职业等级 → 角色等级下限,键与值分别截成无符号字节与有符号 16 位)与
    /// <c>[disjointer big win rate]</c>(键 → 大成功率加成,都是字节)。键重复是装载错误;不认得的标签读过去不管。
    /// </summary>
    public sealed class ExpertJobEtc
    {
        private readonly Dictionary<byte, short> _levelLimit = new Dictionary<byte, short>();
        private readonly Dictionary<byte, byte> _bigWinRate = new Dictionary<byte, byte>();
        private readonly List<string> _errors = new List<string>();

        public static readonly ExpertJobEtc Empty = new ExpertJobEtc();

        /// <summary>装载错误(重复键、半对值);空表示读得干净。</summary>
        public IReadOnlyList<string> Errors => _errors;

        public bool IsValid => _errors.Count == 0;

        public int LevelLimitCount => _levelLimit.Count;

        /// <summary>副职业等级 <paramref name="expertLevel"/>(取低 8 位)要求的角色等级;表里没有返回 false。</summary>
        public bool TryGetLevelLimit(int expertLevel, out int charLevel)
        {
            if (_levelLimit.TryGetValue((byte)expertLevel, out short v))
            {
                charLevel = v;
                return true;
            }
            charLevel = 0;
            return false;
        }

        /// <summary>
        /// 角色等级 <paramref name="charLevel"/> 是不是表里某个上限值(<c>STExpertJobEtcScript::isExpertJobLimitLevel</c> 0x0849f47c:遍历表里的值找相等的)。
        /// </summary>
        public bool IsLimitLevel(int charLevel)
        {
            foreach (short v in _levelLimit.Values)
            {
                if (v == charLevel)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>分解大成功率加成表里键为 <paramref name="key"/>(取低 8 位)的值;没有返回 0(与服务端 <c>find</c> 不到时不加一致)。</summary>
        public int BigWinRate(int key)
        {
            return _bigWinRate.TryGetValue((byte)key, out byte v) ? v : 0;
        }

        public static ExpertJobEtc Parse(IReadOnlyList<ScriptLine> lines)
        {
            var etc = new ExpertJobEtc();
            if (lines == null)
            {
                return etc;
            }
            foreach (ScriptLine line in lines)
            {
                if (line == null || line.Tag == null)
                {
                    continue;
                }
                if (line.TagIs("[expertjob level limit]"))
                {
                    if (!etc.ReadPairs(line, true))
                    {
                        break;
                    }
                }
                else if (line.TagIs("[disjointer big win rate]"))
                {
                    if (!etc.ReadPairs(line, false))
                    {
                        break;
                    }
                }
            }
            return etc;
        }

        private bool ReadPairs(ScriptLine line, bool levelLimit)
        {
            var r = new ExjReader(line.Values);
            string tag = line.Tag;
            while (!r.AtEnd)
            {
                if (!r.TryInt(out int key))
                {
                    break;
                }
                if (!r.TryInt(out int value))
                {
                    _errors.Add(tag + " 的最后一对只有一个值");
                    return false;
                }
                bool added = levelLimit
                    ? _levelLimit.TryAdd((byte)key, (short)value)
                    : _bigWinRate.TryAdd((byte)key, (byte)value);
                if (!added)
                {
                    _errors.Add(tag + " 的键重复:" + (byte)key);
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 一个副职业的 .exj 脚本。字段按 <c>ImportExpertJobScript</c> 的偏移与标签整理;同一个标签出现第二次会先清空再读(服务端 <c>clear()</c> 后重读)。
    /// 种类不是分解师时,分解相关的字段保持空。
    /// </summary>
    public sealed class ExpertJobScript
    {
        private readonly List<ExpertLevelEntry> _levels = new List<ExpertLevelEntry>();
        private readonly List<ExpertSkillGrant> _skills = new List<ExpertSkillGrant>();
        private readonly List<int> _clearQuests = new List<int>();
        private readonly List<int> _giveupCosts = new List<int>();
        private readonly List<EnduranceRepairCost> _enduranceRepair = new List<EnduranceRepairCost>();
        private readonly List<int> _upgradeCosts = new List<int>();
        private readonly Dictionary<int, int> _portable = new Dictionary<int, int>();
        private readonly Dictionary<DisjointKey, DisjointRow> _rows = new Dictionary<DisjointKey, DisjointRow>();
        private readonly Dictionary<int, List<SelectRate>> _additional = new Dictionary<int, List<SelectRate>>();
        private readonly Dictionary<int, List<SelectRate>> _bigWin = new Dictionary<int, List<SelectRate>>();
        private readonly SortedSet<string> _registered = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _errors = new List<string>();

        /// <summary><c>[giveup cost]</c> 最多的项数(≥ 10 项是装载错误)。</summary>
        public const int MaxGiveupCosts = 9;

        public ExpertJobScript(ExpertJobKind kind)
        {
            Kind = kind;
        }

        public ExpertJobKind Kind { get; }

        /// <summary>经验表(阈值从小到大;表长就是最高等级)。</summary>
        public IReadOnlyList<ExpertLevelEntry> Levels => _levels;

        /// <summary>最高副职业等级(= 经验表长度)。</summary>
        public int MaxLevel => _levels.Count;

        public IReadOnlyList<ExpertSkillGrant> Skills => _skills;

        /// <summary>放弃时必须已完成(并随后被重置)的任务编号。</summary>
        public IReadOnlyList<int> ClearQuests => _clearQuests;

        /// <summary>第 n 次放弃要付的金币(n 从 0 起;超过表长就不能再放弃)。</summary>
        public IReadOnlyList<int> GiveupCosts => _giveupCosts;

        public int BaseConst { get; private set; }

        /// <summary><c>[additional const]</c>:服务端读进去,分解计算没有用到(观察)。</summary>
        public int AdditionalConst { get; private set; }

        public IReadOnlyList<EnduranceRepairCost> EnduranceRepair => _enduranceRepair;

        /// <summary>机器档数(= <c>[endurance repair cost]</c> 的项数;最高档是它减 1)。</summary>
        public int MachineMaxGrade => _enduranceRepair.Count;

        public int EnduranceReduceMin { get; private set; }

        public int EnduranceReduceMax { get; private set; }

        public int EnduranceInitial { get; private set; }

        /// <summary>升档费用表:第 k 项是升到机器 k 档的费用(文件里写的序号是 k+1,必须依次排)。</summary>
        public IReadOnlyList<int> UpgradeCosts => _upgradeCosts;

        public int GainExpMin { get; private set; }

        public int GainExpMax { get; private set; }

        /// <summary><c>[limit disjoint gold]</c>:设摊收费的上限(摆摊给他人用,这一版不做)。</summary>
        public int LimitDisjointGold { get; private set; }

        /// <summary><c>[portable disjoint info]</c>:便携分解器序号 → 可分解的最高等级。</summary>
        public IReadOnlyDictionary<int, int> PortableLevels => _portable;

        public bool HasSelfService { get; private set; }

        public DisjointSelfService SelfService { get; private set; }

        public IReadOnlyDictionary<DisjointKey, DisjointRow> Rows => _rows;

        /// <summary>读到过、但这一版不建模的标签(制作、附魔、人偶的内容)。</summary>
        public IReadOnlyCollection<string> RegisteredTags => _registered;

        public IReadOnlyList<string> Errors => _errors;

        public bool IsValid => _errors.Count == 0;

        /// <summary>
        /// 经验 <paramref name="exp"/> 对应的脚本等级(<c>STExpertJobScript::GetLevel</c> 0x08693ba0):不大于 exp 的阈值个数加 1,封顶表长;
        /// 阈值与 exp 都按无符号数比较(所以负经验当成很大的数,等级是表长)。表为空返回 0。
        /// </summary>
        public int GetLevel(int exp)
        {
            int n = _levels.Count;
            int level = 0;
            while (true)
            {
                if (n <= level)
                {
                    return level;
                }
                if ((uint)exp < (uint)_levels[level].Threshold)
                {
                    break;
                }
                level++;
            }
            return level + 1;
        }

        /// <summary>exp 是不是恰好等于某一级的阈值(<c>isBoundaryExpValue</c> 0x0849f420)。</summary>
        public bool IsBoundaryExp(int exp)
        {
            foreach (ExpertLevelEntry e in _levels)
            {
                if (e.Threshold == exp)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary><c>_GetEnduranceRepairCost</c>(0x085d3c26):键从 1 起,第 key 项;越界返回 false。</summary>
        public bool TryGetEnduranceRepair(int key, out EnduranceRepairCost cost)
        {
            if (key >= 1 && key - 1 < _enduranceRepair.Count)
            {
                cost = _enduranceRepair[key - 1];
                return true;
            }
            cost = default;
            return false;
        }

        /// <summary><c>GetUpgradeCost</c>(0x085d3d3a):下标 index 在表内取表里的值,否则 0。</summary>
        public int GetUpgradeCost(int index)
        {
            return index >= 0 && index < _upgradeCosts.Count ? _upgradeCosts[index] : 0;
        }

        /// <summary><c>GetPortableDisjointPossibleLevel</c>(0x0822b1f4 一带):便携分解器序号对应的最高可分解等级;没有这个序号返回 −1。</summary>
        public int GetPortableLevel(int index)
        {
            return _portable.TryGetValue(index, out int level) ? level : -1;
        }

        public bool TryGetRow(DisjointKey key, out DisjointRow row) => _rows.TryGetValue(key, out row);

        /// <summary>按 .exj 的标签流装载。分解师以外的种类只读公共部分与分解相关标签(服务端也是同一个函数)。</summary>
        public static ExpertJobScript Parse(ExpertJobKind kind, IReadOnlyList<ScriptLine> lines)
        {
            var s = new ExpertJobScript(kind);
            if (lines == null)
            {
                return s;
            }
            foreach (ScriptLine line in lines)
            {
                if (line == null || line.Tag == null)
                {
                    continue;
                }
                if (!s.ReadLine(line))
                {
                    break;
                }
            }
            return s;
        }

        private bool Fail(string tag, string message)
        {
            _errors.Add(tag + " " + message);
            return false;
        }

        private bool ReadLine(ScriptLine line)
        {
            string tag = line.Tag;
            var r = new ExjReader(line.Values);
            if (tag.StartsWith("[/", StringComparison.Ordinal))
            {
                return true;
            }
            if (line.TagIs("[expertness exp]"))
            {
                while (!r.AtEnd)
                {
                    if (!r.TryInt(out int threshold))
                    {
                        break;
                    }
                    if (!r.TryInt(out int extra))
                    {
                        return Fail(tag, "的最后一组只有一个整数");
                    }
                    _levels.Add(new ExpertLevelEntry(threshold, extra, r.TakeText()));
                }
                return true;
            }
            if (line.TagIs("[skill]"))
            {
                _skills.Clear();
                while (!r.AtEnd)
                {
                    if (!r.TryInt(out int id))
                    {
                        break;
                    }
                    if (!r.TryInt(out int level))
                    {
                        return Fail(tag, "的最后一对只有一个值");
                    }
                    _skills.Add(new ExpertSkillGrant(id, level));
                }
                return true;
            }
            if (line.TagIs("[additional result]"))
            {
                _additional.Clear();
                return ReadSelectRates(tag, ref r, _additional);
            }
            if (line.TagIs("[big win result]"))
            {
                _bigWin.Clear();
                return ReadSelectRates(tag, ref r, _bigWin);
            }
            if (line.TagIs("[base const]"))
            {
                if (!r.TryInt(out int baseConst))
                {
                    return Fail(tag, "缺值");
                }
                BaseConst = baseConst;
                return true;
            }
            if (line.TagIs("[additional const]"))
            {
                if (!r.TryInt(out int additionalConst))
                {
                    return Fail(tag, "缺值");
                }
                AdditionalConst = additionalConst;
                return true;
            }
            if (line.TagIs("[disjoint result]"))
            {
                return ReadDisjointRows(tag, ref r);
            }
            if (line.TagIs("[clear quests]"))
            {
                _clearQuests.Clear();
                while (r.TryInt(out int q))
                {
                    _clearQuests.Add(q);
                }
                return true;
            }
            if (line.TagIs("[giveup cost]"))
            {
                _giveupCosts.Clear();
                while (r.TryInt(out int c))
                {
                    _giveupCosts.Add(c);
                    if (_giveupCosts.Count > MaxGiveupCosts)
                    {
                        return Fail(tag, "项数过多(服务端上限 9 项,读到第 10 项就装载失败)");
                    }
                }
                return true;
            }
            if (line.TagIs("[endurance repair cost]"))
            {
                _enduranceRepair.Clear();
                while (!r.AtEnd)
                {
                    if (!r.TryInt(out int cost))
                    {
                        break;
                    }
                    if (!r.TryInt(out int max))
                    {
                        return Fail(tag, "的最后一对只有一个值");
                    }
                    _enduranceRepair.Add(new EnduranceRepairCost(cost, max));
                }
                return true;
            }
            if (line.TagIs("[endurance reduce]"))
            {
                if (!r.TryInt(out int min) || !r.TryInt(out int max))
                {
                    return Fail(tag, "需要两个整数");
                }
                EnduranceReduceMin = min;
                EnduranceReduceMax = max;
                return true;
            }
            if (line.TagIs("[endurance initial value]"))
            {
                if (!r.TryInt(out int initial))
                {
                    return Fail(tag, "缺值");
                }
                EnduranceInitial = initial;
                return true;
            }
            if (line.TagIs("[upgrade cost]"))
            {
                _upgradeCosts.Clear();
                while (!r.AtEnd)
                {
                    if (!r.TryInt(out int index))
                    {
                        break;
                    }
                    if (index - 1 != _upgradeCosts.Count)
                    {
                        return Fail(tag, "序号必须从 1 起依次排,读到 " + index);
                    }
                    if (!r.TryInt(out int cost))
                    {
                        return Fail(tag, "的最后一对只有一个值");
                    }
                    _upgradeCosts.Add(cost);
                }
                return true;
            }
            if (line.TagIs("[gain exp]"))
            {
                if (!r.TryInt(out int min) || !r.TryInt(out int max))
                {
                    return Fail(tag, "需要两个整数");
                }
                GainExpMin = min;
                GainExpMax = max;
                return true;
            }
            if (line.TagIs("[limit disjoint gold]"))
            {
                if (!r.TryInt(out int limitGold))
                {
                    return Fail(tag, "缺值");
                }
                LimitDisjointGold = limitGold;
                return true;
            }
            if (line.TagIs("[portable disjoint info]"))
            {
                while (!r.AtEnd)
                {
                    if (!r.TryInt(out int index))
                    {
                        break;
                    }
                    if (!r.TryInt(out int level))
                    {
                        return Fail(tag, "的最后一对只有一个值");
                    }
                    if (!_portable.TryAdd(index, (ushort)level))
                    {
                        return Fail(tag, "的序号重复:" + index);
                    }
                }
                return true;
            }
            if (line.TagIs("[disjoint self service]"))
            {
                if (!r.TryInt(out int chance) || !r.TryInt(out int item) || !r.TryInt(out int count))
                {
                    return Fail(tag, "需要三个整数");
                }
                SelfService = new DisjointSelfService(chance, item, count);
                HasSelfService = true;
                return true;
            }
            _registered.Add(tag);
            return true;
        }

        private bool ReadSelectRates(string tag, ref ExjReader r, Dictionary<int, List<SelectRate>> into)
        {
            while (!r.AtEnd)
            {
                if (!r.TryInt(out int key))
                {
                    break;
                }
                if (!r.TryInt(out int min) || !r.TryInt(out int max) || !r.TryInt(out int item) || !r.TryInt(out int weight) || !r.TryFloat(out float divisor))
                {
                    return Fail(tag, "的一行不足六个值(组号、最低等级、最高等级、物品、权重、除数)");
                }
                if (!into.TryGetValue(key, out List<SelectRate> list))
                {
                    list = new List<SelectRate>();
                    into[key] = list;
                }
                list.Add(new SelectRate(item, weight, divisor, min, max));
            }
            return true;
        }

        private bool ReadDisjointRows(string tag, ref ExjReader r)
        {
            _rows.Clear();
            while (!r.AtEnd)
            {
                if (!r.TryInt(out int grade))
                {
                    break;
                }
                if (!r.TryInt(out int rarity) || !r.TryInt(out int unident) || !r.TryInt(out int item) || !r.TryFloat(out float factor)
                    || !r.TryInt(out int addIdx))
                {
                    return Fail(tag, "的一行没读完");
                }
                List<SelectRate> additional = null;
                if (addIdx != 0 && !_additional.TryGetValue(addIdx, out additional))
                {
                    return Fail(tag, "引用了不存在的 [additional result] 组 " + addIdx);
                }
                if (!r.TryInt(out int bigIdx))
                {
                    return Fail(tag, "的一行没读完");
                }
                List<SelectRate> bigWin = null;
                if (bigIdx != 0 && !_bigWin.TryGetValue(bigIdx, out bigWin))
                {
                    return Fail(tag, "引用了不存在的 [big win result] 组 " + bigIdx);
                }
                if (!r.TryInt(out int percent))
                {
                    return Fail(tag, "的一行没读完");
                }
                var key = new DisjointKey(grade, rarity, (byte)unident != 0);
                _rows.TryAdd(key, new DisjointRow(item, factor, additional?.ToArray(), bigWin?.ToArray(), percent));
            }
            return true;
        }
    }

    /// <summary>读标签值的小游标:整数与浮点(整数写法的数也算浮点)按次序取;一个值也没读到(整行用完或遇到不是数的值)返回 false。</summary>
    internal struct ExjReader
    {
        private readonly ScriptValue[] _values;
        private int _pos;

        public ExjReader(ScriptValue[] values)
        {
            _values = values ?? Array.Empty<ScriptValue>();
            _pos = 0;
        }

        public bool AtEnd => _pos >= _values.Length;

        public bool TryInt(out int value)
        {
            if (_pos < _values.Length && _values[_pos].IsNumber)
            {
                value = _values[_pos].Int;
                _pos++;
                return true;
            }
            value = 0;
            return false;
        }

        public bool TryFloat(out float value)
        {
            if (_pos < _values.Length)
            {
                ScriptValue v = _values[_pos];
                if (v.Kind == ScriptValueKind.Float)
                {
                    value = v.Float;
                    _pos++;
                    return true;
                }
                if (v.IsNumber)
                {
                    value = v.Int;
                    _pos++;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        /// <summary>取下一个字符串类的值(字符串或"种类 + 键"引用)的文本;下一个不是就什么都不取、返回 null。</summary>
        public string TakeText()
        {
            if (_pos < _values.Length)
            {
                ScriptValue v = _values[_pos];
                if (v.Kind == ScriptValueKind.String || v.Kind == ScriptValueKind.Keyed)
                {
                    _pos++;
                    return v.Text;
                }
            }
            return null;
        }
    }
}
