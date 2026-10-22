using System;
using Dnf.Game;

// 证据:E-115(服务端的死亡、复活与副本失败规则;客户端复活后的表现——生命百分比、无敌时长、倒计时从几开始显示——没有读到,下面标"推断"的都是占位)。
namespace Dnf.Play
{
    /// <summary>
    /// 复活币库存(占位:没有背包,先单独放着,以后并入背包里的物品)。副本之间、再次挑战之间共用同一个对象,所以用掉的币不会回来。
    /// </summary>
    public sealed class ReviveCoins
    {
        /// <summary>占位的初始库存(单机没有商店与背包,给够用的数;命令行 --coins 与播放器 -dnfcoins 可以改)。</summary>
        public const int DefaultStock = 5;

        public ReviveCoins(int count = DefaultStock)
        {
            Count = Math.Max(0, count);
        }

        public int Count { get; private set; }

        public bool Has => Count > 0;

        /// <summary>取走一个币;没有就返回 false。</summary>
        public bool TryTake()
        {
            if (Count <= 0)
            {
                return false;
            }
            Count--;
            return true;
        }

        public void Add(int amount)
        {
            Count = Math.Max(0, Count + amount);
        }
    }

    /// <summary>
    /// 主角死后的"复活窗口"与副本失败(证据 E-115,静态读码):
    /// <list type="bullet">
    /// <item>主角躺下(死亡通知,E-084)时队伍全灭,服务端开始 <see cref="CountdownMs"/> 的失败计时(<c>send_dungeon_fail_msg</c> 0x085b35f4:10 秒,队伍有特殊标志时 3 秒);
    /// 计时到了还全灭就让所有人出副本回城(<c>party_dungeon_fail</c> 0x085b3670,先判一次"是不是还全灭",所以计时里有人复活就等于取消)。</item>
    /// <item>窗口里按复活键(X):服务端按 <see cref="DungeonCoinRules.Check"/> 的顺序检查(免费 → 有没有币 → 三个上限),通过就扣币、已用次数加一、让人原地复活(<c>ReviveUser</c> 0x085b27c8、<c>set_charac_live</c> 0x085b29bc);
    /// 免费复活(这座副本这个难度的 [coin info] 标明不用币)不扣币、不计次数、不受上限约束。</item>
    /// <item>复活之后计时作废;已用次数按整座副本累计(开局清零,<c>CParty::dungeon_start</c> 0x085a0954)。</item>
    /// </list>
    /// 单人,队伍人数恒为 1。复活的表现(满血满蓝、短暂无敌)是推断,见 <see cref="InvincibleMs"/>。
    /// </summary>
    public sealed class HeroRevive
    {
        /// <summary>失败计时(毫秒):服务端 <c>send_dungeon_fail_msg</c> 的 10 秒(证据 E-115)。画面上从几开始数没有读到(推断:从 10 数到 0)。</summary>
        public const double CountdownMs = 10000.0;

        /// <summary>
        /// 复活之后的无敌时长(毫秒):<b>推断</b>——客户端复活处理里的血量百分比与无敌时长没有读到;
        /// 全库的等级与职业数据里没有这样的标签,这是个让主角站起来、不被围殴当场再死的占位。
        /// </summary>
        public const double InvincibleMs = 3000.0;

        private readonly DungeonCoinRules _rules;
        private readonly ReviveCoins _coins;
        private readonly int _difficulty;
        private readonly int _members;
        private double _remainingMs;
        private bool _open;

        /// <param name="rules">这座副本的复活币规则(没给就是没有任何限制、不免费)。</param>
        /// <param name="coins">库存;没给就一个币也没有(只有免费复活才能复活)。</param>
        /// <param name="difficulty">副本难度(0 = 普通),查 [coin info] 用。</param>
        /// <param name="members">队伍人数(单人 1),算队伍上限用。</param>
        public HeroRevive(DungeonCoinRules rules, ReviveCoins coins, int difficulty = 0, int members = 1)
        {
            _rules = rules ?? DungeonCoinRules.Default;
            _coins = coins;
            _difficulty = difficulty;
            _members = Math.Max(1, members);
        }

        /// <summary>复活窗口开着:主角躺下了、失败计时在走、还没复活也没失败。</summary>
        public bool Open => _open;

        /// <summary>失败计时走完了(主角没有复活):副本失败,外面该让流程回城。</summary>
        public bool Failed { get; private set; }

        /// <summary>失败计时还剩多少毫秒;窗口没开是 0。</summary>
        public double RemainingMs => _open ? _remainingMs : 0.0;

        /// <summary>画面上显示的秒数:剩余毫秒向上取整(10 到 1;窗口没开是 0)。</summary>
        public int Seconds => _open ? (int)Math.Ceiling(_remainingMs / 1000.0) : 0;

        /// <summary>这次副本里已经用掉的币数(免费复活不算;服务端 <c>GetUsedCoinCount</c>)。</summary>
        public int Used { get; private set; }

        /// <summary>从开局起主角死过几次(每次窗口打开算一次)。</summary>
        public int Deaths { get; private set; }

        /// <summary>从开局起复活了几次(含免费)。</summary>
        public int Revives { get; private set; }

        /// <summary>库存;没给库存是 null。</summary>
        public ReviveCoins Coins => _coins;

        public DungeonCoinRules Rules => _rules;

        /// <summary>此刻按复活键会怎样(<see cref="DungeonCoinRules.Check"/>)。</summary>
        public CoinVerdict Verdict => _rules.Check(_difficulty, Used, _members, _coins != null && _coins.Has);

        /// <summary>窗口开着、而且此刻按复活键能复活(免费或有币又没到上限)。</summary>
        public bool CanRevive => _open && (Verdict == CoinVerdict.Free || Verdict == CoinVerdict.Pay);

        /// <summary>主角躺下了:打开复活窗口、失败计时从 <see cref="CountdownMs"/> 开始。已经开着、或者已经失败时什么也不做。</summary>
        public void Begin()
        {
            if (_open || Failed)
            {
                return;
            }
            _open = true;
            _remainingMs = CountdownMs;
            Deaths++;
        }

        /// <summary>推进 <paramref name="deltaMs"/> 毫秒;计时走完就关窗、置 <see cref="Failed"/>。窗口没开时什么也不做。</summary>
        public void Tick(double deltaMs)
        {
            if (!_open || deltaMs <= 0)
            {
                return;
            }
            _remainingMs -= deltaMs;
            if (_remainingMs <= 0)
            {
                _remainingMs = 0;
                _open = false;
                Failed = true;
            }
        }

        /// <summary>
        /// 按复活键:窗口开着、检查通过就扣币(免费的不扣)、已用次数加一(免费的不加)、关窗并返回 true(外面让主角站起来);否则什么也不变,返回 false。
        /// </summary>
        public bool TryRevive()
        {
            if (!_open)
            {
                return false;
            }
            switch (Verdict)
            {
                case CoinVerdict.Free:
                    break;
                case CoinVerdict.Pay:
                    if (_coins == null || !_coins.TryTake())
                    {
                        return false;
                    }
                    Used++;
                    break;
                default:
                    return false;
            }
            _open = false;
            _remainingMs = 0;
            Revives++;
            return true;
        }
    }
}
