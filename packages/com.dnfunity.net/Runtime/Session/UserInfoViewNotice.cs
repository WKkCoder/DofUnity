using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>用户信息视图(模式 0 / 1)解析的结论。</summary>
    public enum UserInfoViewStatus
    {
        /// <summary>读到装备列表末尾。</summary>
        Ok = 0,

        /// <summary>布局读不下来:载荷为 null、为空、过短,条目个数不是 1,长度前缀超出载荷,装备列表取值不合。</summary>
        Malformed = 1,

        /// <summary>
        /// 模式字节不是 0 或 1:模式 2(角色选择列表)归既有的 <see cref="UserInfoParser"/>,模式 3(标签角色)本机没有样本。
        /// 绝不按模式 0 / 1 的布局去读。
        /// </summary>
        UnsupportedMode = 2,
    }

    /// <summary>
    /// 用户信息通知(类 0、包号 2)的<b>装备视图</b>:模式 0 是公共视图(别人看到的外观级装备,紧凑列表),
    /// 模式 1 是本人详细视图(详细列表)。只做到"装备列表读完"为止:列表之后的非物品字段(技能树、公会、各类次数等)不解释,
    /// 只在 <see cref="TrailingByteCount"/> 里报个数,其中也包含 4 字节包尾校验。
    /// 不可变;构造入口只给 <see cref="UserInfoViewParser"/>。
    /// </summary>
    public sealed class UserInfoViewNotice
    {
        private readonly byte[] _name;
        private readonly byte[] _headerBytes;
        private readonly IReadOnlyList<EquipCompactRecord> _compactEquips;
        private readonly IReadOnlyList<ItemEntry> _detailedEquips;

        internal UserInfoViewNotice(
            int mode,
            ushort uid,
            byte[] name,
            byte[] headerBytes,
            EquipCompactRecord[] compactEquips,
            byte compactTrailingByte,
            ushort x,
            uint y,
            int statBlockLength,
            byte selfByte,
            ItemEntry[] detailedEquips,
            int equipListOffset,
            int equipListLength,
            int consumedByteCount,
            int trailingByteCount)
        {
            Mode = mode;
            Uid = uid;
            _name = name ?? new byte[0];
            _headerBytes = headerBytes ?? new byte[0];
            _compactEquips = Array.AsReadOnly(compactEquips ?? new EquipCompactRecord[0]);
            CompactTrailingByte = compactTrailingByte;
            X = x;
            Y = y;
            StatBlockLength = statBlockLength;
            SelfByte = selfByte;
            _detailedEquips = Array.AsReadOnly(detailedEquips ?? new ItemEntry[0]);
            EquipListOffset = equipListOffset;
            EquipListLength = equipListLength;
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>模式字节:0 公共视图,1 本人详细视图。</summary>
        public int Mode { get; }

        /// <summary>是公共视图(模式 0,紧凑装备列表)。</summary>
        public bool IsPublicView => Mode == UserInfoViewParser.PublicViewMode;

        /// <summary>是本人详细视图(模式 1,详细装备列表)。</summary>
        public bool IsSelfView => Mode == UserInfoViewParser.SelfViewMode;

        /// <summary>模式 0:条目里的 u16 uid(NOTI/339 的 uid 与它是同一个数,样本逐条相等,笔记 §2.4);模式 1 为 0。</summary>
        public ushort Uid { get; }

        /// <summary>模式 0:名字的原始字节(u32 长度 + 字节,不转字符串);模式 1 为空。</summary>
        public IReadOnlyList<byte> Name => Array.AsReadOnly(_name);

        /// <summary>
        /// 模式 0:名字之后、装备列表之前的 6 个 u8 原值(与角色选择列表条目的 X1、职业、成长类型、等级、D、状态同读序;本视图里没有单独验证,不解释)。
        /// 模式 1 为空。
        /// </summary>
        public IReadOnlyList<byte> HeaderBytes => Array.AsReadOnly(_headerBytes);

        /// <summary>模式 0:紧凑装备记录(槽 0 到 11);模式 1 为空。</summary>
        public IReadOnlyList<EquipCompactRecord> CompactEquips => _compactEquips;

        /// <summary>模式 0:紧凑列表之后的尾字节(服务端 <c>UpgradeSeparate</c>);模式 1 为 0。</summary>
        public byte CompactTrailingByte { get; }

        /// <summary>模式 1:条目里的 u16 X(客户端 0x00514c82 读,语义未验证);模式 0 为 0。</summary>
        public ushort X { get; }

        /// <summary>模式 1:条目里的 u32 Y(客户端 0x00513030 读;服务端 0x0865b0bb 写的是经验值,[推断] 未对照取值);模式 0 为 0。</summary>
        public uint Y { get; }

        /// <summary>
        /// 模式 1:统计块的长度前缀(u32 长度 + 字节;长度前缀没有上限守卫,客户端 0x01134620),只对剩余字节做上限。统计块的内容不解释。
        /// 装备列表的偏移 = 14 + 这个长度(笔记 §2.2.5)。模式 0 为 0。
        /// </summary>
        public int StatBlockLength { get; }

        /// <summary>模式 1:统计块之后、装备列表之前的 u8(服务端 <c>GetCurExEquipSlotStat</c>,0x0865b0f0;语义未验证);模式 0 为 0。</summary>
        public byte SelfByte { get; }

        /// <summary>模式 1:详细装备记录(当前装备组,槽 0 到 25);模式 0 为空。</summary>
        public IReadOnlyList<ItemEntry> DetailedEquips => _detailedEquips;

        /// <summary>装备列表(<c>u8 个数</c> 起)在载荷里的偏移。模式 0 = 15 + 名字长度;模式 1 = 14 + 统计块长度。</summary>
        public int EquipListOffset { get; }

        /// <summary>装备列表占的字节数(含个数字节;模式 0 还含列表之后的尾字节)。</summary>
        public int EquipListLength { get; }

        /// <summary>解析消耗的字节数(从载荷起点到装备列表末尾)。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>
        /// 装备列表之后剩余的字节数:非物品字段(不解释)加 4 字节包尾校验。解析器不核对、不消费这段。
        /// </summary>
        public int TrailingByteCount { get; }
    }
}
