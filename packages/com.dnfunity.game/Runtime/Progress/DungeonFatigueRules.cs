using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:r15-growth 对服务端 df_game_r 的静态读码(只读,观察/推断/未知分开):
// 观察 ① 进副本扣疲劳在 CParty::dungeon_start 0x085a0954(对每个队员):不扣的情形 = 队伍模式字段(+0xcd8)为 1、副本的 +0x87a 为 1("村庄袭击复仇副本")、
//        +0x89f 非 0("血之副本")、副本编号为一个固定的特殊编号(汇编里的立即数)、或 CDungeon::isNoFatigueDungeon 0x0822b4de(字节 +0x6c8);
//        其余:CDungeon::isTournamentDungeon 0x0836520a(字节 +0x8a8 恰为 1)的副本扣 getTournamentRoundFatigue 0x08365238(+0x8a4,为 0 时取 1),
//        别的副本 FatigueUp(0),也就是 1 点。
// 观察 ② 副本里每换一次房间再扣:CParty::changePartyMemberInfo 0x085a8d1e 调 canFatigueUp 0x085a8c88(参数不看;队伍模式为 1 不扣、没有副本不扣,
//        否则看副本的 +0x89f 为 0、+0x87a 不为 1、不是免疲劳副本)→ FatigueUp(0),1 点;走回已经去过的房间照样扣。
// 观察 ③ 进副本的门槛在 DisPatcher_SelectDungeon::process 0x081c8102:队伍模式为 1、所选区域有死亡之塔(CWorldMap::hasDeathTower)、蓝色弹珠活动、
//        isNoFatigueDungeon 四种情形不查疲劳;其余调 CParty::CheckMemberFatigue,有队员已耗 ≥ 上限就回错误码 0x16 且不开局。注意门槛只认"免疲劳副本"标志,
//        血之副本之类"不扣"的副本在疲劳用完时照样被门槛挡下(服务端就是这样读的)。
// 观察 ④ 副本脚本标签 → 服务端字段(ImportDungeonScript 0x088e5faa 与 CDungeon::set_dungeon 0x0834acc4 的拷贝语句):[no fatigue] → +0x6c8、
//        [tournament round fatigue] → +0x8a4、[tournament dungeon] → +0x8a8、[village attack revenge dungeon] → +0x87a/+0x87b、[blood dungeon] → +0x89f。
//        [no fatigue] 没有值,出现就是标志(本机全库 162 处,值都为空)。
// 未知:[fatigue]、[fatigue result]、[phase fatigue] 三个标签服务端只读进脚本结构,没有找到用它们的地方(本机数据里 [fatigue] 有 5 处,值都是同一个数),
//        所以不让它们影响扣除;那个固定的特殊副本编号是真实编号,不写进代码,本机数据里有没有这个编号待查。
namespace Dnf.Game
{
    /// <summary>
    /// 一座副本的疲劳规则(副本定义 .dgn 里的几个标签,观察 ④):决定进副本扣几点、换房间扣几点、门槛查不查。
    /// 数值是原版数据,不入库;这里只存"有没有这些标签"与它们的值。
    /// </summary>
    public sealed class DungeonFatigueRules
    {
        /// <summary>没写任何相关标签:扣 1 点、每换房间扣 1 点、门槛要查。</summary>
        public static DungeonFatigueRules Default { get; } = new DungeonFatigueRules(false, 0, false, false, 0);

        public DungeonFatigueRules(bool noFatigue, int bloodKind, bool revenge, bool tournament, int tournamentRoundFatigue)
        {
            NoFatigue = noFatigue;
            BloodKind = bloodKind;
            Revenge = revenge;
            Tournament = tournament;
            TournamentRoundFatigue = tournamentRoundFatigue;
        }

        /// <summary><c>[no fatigue]</c> 标志:不扣、门槛也不查。</summary>
        public bool NoFatigue { get; }

        /// <summary><c>[blood dungeon]</c> 的值(没写是 0);非 0 就不扣。</summary>
        public int BloodKind { get; }

        /// <summary><c>[village attack revenge dungeon]</c> 的第一个值恰为 1:不扣。</summary>
        public bool Revenge { get; }

        /// <summary><c>[tournament dungeon]</c> 的值恰为 1(<c>isTournamentDungeon</c>):进副本扣的不是 1 点而是 <see cref="TournamentRoundFatigue"/>。</summary>
        public bool Tournament { get; }

        /// <summary><c>[tournament round fatigue]</c> 的值(没写是 0)。</summary>
        public int TournamentRoundFatigue { get; }

        /// <summary>"不扣"的副本:免疲劳标志、血之副本、复仇副本(观察 ① ②)。</summary>
        public bool ChargeExempt => NoFatigue || BloodKind != 0 || Revenge;

        /// <summary>门槛不查疲劳:只认免疲劳标志(观察 ③)。</summary>
        public bool GateExempt => NoFatigue;

        /// <summary>进副本扣几点(<c>dungeon_start</c>):不扣的副本 0;锦标赛副本是 <see cref="TournamentRoundFatigue"/>(为 0 取 1);其余 1。</summary>
        public int StartCost
        {
            get
            {
                if (ChargeExempt)
                {
                    return 0;
                }
                if (Tournament)
                {
                    return TournamentRoundFatigue == 0 ? 1 : TournamentRoundFatigue;
                }
                return 1;
            }
        }

        /// <summary>换一次房间扣几点(<c>canFatigueUp</c>):不扣的副本 0,其余 1(含锦标赛副本)。</summary>
        public int MoveCost => ChargeExempt ? 0 : 1;

        /// <summary>从副本定义的标签行里读规则;<paramref name="script"/> 为 null 或没有这些标签时是 <see cref="Default"/>。</summary>
        public static DungeonFatigueRules Parse(ScriptLine[] script)
        {
            if (script == null)
            {
                return Default;
            }
            bool noFatigue = false;
            int blood = 0;
            bool revenge = false;
            bool tournament = false;
            int roundFatigue = 0;
            foreach (ScriptLine line in script)
            {
                if (line.TagIs("[no fatigue]"))
                {
                    noFatigue = true;
                }
                else if (line.TagIs("[blood dungeon]"))
                {
                    blood = First(line, blood);
                }
                else if (line.TagIs("[village attack revenge dungeon]"))
                {
                    revenge = First(line, 0) == 1;
                }
                else if (line.TagIs("[tournament dungeon]"))
                {
                    tournament = First(line, 0) == 1;
                }
                else if (line.TagIs("[tournament round fatigue]"))
                {
                    roundFatigue = Math.Max(0, First(line, 0));
                }
            }
            return new DungeonFatigueRules(noFatigue, blood, revenge, tournament, roundFatigue);
        }

        private static int First(ScriptLine line, int fallback)
        {
            List<int> n = line.Ints();
            return n.Count >= 1 ? n[0] : fallback;
        }
    }
}
