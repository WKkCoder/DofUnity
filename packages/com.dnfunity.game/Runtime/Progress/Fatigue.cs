using System;

// 证据:r15-growth 对服务端 df_game_r 的静态读码(只读;下面的地址都是这份二进制里的虚拟地址,观察/推断/未知分开写):
// 观察 ① 服务端存的是"已消耗"点数,不是剩余:CUser::CheckFatigue 0x08656500 的判断是 已耗 < 上限;CParty::CheckMemberFatigue 0x0859d5e8 逐个队员问它,
//        第一个不过的队员序号加一就是返回值。上限是每个角色自己的一个 16 位数(角色可写区 +0x35),由 CUser::RecoverFatigue 0x08657ada 置成
//        全局 DEFAULT_MAX_FATIGUE(0x09370f5c;本机读 ELF 数据节取值 156)——活动期间换成"活动倍率 × 默认 ÷ 100";整个函数里没有等级参与。
// 观察 ② 扣疲劳 CUser::FatigueUp 0x08655c60:参数 0 当 1;已耗加上它、封顶到上限(上限之后的"已用统计"另有一个不封顶的计数,不参与这里)。
// 观察 ③ 每日恢复有两条路:在线的由 TimerFatigueReset(RegistNextTimer 0x08633a04、dispatch_sig 0x08633750)到点调 GameWorld::ResetAllFatigue 0x086cc3ec
//        (对在线玩家 RecoverFatigue(0));离线的在登录时 Inter_LoadEtc::SetFatigue 0x084c311a 取角色记录里的"上次玩的时刻"(lastplaytick),
//        用 CheckDailyScheduleTime 0x0846c0a8 判断——上次时刻 ≤ 最近一个恢复边界 就恢复;边界 = 今天的"恢复时"整点,当前小时还没到就退一天(减 86400 秒)。
// 观察 ④ RecoverFatigue(0) = 已耗清零 + 上限回到默认 + 清"已用统计"。放弃副本不碰疲劳(CParty::giveup_game 0x085b2baa 里没有疲劳写入;
//        CUser::giveup_panalty 0x086786be 写的是另一个 0 到 100 的体力值 Stamina,台账 E-115 里"放弃把疲劳设成 10"的说法是读错了对象)。
// 推断:每日恢复的"时"是服务器配置项(CheckDailyScheduleTime 的 hour 来自全局环境 +0x37c,配置文件按红线不读),这里默认 6 时是按公开的游戏常识推断,不是读码结论,可由宿主注入。
// 未知:活动倍率、会员(premium)疲劳池、网吧加成、"无限疲劳"会员效果的数值来源(都是服务端活动与计费配置),本项目不做。
namespace Dnf.Game
{
    /// <summary>
    /// 疲劳的规则参数:上限与每日恢复的时刻。与等级无关(观察 ①)。构造后不可变,可以多个角色共用。
    /// </summary>
    public sealed class FatigueRules
    {
        /// <summary>服务端全局 DEFAULT_MAX_FATIGUE(0x09370f5c)的值。</summary>
        public const int DefaultMax = 156;

        /// <summary>每日恢复的整点(0 到 23)的默认值;服务器配置项没有读到,6 是推断。</summary>
        public const int DefaultResetHour = 6;

        /// <summary>默认规则(上限 <see cref="DefaultMax"/>、<see cref="DefaultResetHour"/> 时恢复)。</summary>
        public static FatigueRules Default { get; } = new FatigueRules();

        /// <summary>疲劳上限(点数,至少 1)。</summary>
        public int Max { get; }

        /// <summary>每日恢复的整点(0 到 23)。</summary>
        public int ResetHour { get; }

        public FatigueRules(int max = DefaultMax, int resetHour = DefaultResetHour)
        {
            if (max < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(max), "疲劳上限至少是 1");
            }
            if (resetHour < 0 || resetHour > 23)
            {
                throw new ArgumentOutOfRangeException(nameof(resetHour), "恢复整点要在 0 到 23 之间");
            }
            Max = max;
            ResetHour = resetHour;
        }

        /// <summary>
        /// 距 <paramref name="now"/> 最近的、不晚于它的恢复边界(CheckDailyScheduleTime 0x0846c0a8 的 boundary):
        /// 今天的 <see cref="ResetHour"/> 点整;当前的小时数还小于它就退到昨天的同一时刻。
        /// </summary>
        public DateTime LatestBoundary(DateTime now)
        {
            var today = new DateTime(now.Year, now.Month, now.Day, ResetHour, 0, 0, now.Kind);
            return now.Hour < ResetHour ? today.AddDays(-1) : today;
        }

        /// <summary>
        /// 该不该恢复(CheckDailyScheduleTime 的返回:上次玩的时刻 ≤ 最近的恢复边界,含相等);<paramref name="lastPlay"/> 为 null(从没记过,
        /// 服务端里是 0 时刻)一律该恢复。
        /// </summary>
        public bool DueForReset(DateTime? lastPlay, DateTime now)
        {
            return !lastPlay.HasValue || lastPlay.Value <= LatestBoundary(now);
        }
    }

    /// <summary>
    /// 一个角色的疲劳:已耗点数、上限、上次玩的时刻。时间不自己取——每次要用的地方把"现在"传进来(宿主给时钟,测试给固定时刻)。
    /// 判断进副本用 <see cref="CanEnter"/>(已耗 &lt; 上限,观察 ①),扣用 <see cref="Use"/>(观察 ②),每日恢复在 <see cref="Refresh"/>(观察 ③)。
    /// </summary>
    public sealed class FatigueState
    {
        public FatigueState(FatigueRules rules = null)
        {
            Rules = rules ?? FatigueRules.Default;
            Max = Rules.Max;
        }

        public FatigueRules Rules { get; }

        /// <summary>已消耗的点数(0 到 <see cref="Max"/>)。</summary>
        public int Consumed { get; private set; }

        /// <summary>上限(恢复时回到 <see cref="FatigueRules.Max"/>)。</summary>
        public int Max { get; private set; }

        /// <summary>上次玩的时刻(<see cref="Refresh"/> 记的;从没刷新过是 null)。</summary>
        public DateTime? LastPlay { get; private set; }

        /// <summary>已耗或上限变过的次数(扣与恢复只在点数真的变了才加;读档一律加一,读回来的值一样也算;记时刻不算)。HUD 用它判断要不要重画。</summary>
        public int Version { get; private set; }

        /// <summary>剩余点数(上限减已耗,不小于 0)。</summary>
        public int Remaining => Max > Consumed ? Max - Consumed : 0;

        /// <summary>能进副本:已耗小于上限(CUser::CheckFatigue 0x08656500)——剩 1 点还行、剩 0 点不行,与副本这一趟要扣几点无关。</summary>
        public bool CanEnter => Consumed < Max;

        /// <summary>
        /// 扣 <paramref name="amount"/> 点(CUser::FatigueUp 0x08655c60):已耗加上它、封顶到上限;返回实际多出来的点数(已经满了是 0)。
        /// <paramref name="amount"/> 小于 1 什么都不做(服务端把参数 0 当 1 是它的调用约定,这里调用方明说要几点)。
        /// </summary>
        public int Use(int amount)
        {
            if (amount < 1)
            {
                return 0;
            }
            int before = Consumed;
            long next = (long)Consumed + amount;
            Consumed = (int)Math.Min(next, Max);
            if (Consumed != before)
            {
                Version++;
            }
            return Consumed - before;
        }

        /// <summary>完全恢复(RecoverFatigue(0),观察 ④):已耗清零、上限回到规则里的上限。</summary>
        public void Recover()
        {
            if (Consumed != 0 || Max != Rules.Max)
            {
                Version++;
            }
            Consumed = 0;
            Max = Rules.Max;
        }

        /// <summary>
        /// 按"现在"刷新:到了恢复边界就完全恢复(<see cref="FatigueRules.DueForReset"/>),然后把"现在"记成上次玩的时刻
        /// (服务端是登出与存盘时记 lastplaytick;一个会一直开着的客户端每次要用疲劳前刷新一次,等于在线玩家被计时器恢复)。
        /// 返回这次有没有恢复。
        /// </summary>
        public bool Refresh(DateTime now)
        {
            bool due = Rules.DueForReset(LastPlay, now);
            if (due)
            {
                Recover();
            }
            LastPlay = now;
            return due;
        }

        /// <summary>
        /// 读档还原:已耗夹在 0 到上限,上限取规则里的(存档不存上限);<paramref name="lastPlay"/> 为 null 表示没记过(下次刷新当作到点)。
        /// </summary>
        public void Restore(int consumed, DateTime? lastPlay)
        {
            Max = Rules.Max;
            Consumed = Math.Max(0, Math.Min(consumed, Max));
            LastPlay = lastPlay;
            Version++;
        }
    }
}
