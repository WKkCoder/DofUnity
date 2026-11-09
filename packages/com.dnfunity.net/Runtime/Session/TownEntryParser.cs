using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 进城回程明文帧解析器:输入解码后的帧序列,输出只读的 <see cref="TownEntrySnapshot"/>(E-358、E-359)。
    /// <para>
    /// 约定:短包、错位(载荷读不出或取值不合)、未知帧号一律跳过,绝不抛异常;空引用的列表项直接忽略。
    /// 同一种帧出现多次时后到的有效帧覆盖先到的(都是整份状态下发);一帧错位不会冲掉之前已读到的有效值。
    /// 不碰 561 号信封:它的包号不在下面任何一条规则里,所以天然被当成未知帧号跳过。
    /// </para>
    /// <para>
    /// 当前认得的明文帧(都是类 0 通知,载荷从第一个字段起,尾部可能带折叠哈希与加密补零,读完字段就停,不去核对):
    /// 包号 124(<see cref="SlotJudge.EnterCompletePacketId"/>)进城完成,空载荷;
    /// 包号 <see cref="TimeSyncPacketId"/> 时间同步,u16 时 + u16 分,小端。
    /// </para>
    /// <para>
    /// 缺口:等级、职业、成长类型、区域编号在明文帧里没有承载包(见 <see cref="TownEntrySnapshot"/> 各属性的说明),这里不猜;
    /// 进城流里另有三万字节的已清任务列表(类 0、包号 355)等明文帧,布局可定但字节含义(逐任务一字节还是按位打包)单样本定不了,故不收。
    /// </para>
    /// </summary>
    public static class TownEntryParser
    {
        /// <summary>时间同步通知的包号(类 0)。</summary>
        public const int TimeSyncPacketId = 300;

        /// <summary>
        /// 解析一段进城回程帧。
        /// </summary>
        /// <param name="frames">按到达顺序排列的解码帧(选角窗口加尾窗口);可以为 null 或空。</param>
        /// <param name="slot">选角请求用的槽号,小于 0 表示不知道;原样进快照的 <see cref="TownEntrySnapshot.Slot"/>,不是解析所得。</param>
        /// <returns>永不为 null;什么也没认出来时各字段为空,诊断计数照常给出。</returns>
        public static TownEntrySnapshot Parse(IReadOnlyList<DecodedFrame> frames, int slot = -1)
        {
            bool enterComplete = false;
            int? hour = null;
            int? minute = null;
            int seen = 0;
            int parsed = 0;
            int malformed = 0;

            if (frames != null)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    DecodedFrame frame = frames[i];
                    if (frame == null)
                    {
                        continue;
                    }
                    seen++;
                    if (frame.Header.Class != PacketClass.Noti)
                    {
                        continue;
                    }

                    switch (frame.Header.PacketId)
                    {
                        case SlotJudge.EnterCompletePacketId:
                            enterComplete = true;
                            parsed++;
                            break;
                        case TimeSyncPacketId:
                            if (TryReadTimeSync(frame.Payload, out int h, out int m))
                            {
                                hour = h;
                                minute = m;
                                parsed++;
                            }
                            else
                            {
                                malformed++;
                            }
                            break;
                    }
                }
            }

            return new TownEntrySnapshot(slot >= 0 ? slot : (int?)null, enterComplete, hour, minute, seen, parsed, malformed);
        }

        // 时间同步:u16 时(0..23)+ u16 分(0..59)。载荷不够 4 字节或取值越界都算错位,不猜。
        private static bool TryReadTimeSync(byte[] payload, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;
            if (payload == null)
            {
                return false;
            }
            var reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort h) || !reader.TryGetUShort(out ushort m))
            {
                return false;
            }
            if (h > 23 || m > 59)
            {
                return false;
            }
            hour = h;
            minute = m;
            return true;
        }
    }
}
