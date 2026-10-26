using System;
using Dnf.Game;

// 证据:E-124(评分与通关经验的服务端算法,见 Dnf.Game 的 RankSystem 与 ClearReward);基础分是客户端算的,公式在受保护的代码段里没有恢复,这里是估计。
namespace Dnf.Play
{
    /// <summary>一局副本的战绩,评分与通关经验的输入(单位:毫秒与次数)。</summary>
    public readonly struct RunStats
    {
        public RunStats(int clearMs, int kills, int comboKills, int maxCombo, int hitsTaken, int revivesUsed)
        {
            ClearMs = clearMs;
            Kills = kills;
            ComboKills = comboKills;
            MaxCombo = maxCombo;
            HitsTaken = hitsTaken;
            RevivesUsed = revivesUsed;
        }

        /// <summary>通关用时(毫秒):从进副本到首领清空的副本时钟,不含菜单里停着的时间。</summary>
        public int ClearMs { get; }

        /// <summary>整局击杀的怪物数。</summary>
        public int Kills { get; }

        /// <summary>其中在连击里(连击数已显示)打死的数。</summary>
        public int ComboKills { get; }

        /// <summary>最高连击数。</summary>
        public int MaxCombo { get; }

        /// <summary>主角被怪物打中的次数。</summary>
        public int HitsTaken { get; }

        /// <summary>用掉的复活币数。</summary>
        public int RevivesUsed { get; }
    }

    /// <summary>
    /// 评分里"客户端基础分"的<b>估计</b>(不是原版)。服务端的评分点 = 用时分 + 客户端发来的基础分(一个有符号字节),
    /// 基础分由客户端的结果窗口算出来再上报;算它的代码在客户端受保护的节里,没有读,公式没有恢复(证据 E-124)。
    /// 这里用一个简单的、可以替换的算法顶上:满分 100,每被打中一次扣 <see cref="HitPenalty"/>(最多扣 <see cref="HitPenaltyCap"/>),
    /// 每用一枚复活币扣 <see cref="RevivePenalty"/>(最多扣 <see cref="RevivePenaltyCap"/>),
    /// 不在连击里打死的怪物按比例最多扣 <see cref="ComboPenaltyMax"/>。系数是随手定的,只求"打得干净评分高、被打得多评分低";
    /// 评分门槛读用户自己的 etc/ranksysteminfo.etc,这些系数与它们没有对应关系。
    /// </summary>
    public static class ScoreEstimate
    {
        public const int Full = 100;
        public const int HitPenalty = 2;
        public const int HitPenaltyCap = 40;
        public const int RevivePenalty = 15;
        public const int RevivePenaltyCap = 45;
        public const int ComboPenaltyMax = 10;

        public static int BaseScore(RunStats stats)
        {
            int score = Full;
            score -= Math.Min(HitPenaltyCap, (int)Math.Min(int.MaxValue, (long)Math.Max(0, stats.HitsTaken) * HitPenalty));
            score -= Math.Min(RevivePenaltyCap, (int)Math.Min(int.MaxValue, (long)Math.Max(0, stats.RevivesUsed) * RevivePenalty));
            if (stats.Kills > 0)
            {
                int comboKills = Math.Max(0, Math.Min(stats.ComboKills, stats.Kills));
                score -= (int)((long)(stats.Kills - comboKills) * ComboPenaltyMax / stats.Kills);
            }
            return Math.Max(0, Math.Min(Full, score));
        }
    }

    /// <summary>
    /// 副本通关的结算结果(M32,证据 E-124):评分(用时分、基础分估计、评分点、评分值、字母)与通关经验(总数、基础、评分加成)。
    /// 只用基本类型对外(宿主层不引用 Dnf.Game)。<see cref="Compute"/> 只算不发;发经验由 <see cref="DungeonSession"/> 在首领清空时做,再用 <see cref="MarkGranted"/> 记下。
    /// </summary>
    public sealed class DungeonResult
    {
        private DungeonResult()
        {
        }

        public RunStats Stats { get; private set; }

        /// <summary>副本编号(dungeon/dungeon.lst 的号);不在列表里是 −1(这时没有平均用时、用时分为 0)。</summary>
        public int DungeonId { get; private set; }

        /// <summary>副本的标准等级(.dgn 的 [basis level]),通关经验按它取基础经验与等级差。</summary>
        public int StandardLevel { get; private set; }

        public int Difficulty { get; private set; }

        /// <summary>开局队员数(单人为 1)。</summary>
        public int Members { get; private set; }

        /// <summary>有评分数据(etc/ranksysteminfo.etc 里读到了评分门槛);没有时不出评分,字母恒为 F。</summary>
        public bool HasRank { get; private set; }

        /// <summary>这个副本、这个人数、这个难度的平均通关用时(毫秒);没有记录是 0。</summary>
        public int AverageMs { get; private set; }

        public int TimeBonus { get; private set; }

        /// <summary>客户端基础分的估计(<see cref="ScoreEstimate"/>,不是原版)。</summary>
        public int BaseScore { get; private set; }

        public int RankPoint { get; private set; }

        /// <summary>评分值(服务端发给客户端的那个数:所在门槛本身,不够档为 0)。</summary>
        public int RankValue { get; private set; }

        /// <summary>评分档(0 = F……8 = SSS)。</summary>
        public int Grade { get; private set; }

        /// <summary>评分字母图 Rank_Letter.img 的帧(0 = SSS……8 = F)。</summary>
        public int Frame { get; private set; }

        public string Letter { get; private set; }

        /// <summary>评分档位(4 = 最高……0,−1 = 没有加成)。</summary>
        public int BonusIndex { get; private set; }

        /// <summary>整队的通关总经验(还没按等级差与人数分)。</summary>
        public long ExpDungeonTotal { get; private set; }

        /// <summary>主角分到的基础通关经验。</summary>
        public long ExpBase { get; private set; }

        /// <summary>评分加成的那一份。</summary>
        public long ExpRankBonus { get; private set; }

        /// <summary>应给的通关经验(基础加评分加成)。</summary>
        public long ExpTotal => ExpBase + ExpRankBonus;

        /// <summary>通关经验是不是已经发给了主角(没有等级进度时不发)。</summary>
        public bool Granted { get; private set; }

        /// <summary>发经验之前、之后的主角等级(没发时都是 0)。</summary>
        public int LevelBefore { get; private set; }

        public int LevelAfter { get; private set; }

        public int LevelsGained => LevelAfter > LevelBefore ? LevelAfter - LevelBefore : 0;

        /// <summary>
        /// 按服务端的次序算评分与通关经验(不发经验)。<paramref name="rank"/> 为 null 当没有评分数据;<paramref name="progress"/> 为 null 时经验全是 0。
        /// <paramref name="expWeight"/> 是副本的经验权重(.dgn 的 [experience increasing point])。
        /// </summary>
        public static DungeonResult Compute(RunStats stats, RankSystem rank, HeroProgress progress, int dungeonId, int standardLevel, int difficulty, float expWeight,
            int members = 1)
        {
            RankSystem r = rank ?? RankSystem.Empty;
            int average = dungeonId < 0 ? 0 : r.AverageTime(dungeonId, members, difficulty);
            int timeBonus = r.TimeBonus(average, stats.ClearMs);
            int baseScore = ScoreEstimate.BaseScore(stats);
            int point = RankSystem.RankPoint(timeBonus, baseScore);
            int value = r.RankValue(point);
            var result = new DungeonResult
            {
                Stats = stats,
                DungeonId = dungeonId,
                StandardLevel = standardLevel,
                Difficulty = difficulty,
                Members = members,
                HasRank = r.ThresholdsGiven > 0,
                AverageMs = average,
                TimeBonus = timeBonus,
                BaseScore = baseScore,
                RankPoint = point,
                RankValue = value,
                Frame = r.FrameOf(value),
                Grade = r.GradeOf(value),
                BonusIndex = r.BonusIndex(value),
            };
            result.Letter = RankSystem.LetterOf(result.Grade);
            if (progress != null)
            {
                ClearExp exp = ClearReward.Compute(progress.Rewards, progress.Rates, standardLevel, progress.Level, stats.Kills, difficulty, expWeight, result.BonusIndex, members);
                result.ExpDungeonTotal = exp.DungeonTotal;
                result.ExpBase = exp.Base;
                result.ExpRankBonus = exp.RankBonus;
            }
            return result;
        }

        /// <summary>记下通关经验已发给主角:发之前与之后的等级。</summary>
        internal void MarkGranted(int levelBefore, int levelAfter)
        {
            Granted = true;
            LevelBefore = levelBefore;
            LevelAfter = levelAfter;
        }
    }
}
