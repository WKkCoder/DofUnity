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
    /// 包号 <see cref="TimeSyncPacketId"/> 时间同步,u16 时 + u16 分,小端;
    /// 包号 <see cref="UserInfoParser.PacketId"/> 用户信息通知,按 <see cref="UserInfoParser"/> 解析模式 2(角色选择列表),
    /// 把选中槽位那一条的等级、职业、成长类型回填快照。
    /// </para>
    /// <para>
    /// 用户信息通知的载荷形态(charre_NOTES §3【观】):包号 2 是压缩包号(2、9、0x57,与包类无关),
    /// <see cref="ServerFrameDecoder"/> 在会话解密之后就把它解压了,<see cref="DecodedFrame.Payload"/> 已是从模式字节起的明文。
    /// 这里直接消费既有解码管线给出的这个形态,<b>不另做解压</b>;万一收到的仍是压缩字节,首字节(zlib 头,低 4 位恒为 8)不会是模式 2,
    /// 按"模式不支持"计入错位,不会被误读成列表。
    /// </para>
    /// <para>
    /// 选中槽位:<c>slot</c> 参数就是选角请求的槽号,按条目在列表里的<b>位置</b>去取(客户端点选角色后回传的是界面里的选中下标,
    /// 与条目的 u16 uid 无关,uid 是服务端的运行序号、不回传,charre_NOTES §1.2【观】;"选中下标就是列表位置"是【推】,
    /// §4.3 第 10 条:频道类型特殊时服务端只写 1 条,位置与服务端的真实角色下标可能不同,未验证)。
    /// 没有选中槽位(<c>slot</c> 小于 0)、槽位不在列表里、载荷读不通、模式不是 2:三个字段都保持 null,计入 <see cref="TownEntrySnapshot.FramesMalformed"/>。
    /// </para>
    /// <para>
    /// 成长类型取哪个值(charre_NOTES §2.3 第 5 项、§2.4):条目里的成长类型是一个字节,客户端把它拆成两段——低 4 位与
    /// bit4 到 bit6 分别落到条目结构里的两个字段,bit7 丢弃【观】;服务端写的是同一个字节,由"设置当前成长类型(first, second)"写成 first 加 second 左移 4 位【服务端侧观】,
    /// 所以低 4 位是一转(first)、bit4 到 bit6 是二转(second),拆法与客户端一致【推】。
    /// 快照的 <see cref="TownEntrySnapshot.GrowType"/> 沿用仓库里既有的含义——转职方向、0 是转职前(<c>HeroProgress</c>、<c>GameObject</c> 里的成长类型同此),对应第一段,
    /// 所以给<b>低 4 位</b>,不给原始整字节(整字节在二转非 0 时会得到 16 以上的值,不再是转职方向的下标)。
    /// 第二段在客户端的消费者材料没读到【未】(§6),不并进快照,只在记录里保留(<see cref="UserInfoEntry.SecondGrowType"/>、<see cref="UserInfoEntry.GrowTypeByte"/>)。
    /// </para>
    /// <para>
    /// 缺口:区域编号在明文帧里没有承载包(见 <see cref="TownEntrySnapshot"/> 各属性的说明),这里不猜;
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
        /// <param name="slot">选角请求用的槽号,小于 0 表示不知道;原样进快照的 <see cref="TownEntrySnapshot.Slot"/>,不是解析所得;
        /// 同时是用户信息通知里挑"当前选中"条目的位置下标。</param>
        /// <returns>永不为 null;什么也没认出来时各字段为空,诊断计数照常给出。</returns>
        public static TownEntrySnapshot Parse(IReadOnlyList<DecodedFrame> frames, int slot = -1)
        {
            bool enterComplete = false;
            int? level = null;
            int? job = null;
            int? growType = null;
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
                        case UserInfoParser.PacketId:
                            if (TryReadSelectedCharacter(frame.Payload, slot, out int selectedLevel, out int selectedJob, out int selectedGrowType))
                            {
                                level = selectedLevel;
                                job = selectedJob;
                                growType = selectedGrowType;
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

            return new TownEntrySnapshot(slot >= 0 ? slot : (int?)null, enterComplete, level, job, growType, hour, minute, seen, parsed, malformed);
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

        // 用户信息通知:解出模式 2 的角色列表,取位置 slot 那一条的等级、职业、成长类型(低 4 位,依据见类说明)。
        // 没有选中槽位、载荷读不通(含模式不是 2)、槽位不在列表里都算错位,不猜。
        private static bool TryReadSelectedCharacter(byte[] payload, int slot, out int level, out int job, out int growType)
        {
            level = 0;
            job = 0;
            growType = 0;
            if (slot < 0 || !UserInfoParser.TryParse(payload, out UserInfoNotice notice) || slot >= notice.Entries.Count)
            {
                return false;
            }
            UserInfoEntry entry = notice.Entries[slot];
            level = entry.Level;
            job = entry.Job;
            growType = entry.FirstGrowType;
            return true;
        }
    }
}
