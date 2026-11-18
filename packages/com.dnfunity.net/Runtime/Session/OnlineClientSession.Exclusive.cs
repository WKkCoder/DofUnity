using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.Session
{
    public sealed partial class OnlineClientSession
    {
        /// <summary>
        /// 结束独占收包并切回选角成功后的城镇态(<see cref="OnlineClientSessionState.CharacterSelected"/>)。
        /// <para>
        /// 调用约定(不由会话强行检测):调用方须确保进副本/战斗等独占路径已不再读取或写入 <see cref="GameSession"/>。
        /// 语义:本方法通过内部门闩与其它公开方法串行执行;将 <see cref="State"/> 恢复为 <see cref="OnlineClientSessionState.CharacterSelected"/>;
        /// 心跳开关恢复为进入 <see cref="EnterExclusiveReceiveAsync"/> 之前的状态(进入前正在跑则重新启动心跳泵且空闲计时重新开始,
        /// 进入前已关闭则保持 <see cref="OnlineHeartbeatState.Disabled"/>);原有 <see cref="FrameReceived"/> 订阅保留。
        /// 若独占期间链路已经关闭或发生读/切帧错误,仍切回状态并在返回前按与连接末尾相同口径做一次断线检查,
        /// 保证无论泵是否重启、往返多少次,每条链路至多触发一次 <see cref="Disconnected"/>。
        /// </para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌(等待门闩期间有效)。</param>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        /// <exception cref="InvalidOperationException">会话当前未处于 <see cref="OnlineClientSessionState.ExclusiveReceive"/>。</exception>
        /// <exception cref="OperationCanceledException">在进入门闩前被取消或会话正在释放。</exception>
        public async Task LeaveExclusiveReceiveAsync(CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource operation = BeginOperation(cancellationToken);
            cancellationToken = operation.Token;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State != OnlineClientSessionState.ExclusiveReceive)
                {
                    throw new InvalidOperationException("只有处于独占收包状态时才能切回会话收包");
                }

                OnlineChannelLink link = _link;
                bool restoreHeartbeat = _restoreHeartbeatOnLeave;
                _restoreHeartbeatOnLeave = false;
                State = OnlineClientSessionState.CharacterSelected;
                HeartbeatState = OnlineHeartbeatState.Disabled;
                if (link != null)
                {
                    NotifyIfDisconnected(link);
                }
                if (restoreHeartbeat)
                {
                    StartHeartbeatNoGate();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
