using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-115(服务端 ImportDungeonScript 0x088e5faa 读这几个标签与默认值 STDungeonScript::Clear 0x0836abc6;
// 检查顺序 DisPatcher_UseCoin::usedCoinDungeonCheck 0x081cab3a、CParty::checkLimitUsedCoinCondition 0x085be56c、
// CParty::dungeon_start 0x085a0954 里"队伍上限 = 人数 × 每人上限")。
namespace Dnf.Game
{
    /// <summary>用复活币检查的结果(服务端 <c>usedCoinDungeonCheck</c> 的返回:0 可以、0x11 没有币、0x16 到上限)。</summary>
    public enum CoinVerdict
    {
        /// <summary>免费复活:这座副本这个难度的 [coin info] 标明"不用币"(<c>checkFreeRevivalCondition</c> 0x08365298):不扣币、不计次数、不受上限约束。</summary>
        Free = 0,

        /// <summary>可以用:扣一个币,这次副本的已用次数加一。</summary>
        Pay = 1,

        /// <summary>没有币(服务端错误码 0x11)。</summary>
        NoCoin = 2,

        /// <summary>这次副本里用币的次数到上限了(服务端错误码 0x16)。</summary>
        LimitReached = 3,
    }

    /// <summary>
    /// 一座副本的复活币规则(副本定义 .dgn 的三个标签,证据 E-115):
    /// <list type="bullet">
    /// <item><c>[coin limit]</c>:这次副本最多用几次币;没写 = −1 = 不限(服务端读脚本前先清成 −1);0 = 一次也不能用。</item>
    /// <item><c>[party member coin limit]</c>:每个队员最多几次;队伍上限 = 人数 × 它(<c>CParty::dungeon_start</c>),0(没写)= 没有这一条限制。</item>
    /// <item><c>[coin info]</c>:按难度分的一串三元组 <c>"[easy]" 上限 标志 "[medium]" 上限 标志 …</c>(难度名依次对应难度 0 到 3):
    /// 上限 −1 = 不限;标志为 1 才要币,不是 1 就是免费复活("保障")。</item>
    /// </list>
    /// 检查顺序照服务端:免费 → 没有币 → 队伍上限 → 难度上限 → 副本上限(每一条都是"上限 ≤ 已用次数"就挡下)。
    /// 没有实现:服务端的"活动期间不限制"(<c>checkUnLimitUsedCoinCondition</c>,要一个服务器活动开着)、<c>[hell coin limit]</c>(地狱派对)、<c>[character coin limit]</c>(读到了、没找到用处)。
    /// 数值是原版数据,不入库。
    /// </summary>
    public sealed class DungeonCoinRules
    {
        /// <summary>[coin info] 里难度名的写法,下标就是难度编号(<c>ImportDungeonScript</c> 的名字表,0x088f03f2 附近:easy 0、medium 1、hard 2、ultimate 3)。</summary>
        public static readonly string[] DifficultyNames = { "[easy]", "[medium]", "[hard]", "[ultimate]" };

        private readonly Dictionary<int, (int Limit, bool Pay)> _difficulties;

        /// <summary>没写任何标签:币不限次数、也不免费。</summary>
        public static DungeonCoinRules Default => new DungeonCoinRules(-1, 0, null);

        public DungeonCoinRules(int coinLimit, int partyMemberCoinLimit, IDictionary<int, (int Limit, bool Pay)> difficulties)
        {
            CoinLimit = coinLimit;
            PartyMemberCoinLimit = partyMemberCoinLimit;
            _difficulties = difficulties == null ? new Dictionary<int, (int, bool)>() : new Dictionary<int, (int, bool)>(difficulties);
        }

        /// <summary><c>[coin limit]</c>:−1 不限,否则最多这么多次。</summary>
        public int CoinLimit { get; }

        /// <summary><c>[party member coin limit]</c>:每个队员的次数上限;0 = 没有这条限制。</summary>
        public int PartyMemberCoinLimit { get; }

        /// <summary>写了几个难度的 [coin info]。</summary>
        public int DifficultyEntries => _difficulties.Count;

        /// <summary>这个难度写了 [coin info]:返回它的次数上限(−1 不限)与"要币"标志。</summary>
        public bool TryGetDifficulty(int difficulty, out int limit, out bool pay)
        {
            if (_difficulties.TryGetValue(difficulty, out (int Limit, bool Pay) entry))
            {
                limit = entry.Limit;
                pay = entry.Pay;
                return true;
            }
            limit = -1;
            pay = true;
            return false;
        }

        /// <summary>这个难度复活免费:写了 [coin info] 而且"要币"标志不是 1(<c>checkFreeRevivalCondition</c>)。</summary>
        public bool IsFree(int difficulty)
        {
            return TryGetDifficulty(difficulty, out _, out bool pay) && !pay;
        }

        /// <summary>
        /// 此刻用币行不行(服务端 <c>usedCoinDungeonCheck</c> 的顺序):
        /// 免费 → <see cref="CoinVerdict.Free"/>;没有币 → <see cref="CoinVerdict.NoCoin"/>;
        /// 队伍上限(<paramref name="members"/> × <see cref="PartyMemberCoinLimit"/>,不为 0 才查)、难度上限、副本上限里任何一条"上限 ≤ <paramref name="used"/>"
        /// → <see cref="CoinVerdict.LimitReached"/>;否则 <see cref="CoinVerdict.Pay"/>。
        /// </summary>
        /// <param name="difficulty">副本难度(0 = 普通)。</param>
        /// <param name="used">这次副本里已经用掉的币数(免费复活不算)。</param>
        /// <param name="members">队伍人数(单人 1)。</param>
        /// <param name="haveCoin">背包里有可用的币。</param>
        public CoinVerdict Check(int difficulty, int used, int members, bool haveCoin)
        {
            if (IsFree(difficulty))
            {
                return CoinVerdict.Free;
            }
            if (!haveCoin)
            {
                return CoinVerdict.NoCoin;
            }
            int partyLimit = Math.Max(1, members) * PartyMemberCoinLimit;
            if (partyLimit != 0 && partyLimit <= used)
            {
                return CoinVerdict.LimitReached;
            }
            if (TryGetDifficulty(difficulty, out int diffLimit, out _) && diffLimit != -1 && diffLimit <= used)
            {
                return CoinVerdict.LimitReached;
            }
            if (CoinLimit != -1 && CoinLimit <= used)
            {
                return CoinVerdict.LimitReached;
            }
            return CoinVerdict.Pay;
        }

        /// <summary>从副本定义的标签行里读规则;<paramref name="doc"/> 为 null 或没有这些标签时是 <see cref="Default"/>。</summary>
        public static DungeonCoinRules Parse(ScriptLine[] doc)
        {
            if (doc == null)
            {
                return Default;
            }
            int coinLimit = -1;
            int memberLimit = 0;
            var difficulties = new Dictionary<int, (int, bool)>();
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[coin limit]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 1)
                    {
                        coinLimit = n[0];
                    }
                }
                else if (line.TagIs("[party member coin limit]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 1)
                    {
                        memberLimit = n[0];
                    }
                }
                else if (line.TagIs("[coin info]"))
                {
                    ReadCoinInfo(line, difficulties);
                }
            }
            return new DungeonCoinRules(coinLimit, memberLimit, difficulties);
        }

        /// <summary>[coin info]:难度名(字符串)后面跟两个整数(上限、标志);同一个难度写两次以先写的为准(服务端往 std::map 里 insert,不覆盖已有的键)。</summary>
        private static void ReadCoinInfo(ScriptLine line, Dictionary<int, (int, bool)> into)
        {
            int current = -1;
            var numbers = new List<int>(2);
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.String)
                {
                    current = Array.FindIndex(DifficultyNames, name => string.Equals(name, v.Text, StringComparison.OrdinalIgnoreCase));
                    numbers.Clear();
                    continue;
                }
                if (current < 0 || !v.IsNumber)
                {
                    continue;
                }
                numbers.Add(v.Int);
                if (numbers.Count == 2)
                {
                    if (!into.ContainsKey(current))
                    {
                        into[current] = (numbers[0], numbers[1] != 0);
                    }
                    current = -1;
                    numbers.Clear();
                }
            }
        }
    }
}
