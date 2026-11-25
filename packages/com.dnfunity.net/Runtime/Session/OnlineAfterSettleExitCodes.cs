using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 探针 <c>fight</c> 子命令开启 <c>--after-settle</c> 时的结算后菜单阶段专用退出码(16 起):
    /// 既有的 <see cref="OnlineFightExitCodes"/> 退出码一字不改;成功回城返回 <see cref="OnlineFightExitCodes.Success"/>(0),
    /// 取消返回 <see cref="OnlineFightExitCodes.Cancelled"/>(15)。
    /// </summary>
    public static class OnlineAfterSettleExitCodes
    {
        /// <summary>推进阶段超时(1/72 或 1/73 等待回包超时)。</summary>
        public const int AdvanceTimeout = 16;

        /// <summary>结算后菜单命令 1/75 等待回包超时。</summary>
        public const int EplpTimeout = 17;

        /// <summary>等待回城信号(0/2 或 0/23+0/24)超时。</summary>
        public const int ReturnTimeout = 18;

        /// <summary>服务端回包明确带失败标志(<c>(1, 75)</c> 首字节为 0)。</summary>
        public const int ReplyFailed = 19;

        /// <summary>服务端回包载荷畸形、解析失败。</summary>
        public const int ReplyMalformed = 20;

        /// <summary>对端关闭了连接或切帧出错。</summary>
        public const int SessionClosed = 21;

        /// <summary>收发抛了异常或发送帧编码失败。</summary>
        public const int Faulted = 22;

        /// <summary>1/74 选牌命令发送失败。</summary>
        public const int SelectCardSendFailed = 23;

        /// <summary>结算后菜单结局对应的进程退出码。</summary>
        public static int ForOutcome(OnlineAfterSettleOutcome outcome)
        {
            switch (outcome)
            {
                case OnlineAfterSettleOutcome.ReturnedToTown:
                    return OnlineFightExitCodes.Success;
                case OnlineAfterSettleOutcome.ScoreScrollTimeout:
                case OnlineAfterSettleOutcome.CardRightTimeout:
                    return AdvanceTimeout;
                case OnlineAfterSettleOutcome.SelectCardSendFailed:
                    return SelectCardSendFailed;
                case OnlineAfterSettleOutcome.EplpTimeout:
                    return EplpTimeout;
                case OnlineAfterSettleOutcome.ReturnTimeout:
                    return ReturnTimeout;
                case OnlineAfterSettleOutcome.ReplyFailed:
                    return ReplyFailed;
                case OnlineAfterSettleOutcome.ReplyMalformed:
                    return ReplyMalformed;
                case OnlineAfterSettleOutcome.SessionClosed:
                    return SessionClosed;
                case OnlineAfterSettleOutcome.Cancelled:
                    return OnlineFightExitCodes.Cancelled;
                default:
                    return Faulted;
            }
        }
    }
}
