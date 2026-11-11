using System;

// 本项目自己的设计:宠物状态层里所有会碰饱食的方法(装备、卸下、喂食、结算)都要宿主给"现在是第几秒"(单调递增的整数秒,见 PetInstance)。
// 城镇这一侧不自己数秒,统一从这个时间源取:宿主给墙钟,测试给手动推进的钟,饱食的流逝在测试里就不用真等。
namespace Dnf.Play
{
    /// <summary>宠物用的时间源:当前的整数秒。同一个会话里窗口、跟随实体与存档必须用同一个时间源,否则各读各的"现在"。</summary>
    public interface IPetClock
    {
        /// <summary>当前秒数(单调递增;时钟倒退时状态层按"过了 0 秒"算,不会让饱食反向变化)。</summary>
        int NowSeconds { get; }
    }

    /// <summary>手动推进的时钟(测试、无人值守检查用):想让它走多少秒就走多少秒。</summary>
    public sealed class ManualPetClock : IPetClock
    {
        public ManualPetClock(int startSeconds = 0)
        {
            NowSeconds = startSeconds;
        }

        public int NowSeconds { get; private set; }

        /// <summary>直接把钟拨到 <paramref name="seconds"/>(可以拨回去,用来试"时钟倒退")。</summary>
        public void Set(int seconds)
        {
            NowSeconds = seconds;
        }

        /// <summary>往前走 <paramref name="seconds"/> 秒。</summary>
        public void Advance(int seconds)
        {
            NowSeconds += seconds;
        }
    }

    /// <summary>
    /// 由毫秒读数折成整数秒的时钟(宿主把墙钟或单调计时器的毫秒读数交进来):向下取整,小于 0 按 0,超过 <see cref="int.MaxValue"/> 按上限。
    /// </summary>
    public sealed class MillisecondsPetClock : IPetClock
    {
        private readonly Func<long> _milliseconds;

        public MillisecondsPetClock(Func<long> milliseconds)
        {
            _milliseconds = milliseconds ?? throw new ArgumentNullException(nameof(milliseconds));
        }

        public int NowSeconds
        {
            get
            {
                long ms = _milliseconds();
                if (ms <= 0)
                {
                    return 0;
                }
                long seconds = ms / 1000;
                return seconds > int.MaxValue ? int.MaxValue : (int)seconds;
            }
        }
    }
}
