using System.IO.Compression;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 角色列表通知(类 0、包号 2,模式 2)测试用的合成数据与手工拼包。名字是合成字节序列(不是可读文本),装备编号取 900 以上的合成值,
/// 其余字段是任意取值;不含任何真实角色名、数值或帧文件内容。
/// 拼包只用 <see cref="NoticePayloadBuilder"/> 显式小端写入,不借助解析器:读写两边才不会同错而互相掩盖。
/// </summary>
internal static class UserInfoTestData
{
    /// <summary>头部默认值:A、B、C 互不相同,D 的四个字节互不相同(查得出字节序与错位)。</summary>
    public const int HeaderA = 0x18;
    public const int HeaderB = 0x06;
    public const int HeaderC = 0x0B;
    public const long HeaderD = 0x0A0B0C0D;

    /// <summary>一条装备记录的合成值(拼包用);默认值各字段互不相同,四字节整数的四个字节也互不相同。</summary>
    internal sealed class EquipSpec
    {
        public int Slot = 3;
        public long ItemId = 0x00010385;
        public long BlobLength = 4;
        public byte[] Blob = { 0xB1, 0xB2, 0xB3, 0xB4 };
        public int Packed = 0x2D;
        public long ClearAvatar = 0x13572468;
        public long Extra = 0x2468ACE0;
    }

    /// <summary>一个条目的合成值(拼包用);默认值里 13 个单字节字段互不相同,换位或错位一定看得出。</summary>
    internal sealed class EntrySpec
    {
        public int Uid = 0x0201;
        public byte[] Name = { 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 };

        /// <summary>null 表示按 Name 的真实长度写前缀;非 null 表示故意谎报的前缀值。</summary>
        public long? NameLengthPrefix;

        public int X1 = 0x21;
        public int Job = 0x32;
        public int GrowType = 0x25;
        public int Level = 0x3D;
        public int D = 0x44;
        public int Status = 0x04;
        public List<EquipSpec> Equips = new List<EquipSpec>();

        /// <summary>null 表示按 Equips 的真实条数写;非 null 表示故意谎报的条数。</summary>
        public int? EquipCountPrefix;

        public int G = 0x57;
        public int H = 0x68;
        public long I = 0x11223344;
        public int J = 0x01;
        public int Premium = 0x02;
        public long K = 0x55667788;
        public int A2 = 0x7A;
        public int B2 = 0x7B;
        public int C2 = 0x7C;
        public long D2 = 0x99AABBCC;
    }

    /// <summary>确定性伪随机(线性同余,固定种子):测试结果可复现。</summary>
    internal sealed class Lcg
    {
        private uint _state;

        public Lcg(uint seed)
        {
            _state = seed;
        }

        public uint NextUInt()
        {
            _state = (_state * 1664525u) + 1013904223u;
            return _state;
        }

        /// <summary>[0, maxExclusive)。</summary>
        public int Next(int maxExclusive)
        {
            return (int)((NextUInt() >> 8) % (uint)maxExclusive);
        }

        public byte NextByte()
        {
            return (byte)(NextUInt() >> 24);
        }

        public byte[] Bytes(int count)
        {
            byte[] bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                bytes[i] = NextByte();
            }
            return bytes;
        }
    }

    /// <summary>测试里所有装备编号的下限:编号一律是 900 以上的合成值。</summary>
    public const long MinimumSyntheticItemId = 900;

    /// <summary>把一个任意的 u32 取值变成合法的合成装备编号:不足 900 的整体抬高 900,其余原样。</summary>
    public static long ItemIdFor(long value)
    {
        return value < MinimumSyntheticItemId ? value + MinimumSyntheticItemId : value;
    }

    /// <summary>合成名字:长度 length、内容是一串与 seed 有关的、不成文本的字节。</summary>
    public static byte[] SyntheticName(int seed, int length)
    {
        byte[] name = new byte[length];
        for (int i = 0; i < length; i++)
        {
            name[i] = (byte)(0x80 + (seed * 0x11) + i);
        }
        return name;
    }

    /// <summary>第 index 条合成装备记录:各字段随 index 变化,编号恒 ≥ 900。</summary>
    public static EquipSpec StandardEquip(int index)
    {
        return new EquipSpec
        {
            Slot = (3 + (7 * index)) & 0xFF,
            ItemId = 0x00010385 + (index * 0x101),
            Blob = new[] { (byte)(0xB1 + index), (byte)(0xB2 + index), (byte)(0xB3 + index), (byte)(0xB4 + index) },
            Packed = (0x2D + index) & 0xFF,
            ClearAvatar = 0x13572468 + index,
            Extra = 0x2468ACE0 + index,
        };
    }

    /// <summary>第 index 条合成角色条目:单字节字段都在默认值上加 index(模 256),名字长度与装备条数另给。</summary>
    public static EntrySpec Standard(int index, int nameLength = 5, int equipCount = 0)
    {
        var e = new EntrySpec
        {
            Uid = (0x0201 + (index * 0x0107)) & 0xFFFF,
            Name = SyntheticName(index, nameLength),
            X1 = (0x21 + index) & 0xFF,
            Job = (0x32 + index) & 0xFF,
            GrowType = (0x25 + index) & 0xFF,
            Level = (0x3D + index) & 0xFF,
            D = (0x44 + index) & 0xFF,
            Status = (0x04 + index) & 0xFF,
            G = (0x57 + index) & 0xFF,
            H = (0x68 + index) & 0xFF,
            I = 0x11223344 + index,
            J = (0x01 + index) & 0xFF,
            Premium = (0x02 + index) & 0xFF,
            K = 0x55667788 + index,
            A2 = (0x7A + index) & 0xFF,
            B2 = (0x7B + index) & 0xFF,
            C2 = (0x7C + index) & 0xFF,
            D2 = 0x99AABBCC + index,
        };
        for (int i = 0; i < equipCount; i++)
        {
            e.Equips.Add(StandardEquip((index * 5) + i));
        }
        return e;
    }

    /// <summary>全字段随机的条目(名字 0 到 40 字节,装备 0 到 4 条;blobLen 恒为 4)。</summary>
    public static EntrySpec RandomEntry(Lcg r)
    {
        var e = new EntrySpec
        {
            Uid = r.Next(65536),
            Name = r.Bytes(r.Next(41)),
            X1 = r.NextByte(),
            Job = r.NextByte(),
            GrowType = r.NextByte(),
            Level = r.NextByte(),
            D = r.NextByte(),
            Status = r.NextByte(),
            G = r.NextByte(),
            H = r.NextByte(),
            I = r.NextUInt(),
            J = r.NextByte(),
            Premium = r.NextByte(),
            K = r.NextUInt(),
            A2 = r.NextByte(),
            B2 = r.NextByte(),
            C2 = r.NextByte(),
            D2 = r.NextUInt(),
        };
        int equipCount = r.Next(5);
        for (int i = 0; i < equipCount; i++)
        {
            e.Equips.Add(new EquipSpec
            {
                Slot = r.NextByte(),
                ItemId = MinimumSyntheticItemId + (r.NextUInt() % (0xFFFFFFFFL - MinimumSyntheticItemId)),
                Blob = r.Bytes(4),
                Packed = r.NextByte(),
                ClearAvatar = r.NextUInt(),
                Extra = r.NextUInt(),
            });
        }
        return e;
    }

    /// <summary>条目的线上字节。</summary>
    public static byte[] Entry(EntrySpec e)
    {
        var b = new NoticePayloadBuilder();
        b.U16(e.Uid);
        b.U32(e.NameLengthPrefix ?? e.Name.Length);
        b.Raw(e.Name);
        b.U8(e.X1).U8(e.Job).U8(e.GrowType).U8(e.Level).U8(e.D).U8(e.Status);
        b.U8(e.EquipCountPrefix ?? e.Equips.Count);
        foreach (EquipSpec q in e.Equips)
        {
            b.U8(q.Slot).U32(q.ItemId).U32(q.BlobLength).Raw(q.Blob).U8(q.Packed).U32(q.ClearAvatar).U32(q.Extra);
        }
        b.U8(e.G).U8(e.H).U32(e.I).U8(e.J).U8(e.Premium).U32(e.K).U8(e.A2).U8(e.B2).U8(e.C2).U32(e.D2);
        return b.ToArray();
    }

    /// <summary>头 10 字节:u8 mode、u8 a、u8 b、u8 c、u32 d、u16 count。</summary>
    public static byte[] Header(int mode, int a, int b, int c, long d, int count)
    {
        return new NoticePayloadBuilder().U8(mode).U8(a).U8(b).U8(c).U32(d).U16(count).ToArray();
    }

    /// <summary>模式 2 整包(默认头部值),条数按给的条目数写。</summary>
    public static byte[] Packet(params EntrySpec[] entries)
    {
        return Packet(HeaderA, HeaderB, HeaderC, HeaderD, entries);
    }

    /// <summary>模式 2 整包,头部值自定,条数按给的条目数写。</summary>
    public static byte[] Packet(int a, int b, int c, long d, params EntrySpec[] entries)
    {
        return PacketWithCount(a, b, c, d, entries.Length, entries);
    }

    /// <summary>模式 2 整包,头部值自定,count 前缀单独给(可以谎报)。</summary>
    public static byte[] PacketWithCount(int a, int b, int c, long d, int count, params EntrySpec[] entries)
    {
        var bytes = new List<byte>(Header(2, a, b, c, d, count));
        foreach (EntrySpec e in entries)
        {
            bytes.AddRange(Entry(e));
        }
        return bytes.ToArray();
    }

    /// <summary>线上长度公式:条目 = 32 + 名字字节数 + 22 × 装备条数。</summary>
    public static int EntryLength(int nameLength, int equipCount)
    {
        return 32 + nameLength + (22 * equipCount);
    }

    /// <summary>把整包拼上尾部字节。</summary>
    public static byte[] WithTail(byte[] payload, params byte[] tail)
    {
        byte[] all = new byte[payload.Length + tail.Length];
        payload.CopyTo(all, 0);
        tail.CopyTo(all, payload.Length);
        return all;
    }

    /// <summary>标准 zlib 流(头 0x78 起)。</summary>
    public static byte[] Zlib(byte[] data)
    {
        if (data.Length == 0)
        {
            // .NET 的 ZLibStream 一个字节都没写时什么也不输出(连头都没有),解码器会判为解压失败;
            // 这里手工给出合法的"空内容"zlib 流:头 0x78 0x9C、固定哈夫曼的空终止块、Adler-32 = 1
            return new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(data, 0, data.Length);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// 把明文载荷当成线上的一帧(类 cls、包号 2,载荷经 zlib 压缩,不加密的替身密码)走真实的 <see cref="ServerFrameDecoder"/> 解码,
    /// 返回解码帧:包号 2 是压缩包号,解码器在解密之后把它解压,所以帧里的载荷就是传进来的明文。
    /// </summary>
    public static DecodedFrame DecodedFrameOf(byte[] plaintext, byte cls = 0)
    {
        byte[] body = Zlib(plaintext);
        uint crc = Crc32N.Compute(body);
        byte[] wire = new byte[15 + body.Length];
        wire[0] = cls;
        wire[1] = 2;
        wire[2] = 0;
        uint length = (uint)wire.Length;
        for (int i = 0; i < 4; i++)
        {
            wire[3 + i] = (byte)(length >> (8 * i));
            wire[7 + i] = (byte)(crc >> (8 * i));
            wire[11 + i] = (byte)(crc >> (8 * i));
        }
        body.CopyTo(wire, 15);

        var decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame frame));
        Assert.True(frame.WasCompressed);
        Assert.Equal(plaintext, frame.Payload);
        return frame;
    }

    /// <summary>断言解析出的条目与拼包时的合成值逐字段一致(含装备、名字、拆好的成长类型)。</summary>
    public static void AssertEntry(EntrySpec spec, UserInfoEntry actual)
    {
        Assert.Equal(spec.Uid, actual.Uid);
        Assert.Equal(spec.Name, actual.NameBytes.ToArray());
        Assert.Equal(spec.X1, actual.X1);
        Assert.Equal(spec.Job, actual.Job);
        Assert.Equal(spec.GrowType, actual.GrowTypeByte);
        Assert.Equal(spec.GrowType & 0x0F, actual.FirstGrowType);
        Assert.Equal((spec.GrowType >> 4) & 0x07, actual.SecondGrowType);
        Assert.Equal(spec.Level, actual.Level);
        Assert.Equal(spec.D, actual.D);
        Assert.Equal(spec.Status, actual.Status);
        Assert.Equal(spec.Equips.Count, actual.Equips.Count);
        for (int i = 0; i < spec.Equips.Count; i++)
        {
            EquipSpec q = spec.Equips[i];
            UserInfoEquip equip = actual.Equips[i];
            Assert.Equal(q.Slot, equip.Slot);
            Assert.Equal(q.ItemId, (long)equip.ItemId);
            Assert.Equal(LittleEndian32(q.Blob), (long)equip.Blob);
            Assert.Equal(q.Packed, equip.Packed);
            Assert.Equal(q.ClearAvatar, (long)equip.ClearAvatar);
            Assert.Equal(q.Extra, (long)equip.Extra);
        }
        Assert.Equal(spec.G, actual.G);
        Assert.Equal(spec.H, actual.H);
        Assert.Equal(spec.I, (long)actual.I);
        Assert.Equal(spec.J, actual.J);
        Assert.Equal(spec.Premium, actual.Premium);
        Assert.Equal(spec.K, (long)actual.K);
        Assert.Equal(spec.A2, actual.A2);
        Assert.Equal(spec.B2, actual.B2);
        Assert.Equal(spec.C2, actual.C2);
        Assert.Equal(spec.D2, (long)actual.D2);
    }

    /// <summary>4 个原始字节按小端读成整数(预期值用,显式移位)。</summary>
    public static long LittleEndian32(byte[] bytes)
    {
        return bytes[0] | ((long)bytes[1] << 8) | ((long)bytes[2] << 16) | ((long)bytes[3] << 24);
    }

    /// <summary>
    /// 把解析出的记录按读序重新拼回字节(只用公共属性,拼法独立于解析器)。
    /// 往返性质:任何解析成功的载荷,<c>Serialize(记录)</c> 必须恰好等于载荷去掉 <see cref="UserInfoNotice.TrailingByteCount"/> 个尾字节后的前缀。
    /// </summary>
    public static byte[] Serialize(UserInfoNotice n)
    {
        var b = new NoticePayloadBuilder().U8(2).U8(n.A).U8(n.B).U8(n.C).U32(n.D).U16(n.Entries.Count);
        foreach (UserInfoEntry e in n.Entries)
        {
            b.U16(e.Uid).U32(e.NameBytes.Count).Raw(e.NameBytes.ToArray());
            b.U8(e.X1).U8(e.Job).U8(e.GrowTypeByte).U8(e.Level).U8(e.D).U8(e.Status);
            b.U8(e.Equips.Count);
            foreach (UserInfoEquip q in e.Equips)
            {
                b.U8(q.Slot).U32(q.ItemId).U32(4).U32(q.Blob).U8(q.Packed).U32(q.ClearAvatar).U32(q.Extra);
            }
            b.U8(e.G).U8(e.H).U32(e.I).U8(e.J).U8(e.Premium).U32(e.K).U8(e.A2).U8(e.B2).U8(e.C2).U32(e.D2);
        }
        return b.ToArray();
    }
}
