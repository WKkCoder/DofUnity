using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 进城后的连接保活:服务端每 300 秒发一次空载荷心跳通知,连续三次没回应就断开;回一帧类 1、包号 0、
    /// 载荷是两个 u32 校验值即可清零计数(E-306)。两个校验值的真算法要读背包与技能槽的聚合布局(E-307 只读通叶子函数),
    /// 服务端对不匹配只记录、不处罚,所以这里先回两个 0;等角色本体数据接进来之后再换真值。
    /// 保活泵独占会话的接收(单独一条逻辑线,与会话的单线使用约定一致)。
    /// </summary>
    public static class OnlineHeartbeat
    {
        /// <summary>心跳通知的包号(类 0)。</summary>
        public const ushort HeartbeatPacketId = 0;

        /// <summary>心跳回包的包号(类 1)。</summary>
        public const ushort ReplyPacketId = 0;

        /// <summary>心跳回包载荷:u32 背包校验 + u32 技能校验;真值未接,先填 0(E-306:不处罚)。</summary>
        public static readonly byte[] ReplyPayload = new byte[8];

        /// <summary>一帧是不是服务端心跳(类 0、包号 0)。</summary>
        public static bool IsHeartbeat(DecodedFrame frame)
        {
            return frame != null && frame.Header.Class == PacketClass.Noti && frame.Header.PacketId == HeartbeatPacketId;
        }

        /// <summary>
        /// 在已进城的会话上跑保活:循环收帧,收到心跳就回一帧;返回一共回了多少次。
        /// 对端关闭连接时正常结束;取消时抛 <see cref="OperationCanceledException"/>。
        /// </summary>
        public static async Task<int> RunAsync(GameClientSession session, CancellationToken cancellationToken = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            int answered = 0;
            while (!session.PeerClosed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<DecodedFrame> window = await session.ReceiveAsync(TimeSpan.FromSeconds(5), 0, cancellationToken).ConfigureAwait(false);
                foreach (DecodedFrame frame in window)
                {
                    if (!IsHeartbeat(frame))
                    {
                        continue;
                    }
                    FrameStatus sent = await session.SendAsync(PacketClass.Cmd, ReplyPacketId, ReplyPayload, cancellationToken).ConfigureAwait(false);
                    if (sent != FrameStatus.Ok)
                    {
                        return answered;
                    }
                    answered++;
                }
            }
            return answered;
        }
    }
}
