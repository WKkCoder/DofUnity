using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>用户信息通知载荷的解析结论。</summary>
    public enum UserInfoParseStatus
    {
        /// <summary>模式 2(角色选择列表),整份读完。</summary>
        Ok = 0,

        /// <summary>
        /// 布局读不下来:载荷为 null、为空、过短,或某个个数、长度声明超出载荷,或装备记录的 blobLen 不是 4。
        /// 首字节就是模式 2 但后面读不通,也算在这里。
        /// </summary>
        Malformed = 1,

        /// <summary>
        /// 首字节不是 2:本解析器只实现模式 2。模式 0、1、3 是"查看他人"的单条目类型,读序只有类型级信息;
        /// 其余模式值客户端不读任何字节。这些一律不解析、绝不按模式 2 的布局去读。
        /// </summary>
        UnsupportedMode = 2,
    }

    /// <summary>
    /// 用户信息通知(类 0、包号 <see cref="PacketId"/>,包名表里叫 USERINFO)<b>模式 2(角色选择列表)</b>的明文载荷解析器:
    /// 把客户端静态读码定案的读序落成只读记录 <see cref="UserInfoNotice"/>。
    /// 载荷首字节是模式。模式 2 是"我的角色列表"(登录后拉角色列表的应答;建角与删角成功后服务端也会主动再推一次);
    /// 模式 0、1、3 是"查看他人"的单条目类型,读序只有类型级信息,本解析器不实现,一律返回不支持
    /// (<see cref="UserInfoParseStatus.UnsupportedMode"/>),绝不按模式 2 的布局误读。
    /// <para>
    /// 范围:纯明文解析。解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰 561 信封。
    /// 压缩层不在这里:包号 2 属于压缩包号集合 {2、9、0x57},客户端与服务端都按包号无条件压缩与解压、不看包类(charre_NOTES §3.2【观】),
    /// 本仓库里这一步由 <see cref="ServerFrameDecoder"/> 在会话解密之后做掉,<see cref="DecodedFrame.Payload"/> 已是从模式字节起的明文;解析器只吃这种明文。
    /// 万一误把仍压缩的字节喂进来也不会被误读:zlib 流的首字节低 4 位恒为 8,而模式字节要求是 2,所以按 <see cref="UserInfoParseStatus.UnsupportedMode"/> 拒绝。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="DungeonStartParser"/> 同风格):短包、读越界、个数或长度声明超出载荷一律返回 false 且 notice 为 null,绝不抛异常(包括 null 载荷);
    /// 读完最后一个条目的最后一个字段(d2)即停,容忍尾部多余字节(只在 <see cref="UserInfoNotice.TrailingByteCount"/> 报告个数,不核对);
    /// 字段取值不做范围检查(语义未定,没有可核对的范围),唯一的取值要求是装备记录的 blobLen 必须为 4(见下)。
    /// </para>
    /// <para>
    /// 读序(小端;charre_NOTES §2.3 的【观】,来自客户端处理器与条目函数的静态读码):
    /// <c>u8 mode(=2), u8 a, u8 b, u8 c, u32 d, u16 count, count × 条目</c>;
    /// 条目 = <c>u16 uid, u32 L + L 字节名字, u8 X1, u8 job, u8 growType, u8 level, u8 D, u8 status, u8 n + n × 装备记录,
    /// u8 G, u8 H, u32 I, u8 J, u8 premium, u32 K, u8 a2, u8 b2, u8 c2, u32 d2</c>;
    /// 装备记录(22 字节)= <c>u8 slot, u32 itemId, u32 blobLen(=4) + 4 字节, u8 packed, u32 clearAvatar, u32 extra</c>。
    /// 头 10 字节;条目最短 32 字节(名字 0 字节、没有装备),长度 = 32 + L + 22n。
    /// </para>
    /// <para>
    /// 【客户端行为,解析器都不模拟】共两处,都与"按线上结构直读、不做范围检查"的约定一致:
    /// (1) 名字:客户端只在 1 ≤ L ≤ 30 时读那 L 个字节;L 为 0 时用默认名且不失步;L ≥ 31 时读完 u32 就不再读字节,此后整个包错位
    /// (§2.3 第 2 项、§4.3 第 3 条【观】)。解析器按线上结构直读:只要 L 个字节没超出载荷就全读,不套 30 的上限、不模拟失步,L 为 0 得到空名。
    /// (2) 装备 blob:客户端把 blobLen 个字节读进一个 4 字节的栈变量,没有检查,大于 4 会栈溢出;服务端恒写 4(§4.3 第 4 条【观】)。
    /// 解析器严格要求 blobLen 恰为 4,否则整包按错位处理(不去猜变长布局)。
    /// 另外,槽号越界、count 与头部 a 的关系、X1 为 1 或 status 为 8 时客户端会拦"开始游戏"等,都不核对。
    /// 所以"解析成功"只表示布局读得下来,不表示客户端会按同一个字节边界消费这个包。
    /// </para>
    /// <para>
    /// 尾巴:客户端读完就不再读,也没有"必须读完"的检查。真实帧里正文长度在 1 到 0x190 字节时,服务端会在明文末尾追加 4 字节折叠 PI 尾(客户端从不检查,§3.5【观】);
    /// 解析器不消费、不核对,只在 <see cref="UserInfoNotice.TrailingByteCount"/> 报告个数。
    /// </para>
    /// </summary>
    public static class UserInfoParser
    {
        /// <summary>用户信息通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。它也是压缩包号之一。</summary>
        public const int PacketId = 2;

        /// <summary>本解析器唯一支持的模式:2(角色选择列表)。</summary>
        public const int CharacterListMode = 2;

        // 条目最短长度:u16 uid + u32 L(名字 0 字节)+ 6 个 u8 + u8 n(0 条装备)= 13,再加装备之后的固定 19 字节
        // (G、H 各 1,I 4,J、premium 各 1,K 4,a2、b2、c2 各 1,d2 4)= 32。
        private const int MinEntryLength = 32;

        // 装备记录定长:1 + 4 + 4 + 4 + 1 + 4 + 4。
        private const int EquipRecordLength = 22;

        // 装备 blob 的长度(线上 u32 blobLen 的唯一合法值)。
        private const uint EquipBlobLength = 4;

        /// <summary>
        /// 解析一份载荷(从模式字节起)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>模式 2 且读完全部字段为 true;其余(含不支持的模式)为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out UserInfoNotice notice)
        {
            return TryParse(payload, out notice, out UserInfoParseStatus _);
        }

        /// <summary>
        /// 解析一份载荷(从模式字节起),并说明失败的原因:布局读不下来与"模式不支持"分开报告。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <param name="status">结论:成功是 <see cref="UserInfoParseStatus.Ok"/>;模式字节不是 2 是 <see cref="UserInfoParseStatus.UnsupportedMode"/>;
        /// 其余失败(含 null、空载荷)是 <see cref="UserInfoParseStatus.Malformed"/>。</param>
        /// <returns>模式 2 且读完全部字段为 true;其余为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out UserInfoNotice notice, out UserInfoParseStatus status)
        {
            notice = null;
            status = UserInfoParseStatus.Malformed;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte mode))
            {
                return false;
            }

            if (mode != CharacterListMode)
            {
                status = UserInfoParseStatus.UnsupportedMode;
                return false;
            }

            if (!reader.TryGetByte(out byte a)
                || !reader.TryGetByte(out byte b)
                || !reader.TryGetByte(out byte c)
                || !reader.TryGetUInt(out uint d)
                || !reader.TryGetUShort(out ushort count))
            {
                return false;
            }

            // 声明的条目数连"每条最短 32 字节"都放不下:按越界处理,同时避免为一个谎报的 count 白白分配数组。
            if (count * MinEntryLength > reader.Remaining)
            {
                return false;
            }

            var entries = new UserInfoEntry[count];
            for (int i = 0; i < entries.Length; i++)
            {
                if (!TryReadEntry(reader, out UserInfoEntry entry))
                {
                    return false;
                }
                entries[i] = entry;
            }

            notice = new UserInfoNotice(a, b, c, d, entries, reader.Remaining);
            status = UserInfoParseStatus.Ok;
            return true;
        }

        // 一个条目:u16 uid、名字、6 个 u8、装备子表、G、H、I、J、premium、K、a2、b2、c2、d2。
        private static bool TryReadEntry(PacketReader reader, out UserInfoEntry entry)
        {
            entry = null;
            if (!reader.TryGetUShort(out ushort uid) || !TryReadName(reader, out byte[] name))
            {
                return false;
            }

            if (!reader.TryGetByte(out byte x1)
                || !reader.TryGetByte(out byte job)
                || !reader.TryGetByte(out byte growType)
                || !reader.TryGetByte(out byte level)
                || !reader.TryGetByte(out byte d)
                || !reader.TryGetByte(out byte status))
            {
                return false;
            }

            if (!TryReadEquips(reader, out UserInfoEquip[] equips))
            {
                return false;
            }

            if (!reader.TryGetByte(out byte g)
                || !reader.TryGetByte(out byte h)
                || !reader.TryGetUInt(out uint i)
                || !reader.TryGetByte(out byte j)
                || !reader.TryGetByte(out byte premium)
                || !reader.TryGetUInt(out uint k)
                || !reader.TryGetByte(out byte a2)
                || !reader.TryGetByte(out byte b2)
                || !reader.TryGetByte(out byte c2)
                || !reader.TryGetUInt(out uint d2))
            {
                return false;
            }

            entry = new UserInfoEntry(uid, name, x1, job, growType, level, d, status, equips, g, h, i, j, premium, k, a2, b2, c2, d2);
            return true;
        }

        // 名字:u32 长度 L + L 个原始字节。只拷贝字节,不转字符串;L 只对剩余字节做上限(客户端的 1..30 限制与失步不模拟,见类说明)。
        // L 与剩余字节数都按无符号比较:u32 的高位为 1 时不会被当成负数放过去。
        private static bool TryReadName(PacketReader reader, out byte[] name)
        {
            name = null;
            if (!reader.TryGetUInt(out uint length) || length > (uint)reader.Remaining)
            {
                return false;
            }

            if (length == 0)
            {
                name = new byte[0];
                return true;
            }

            return reader.TryGetBinary((int)length, out name);
        }

        // 装备子表:u8 条数 n + n 条 22 字节记录。blobLen 必须恰为 4(见类说明)。
        private static bool TryReadEquips(PacketReader reader, out UserInfoEquip[] equips)
        {
            equips = null;
            if (!reader.TryGetByte(out byte count))
            {
                return false;
            }

            // 声明的条数连每条 22 字节都放不下:按越界处理。
            if (count * EquipRecordLength > reader.Remaining)
            {
                return false;
            }

            var list = new UserInfoEquip[count];
            for (int n = 0; n < list.Length; n++)
            {
                if (!reader.TryGetByte(out byte slot)
                    || !reader.TryGetUInt(out uint itemId)
                    || !reader.TryGetUInt(out uint blobLength)
                    || blobLength != EquipBlobLength
                    || !reader.TryGetUInt(out uint blob)
                    || !reader.TryGetByte(out byte packed)
                    || !reader.TryGetUInt(out uint clearAvatar)
                    || !reader.TryGetUInt(out uint extra))
                {
                    return false;
                }
                list[n] = new UserInfoEquip(slot, itemId, blob, packed, clearAvatar, extra);
            }

            equips = list;
            return true;
        }
    }
}
