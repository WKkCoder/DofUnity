using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-124(服务端 importRankSystemScript 0x0890ed7f 读文件;CDataManager::getDungeonClearAverageTime 0x083639d2、getClearTimeBonusPoint 0x08363ac8、
// calRankPoint 0x083639c4、getRankIndex 0x083638c0、getRankBonusIndex 0x08363838 的逐行读法;CParty::SetPlayResult 0x085b2412 的调用次序;
// 客户端 0x0102baf0 把评分值换成字母图帧的映射)。
namespace Dnf.Game
{
    /// <summary>通关用时奖励的一档:比平均用时快至少 <see cref="Threshold"/> 毫秒时给 <see cref="Bonus"/> 分(<c>stBonusPointValues</c>)。</summary>
    public readonly struct TimeBonusStep
    {
        public TimeBonusStep(int threshold, int bonus)
        {
            Threshold = threshold;
            Bonus = bonus;
        }

        public int Threshold { get; }

        public int Bonus { get; }
    }

    /// <summary>
    /// 副本评分的数据与算术(etc/ranksysteminfo.etc,证据 E-124)。评分分九档,从低到高是 F、E、D、C、B、A、S、SS、SSS(客户端字母图 Rank_Letter.img 的帧 8 到 0)。
    /// 服务端算评分的次序(<c>CParty::SetPlayResult</c>):
    /// <list type="number">
    /// <item>平均用时 = <see cref="AverageTime"/>(副本编号, 开局队伍人数, 难度)——表里没有这个副本就是 0;</item>
    /// <item>用时分 = <see cref="TimeBonus"/>(平均用时, 实际用时毫秒)——任何一个是 0 就是 0;否则 差 = 平均 − 实际,取"门槛 ≤ 差"的最后一档的分数(一档也不到是 0);</item>
    /// <item>评分点 = <see cref="RankPoint"/>(用时分, 客户端报上来的基础分)——两数相加,基础分是客户端发来的一个<b>有符号字节</b>;</item>
    /// <item>评分值 = <see cref="RankValue"/>(评分点)——八个门槛(从高到低)里第一个"评分点 ≥ 门槛"的门槛本身,八个都不到是 0;发给客户端的就是这个值;</item>
    /// <item>评分档位 = <see cref="BonusIndex"/>(评分值)——前五个门槛对应 4 到 0,其余 −1;通关经验按它取加成比例(<see cref="ExpRates.RankBonusRate"/>)。</item>
    /// </list>
    /// 客户端把评分值换成字母图的帧:与第 i 个门槛相等就是第 i 帧(<see cref="FrameOf"/>),都不等是第 8 帧。
    /// 文件里后两块(<c>[combo interval boundaries]</c>、<c>[combo practice bonus inerval]</c>)服务端只读不用,是客户端算连击的数据,这里没有读。
    /// <b>基础分(客户端算的那个字节)的公式在客户端受保护的代码段里,没有恢复</b>——见 Dnf.Play 的 <c>ScoreEstimate</c>(估计值,不是原版)。数值是原版数据,不入库。
    /// </summary>
    public sealed class RankSystem
    {
        public const string Path = "etc/ranksysteminfo.etc";

        /// <summary>评分的档数(F 到 SSS)。</summary>
        public const int GradeCount = 9;

        /// <summary>评分门槛的个数;文件里写不满时后面的取 0。</summary>
        public const int ThresholdCount = 8;

        /// <summary>每个副本记录里的平均用时个数(难度 0 到 4;服务端只取前四个,第五个读进来不用)。</summary>
        public const int AverageSlots = 5;

        private const int RecordInts = 2 + AverageSlots;

        private readonly int[] _thresholds = new int[ThresholdCount];
        private readonly int _given;
        private readonly TimeBonusStep[] _steps;
        private readonly Dictionary<long, int[]> _averages = new Dictionary<long, int[]>();

        /// <summary>什么数据也没有:平均用时全是 0(用时分恒为 0),没有门槛(评分值恒为 0、档位恒为 −1、字母恒为 F)。</summary>
        public static RankSystem Empty => new RankSystem(null, null, null);

        /// <summary>
        /// 直接给数据造(测试用):<paramref name="thresholds"/> 是从高到低的门槛(最多 8 个,存成无符号 16 位),<paramref name="steps"/> 是用时奖励的各档(按文件里的次序),
        /// <paramref name="averages"/> 的每个元素是 (副本编号, 队伍人数, 难度 0 到 4 的平均用时毫秒);同一个 (副本编号, 队伍人数) 以先给的为准。
        /// </summary>
        public RankSystem(IReadOnlyList<int> thresholds, IReadOnlyList<TimeBonusStep> steps, IEnumerable<(int Dungeon, int Party, int[] Averages)> averages)
        {
            for (int i = 0; thresholds != null && i < thresholds.Count && i < ThresholdCount; i++)
            {
                _thresholds[i] = unchecked((ushort)thresholds[i]);
                _given = i + 1;
            }
            _steps = new TimeBonusStep[steps == null ? 0 : steps.Count];
            for (int i = 0; i < _steps.Length; i++)
            {
                _steps[i] = steps[i];
            }
            if (averages != null)
            {
                foreach ((int dungeon, int party, int[] values) in averages)
                {
                    long key = KeyOf(dungeon, party);
                    if (_averages.ContainsKey(key))
                    {
                        continue;
                    }
                    var slots = new int[AverageSlots];
                    for (int i = 0; values != null && i < values.Length && i < AverageSlots; i++)
                    {
                        slots[i] = values[i];
                    }
                    _averages[key] = slots;
                }
            }
        }

        private static long KeyOf(int dungeon, int party)
        {
            return ((long)dungeon << 32) | (uint)party;
        }

        /// <summary>读进来的 (副本, 队伍人数) 平均用时记录条数。</summary>
        public int AverageRecordCount => _averages.Count;

        /// <summary>用时奖励的档数。</summary>
        public int StepCount => _steps.Length;

        /// <summary>第 <paramref name="index"/> 档用时奖励(越界返回全 0)。</summary>
        public TimeBonusStep StepAt(int index)
        {
            return (uint)index < (uint)_steps.Length ? _steps[index] : default;
        }

        /// <summary>读到几个评分门槛(0 到 8)。</summary>
        public int ThresholdsGiven => _given;

        /// <summary>第 <paramref name="index"/> 个评分门槛(0 是最高;没读到的与越界返回 0)。</summary>
        public int Threshold(int index)
        {
            return (uint)index < (uint)_given ? _thresholds[index] : 0;
        }

        /// <summary>
        /// 副本的平均通关用时(毫秒;<c>getDungeonClearAverageTime</c>):按 (副本编号, 队伍人数) 找记录,<paramref name="difficulty"/> 取 0 到 3 对应前四个数,
        /// 其余难度(含 4 与负数)与找不到记录都是 0。
        /// </summary>
        public int AverageTime(int dungeonId, int partyCount, int difficulty)
        {
            if (difficulty < 0 || difficulty > 3 || !_averages.TryGetValue(KeyOf(dungeonId, partyCount), out int[] slots))
            {
                return 0;
            }
            return slots[difficulty];
        }

        /// <summary>
        /// 用时分(<c>getClearTimeBonusPoint</c>):<paramref name="averageMs"/> 或 <paramref name="clearMs"/> 为 0 时是 0;否则差 = 平均 − 实际(32 位回绕),
        /// 按文件里的次序走各档,遇到"差 &lt; 门槛"就停,给前一档的分数(一档也没过是 0),全过了给最后一档的分数。
        /// </summary>
        public int TimeBonus(int averageMs, int clearMs)
        {
            if (averageMs == 0 || clearMs == 0)
            {
                return 0;
            }
            int diff = unchecked(averageMs - clearMs);
            int bonus = 0;
            foreach (TimeBonusStep step in _steps)
            {
                if (diff < step.Threshold)
                {
                    return bonus;
                }
                bonus = step.Bonus;
            }
            return bonus;
        }

        /// <summary>评分点(<c>calRankPoint</c>):用时分加基础分(32 位回绕)。</summary>
        public static int RankPoint(int timeBonus, int baseScore)
        {
            return unchecked(timeBonus + baseScore);
        }

        /// <summary>
        /// 评分值(<c>getRankIndex</c>):从最高的门槛起,第一个"评分点 ≥ 门槛"的门槛本身;八个都不到是 0。
        /// 逐个比较、不假定门槛有序(文件里是从高到低);没读到的门槛不参与(服务端把它们补成 0,那样什么分数都到档,没有意义)。
        /// </summary>
        public int RankValue(int rankPoint)
        {
            for (int i = 0; i < _given; i++)
            {
                if (rankPoint >= _thresholds[i])
                {
                    return _thresholds[i];
                }
            }
            return 0;
        }

        /// <summary>
        /// 评分档位(<c>getRankBonusIndex</c>):前五个门槛里第一个"值 ≥ 门槛"的位置 i 对应 4 − i(最高档是 4),五个都不到是 −1。
        /// 参数是评分值(<see cref="RankValue"/> 的结果;传评分点结果相同,因为评分值是它所在的那个门槛)。
        /// </summary>
        public int BonusIndex(int rankValue)
        {
            for (int i = 0; i < ExpRates.RankBonusCount && i < _given; i++)
            {
                if (rankValue >= _thresholds[i])
                {
                    return ExpRates.RankBonusCount - 1 - i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 评分值在字母图 Rank_Letter.img 里的帧(客户端 0x0102baf0):与第 i 个门槛相等就是 i(0 = SSS……7 = E),都不等是 8(F)。
        /// </summary>
        public int FrameOf(int rankValue)
        {
            for (int i = 0; i < _given; i++)
            {
                if (rankValue == _thresholds[i])
                {
                    return i;
                }
            }
            return GradeCount - 1;
        }

        /// <summary>评分值的档(0 = F……8 = SSS),即 <c>8 − <see cref="FrameOf"/></c>。</summary>
        public int GradeOf(int rankValue)
        {
            return GradeCount - 1 - FrameOf(rankValue);
        }

        private static readonly string[] Letters = { "F", "E", "D", "C", "B", "A", "S", "SS", "SSS" };

        /// <summary>档(0 到 8)的字母;越界返回 "?"。</summary>
        public static string LetterOf(int grade)
        {
            return (uint)grade < (uint)Letters.Length ? Letters[grade] : "?";
        }

        /// <summary>
        /// 读 etc/ranksysteminfo.etc 的四个块:<c>[dungeon clear time average]</c>(每条七个整数:副本编号、队伍人数、五个难度的平均用时)、
        /// <c>[dungeon clear time reduction bonus]</c>(每条两个整数:门槛、分数)、<c>[rank grade]</c>(门槛,最多取前 8 个);
        /// 块里最后不满一条的整数丢掉(服务端读不全一条就停)。<paramref name="lines"/> 为 null 返回 <see cref="Empty"/>。
        /// </summary>
        public static RankSystem Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return Empty;
            }
            List<int> grades = null;
            var steps = new List<TimeBonusStep>();
            var records = new List<(int, int, int[])>();
            bool sawSteps = false, sawAverages = false;
            foreach (ScriptLine line in lines)
            {
                if (!sawAverages && line.TagIs("[dungeon clear time average]"))
                {
                    sawAverages = true;
                    List<int> n = line.Ints();
                    for (int i = 0; i + RecordInts <= n.Count; i += RecordInts)
                    {
                        var slots = new int[AverageSlots];
                        for (int k = 0; k < AverageSlots; k++)
                        {
                            slots[k] = n[i + 2 + k];
                        }
                        records.Add((n[i], n[i + 1], slots));
                    }
                }
                else if (!sawSteps && line.TagIs("[dungeon clear time reduction bonus]"))
                {
                    sawSteps = true;
                    List<int> n = line.Ints();
                    for (int i = 0; i + 2 <= n.Count; i += 2)
                    {
                        steps.Add(new TimeBonusStep(n[i], n[i + 1]));
                    }
                }
                else if (grades == null && line.TagIs("[rank grade]"))
                {
                    grades = line.Ints();
                }
            }
            return new RankSystem(grades, steps, records);
        }
    }
}
