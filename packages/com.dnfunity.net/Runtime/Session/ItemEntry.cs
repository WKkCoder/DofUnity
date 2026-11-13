using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品条目的"形状":同一套主干(槽号、物品号、Data、强化字节……)在不同包里的细微差别由形状决定,
    /// 由 <see cref="ItemEntryReader"/> 统一读出(笔记 §2.1.4、§2.2.4、§2.3.2、§4.1)。
    /// </summary>
    public enum ItemEntryShape
    {
        /// <summary>
        /// 背包 / 仓库 / 账号仓库形:u16 槽号,25 字节主干,没有可选块。
        /// 主干 = 槽 2 + 物品号 4 + Data 4 + 强化字节 1 + 耐久 2 + 封装字节 1 + 附魔 4 + 增幅类型 1 + 增幅值 2
        /// + C3 块(至少 1)+ 随机选项块(至少 1)+ 单独强化 1 + 交易限制 1,C3 与随机选项为空时共 25 字节。
        /// 来源:客户端 NOTI/13 处理器 0x00752b70(条目读点 0x00752e75 起)、服务端写入点 0x084fdb53..0x084fdd3a。
        /// </summary>
        Bag = 0,

        /// <summary>
        /// 装扮背包形:主干同 <see cref="Bag"/>,但 Data 位是<b>剩余时长</b>,且增幅值之后<b>固定</b>带两个"u32 长度 + 字节"块
        /// (客户端读空间 1 时无条件读,读点 0x007522e0 / 0x00753235 / 0x007532ab;服务端写入点 0x084fdf33..0x084fdffd 同样无条件写)。
        /// C3 块照常读:服务端空间 1 的 NOTI/13 内联写 0,NOTI/14 的写入器可能写出真内容,所以不假定为 0。
        /// </summary>
        AvatarBag = 1,

        /// <summary>
        /// 宠物栏形:主干同 <see cref="Bag"/>,物品是宠物类时增幅值之后多一个 u32(宠物剩余时长);
        /// 物品类型线上不带,靠 <see cref="IItemKindResolver"/>(没有回调即整包拒绝)。
        /// </summary>
        PetBag = 2,

        /// <summary>
        /// 增量包空间 3(身上装备)形:主干同 <see cref="Bag"/>;增幅值之后,装扮类物品多两个块、宠物类物品多一个 u32,
        /// 两者都靠物品类型判定(服务端 <c>CInventory::MakeItemPacket</c> 0x084fc6bc..0x084fd6d2 按 INVEN_TYPE 分派;客户端 NOTI/14 处理器 0x00753720)。
        /// </summary>
        BodyEquip = 3,

        /// <summary>
        /// 装备详细记录形(用户信息的自身视图、备用装备组):u8 槽号,<b>没有封装字节</b>,耐久之后多一个 u32(清除装扮),
        /// 其余同主干;装扮块与宠物 u32 默认按槽位近似判定,可由回调覆盖。27 字节起(客户端读取器 0x007523c0,服务端写入点 0x084fe40a..0x084feb1f)。
        /// </summary>
        EquipDetail = 4,
    }

    /// <summary>条目读取失败的原因(由上层映射成各自的状态枚举)。</summary>
    public enum ItemEntryFault
    {
        /// <summary>没有失败。</summary>
        None = 0,

        /// <summary>读到一半没字节了,或某个块声明的长度超出剩余字节。</summary>
        Truncated = 1,

        /// <summary>块长度字段取值不合(镶嵌块不是 30、扩展块不是 4)。</summary>
        InvalidBlockLength = 2,

        /// <summary>随机选项块的个数大于 3(服务端 0x7c7eb0 / 0x858e3ec 只可能给 0 到 3)。</summary>
        InvalidRandomOption = 3,

        /// <summary>需要物品类型却没有回调,或回调回答 <see cref="ItemKind.Unknown"/>。</summary>
        KindUnresolved = 4,
    }

    /// <summary>
    /// C3 块里的一项:一个 u32 加四个 u8,共 8 字节(客户端读取函数 0x00749b50:先读 u8 个数,再逐项读)。
    /// 语义未知([未知]),字段只按读序命名;本机抓帧里个数恒为 0,取值从未被观察过。
    /// 注意:笔记 §2.1.4 表里的"1+5n"是笔误,以每项 8 字节为准(E-410)。
    /// </summary>
    public readonly struct ItemC3Record
    {
        internal ItemC3Record(uint value, byte byte1, byte byte2, byte byte3, byte byte4)
        {
            Value = value;
            Byte1 = byte1;
            Byte2 = byte2;
            Byte3 = byte3;
            Byte4 = byte4;
        }

        /// <summary>那个 u32。</summary>
        public uint Value { get; }

        /// <summary>第一个 u8。</summary>
        public byte Byte1 { get; }

        /// <summary>第二个 u8。</summary>
        public byte Byte2 { get; }

        /// <summary>第三个 u8。</summary>
        public byte Byte3 { get; }

        /// <summary>第四个 u8。</summary>
        public byte Byte4 { get; }
    }

    /// <summary>随机选项块里的一项:三个 u8(语义未知,只按读序命名)。</summary>
    public readonly struct ItemRandomOptionField
    {
        internal ItemRandomOptionField(byte first, byte second, byte third)
        {
            First = first;
            Second = second;
            Third = third;
        }

        /// <summary>第一个 u8。</summary>
        public byte First { get; }

        /// <summary>第二个 u8。</summary>
        public byte Second { get; }

        /// <summary>第三个 u8。</summary>
        public byte Third { get; }
    }

    /// <summary>
    /// 物品条目(不可变)。线上读到什么就是什么:字段只做按位拆分与布尔化,不翻译成游戏含义;
    /// 有"推断"成分的拆分在各属性注释里标明。条目由 <see cref="ItemEntryReader"/> 产出,调用方拿不到可写入口。
    /// </summary>
    public sealed class ItemEntry
    {
        /// <summary>可选块的长度:镶嵌块 30 字节。</summary>
        public const int SocketBlockLength = 30;

        /// <summary>可选块的长度:扩展块 4 字节。</summary>
        public const int ExpansionBlockLength = 4;

        private static readonly byte[] EmptyBytes = new byte[0];
        private static readonly ItemC3Record[] EmptyC3 = new ItemC3Record[0];
        private static readonly ItemRandomOptionField[] EmptyFields = new ItemRandomOptionField[0];

        private readonly byte[] _socketBlock;
        private readonly byte[] _expansionBlock;
        private readonly ItemC3Record[] _c3Records;
        private readonly ItemRandomOptionField[] _randomOptionFields;
        private readonly byte[] _randomOptionTail;

        internal ItemEntry(
            ItemEntryShape shape,
            ushort slot,
            uint itemId,
            uint data,
            bool dataIsRemainingTime,
            byte ext1,
            ushort durability,
            bool hasSealedByte,
            byte sealedByte,
            bool hasClearAvatar,
            uint clearAvatar,
            uint enchantIndex,
            byte amplifyType,
            ushort amplifyValue,
            bool hasAvatarBlocks,
            byte[] socketBlock,
            byte[] expansionBlock,
            bool hasPetRemainTime,
            uint petRemainTime,
            ItemC3Record[] c3Records,
            ItemRandomOptionField[] randomOptionFields,
            byte randomOptionSelector,
            byte randomOptionTailMarker,
            byte[] randomOptionTail,
            byte separateUpgrade,
            byte tradeRestriction,
            int wireLength)
        {
            Shape = shape;
            Slot = slot;
            ItemId = itemId;
            Data = data;
            DataIsRemainingTime = dataIsRemainingTime;
            Ext1 = ext1;
            Durability = durability;
            HasSealedByte = hasSealedByte;
            SealedByte = sealedByte;
            HasClearAvatar = hasClearAvatar;
            ClearAvatar = clearAvatar;
            EnchantIndex = enchantIndex;
            AmplifyType = amplifyType;
            AmplifyValue = amplifyValue;
            HasAvatarBlocks = hasAvatarBlocks;
            _socketBlock = socketBlock ?? EmptyBytes;
            _expansionBlock = expansionBlock ?? EmptyBytes;
            HasPetRemainTime = hasPetRemainTime;
            PetRemainTime = petRemainTime;
            _c3Records = c3Records ?? EmptyC3;
            _randomOptionFields = randomOptionFields ?? EmptyFields;
            RandomOptionSelector = randomOptionSelector;
            RandomOptionTailMarker = randomOptionTailMarker;
            _randomOptionTail = randomOptionTail ?? EmptyBytes;
            SeparateUpgrade = separateUpgrade;
            TradeRestriction = tradeRestriction;
            WireLength = wireLength;
        }

        /// <summary>读出这条记录时用的形状。</summary>
        public ItemEntryShape Shape { get; }

        /// <summary>槽号(装备详细记录形线上是 u8,这里统一放宽成 u16)。</summary>
        public ushort Slot { get; }

        /// <summary>物品号。增量包里 <see cref="ItemEntryReader.DeleteMarker"/> 表示"清空该槽",此时本条目不会出现在 Upsert 里。</summary>
        public uint ItemId { get; }

        /// <summary>
        /// Data 位:装备/消耗品等是原始数值(例如堆叠数),<b>货币伪条目</b>里是金额;
        /// 装扮背包与装备详细记录的低槽位(槽 ≤ 9)里是剩余时长,见 <see cref="DataIsRemainingTime"/>。
        /// </summary>
        public uint Data { get; }

        /// <summary>
        /// Data 位按剩余时长解释(装扮背包形恒真;装备详细记录形槽 ≤ 9 为真);服务端取值点 0x084fde46(<c>GetRemainDate</c>,空间 1)/ 0x084fe464(装备详细,槽 ≤ 9)。
        /// 时长的单位未验证([推断] 笔记写作"剩余天数",本线不认定)。
        /// </summary>
        public bool DataIsRemainingTime { get; }

        /// <summary>强化字节原值(服务端 <c>ext_data1</c>)。</summary>
        public byte Ext1 { get; }

        /// <summary>强化字节低 5 位:强化等级(服务端 <c>Inven_Item::GetUpgrade</c> 取 <c>ItemAttr</c> 低 5 位,0x080f506c 一带,笔记 §2.1.9)。</summary>
        public int UpgradeLevel => Ext1 & 0x1F;

        /// <summary>强化字节高 3 位:[推断] 是重封次数 / 交易限制次数,未验证。</summary>
        public int Ext1HighBits => Ext1 >> 5;

        /// <summary>耐久。</summary>
        public ushort Durability { get; }

        /// <summary>这个形状里是否带封装字节(装备详细记录形没有)。</summary>
        public bool HasSealedByte { get; }

        /// <summary>封装字节(<see cref="HasSealedByte"/> 为假时为 0)。</summary>
        public byte SealedByte { get; }

        /// <summary>这个形状里是否带"清除装扮"u32(只有装备详细记录形有)。</summary>
        public bool HasClearAvatar { get; }

        /// <summary>"清除装扮"u32(<see cref="HasClearAvatar"/> 为假时为 0)。</summary>
        public uint ClearAvatar { get; }

        /// <summary>附魔索引(原始 u32)。</summary>
        public uint EnchantIndex { get; }

        /// <summary>增幅类型字节原值:0 无增幅;0x80 有增幅但未鉴定(服务端 <c>getAbilityType</c> 0x08150732 对未鉴定者返回 0x80);其余是类型号。</summary>
        public byte AmplifyType { get; }

        /// <summary>增幅值(服务端 <c>getAbilityValue</c> 0x08150772:无增幅或未鉴定时恒为 0)。</summary>
        public ushort AmplifyValue { get; }

        /// <summary>是否增幅:类型字节非 0(服务端 <c>hasAbility</c> 0x081507cc)。</summary>
        public bool IsAmplified => AmplifyType != 0;

        /// <summary>
        /// 是否已鉴定:类型字节最高位为 0(服务端 <c>isIdentified</c> 0x081507bc 的口径)。无增幅(0)按此口径也是"已鉴定"——
        /// 它只在 <see cref="IsAmplified"/> 为真时才有意义;0x81 到 0xFF 服务端不会产生,按口径判为未鉴定。
        /// </summary>
        public bool IsIdentified => (AmplifyType & 0x80) == 0;

        /// <summary>是否带两个装扮块(镶嵌块与扩展块)。</summary>
        public bool HasAvatarBlocks { get; }

        /// <summary>30 字节镶嵌块的内容(不带装扮块时为空;只读视图)。内容语义未知。</summary>
        public IReadOnlyList<byte> SocketBlock => Array.AsReadOnly(_socketBlock);

        /// <summary>4 字节扩展块的内容(不带装扮块时为空)。</summary>
        public IReadOnlyList<byte> ExpansionBlock => Array.AsReadOnly(_expansionBlock);

        /// <summary>是否带宠物剩余时长 u32。</summary>
        public bool HasPetRemainTime { get; }

        /// <summary>宠物剩余时长(<see cref="HasPetRemainTime"/> 为假时为 0)。单位未验证。</summary>
        public uint PetRemainTime { get; }

        /// <summary>C3 块各项(个数 0 为常态)。</summary>
        public IReadOnlyList<ItemC3Record> C3Records => Array.AsReadOnly(_c3Records);

        /// <summary>随机选项块的各项(个数 0 到 3)。</summary>
        public IReadOnlyList<ItemRandomOptionField> RandomOptionFields => Array.AsReadOnly(_randomOptionFields);

        /// <summary>随机选项的选择字节(只在个数大于 0 时读;否则为 0)。</summary>
        public byte RandomOptionSelector { get; }

        /// <summary>
        /// 随机选项的尾部标记字节 X(只在个数大于 0 时读;否则为 0):0xFF 表示后面没有尾部;
        /// 其余值后面还有 4 个 u8(客户端 0x007c7eb0、服务端 0x0858e3ec)。
        /// </summary>
        public byte RandomOptionTailMarker { get; }

        /// <summary>随机选项是否带 4 字节尾部(个数大于 0 且 X 不为 0xFF)。</summary>
        public bool HasRandomOptionTail => _randomOptionTail.Length > 0;

        /// <summary>随机选项的 4 字节尾部(没有时为空)。</summary>
        public IReadOnlyList<byte> RandomOptionTail => Array.AsReadOnly(_randomOptionTail);

        /// <summary>单独强化字节(主干倒数第二个字节,服务端 <c>put_packet</c> 0x0858dcab 一带;语义未验证)。</summary>
        public byte SeparateUpgrade { get; }

        /// <summary>交易限制字节(主干最后一个字节,服务端 <c>put_packet</c> 0x0858dccb;语义未验证)。</summary>
        public byte TradeRestriction { get; }

        /// <summary>本条目在线上占的字节数(用于核对"消耗字节数 + 4 + 补零 = 直连帧载荷")。</summary>
        public int WireLength { get; }
    }
}
