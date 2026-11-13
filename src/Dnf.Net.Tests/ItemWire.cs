namespace Dnf.Net.Tests;

/// <summary>
/// 物品类包的合成夹具(手工拼字节,独立于产品代码):物品条目、货币伪条目、各空间的包头。
/// 取值全是编造的:合成物品号从 900001 起、合成唯一号从 9001 起,不含任何真实物品号、名字或数值。
/// 字段顺序写成字面量,与产品读序分别独立书写,读写两边同错时测试会失败。
/// </summary>
internal static class ItemWire
{
    /// <summary>合成物品号(不是任何真实物品)。</summary>
    public const long ItemA = 900001;

    /// <summary>合成物品号。</summary>
    public const long ItemB = 900002;

    /// <summary>合成物品号。</summary>
    public const long ItemC = 900003;

    /// <summary>增量包里"清空该槽"的物品号。</summary>
    public const long DeleteMarker = 0xFFFFFFFFL;

    /// <summary>一条合成条目的取值;字段是否写出由形状决定,可选块由非 null 决定。</summary>
    internal sealed class ItemSpec
    {
        public int Slot;
        public long ItemId = ItemA;
        public long Data;
        public int Ext1;
        public int Durability = 0;
        public int Sealed;
        public long ClearAvatar = 0;
        public long Enchant = 0;
        public int AmpType;
        public int AmpValue;

        /// <summary>非 null:写 u32 30 + 这 30 字节(装扮镶嵌块)。</summary>
        public byte[] SocketBlock;

        /// <summary>非 null:写 u32 4 + 这 4 字节(装扮扩展块)。</summary>
        public byte[] ExpansionBlock;

        /// <summary>非 null:写这个 u32(宠物剩余时长)。</summary>
        public long? PetTime;

        /// <summary>C3 块各项:u32 + 四个 u8。</summary>
        public (long Value, int B1, int B2, int B3, int B4)[] C3 = new (long, int, int, int, int)[0];

        /// <summary>随机选项各项:三个 u8。个数 0 到 3。</summary>
        public (int A, int B, int C)[] RoFields = new (int, int, int)[0];

        public int RoSelector;

        /// <summary>随机选项尾部标记 X;0xFF 表示没有尾部。个数为 0 时不写。</summary>
        public int RoTailMarker = 0xFF;

        /// <summary>X 不为 0xFF 时写的 4 个字节。</summary>
        public byte[] RoTail = new byte[4];

        public int Separate;
        public int Trade;
    }

    /// <summary>30 字节的合成镶嵌块:每个字节 = 起始值 + 下标。</summary>
    public static byte[] Socket(int start = 0x40)
    {
        byte[] b = new byte[30];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)(start + i);
        }
        return b;
    }

    /// <summary>4 字节的合成扩展块。</summary>
    public static byte[] Expansion(int start = 0xA0)
    {
        return new[] { (byte)start, (byte)(start + 1), (byte)(start + 2), (byte)(start + 3) };
    }

    /// <summary>空条目规格:只给槽号与物品号,其余为零。</summary>
    public static ItemSpec Item(int slot, long itemId = ItemA)
    {
        return new ItemSpec { Slot = slot, ItemId = itemId };
    }

    /// <summary>货币伪条目(槽号 = 物品号 = 0、1、2;金额放在 Data 位;其余字段全零)。</summary>
    public static ItemSpec Pseudo(int slot, long amount)
    {
        return new ItemSpec { Slot = slot, ItemId = slot, Data = amount };
    }

    /// <summary>
    /// 背包 / 仓库 / 装扮背包 / 宠物栏 / 身上装备形条目:u16 槽号 + 物品号 + Data + ext1 + 耐久 + 封装 + 附魔 + 增幅类型 + 增幅值
    /// + [装扮块] + [宠物 u32] + C3 + 随机选项 + 单独强化 + 交易限制。
    /// </summary>
    public static NoticePayloadBuilder AppendBagEntry(this NoticePayloadBuilder b, ItemSpec s)
    {
        b.U16(s.Slot).U32(s.ItemId).U32(s.Data).U8(s.Ext1).U16(s.Durability).U8(s.Sealed)
            .U32(s.Enchant).U8(s.AmpType).U16(s.AmpValue);
        AppendOptionalAndTail(b, s);
        return b;
    }

    /// <summary>
    /// 装备详细记录形条目:u8 槽号 + 物品号 + Data + ext1 + 耐久 + 清除装扮 + 附魔 + 增幅类型 + 增幅值(没有封装字节)
    /// + [装扮块] + [宠物 u32] + C3 + 随机选项 + 单独强化 + 交易限制。
    /// </summary>
    public static NoticePayloadBuilder AppendDetailEntry(this NoticePayloadBuilder b, ItemSpec s)
    {
        b.U8(s.Slot).U32(s.ItemId).U32(s.Data).U8(s.Ext1).U16(s.Durability).U32(s.ClearAvatar)
            .U32(s.Enchant).U8(s.AmpType).U16(s.AmpValue);
        AppendOptionalAndTail(b, s);
        return b;
    }

    private static void AppendOptionalAndTail(NoticePayloadBuilder b, ItemSpec s)
    {
        if (s.SocketBlock != null)
        {
            b.U32(30).Raw(s.SocketBlock);
        }
        if (s.ExpansionBlock != null)
        {
            b.U32(4).Raw(s.ExpansionBlock);
        }
        if (s.PetTime.HasValue)
        {
            b.U32(s.PetTime.Value);
        }
        b.U8(s.C3.Length);
        foreach ((long value, int b1, int b2, int b3, int b4) in s.C3)
        {
            b.U32(value).U8(b1).U8(b2).U8(b3).U8(b4);
        }
        b.U8(s.RoFields.Length);
        foreach ((int a, int bb, int c) in s.RoFields)
        {
            b.U8(a).U8(bb).U8(c);
        }
        if (s.RoFields.Length > 0)
        {
            b.U8(s.RoSelector).U8(s.RoTailMarker);
            if (s.RoTailMarker != 0xFF)
            {
                b.Raw(s.RoTail);
            }
        }
        b.U8(s.Separate).U8(s.Trade);
    }

    /// <summary>单个背包形条目的字节(最小 25 字节)。</summary>
    public static byte[] BagEntryBytes(ItemSpec s)
    {
        return new NoticePayloadBuilder().AppendBagEntry(s).ToArray();
    }

    /// <summary>单个装备详细记录的字节(最小 27 字节)。</summary>
    public static byte[] DetailEntryBytes(ItemSpec s)
    {
        return new NoticePayloadBuilder().AppendDetailEntry(s).ToArray();
    }

    /// <summary>
    /// 空间 0(背包)的物品空间包:u8 0 + u16 容量 + u16 个数(= 3 + 物品数)+ 三个货币伪条目 + 各物品条目。
    /// </summary>
    public static byte[] BagSpacePacket(int capacity, long gold, long coin, long winPoint, params ItemSpec[] items)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(0).U16(capacity).U16(3 + items.Length);
        b.AppendBagEntry(Pseudo(0, gold)).AppendBagEntry(Pseudo(1, coin)).AppendBagEntry(Pseudo(2, winPoint));
        foreach (ItemSpec item in items)
        {
            b.AppendBagEntry(item);
        }
        return b.ToArray();
    }

    /// <summary>空间 2(仓库)的物品空间包:u8 2 + u16 容量 + u16 个数 + 条目。</summary>
    public static byte[] CargoSpacePacket(int capacity, params ItemSpec[] items)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(2).U16(capacity).U16(items.Length);
        foreach (ItemSpec item in items)
        {
            b.AppendBagEntry(item);
        }
        return b.ToArray();
    }

    /// <summary>空间 1(装扮背包)的物品空间包:u8 1 + u16 个数 + 条目(每条都带两个固定块)。</summary>
    public static byte[] AvatarSpacePacket(params ItemSpec[] items)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(1).U16(items.Length);
        foreach (ItemSpec item in items)
        {
            b.AppendBagEntry(item);
        }
        return b.ToArray();
    }

    /// <summary>空间 7(宠物栏)的物品空间包:u8 7 + u16 个数 + 条目。</summary>
    public static byte[] PetSpacePacket(params ItemSpec[] items)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(7).U16(items.Length);
        foreach (ItemSpec item in items)
        {
            b.AppendBagEntry(item);
        }
        return b.ToArray();
    }

    /// <summary>空间 0xC(账号仓库)的物品空间包:u8 0xC + u16 + u32 + u16 个数 + 条目。</summary>
    public static byte[] AccountCargoSpacePacket(int word, long dword, params ItemSpec[] items)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(0x0C).U16(word).U32(dword).U16(items.Length);
        foreach (ItemSpec item in items)
        {
            b.AppendBagEntry(item);
        }
        return b.ToArray();
    }

    /// <summary>物品增量包:u8 空间 + u16 个数 + 条目。</summary>
    public static byte[] UpdatePacket(int space, params ItemSpec[] items)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(space).U16(items.Length);
        foreach (ItemSpec item in items)
        {
            b.AppendBagEntry(item);
        }
        return b.ToArray();
    }

    /// <summary>
    /// 紧凑装备记录(22 字节):u8 槽号 + 物品号 + u32 长度 4 + 4 字节扩展信息 + u8 packed + 清除装扮 + 附魔索引。
    /// </summary>
    public static NoticePayloadBuilder AppendCompact(this NoticePayloadBuilder b, int slot, long itemId, long expansion = 0, int packed = 0, long clearAvatar = 0, long enchant = 0)
    {
        return b.U8(slot).U32(itemId).U32(4).U32(expansion).U8(packed).U32(clearAvatar).U32(enchant);
    }

    /// <summary>紧凑装备列表:u8 个数 + 记录;<paramref name="trailing"/> 非 null 时再写列表之后的尾字节。</summary>
    public static NoticePayloadBuilder AppendCompactList(this NoticePayloadBuilder b, int? trailing, params (int Slot, long ItemId, int Packed)[] records)
    {
        b.U8(records.Length);
        foreach ((int slot, long itemId, int packed) in records)
        {
            b.AppendCompact(slot, itemId, 0, packed);
        }
        if (trailing.HasValue)
        {
            b.U8(trailing.Value);
        }
        return b;
    }

    /// <summary>详细装备列表:u8 个数 + 详细记录。</summary>
    public static NoticePayloadBuilder AppendDetailList(this NoticePayloadBuilder b, params ItemSpec[] entries)
    {
        b.U8(entries.Length);
        foreach (ItemSpec e in entries)
        {
            b.AppendDetailEntry(e);
        }
        return b;
    }

    /// <summary>
    /// 用户信息公共视图(模式 0):u8 0 + u16 个数 + u16 uid + u32 名字长度 + 名字 + 6 个 u8 + 紧凑装备列表 + 尾字节 + 非物品尾巴。
    /// </summary>
    public static byte[] PublicViewPacket(int count, int uid, byte[] name, byte[] header6, int trailing, byte[] tail, params (int Slot, long ItemId, int Packed)[] records)
    {
        return new NoticePayloadBuilder().U8(0).U16(count).U16(uid).U32(name.Length).Raw(name).Raw(header6)
            .AppendCompactList(trailing, records).Raw(tail).ToArray();
    }

    /// <summary>
    /// 用户信息本人详细视图(模式 1):u8 1 + u16 个数 + u16 X + u32 Y + u32 统计块长度 + 统计块 + u8 + 详细装备列表 + 非物品尾巴。
    /// </summary>
    public static byte[] SelfViewPacket(int count, int x, long y, byte[] stat, int selfByte, byte[] tail, params ItemSpec[] entries)
    {
        return new NoticePayloadBuilder().U8(1).U16(count).U16(x).U32(y).U32(stat.Length).Raw(stat).U8(selfByte)
            .AppendDetailList(entries).Raw(tail).ToArray();
    }

    /// <summary>在包体后追加 4 字节 PI 尾,再补零到块长的整数倍(直连帧载荷的形态,笔记 §1.1 注)。</summary>
    public static byte[] DirectFramePayload(byte[] body, int packetId)
    {
        int block = Dnf.Net.Crypto.CipherSlots.BlockSizes[Dnf.Net.Crypto.CipherSlots.SlotForPacket(packetId)];
        int padded = (body.Length + 4 + block - 1) / block * block;
        byte[] payload = new byte[padded];
        body.CopyTo(payload, 0);
        // 4 字节 PI 尾:取合成的非零值(真实帧是折叠哈希;解析器不核对)。
        payload[body.Length] = 0x5A;
        payload[body.Length + 1] = 0x3C;
        payload[body.Length + 2] = 0x96;
        payload[body.Length + 3] = 0xE1;
        return payload;
    }

    /// <summary>在包体后追加 4 字节 PI 尾(信封内层帧的形态,不补零)。</summary>
    public static byte[] InnerFramePayload(byte[] body)
    {
        byte[] payload = new byte[body.Length + 4];
        body.CopyTo(payload, 0);
        payload[body.Length] = 0x5A;
        payload[body.Length + 1] = 0x3C;
        payload[body.Length + 2] = 0x96;
        payload[body.Length + 3] = 0xE1;
        return payload;
    }

    /// <summary>类号:通知。</summary>
    public const byte ClassNoti = 0;

    /// <summary>类号:命令(应答)。</summary>
    public const byte ClassCmd = 1;

    /// <summary>
    /// 手工拼一个服务端整帧(15 字节头 + 载荷,两个校验值都是载荷的 crc32N)并走真实的 <see cref="Dnf.Net.Frame.ServerFrameDecoder"/>
    /// (不加密的替身密码)解码,返回解码帧。载荷原样进帧,所以解码帧的 Payload 与传入的字节逐字节相同。
    /// 别用包号 1、2、9、87(启动包、压缩包号)与类 1 的 21 字节包号 1(拒绝帧),它们走特殊解码路径。
    /// </summary>
    public static Dnf.Net.Frame.DecodedFrame FrameOf(byte cls, int packetId, byte[] payload)
    {
        uint crc = Dnf.Net.Frame.Crc32N.Compute(payload);
        byte[] wire = new byte[15 + payload.Length];
        wire[0] = cls;
        wire[1] = (byte)packetId;
        wire[2] = (byte)(packetId >> 8);
        BitConverter.GetBytes((uint)wire.Length).CopyTo(wire, 3);
        BitConverter.GetBytes(crc).CopyTo(wire, 7);
        BitConverter.GetBytes(crc).CopyTo(wire, 11);
        payload.CopyTo(wire, 15);
        Dnf.Net.Frame.ServerFrameDecoder decoder = new Dnf.Net.Frame.ServerFrameDecoder { Cipher = new Dnf.Net.Frame.NullPayloadCipher() };
        Assert.Equal(Dnf.Net.Frame.FrameStatus.Ok, decoder.Decode(wire, out Dnf.Net.Frame.DecodedFrame frame));
        return frame;
    }

    /// <summary>字典式的物品类型判定替身:字典里没有的物品号回答 Unknown。</summary>
    internal sealed class DictionaryKindResolver : Dnf.Net.Session.IItemKindResolver
    {
        private readonly Dictionary<uint, Dnf.Net.Session.ItemKind> _kinds = new Dictionary<uint, Dnf.Net.Session.ItemKind>();

        /// <summary>被问过的物品号个数(含重复)。</summary>
        public int CallCount { get; private set; }

        public DictionaryKindResolver With(long itemId, Dnf.Net.Session.ItemKind kind)
        {
            _kinds[(uint)itemId] = kind;
            return this;
        }

        public Dnf.Net.Session.ItemKind Resolve(uint itemId)
        {
            CallCount++;
            return _kinds.TryGetValue(itemId, out Dnf.Net.Session.ItemKind kind) ? kind : Dnf.Net.Session.ItemKind.Unknown;
        }
    }
}
