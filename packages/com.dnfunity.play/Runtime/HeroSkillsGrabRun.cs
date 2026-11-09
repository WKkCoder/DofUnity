namespace Dnf.Play
{
    /// <summary>
    /// 抓取类技能"已抓住"那一半(客户端状态 0x1a 的模式 1:每帧块 0x00cbf805、结束守卫 0x00cc8506;证据 E-346、E-366)一次施放的阶段机:
    /// 纯计时与判定,不碰主角、怪物与房间。目前没有调用方——抓取成立的触发(谁带着目标重新进入状态)与被抓者怎么跟着携带点走这两环还没读通,
    /// 所以只落这一块读通了的内核,不接线。
    /// 时间一律是"状态计时"(进入模式 1 起的毫秒数,客户端 +0x30a0),每帧给一次**绝对值**,不是增量:客户端每帧读计时器、自己不累加。
    /// 阶段(客户端的阶段保护整数 +0x6a84):0 带着走,1 收尾待释放,2 已释放。
    /// <list type="bullet">
    /// <item>状态计时不到 <see cref="HoldStartMs"/>:携带点与命中都不算,什么也不发生(只有"目标出现过出血"的记录照常更新)。</item>
    /// <item>阶段 0、计时到了:目标没了,或命中计时器次数用完,就进阶段 1;否则——多段命中的施放按计时器到点命中(目标活着且本机控制才真打出去),
    /// 单次命中的施放(客户端强化态标志为假)打一次就进阶段 1。</item>
    /// <item>阶段 1:状态计时到了 <see cref="ReleaseDelayMs"/> 就释放(调用方放摔打对象并给主角挂自身效果),进阶段 2。</item>
    /// <item>阶段 2:状态计时到了总时长就结束(回站立);不到阶段 2 即使超过总时长也不结束。</item>
    /// </list>
    /// 命中计时器与客户端那一个同样(0x0112f290/0x0112f310/0x0112f3d0,两个标志都为 1):起点算在状态计时 0 之前一个间隔,所以第一次检查就到点;
    /// 每次到点从**此刻**重数下一个间隔(不是在上一个到点时刻上累加);次数是 -1 表示不限;次数用完之后再检查不到点。
    /// 命中计时器"用完"的判定(客户端 0x0226aeb6,函数体在受保护区读不到)是按用法读成"到点次数 ≥ 最大次数"的(推断):两处调用的真值分支都是进阶段 1,
    /// 与计时器自己"用完就不再到点"的判断同一个条件。
    /// </summary>
    public sealed class GrabRun
    {
        /// <summary>状态计时不到这个毫秒数,携带点与命中都不算(客户端 0x00cbf8aa:比较 0x64)。</summary>
        public const int HoldStartMs = 100;

        /// <summary>阶段 1 里状态计时到了这个毫秒数才释放(客户端 0x00cbfbae:比较 0x2ee)。</summary>
        public const int ReleaseDelayMs = 750;

        private int _lastTickMs;

        /// <param name="durationMs">总时长(毫秒):阶段 2 且状态计时到了它就结束(客户端 +0x6a80,取自技能静态数据的一项)。</param>
        /// <param name="tickIntervalMs">命中计时器的间隔(毫秒)。</param>
        /// <param name="tickCount">命中计时器的最大次数;-1 不限。</param>
        /// <param name="multiHit">多段命中(按计时器命中,次数用完才收尾);假则≥100 毫秒后只打一次就收尾。</param>
        public GrabRun(int durationMs, int tickIntervalMs, int tickCount, bool multiHit)
        {
            DurationMs = durationMs;
            TickIntervalMs = tickIntervalMs;
            TickCount = tickCount;
            MultiHit = multiHit;
            _lastTickMs = -tickIntervalMs;   // 起点标志为 1:上次到点 = 0 - 间隔,所以状态计时一到就能响
        }

        public int DurationMs { get; }

        public int TickIntervalMs { get; }

        public int TickCount { get; }

        public bool MultiHit { get; }

        /// <summary>当前阶段:0 带着走,1 收尾待释放,2 已释放。</summary>
        public int Phase { get; private set; }

        /// <summary>命中计时器已经到点的次数(多段命中里目标死了或不归本机控制时,到点照样计数,只是没有真的命中)。</summary>
        public int Ticks { get; private set; }

        /// <summary>带着走期间目标出现过出血(异常状态 11):一旦出现就一直为真,只在阶段 0 的帧里更新(客户端 +0x6af4)。</summary>
        public bool BleedSeen { get; private set; }

        /// <summary>已结束(阶段 2 且状态计时到了总时长),调用方此时让主角回站立。</summary>
        public bool Finished { get; private set; }

        /// <summary>命中计时器的次数是否已经用完(次数不是 -1 且到点次数不小于它)。</summary>
        public bool TicksExhausted => TickCount != -1 && Ticks >= TickCount;

        /// <summary>
        /// 走一帧。<paramref name="stateMs"/> 是此刻的状态计时(绝对毫秒数);<paramref name="targetPresent"/> 被抓的目标还在(没被移除);
        /// <paramref name="targetAlive"/> 目标活着(客户端目标槽 +0x500 返回假);<paramref name="targetBleeding"/> 目标此刻带出血;
        /// <paramref name="controlled"/> 本机是权威端(客户端 0x007f9eb0;单机恒为真)。
        /// </summary>
        public GrabStep Step(int stateMs, bool targetPresent, bool targetAlive, bool targetBleeding, bool controlled)
        {
            bool hit = false;
            bool release = false;
            if (Finished)
            {
                return new GrabStep(false, false, true);
            }
            if (Phase == 0)
            {
                if (stateMs >= HoldStartMs)
                {
                    if (!targetPresent || TicksExhausted)
                    {
                        Phase = 1;
                    }
                    else if (MultiHit)
                    {
                        if (CheckTick(stateMs) && controlled && targetAlive)
                        {
                            hit = true;
                        }
                    }
                    else
                    {
                        hit = controlled && targetAlive;
                        Phase = 1;
                    }
                }
                if (targetPresent && targetBleeding)
                {
                    BleedSeen = true;
                }
            }
            else if (Phase == 1 && stateMs >= ReleaseDelayMs)
            {
                release = true;
                Phase = 2;
            }
            if (Phase == 2 && stateMs >= DurationMs)
            {
                Finished = true;
            }
            return new GrabStep(hit, release, Finished);
        }

        /// <summary>命中计时器检查一次(客户端 0x0112f3d0):次数用完或间隔没到返回假;到点就从此刻重数、计数加一。</summary>
        private bool CheckTick(int stateMs)
        {
            if (TicksExhausted || stateMs < _lastTickMs + TickIntervalMs)
            {
                return false;
            }
            _lastTickMs = stateMs;
            Ticks++;
            return true;
        }
    }

    /// <summary>
    /// <see cref="GrabRun.Step"/> 这一帧发生的事:<see cref="Hit"/> 对被抓者打一次(客户端强制命中 0x00878f50);<see cref="Release"/> 释放(放摔打对象、给主角挂自身效果);
    /// <see cref="Finished"/> 状态结束(回站立)。
    /// </summary>
    public readonly struct GrabStep
    {
        public GrabStep(bool hit, bool release, bool finished)
        {
            Hit = hit;
            Release = release;
            Finished = finished;
        }

        public bool Hit { get; }

        public bool Release { get; }

        public bool Finished { get; }
    }
}
