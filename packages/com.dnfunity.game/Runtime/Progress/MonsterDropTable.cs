using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-116(服务端 WongWork::CMonsterDrop::loadScript 0x08537938 读这些标签、_getGenItemProbIdx 0x085372ee 选行、
// _getBossGoldDrop 0x08535dc8 掷首领的金币堆数与倍率、_generateRandomItem 0x08535ed2 里的金币概率;
// CGenerateRandomNumber::generateNumber 0x085334a4 与 GetRandomRange 0x08539d90 的取值范围)。
namespace Dnf.Game
{
    /// <summary>
    /// 掷骰用的随机数来源(服务端的两个随机函数,证据 E-116):
    /// <see cref="NextInclusive"/> 对应 <c>CGenerateRandomNumber::generateNumber</c>(梅森旋转的 randInt(n),<b>含上界</b>),
    /// <see cref="Range"/> 对应 <c>GetRandomRange</c>(<c>rand() % (hi − lo + 1) + lo</c>,两端都含)。测试里换成按剧本给数的实现。
    /// </summary>
    public interface IDropRandom
    {
        /// <summary>[0, <paramref name="max"/>] 里的一个整数(含两端);max 小于 0 时按 0。</summary>
        int NextInclusive(int max);

        /// <summary>[<paramref name="lo"/>, <paramref name="hi"/>] 里的一个整数(含两端);lo 大于 hi 时两者对调(服务端也是这样)。</summary>
        int Range(int lo, int hi);
    }

    /// <summary>
    /// 自带的伪随机数(xorshift32 + 乘法取高位):同一个种子在 Unity 与命令行里掷出同一串数,不依赖 <see cref="Random"/> 的实现差异。
    /// 原版的随机数用时间做种子,所以复现的是"取值范围与调用顺序",不是具体数列。
    /// </summary>
    public sealed class DropRandom : IDropRandom
    {
        private uint _state;

        public DropRandom(int seed)
        {
            _state = unchecked((uint)seed * 2654435761u + 0x9E3779B9u);
            if (_state == 0)
            {
                _state = 1;
            }
        }

        private uint Next32()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>[0, <paramref name="count"/>) 里均匀取一个(乘法取高位,没有取模的偏差)。</summary>
        private int Below(uint count)
        {
            return (int)(((ulong)Next32() * count) >> 32);
        }

        public int NextInclusive(int max)
        {
            return max <= 0 ? 0 : Below((uint)max + 1u);
        }

        public int Range(int lo, int hi)
        {
            if (hi < lo)
            {
                int t = lo;
                lo = hi;
                hi = t;
            }
            return lo + Below((uint)(hi - lo) + 1u);
        }
    }

    /// <summary>怪物掉落概率表里的一行:一个副本等级区间对应的五个数(金币与四类物品的基础概率,万分之几)。</summary>
    public readonly struct DropProbRow
    {
        public DropProbRow(int levelFrom, int levelTo, int[] values)
        {
            LevelFrom = levelFrom;
            LevelTo = levelTo;
            Values = values ?? new int[MonsterDropTable.TypeCount];
        }

        /// <summary>区间下界(含)。</summary>
        public int LevelFrom { get; }

        /// <summary>区间上界(含)。</summary>
        public int LevelTo { get; }

        /// <summary>五个数:下标 0 是金币,1 到 4 是四类物品(1 可叠加物品、2 装备、3 设计图、4 宠物与宠物装备,见 <see cref="ItemPool.TypeAccepts"/>)。</summary>
        public int[] Values { get; }

        /// <summary>金币的基础概率(万分之几)。</summary>
        public int Gold => Values != null && Values.Length > 0 ? Values[0] : 0;
    }

    /// <summary>
    /// 怪物掉落表:脚本 <c>Etc/ItemDropInfo_Monseter.etc</c>(原版数据文件名就是这么拼的)里金币要用的部分(证据 E-116)。
    /// 服务端 <c>CMonsterDrop::loadScript</c> 按"一个标签一块"依次读下面这些标签,这里照同一套读取原语(<see cref="ScriptCursor"/>)读:
    /// <list type="bullet">
    /// <item><c>[drop prob count]</c> 行数 n;<c>[drop prob]</c> n 行,每行 <c>区间下界 区间上界</c> 加五个数(金币与四类物品的基础概率);</item>
    /// <item><c>[basis of rarity dicision]</c> 四行各六个整数(物品类型 1 到 4 的稀有度分界,<see cref="ItemRarity"/>);</item>
    /// <item><c>[party member drop bonusrate]</c> 5 × 4 个浮点(行:金币与四类物品;列:队伍人数 1 到 4);
    /// <c>[dungeon difficulty drop bonusrate]</c> 5 × 5(列:副本难度 0 到 4);<c>[monster type drop bonusrate]</c> 5 × 4(列:怪物种类 0 到 3);</item>
    /// <item><c>[item drop ref table]</c> 200 组三个整数(等级、向下、向上:物品池的等级窗口,<see cref="ItemWindow"/>);<c>[first boss/named mob hunting]</c> 两个整数(只读不用);</item>
    /// <item><c>[condition rate]</c> 四对整数(按队伍人数:不触发/触发的权重);<c>[gold quantity]</c> 四个整数(两组 最少、最多 堆数);
    /// <c>[gold volume]</c> 两个整数(两组的金额百分比)——这三个只用于首领的金币堆。</item>
    /// </list>
    /// 读不下去(数不够、标签不认识)就整个表作废(服务端是加载失败、起不来);标签之间夹着游离的值时读到那里为止,前面读到的有效(<see cref="ScriptCursor.ScanType"/> 的规则)。
    /// 数值是原版数据,不入库。
    /// </summary>
    public sealed class MonsterDropTable
    {
        /// <summary>概率行里的数个数:金币 1 个 + 四类物品。</summary>
        public const int TypeCount = 5;

        /// <summary>怪物种类数(普通、普通精英、超级精英、首领)。</summary>
        public const int RoleCount = 4;

        /// <summary>队伍人数的列数(1 到 4 人)。</summary>
        public const int MemberCount = 4;

        /// <summary>副本难度的列数(0 到 4)。</summary>
        public const int DifficultyCount = 5;

        /// <summary>金币掷骰的分母:随机数取 0 到 10000(含),比概率小就掉(<c>_generateRandomItem</c> 里的 10000)。</summary>
        public const int RollMax = 10000;

        /// <summary>稀有度的档数(0 到 5;<c>[basis of rarity dicision]</c> 每行六个分界)。</summary>
        public const int RarityCount = 6;

        /// <summary>物品等级窗口表的最大等级(<c>[item drop ref table]</c> 的 200 组)。</summary>
        public const int ItemLevelMax = 200;

        /// <summary>稀有度掷骰的上界:随机数取 1 到 1000000(含,<c>generateNumber(1, 1000000)</c>)。</summary>
        public const int RarityRollMax = 1000000;

        private DropProbRow[] _rows;
        private readonly int[,] _rarityBasis = new int[TypeCount, RarityCount];
        private readonly byte[] _windowBefore = new byte[ItemLevelMax + 1];
        private readonly byte[] _windowAfter = new byte[ItemLevelMax + 1];
        private readonly float[,] _party = new float[TypeCount, MemberCount];
        private readonly float[,] _difficulty = new float[TypeCount, DifficultyCount];
        private readonly float[,] _role = new float[TypeCount, RoleCount];
        private readonly int[] _conditionNone = new int[MemberCount];
        private readonly int[] _conditionLucky = new int[MemberCount];
        private readonly int[] _quantity = new int[4];
        private readonly int[] _volume = new int[2];

        private MonsterDropTable(DropProbRow[] rows)
        {
            _rows = rows;
            // 服务端的这几张表在读脚本之前是什么值没有读到(整个对象是 new 出来的);脚本里都写了。没写时按"不加成"取 1 比取 0 安全
            Fill(_party);
            Fill(_difficulty);
            Fill(_role);
        }

        private static void Fill(float[,] table)
        {
            for (int i = 0; i < table.GetLength(0); i++)
            {
                for (int j = 0; j < table.GetLength(1); j++)
                {
                    table[i, j] = 1f;
                }
            }
        }

        /// <summary>概率行数。</summary>
        public int RowCount => _rows.Length;

        /// <summary>第 <paramref name="index"/> 行。</summary>
        public DropProbRow Row(int index) => _rows[index];

        /// <summary>
        /// 副本等级 <paramref name="dungeonLevel"/> 落在哪一行(<c>_getGenItemProbIdx</c>):从头找第一行 <c>下界 ≤ 等级 ≤ 上界</c>,没有返回 −1(这一次不掉金币)。
        /// </summary>
        public int FindRow(int dungeonLevel)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (dungeonLevel >= _rows[i].LevelFrom && dungeonLevel <= _rows[i].LevelTo)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>队伍人数加成:<paramref name="type"/> 0 是金币;<paramref name="members"/> 为 1 到 4。越界返回 1。</summary>
        public float PartyBonus(int type, int members)
        {
            int m = members - 1;
            return type >= 0 && type < TypeCount && m >= 0 && m < MemberCount ? _party[type, m] : 1f;
        }

        /// <summary>副本难度加成:<paramref name="difficulty"/> 为 0 到 4。越界返回 1。</summary>
        public float DifficultyBonus(int type, int difficulty)
        {
            return type >= 0 && type < TypeCount && difficulty >= 0 && difficulty < DifficultyCount ? _difficulty[type, difficulty] : 1f;
        }

        /// <summary>怪物种类加成:<paramref name="role"/> 为 0 到 3。越界返回 1。</summary>
        public float RoleBonus(int type, int role)
        {
            return type >= 0 && type < TypeCount && role >= 0 && role < RoleCount ? _role[type, role] : 1f;
        }

        /// <summary>
        /// 金币概率(万分之几,<c>_generateRandomItem</c> 开头那一串乘法):
        /// 难度加成 × 种类加成 × 队伍加成 × 该行的金币基础概率,再取整(x87 的 fistp,默认舍入是"到最近、平局取偶数")。
        /// 行号越界返回 0。拜金率与掉落率的修正在 <see cref="GoldDrop"/> 里接着做。
        /// </summary>
        public int GoldProbability(int row, int role, int difficulty, int members)
        {
            if (row < 0 || row >= _rows.Length)
            {
                return 0;
            }
            double product = (double)DifficultyBonus(0, difficulty);
            product *= RoleBonus(0, role);
            product *= PartyBonus(0, members);
            product *= _rows[row].Gold;
            return (int)Math.Round(product, MidpointRounding.ToEven);
        }

        /// <summary>
        /// 某一类物品(<paramref name="type"/> 为 1 到 4)的掉落概率(万分之几,<c>_generateRandomItem</c> 里物品那一段,证据 E-117):
        /// 队伍加成 × 种类加成 × 难度加成 × 该行这一类的基础概率,取整(x87 默认舍入,到最近、平局取偶),再乘掉落率 / 100(整数除法)。
        /// 第 2 类(装备)在服务端还要乘两个"行情"因子——自动市场控制的 <c>GetItemDropRate</c> 0x082f8ae8 和对象上的两个全局值(0x259380、0x259384),
        /// 默认都是 1(控制没打开时 <c>GetItemDropRate</c> 返回 1.0、构造函数把后两者设成 1 与 1.0f),这里不做。行号或类型越界返回 0。
        /// </summary>
        public int ItemProbability(int row, int type, int role, int difficulty, int members, int dropRate = 100)
        {
            if (row < 0 || row >= _rows.Length || type < 1 || type >= TypeCount)
            {
                return 0;
            }
            double product = (double)PartyBonus(type, members);
            product *= RoleBonus(type, role);
            product *= DifficultyBonus(type, difficulty);
            product *= (float)_rows[row].Values[type];
            int rounded = (int)Math.Round(product, MidpointRounding.ToEven);
            return (int)((long)rounded * dropRate / 100);
        }

        /// <summary>
        /// 稀有度掷骰(<c>CLuckPoint::GetItemRarity</c> 0x08550be4 里没有幸运点、没有队伍重叠加成时的那一支,证据 E-117):
        /// 在这一类的六个分界里从头找第一个 <c>掷出的数 ≤ 分界</c>,返回它的下标 0 到 5;一个也不满足返回 0。
        /// 幸运点与组队重叠掉落率会把分界往下挪,默认都是 0,这里不做。类型越界返回 0。
        /// </summary>
        public int ItemRarity(int type, int roll)
        {
            if (type < 1 || type >= TypeCount)
            {
                return 0;
            }
            for (int i = 0; i < RarityCount; i++)
            {
                if (roll <= _rarityBasis[type, i])
                {
                    return i;
                }
            }
            return 0;
        }

        /// <summary>
        /// 等级 <paramref name="level"/> 的怪物能掉的物品等级窗口(<c>generateTable</c> 0x0853466c 里的 <c>[item drop ref table]</c>,证据 E-117):
        /// 物品的 <c>[grade]</c> 在 [level − <paramref name="before"/>, level + <paramref name="after"/> − 1] 里(再截到 200 以内)的才进池。
        /// 等级越界(不在 0 到 200)时两端都是 0。
        /// </summary>
        public void ItemWindow(int level, out int before, out int after)
        {
            if (level < 0 || level > ItemLevelMax)
            {
                before = 0;
                after = 0;
                return;
            }
            before = _windowBefore[level];
            after = _windowAfter[level];
        }

        /// <summary>
        /// 首领的金币堆(<c>_getBossGoldDrop</c>):按队伍人数(<paramref name="members"/> 为 1 到 4,其余返回 false = 这一次不分堆、只出一堆)
        /// 先掷 [0, 不触发权重 + 触发权重],比"触发权重"小就走第二组,否则第一组;堆数在该组的 [最少, 最多] 里均匀取(含两端);
        /// 每堆的金额 = 基础金额 × 该组的百分比 / 100。
        /// </summary>
        public bool TryBossGold(int members, IDropRandom random, out int count, out int volumePercent)
        {
            count = 0;
            volumePercent = 100;
            int m = members - 1;
            if (m < 0 || m >= MemberCount)
            {
                return false;
            }
            int none = _conditionNone[m];
            int lucky = _conditionLucky[m];
            int group = random.NextInclusive(none + lucky) < lucky ? 1 : 0;
            int lo = _quantity[group * 2];
            int hi = _quantity[group * 2 + 1];
            count = random.NextInclusive(hi - lo) + lo;
            volumePercent = _volume[group];
            return true;
        }

        /// <summary>
        /// 按服务端的顺序读各个标签(见类说明)。成功时返回表、<paramref name="error"/> 为 null;失败返回 null,
        /// <paramref name="error"/> 写明读到哪个标签时出的问题。
        /// </summary>
        public static MonsterDropTable Load(ScriptCursor cursor, out string error)
        {
            error = null;
            if (cursor == null)
            {
                error = "没有脚本";
                return null;
            }
            var table = new MonsterDropTable(new DropProbRow[0]);
            while (cursor.ScanType(out string name))
            {
                bool ok = true;
                if (Is(name, "[drop prob count]"))
                {
                    int n = cursor.ScanInt(out ok);
                    if (ok)
                    {
                        // 服务端按行数改容器大小,新行全 0;行数是负数时 resize 会失败,这里当作读错
                        if (n < 0 || n > 100000)
                        {
                            ok = false;
                        }
                        else
                        {
                            table._rows = new DropProbRow[n];
                            for (int i = 0; i < n; i++)
                            {
                                table._rows[i] = new DropProbRow(0, 0, null);
                            }
                        }
                    }
                }
                else if (Is(name, "[drop prob]"))
                {
                    for (int i = 0; ok && i < table._rows.Length; i++)
                    {
                        int from = cursor.ScanInt(out ok);
                        int to = ok ? cursor.ScanInt(out ok) : 0;
                        var values = new int[TypeCount];
                        for (int k = 0; ok && k < TypeCount; k++)
                        {
                            values[k] = cursor.ScanInt(out ok);
                        }
                        if (ok)
                        {
                            table._rows[i] = new DropProbRow(from, to, values);
                        }
                    }
                }
                else if (Is(name, "[basis of rarity dicision]"))
                {
                    // 四行各六个数,依次是物品类型 1 到 4 的稀有度分界(服务端存在对象偏移 0x30 + 类型 × 0x18 起的六个整数)
                    for (int type = 1; ok && type < TypeCount; type++)
                    {
                        for (int i = 0; ok && i < RarityCount; i++)
                        {
                            int v = cursor.ScanInt(out ok);
                            if (ok)
                            {
                                table._rarityBasis[type, i] = v;
                            }
                        }
                    }
                }
                else if (Is(name, "[party member drop bonusrate]"))
                {
                    ok = ReadFloats(cursor, table._party);
                }
                else if (Is(name, "[dungeon difficulty drop bonusrate]"))
                {
                    ok = ReadFloats(cursor, table._difficulty);
                }
                else if (Is(name, "[monster type drop bonusrate]"))
                {
                    ok = ReadFloats(cursor, table._role);
                }
                else if (Is(name, "[item drop ref table]"))
                {
                    // 200 组 "等级 向下 向上":第一个数是等级(下标),后两个数各取低 8 位存成窗口的两端(服务端存成两个字节)
                    for (int i = 0; ok && i < ItemLevelMax; i++)
                    {
                        int level = cursor.ScanInt(out ok);
                        int before = ok ? cursor.ScanInt(out ok) : 0;
                        int after = ok ? cursor.ScanInt(out ok) : 0;
                        if (ok && level >= 0 && level <= ItemLevelMax)
                        {
                            table._windowBefore[level] = (byte)before;
                            table._windowAfter[level] = (byte)after;
                        }
                    }
                }
                else if (Is(name, "[first boss/named mob hunting]"))
                {
                    for (int i = 0; ok && i < 2; i++)
                    {
                        cursor.ScanInt(out ok);
                    }
                }
                else if (Is(name, "[condition rate]"))
                {
                    for (int i = 0; ok && i < MemberCount; i++)
                    {
                        int none = cursor.ScanInt(out ok);
                        int lucky = ok ? cursor.ScanInt(out ok) : 0;
                        if (ok)
                        {
                            table._conditionNone[i] = Clamp16(none);
                            table._conditionLucky[i] = Clamp16(lucky);
                        }
                    }
                }
                else if (Is(name, "[gold quantity]"))
                {
                    for (int i = 0; ok && i < 4; i++)
                    {
                        int v = cursor.ScanInt(out ok);
                        if (ok)
                        {
                            table._quantity[i] = Clamp16(v);
                        }
                    }
                }
                else if (Is(name, "[gold volume]"))
                {
                    for (int i = 0; ok && i < 2; i++)
                    {
                        int v = cursor.ScanInt(out ok);
                        if (ok)
                        {
                            table._volume[i] = Clamp16(v);
                        }
                    }
                }
                else
                {
                    error = "不认识的标签 " + name;
                    return null;
                }
                if (!ok)
                {
                    error = "读 " + name + " 时数不够或类型不对";
                    return null;
                }
            }
            return table;
        }

        private static bool Is(string name, string tag)
        {
            return string.Equals(name, tag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>服务端把这几个值存成 16 位无符号数;脚本里的值本来就在范围内,越界时只取低 16 位。</summary>
        private static int Clamp16(int v)
        {
            return v & 0xFFFF;
        }

        private static bool ReadFloats(ScriptCursor cursor, float[,] into)
        {
            for (int i = 0; i < into.GetLength(0); i++)
            {
                for (int j = 0; j < into.GetLength(1); j++)
                {
                    float v = cursor.ScanFloat(out bool ok);
                    if (!ok)
                    {
                        return false;
                    }
                    into[i, j] = v;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 金币基础金额表:脚本 <c>Etc/ItemDropInfo_Common.etc</c> 的 <c>[gold drop ref table]</c>(证据 E-116,服务端 <c>CGeneratorCommonData::loadScript</c> 0x08534456):
    /// 200 组 <c>怪物等级 基础金额 浮动百分比</c>;<c>getGoldGenTable</c>(0x08533424)取法:等级 0 到 200 取该项,更大的等级退回第 0 项(全 0)。
    /// 服务端取完以后还要过"行情调节"(<c>GetGoldDropAmountRate</c> 0x082f89bc),那个开关默认关着,此时金额原样返回——这里不做。
    /// 数值是原版数据,不入库。
    /// </summary>
    public sealed class GoldGenTable
    {
        /// <summary>表里最大的怪物等级。</summary>
        public const int MaxLevel = 200;

        private readonly int[] _base = new int[MaxLevel + 1];
        private readonly int[] _variance = new int[MaxLevel + 1];

        /// <summary>怪物等级 <paramref name="level"/> 的(基础金额,浮动百分比);等级超过 200 或小于 0 取第 0 项。</summary>
        public (int Base, int Variance) Get(int level)
        {
            int i = level >= 0 && level <= MaxLevel ? level : 0;
            return (_base[i], _variance[i]);
        }

        /// <summary>读表;成功返回表、<paramref name="error"/> 为 null,标签不对或数不够返回 null。</summary>
        public static GoldGenTable Load(ScriptCursor cursor, out string error)
        {
            error = null;
            if (cursor == null)
            {
                error = "没有脚本";
                return null;
            }
            var table = new GoldGenTable();
            while (cursor.ScanType(out string name))
            {
                if (!string.Equals(name, "[gold drop ref table]", StringComparison.OrdinalIgnoreCase))
                {
                    error = "不认识的标签 " + name;
                    return null;
                }
                for (int n = 1; n <= MaxLevel; n++)
                {
                    int level = cursor.ScanInt(out bool ok);
                    int baseAmount = ok ? cursor.ScanInt(out ok) : 0;
                    int variance = ok ? cursor.ScanInt(out ok) : 0;
                    if (!ok)
                    {
                        error = "读 " + name + " 时数不够或类型不对";
                        return null;
                    }
                    if (level >= 0 && level <= MaxLevel)
                    {
                        table._base[level] = baseAmount;
                        table._variance[level] = variance;
                    }
                }
            }
            return table;
        }
    }

    /// <summary>一次掷骰的参数(服务端 <c>stGenerateRefData_t</c> 里金币用得到的几项,证据 E-116)。</summary>
    public readonly struct DropContext
    {
        public DropContext(int monsterLevel, int role, int dungeonLevel, int difficulty, int members = 1, int bourgeoisRate = 0, int dropRate = 100, int rolls = 1)
        {
            MonsterLevel = monsterLevel;
            Role = role;
            DungeonLevel = dungeonLevel;
            Difficulty = difficulty;
            Members = members;
            BourgeoisRate = bourgeoisRate;
            DropRate = dropRate;
            Rolls = rolls;
        }

        /// <summary>怪物等级(查基础金额表)。</summary>
        public int MonsterLevel { get; }

        /// <summary>怪物种类 0 到 3(普通、普通精英、超级精英、首领,<see cref="MonsterKind"/>);4 及以上不掉东西。</summary>
        public int Role { get; }

        /// <summary>副本等级(<c>CDungeon::get_standard_level</c>;选概率行用)。</summary>
        public int DungeonLevel { get; }

        /// <summary>副本难度 0 到 4。</summary>
        public int Difficulty { get; }

        /// <summary>队伍人数 1 到 4。</summary>
        public int Members { get; }

        /// <summary>拜金率(服务端对"账号金币过多"的抑制,0 到 100;超过 50 才起作用)。这里没有实现,恒按 0。</summary>
        public int BourgeoisRate { get; }

        /// <summary>掉落率百分比(服务端全局或活动加成,100 = 不变)。</summary>
        public int DropRate { get; }

        /// <summary>这只怪物掷几次(服务端 <c>CMonsterDrop::generateRandomItem</c> 里的循环次数;普通怪 1)。</summary>
        public int Rolls { get; }
    }

    /// <summary>
    /// 一只怪物死后的金币掷骰(服务端 <c>CMonsterDrop::_generateRandomItem</c> 0x08535ed2 的金币一段,证据 E-116):
    /// <list type="number">
    /// <item>按副本等级选概率行(没有行就不掉);</item>
    /// <item>概率 = 难度加成 × 种类加成 × 队伍加成 × 行里的金币概率,取整;拜金率超过 50 时再乘 (1 − (A + B × (拜金率 − 50)))(<b>没有实现</b>,A、B 是服务端的两个全局常量,我们的拜金率恒为 0);再乘 掉落率 / 100(整数除法);</item>
    /// <item>随机数取 0 到 10000(含),<b>比概率小</b>才掉;</item>
    /// <item>金额 = 基础金额 + 基础金额 × 浮动 / 100,浮动在 [−浮动百分比, +浮动百分比] 里均匀取(C 的整数除法向零取整),至少 1;</item>
    /// <item>首领(种类 3)再按 <see cref="MonsterDropTable.TryBossGold"/> 分成几堆、每堆乘一个百分比(至少 1);其余种类一堆。</item>
    /// </list>
    /// </summary>
    public static class GoldDrop
    {
        /// <summary>
        /// 副本的标准等级(选概率行用):.dgn 的 <c>[basis level]</c>。证据:服务端 <c>ImportDungeonScript</c> 0x088e5faa 把这个标签(内部编号 0x199)读进
        /// <c>STDungeonScript</c> 偏移 0xac,<c>CDungeon::set_dungeon</c> 0x0834acc4 再抄到 <c>CDungeon</c> 偏移 0x10,<c>get_standard_level</c> 0x080f9810 返回它。
        /// 标签出现多次时后一次覆盖前一次(加载器每次都重新赋值);没有这个标签或没有数时取 0(<c>STDungeonScript</c> 的默认值没有核对,推断)。
        /// </summary>
        public static int StandardLevel(ScriptLine[] dungeon)
        {
            int level = 0;
            if (dungeon == null)
            {
                return level;
            }
            foreach (ScriptLine line in dungeon)
            {
                if (line.TagIs("[basis level]"))
                {
                    List<int> numbers = line.Ints();
                    level = numbers.Count > 0 ? numbers[0] : 0;
                }
            }
            return level;
        }

        /// <summary>
        /// 掷一只怪物的金币,返回每一堆的金额(可能是空表)。<paramref name="drops"/> 或 <paramref name="gold"/> 为 null、
        /// 种类不在 0 到 3 之间时返回空表(服务端 <c>generateRandomItem</c> 对种类 ≥ 4 什么都不做)。
        /// </summary>
        public static List<int> Roll(MonsterDropTable drops, GoldGenTable gold, DropContext context, IDropRandom random)
        {
            var pieces = new List<int>();
            if (drops == null || gold == null || random == null || context.Role < 0 || context.Role >= MonsterDropTable.RoleCount)
            {
                return pieces;
            }
            for (int n = 0; n < context.Rolls; n++)
            {
                RollOnce(drops, gold, context, random, pieces);
            }
            return pieces;
        }

        private static void RollOnce(MonsterDropTable drops, GoldGenTable gold, DropContext c, IDropRandom random, List<int> pieces)
        {
            int row = drops.FindRow(c.DungeonLevel);
            if (row < 0)
            {
                return;
            }
            int probability = drops.GoldProbability(row, c.Role, c.Difficulty, c.Members);
            probability = (int)((long)probability * c.DropRate / 100);
            int roll = random.NextInclusive(MonsterDropTable.RollMax);
            if (roll >= probability)
            {
                return;
            }
            (int baseAmount, int variance) = gold.Get(c.MonsterLevel);
            int swing = random.Range(-variance, variance);
            int amount = baseAmount + baseAmount * swing / 100;
            if (amount < 1)
            {
                amount = 1;
            }
            if (c.Role != 3)
            {
                pieces.Add(amount);
                return;
            }
            if (!drops.TryBossGold(c.Members, random, out int count, out int volumePercent))
            {
                pieces.Add(amount);
                return;
            }
            amount = (int)((long)volumePercent * amount / 100);
            if (amount < 1)
            {
                amount = 1;
            }
            for (int i = 0; i < count; i++)
            {
                pieces.Add(amount);
            }
        }
    }
}
