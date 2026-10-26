using System;
using System.Numerics;

// 证据:E-124(服务端 CParty::clear_reward 0x085ae0d4 里通关经验一段:0x085af8dd 到 0x085af9d6 是队伍总经验,0x085afc9a 到 0x085afd33 是每位队员的一份;
// set_clear_reward_exp_parameter 0x085adb00 与 getClearRewardBonusExp 0x085ad278 给评分加成;运算顺序与落地精度按反汇编的 x87 指令序列复现)。
namespace Dnf.Game
{
    /// <summary>一次通关经验的结算结果(单位:经验点)。</summary>
    public readonly struct ClearExp
    {
        public ClearExp(uint dungeonTotal, uint baseExp, uint rankBonus)
        {
            DungeonTotal = dungeonTotal;
            Base = baseExp;
            RankBonus = rankBonus;
        }

        /// <summary>整队的通关总经验(按击杀数、难度、副本权重算,还没按等级差与人数分)。</summary>
        public uint DungeonTotal { get; }

        /// <summary>这位队员分到的基础通关经验(乘了等级差倍率、除了队员数)。</summary>
        public uint Base { get; }

        /// <summary>评分加成的那一份(<see cref="Base"/> × 评分档位的比例)。</summary>
        public uint RankBonus { get; }

        /// <summary>实际给的经验:基础加评分加成(单人没有头像、宠物、活动等其余加成,服务端那几项在单机里都是 0)。</summary>
        public long Total => (long)Base + RankBonus;
    }

    /// <summary>
    /// 通关经验(证据 E-124)。服务端 <c>CParty::clear_reward</c> 在副本通关时一次性给的经验,与逐只击杀给的经验(<see cref="KillExp"/>)是两码事。
    /// 非"等级制"副本(<c>CDungeon</c> 偏移 0x89e 的标志为 0)的算法:
    /// <list type="number">
    /// <item>标准等级:副本的 <c>[basis level]</c>(<see cref="GoldDrop.StandardLevel"/>);"位面"类副本改用队伍的标准位面等级,这里不实现;</item>
    /// <item>基础经验 <c>mobExp</c> = <see cref="MobRewardTable"/> 里标准等级的值(与击杀同一张表,按标准等级取);</item>
    /// <item>队伍总经验 = 截断( 副本经验权重 × 单精度( 难度倍率 × 单精度( ((击杀数 × 单精度(mobExp)) / 2) × 队伍倍率 ) ) ),击杀数是整场副本里被杀的怪物总数;
    /// 队伍倍率取 <see cref="ExpRates.PartyRate"/>(服务端再加一个活动加成,无活动时是 0);</item>
    /// <item>每位队员 = 截断( (1 × 单精度( 等级差倍率 × 单精度(总经验) )) / 队员数 ),等级差倍率是 <see cref="KillExp.LevelGapRate"/>(主角等级, 标准等级),1 是 <c>MemberPenaltyRate</c>;</item>
    /// <item>评分加成 = 截断( 基础经验 × 评分档位的比例 ),比例取 <see cref="ExpRates.RankBonusRate"/>;总共给 基础 + 评分加成
    /// (服务端还有"死亡队员惩罚":队伍多于一人时才有,单人恒为 0,没有实现)。</item>
    /// </list>
    /// 每一步中间值在 80 位寄存器里,"单精度"是 FSTP 落成 32 位浮点(舍入到最近),取整是 FISTP 64 位(控制字设成截断)后只取低 32 位,所以结果是无符号 32 位。
    /// 基础经验表在没有用户导出时是占位值(<see cref="MobRewardTable.IsPlaceholder"/>),这时通关经验也是占位量级,不是原版数据。
    /// </summary>
    public static class ClearReward
    {
        /// <summary>
        /// 整队的通关总经验(0x085af8dd 起的一段)。<paramref name="killed"/> 是整场副本击杀的怪物总数,<paramref name="members"/> 是开局的队员数
        /// (小于 1 时队伍倍率取 1),<paramref name="difficulty"/> 是副本难度(0 到 4,越界难度倍率取 1),<paramref name="expWeight"/> 是副本经验权重。
        /// </summary>
        public static uint DungeonTotal(MobRewardTable rewards, ExpRates rates, int standardLevel, int killed, int members, int difficulty, float expWeight)
        {
            ExpRates r = rates ?? ExpRates.Neutral;
            uint mobExp = rewards == null ? 0u : rewards.BaseExp(standardLevel);
            float partyRate = r.PartyRate(members);
            float mobSingle = X87.ToSingle(FromUInt(mobExp));                                   // FILD qword → FSTP float
            X87Value x = X87.Mul(X87.FromInt(killed), X87.FromSingle(mobSingle));              // FILD dword → FMUL float
            x = X87.Div(x, X87.FromSingle(KillExp.MobExpDivisor));                              // FLD float(2.0) → FDIVP
            x = X87.Mul(x, X87.FromSingle(partyRate));                                          // FMUL float
            float stage = X87.ToSingle(x);                                                      // FSTP float
            float byDifficulty = X87.ToSingle(X87.Mul(X87.FromSingle(r.DifficultyRate(difficulty)), X87.FromSingle(stage)));   // FLD、FLD、FMULP → FSTP float
            X87Value total = X87.Mul(X87.FromSingle(expWeight), X87.FromSingle(byDifficulty)); // call 返回 st0;FMUL float
            return unchecked((uint)X87.TruncateToInt64(total));                                 // FISTP qword(截断)→ 低 32 位
        }

        /// <summary>
        /// 每位队员分到的基础通关经验(0x085afc9a 起的一段):<paramref name="dungeonTotal"/> 是 <see cref="DungeonTotal"/>,
        /// 等级差用主角等级与标准等级。<paramref name="members"/> 小于 1 当 1。
        /// </summary>
        public static uint MemberBase(uint dungeonTotal, int heroLevel, int standardLevel, int members)
        {
            if (members < 1)
            {
                members = 1;
            }
            float totalSingle = X87.ToSingle(FromUInt(dungeonTotal));                           // FILD qword → FSTP float
            float gapRate = KillExp.LevelGapRate(heroLevel, standardLevel);
            float scaled = X87.ToSingle(X87.Mul(X87.FromSingle(gapRate), X87.FromSingle(totalSingle)));   // FLD float → FMULP → FSTP float
            X87Value x = X87.Mul(X87.FromInt(1), X87.FromSingle(scaled));                       // FILD dword(MemberPenaltyRate = 1) → FLD float → FMULP
            x = X87.Div(x, X87.FromInt(members));                                               // FILD dword(队员数) → FDIVP
            return unchecked((uint)X87.TruncateToInt64(x));                                     // FISTP qword(截断)→ 低 32 位
        }

        /// <summary>评分加成(0x085ad2f8 起):截断( 基础经验 × <paramref name="rate"/> ),取低 32 位。比例为 0 时是 0。</summary>
        public static uint RankBonus(uint baseExp, float rate)
        {
            X87Value x = X87.Mul(FromUInt(baseExp), X87.FromSingle(rate));                      // FILD qword → FLD float → FMULP
            return unchecked((uint)X87.TruncateToInt64(x));
        }

        /// <summary>
        /// 单人(<paramref name="members"/> = 1)的整条链:总经验 → 基础 → 评分加成。<paramref name="bonusIndex"/> 是评分档位(<see cref="RankSystem.BonusIndex"/>,−1 没有加成)。
        /// </summary>
        public static ClearExp Compute(MobRewardTable rewards, ExpRates rates, int standardLevel, int heroLevel, int killed, int difficulty, float expWeight,
            int bonusIndex, int members = 1)
        {
            ExpRates r = rates ?? ExpRates.Neutral;
            uint total = DungeonTotal(rewards, r, standardLevel, killed, members, difficulty, expWeight);
            uint baseExp = MemberBase(total, heroLevel, standardLevel, members);
            uint bonus = RankBonus(baseExp, r.RankBonusRate(bonusIndex));
            return new ClearExp(total, baseExp, bonus);
        }

        private static X87Value FromUInt(uint value)
        {
            return new X87Value(new BigInteger(value), 0);
        }
    }
}
