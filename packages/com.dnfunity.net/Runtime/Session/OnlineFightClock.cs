using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 无头战斗驱动(<see cref="OnlineFightDriver"/>)用的时钟接缝:驱动只通过它读"现在几点"和"等一会儿",不碰墙钟与 <c>Task.Delay</c>,
    /// 所以测试能换成手拨的假时钟,不用真等(线上用 <see cref="SystemFightClock"/>)。
    /// 注意:战斗跑器(<see cref="OnlineDungeonCombatRunner"/>)自己的轮询窗口与上限仍走真时间,不受本接缝影响;驱动只用它管自己的节奏与两段等待。
    /// </summary>
    public interface IFightClock
    {
        /// <summary>单调时间(自某个固定起点起算),只用来算差值。</summary>
        TimeSpan Now { get; }

        /// <summary>
        /// 按本时钟的时间等 <paramref name="delay"/>。<paramref name="delay"/> 不大于 0 时立即完成;
        /// 取消令牌触发时以取消结束(返回的任务取消,<c>await</c> 它会抛 <see cref="OperationCanceledException"/>)。
        /// </summary>
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    /// <summary>线上用的时钟:单调的秒表加 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。</summary>
    public sealed class SystemFightClock : IFightClock
    {
        /// <summary>共用的一个实例(无状态,起点是首次使用时)。</summary>
        public static readonly SystemFightClock Instance = new SystemFightClock();

        private readonly Stopwatch _watch = Stopwatch.StartNew();

        /// <inheritdoc />
        public TimeSpan Now => _watch.Elapsed;

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero)
            {
                return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
            }
            return Task.Delay(delay, cancellationToken);
        }
    }
}
