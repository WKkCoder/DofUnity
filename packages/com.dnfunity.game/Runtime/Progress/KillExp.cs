using System;
using System.Numerics;

// 证据:E-111(服务端 CParty::kill_monster、getMonsterTotalExp、CDataManager::BaseExpPenalty、CParty::MemberPenaltyRate 的逐行读法;
// 运算顺序与舍入按反汇编的 x87 指令序列复现:中间结果留在 80 位寄存器,落地用截断)。
namespace Dnf.Game
{
    /// <summary>
    /// 击杀一只怪物给的经验(证据 E-111)。服务端的链条:
    /// <list type="number">
    /// <item>基础经验 <c>mobExp</c> = 怪物等级在 <see cref="MobRewardTable"/> 里的值(<c>CBattle_Field::kill_monster</c> 取的是怪物自己的等级);</item>
    /// <item>怪物总经验 = 截断( (((mobExp / 2) × 队伍倍率) × 难度倍率 × 副本经验权重) × 种类倍率 ),种类倍率在"有名怪物"时再乘 3;</item>
    /// <item>每个队员 = 截断( ((1 × 等级差倍率) × 总经验) / 队员数 ),1 是 <c>MemberPenaltyRate</c>(恒返回 1);</item>
    /// <item>之后服务端还会叠加各种活动与加成(<c>sumGainedEachExp</c>)、帮助惩罚,这些与单机无关,没做。</item>
    /// </list>
    /// 队伍倍率、难度倍率、种类倍率来自 <see cref="ExpRates"/>;副本经验权重是副本定义 .dgn 的 [experience increasing point]。
    /// 另有一个分支(副本编号 100,以及某两个标志位被置上时)改用"队员自己等级的基础经验、不算等级差";这里不实现——
    /// 它们是特殊副本与 GM 设定,与普通副本无关。
    /// 两次取整都是 64 位的 FISTP,结果存进 32 位变量(只取低 32 位),所以返回无符号 32 位。
    /// </summary>
    public static class KillExp
    {
        /// <summary>"有名怪物"的种类倍率再乘的倍数(服务端全局常量)。</summary>
        public const float NamedMultiplier = 3.0f;

        /// <summary>基础经验的除数(服务端全局常量)。</summary>
        public const float MobExpDivisor = 2.0f;

        /// <summary>
        /// 等级差倍率(CDataManager::BaseExpPenalty):差 = 怪物等级 - 主角等级。
        /// 差 ≤ -7 → 0.05;-6 → 0.2;-5 → 0.5;-4 → 0.75;-3 到 0 → 1;+1 到 +3 → 1.12;+4、+5 → 1;+6 → 0.75;+7 → 0.7;+8 → 0.6;+9 → 0.5;≥ +10 → 0.05。
        /// (单精度常量,与反汇编里的立即数逐位一致。)
        /// </summary>
        public static float LevelGapRate(int heroLevel, int monsterLevel)
        {
            int gap = monsterLevel - heroLevel;
            if (gap < -6)
            {
                return 0.05f;
            }
            switch (gap)
            {
                case -6:
                    return 0.2f;
                case -5:
                    return 0.5f;
                case -4:
                    return 0.75f;
                case -3:
                case -2:
                case -1:
                case 0:
                    return 1f;
                case 1:
                case 2:
                case 3:
                    return 1.12f;
                case 4:
                case 5:
                    return 1f;
                case 6:
                    return 0.75f;
                case 7:
                    return 0.7f;
                case 8:
                    return 0.6f;
                case 9:
                    return 0.5f;
                default:
                    return 0.05f;
            }
        }

        /// <summary>
        /// 怪物总经验(CParty::getMonsterTotalExp)。<paramref name="role"/> 是怪物种类 0 到 3(普通、小精英、大精英、首领;大于 3 时种类倍率取 1),
        /// <paramref name="difficulty"/> 是副本难度 0 到 4,<paramref name="expWeight"/> 是副本经验权重,<paramref name="partyRate"/> 是队伍倍率
        /// (<see cref="ExpRates.PartyRate"/>),<paramref name="named"/> 是否有名怪物。全部在 x87 扩展精度里按服务端的顺序乘,最后截断。
        /// </summary>
        public static uint MonsterTotalExp(uint mobExp, int role, int difficulty, float expWeight, float partyRate, bool named, ExpRates rates)
        {
            ExpRates r = rates ?? ExpRates.Neutral;
            float kind = r.KindRate(role);
            if (named)
            {
                kind = NamedMultiplier * kind;   // FMULP 后 FSTP 落成单精度
            }
            // FILD qword(mobExp) / 2.0 → × 队伍倍率 → × 难度倍率 → × 经验权重 → × 种类倍率 → FISTP qword(截断)
            X87Value x = X87.Div(new X87Value(new BigInteger(mobExp), 0), X87.FromSingle(MobExpDivisor));
            x = X87.Mul(x, X87.FromSingle(partyRate));
            x = X87.Mul(x, X87.FromSingle(r.DifficultyRate(difficulty)));
            x = X87.Mul(x, X87.FromSingle(expWeight));
            x = X87.Mul(x, X87.FromSingle(kind));
            return unchecked((uint)X87.TruncateToInt64(x));
        }

        /// <summary>
        /// 每个队员分到的经验:截断( ((1 × <paramref name="gapRate"/>) × <paramref name="total"/>) / <paramref name="members"/> )。
        /// 队员数小于 1 当 1(服务端队里至少有击杀者本人)。
        /// </summary>
        public static uint MemberExp(uint total, float gapRate, int members)
        {
            if (members < 1)
            {
                members = 1;
            }
            X87Value x = X87.Mul(X87.FromInt(1), X87.FromSingle(gapRate));
            x = X87.Mul(x, new X87Value(new BigInteger(total), 0));
            x = X87.Div(x, X87.FromInt(members));
            return unchecked((uint)X87.TruncateToInt64(x));
        }

        /// <summary>
        /// 一只怪物被击杀时,主角这一份经验(单人队伍,<paramref name="members"/> = 1;普通副本的分支):
        /// 基础经验取怪物等级,总经验按种类、难度与副本权重算,再乘等级差倍率。基础经验为 0(没有表或等级越界)时是 0。
        /// 结果超过 int.MaxValue 时截到 int.MaxValue(真实数据里到不了)。
        /// </summary>
        public static int ForKill(MobRewardTable rewards, ExpRates rates, int heroLevel, int monsterLevel, int role, bool named, int difficulty, float expWeight,
            int members = 1)
        {
            if (rewards == null)
            {
                return 0;
            }
            uint mobExp = rewards.BaseExp(monsterLevel);
            float partyRate = (rates ?? ExpRates.Neutral).PartyRate(members);
            uint total = MonsterTotalExp(mobExp, role, difficulty, expWeight, partyRate, named, rates);
            uint member = MemberExp(total, LevelGapRate(heroLevel, monsterLevel), members);
            return (int)Math.Min(member, (uint)int.MaxValue);
        }
    }
}
