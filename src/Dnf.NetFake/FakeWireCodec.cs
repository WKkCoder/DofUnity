using System.IO.Compression;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;

namespace Dnf.NetFake;

/// <summary>合成角色定义(仅用于本地回环假服,不含真实角色数据)。</summary>
public sealed class FakeCharacterDef
{
    public ushort Uid { get; set; } = 901;
    public string Name { get; set; } = "Hero901";
    public byte Job { get; set; } = 0;
    public byte GrowTypeByte { get; set; } = 0;
    public byte Level { get; set; } = 15;
    public byte Status { get; set; } = 0;
}

/// <summary>假游戏服收到的一条客户端命令的脱敏记录。</summary>
public sealed class FakeReceivedCommand
{
    public FakeReceivedCommand(PacketClass packetClass, ushort packetId, int payloadLength, byte[] payload)
    {
        PacketClass = packetClass;
        PacketId = packetId;
        PayloadLength = payloadLength;
        Payload = payload ?? Array.Empty<byte>();
    }

    public PacketClass PacketClass { get; }
    public ushort PacketId { get; }
    public int PayloadLength { get; }
    public byte[] Payload { get; }
}

/// <summary>假服务端按真实协议规格独立拼帧与解析客户端帧的编解码助手(全走真 14 槽 SessionCipher / 真 AntibotCipher / 真 zlib)。</summary>
public static class FakeWireCodec
{
    public static byte[] MakeSyntheticKeyBlock(byte seed = 0x31)
    {
        byte[] block = new byte[ChannelInfo.KeyBlockLength];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = unchecked((byte)(seed + (i * 7)));
        }
        return block;
    }

    public static byte[] MakeSyntheticAesKey(byte seed = 77)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = unchecked((byte)(seed + (i * 11)));
        }
        return key;
    }

    public static string HexLower(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    /// <summary>15 字节服务端到客户端帧头(类、包号、总长、双 CRC32N)。</summary>
    public static byte[] BuildServerFrame(byte packetClass, ushort packetId, byte[] body)
    {
        byte[] data = body ?? Array.Empty<byte>();
        int totalLen = 15 + data.Length;
        uint crc = Crc32N.Compute(data);
        byte[] frame = new byte[totalLen];
        frame[0] = packetClass;
        WriteU16Le(frame, 1, packetId);
        WriteU32Le(frame, 3, (uint)totalLen);
        WriteU32Le(frame, 7, crc);
        WriteU32Le(frame, 11, crc);
        if (data.Length > 0)
        {
            Buffer.BlockCopy(data, 0, frame, 15, data.Length);
        }
        return frame;
    }

    /// <summary>走真实 14 槽 SessionCipher(且对 2/9/0x57 先做 zlib 压缩)编码服务端帧。</summary>
    public static byte[] BuildEncryptedServerFrame(SessionCipher cipher, byte packetClass, ushort packetId, byte[] plainPayload)
    {
        byte[] payload = plainPayload ?? Array.Empty<byte>();
        bool compressed = packetId == 2 || packetId == 9 || packetId == 0x57;
        byte[] preEncrypt = compressed ? ZlibCompress(payload) : payload;
        byte[] encrypted = cipher.Encrypt(packetId, preEncrypt);
        return BuildServerFrame(packetClass, packetId, encrypted);
    }

    /// <summary>用真实 AntibotCipher 封装一条 0/561(0x231) 加密信封外层明文帧。</summary>
    public static byte[] BuildEnvelope561Frame(byte innerClass, ushort innerPacketId, byte[] innerPlainPayload, uint seq, int algo = 19)
    {
        byte[] innerBody = innerPlainPayload ?? Array.Empty<byte>();
        bool compressed = innerPacketId == 2 || innerPacketId == 9 || innerPacketId == 0x57;
        byte[] bodyBytes = compressed ? ZlibCompress(innerBody) : innerBody;

        byte[] innerPacket = BuildServerFrame(innerClass, innerPacketId, bodyBytes);

        // 9 字节算法包头(dataLength=0) + 内层包
        byte[] algoPacket = new byte[9];
        for (int i = 0; i < 8; i++)
        {
            algoPacket[i] = unchecked((byte)(0xA0 + (i * 3)));
        }
        algoPacket[8] = 0;

        byte[] plain = new byte[algoPacket.Length + innerPacket.Length];
        Buffer.BlockCopy(algoPacket, 0, plain, 0, algoPacket.Length);
        Buffer.BlockCopy(innerPacket, 0, plain, algoPacket.Length, innerPacket.Length);

        byte[] cipher = AntibotCipher.Encrypt(algo, seq, plain);
        uint crc = ComputeIeeeCrc32(plain);
        int total = 17 + cipher.Length;
        byte[] envelopePayload = new byte[21 + cipher.Length];
        WriteU32Be(envelopePayload, 0, (uint)(envelopePayload.Length - 4));
        envelopePayload[4] = (byte)(total >> 8);
        envelopePayload[5] = (byte)total;
        envelopePayload[6] = 0;
        envelopePayload[7] = 17;
        envelopePayload[8] = 0x5A;
        WriteU32Be(envelopePayload, 9, seq);
        WriteU32Be(envelopePayload, 13, (uint)algo);
        WriteU32Be(envelopePayload, 17, crc);
        Buffer.BlockCopy(cipher, 0, envelopePayload, 21, cipher.Length);

        return BuildServerFrame(0, (ushort)AntibotEnvelope.PacketId, envelopePayload);
    }

    public static byte[] BuildBootstrapFrame(byte[] keyBlock, string channelName = "syn01")
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(channelName ?? "syn01");
        byte[] plain = new NoticePayloadBuilder()
            .Raw(keyBlock)
            .I32(nameBytes.Length).Raw(nameBytes)
            .I32(0).I32(1)
            .U8(0).U8(0).U8(0)
            .I32(1000).I32(0)
            .I32(100).I32(100)
            .ToArray();
        return BuildServerFrame(0, 1, BootstrapTransform.Encode(plain));
    }

    public static byte[] BuildLoginOk(string name = "n900", int accountId = 905)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[5 + nameBytes.Length + 8 + 4 + 7];
        payload[0] = 1;
        WriteI32Le(payload, 1, nameBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, payload, 5, nameBytes.Length);
        WriteI32Le(payload, 5 + nameBytes.Length + 8, accountId);
        return payload;
    }

    public static byte[] BuildLoginRefused(byte code = 2)
    {
        return new byte[] { 0, code };
    }

    public static byte[] BuildUserInfoMode2(IReadOnlyList<FakeCharacterDef> chars)
    {
        var b = new NoticePayloadBuilder()
            .U8(UserInfoParser.CharacterListMode)
            .U8(0x18).U8(0x06).U8(0x0B).U32(0x0A0B0C0Du)
            .U16(chars.Count);
        for (int i = 0; i < chars.Count; i++)
        {
            FakeCharacterDef c = chars[i];
            byte[] nameBytes = Encoding.UTF8.GetBytes(c.Name ?? ("Hero" + (900 + i)));
            b.U16(c.Uid)
                .U32((uint)nameBytes.Length).Raw(nameBytes)
                .U8(0).U8(c.Job).U8(c.GrowTypeByte).U8(c.Level).U8(0x44).U8(c.Status)
                .U8(0) // equips count
                .U8(0x57).U8(0x68).U32(0x11223344u).U8(0x01).U8(0x02).U32(0x55667788u)
                .U8(0x7A).U8(0x7B).U8(0x7C).U32(0u);
        }
        return b.ToArray();
    }

    public static byte[] BuildSelectCharacterOk(ushort uniqueId, byte village = 1)
    {
        var b = new NoticePayloadBuilder()
            .U8(1)
            .U32(9000001u).U32(9000002u)
            .U16(uniqueId).U16(156).U16(156).U16(0)
            .U8(0) // premiums
            .I32(0); // cera
        for (int i = 0; i < 20; i++)
        {
            b.U16(0xFFFF).U32(0u);
        }
        for (int i = 0; i < 5; i++)
        {
            b.U32(0u);
        }
        b.U8(village).U8(1).U8(0).U16(0).U16(0).U8(0);
        return b.ToArray();
    }

    public static byte[] BuildSelectCharacterFailure(byte code = 1)
    {
        return new NoticePayloadBuilder().U8(0).U8(code).ToArray();
    }

    public static byte[] BuildDungeonPermissions(IReadOnlyList<(ushort DungeonId, byte Tier)> entries)
    {
        var b = new NoticePayloadBuilder().U16(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            b.U16(entries[i].DungeonId).U8(entries[i].Tier);
        }
        return b.ToArray();
    }

    public static byte[] BuildBagSpaceWithCurrency(uint gold, uint coin = 10, uint winPoint = 0)
    {
        var b = new NoticePayloadBuilder()
            .U8((byte)ItemSpace.Bag)
            .U16(32)
            .U16(3);
        AppendPseudoBagItem(b, ItemCurrency.GoldSlot, gold);
        AppendPseudoBagItem(b, ItemCurrency.CoinTotalSlot, coin);
        AppendPseudoBagItem(b, ItemCurrency.WinPointSlot, winPoint);
        return b.ToArray();
    }

    private static void AppendPseudoBagItem(NoticePayloadBuilder b, ushort slot, uint amount)
    {
        // Bag item wire layout (E-410/E-425): u16 slot, u32 itemId(=slot), u32 data(=amount), u8 ext1, u16 durability, u8 sealed, u32 enchant, u8 amplifyType, u16 amplifyVal, u8 c3Count, u8 roCount, u8 sepUp, u8 trade
        b.U16(slot).U32(slot).U32(amount)
            .U8(0).U16(0).U8(0).U32(0u).U8(0).U16(0)
            .U8(0).U8(0).U8(0).U8(0);
    }

    public static byte[] BuildDungeonStart27()
    {
        return new NoticePayloadBuilder().I32(1).U8(0).U8(0).I32(2).U8(1).ToArray();
    }

    public static byte[] BuildDungeonInfo28(ushort dungeonId)
    {
        return new NoticePayloadBuilder()
            .U16(dungeonId)
            .U8(0).U8(0).U8(0).U8(0)
            .U8(0).U8(0).U8(0).U8(0)
            .U16(0).U8(0)
            .ToArray();
    }

    public static byte[] BuildStartMap29(byte cellX, byte cellY, ushort mapId, int[] monsterServerIds, int seed = 905)
    {
        int[] ids = monsterServerIds ?? Array.Empty<int>();
        var b = new NoticePayloadBuilder()
            .U8(cellX).U8(cellY).U8(0)
            .I32(seed)
            .U8(0).U8(0).U8(1) // mapType = 1
            .U16(mapId)
            .U8(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            // 16B monster record: u16 A, i32 B, u16 C(=serverId), i32 D, u8 E, F, G, H
            b.U16(9200 + i).I32(70000 + i).U16(ids[i]).I32(80000 + i).U8(1).U8(2).U8(3).U8(4);
        }
        b.U8(0) // item records
            .U8(0x63) // global3
            .U8(0) // quads
            .U8(0); // playerIndex
        return b.ToArray();
    }

    public static byte[] BuildKillConfirm(ushort monsterServerId)
    {
        return new NoticePayloadBuilder().U16(monsterServerId).U8(0).U8(0).U8(0xFF).U8(0).ToArray();
    }

    public static byte[] BuildSettlementA22(int clearTimeMs = 15200, byte rank = 1, ushort uid = 901)
    {
        return new NoticePayloadBuilder()
            .U8(rank)
            .I32(clearTimeMs)
            .U8(80)
            .U8(90)
            .U8(1)
            .U8(1)
            .U16(uid).I32(clearTimeMs).U8(1)
            .ToArray();
    }

    public static byte[] BuildSettlementB23(int baseExp = 1200, int rankBonusExp = 300, int goldReward = 5500)
    {
        var b = new NoticePayloadBuilder();
        b.I32(baseExp).I32(rankBonusExp);
        for (int i = 2; i < 16; i++)
        {
            b.I32(0);
        }
        b.U8(1) // kind
            .U8(0); // quest items count
        // 4 reward slots: slot 0 status=1 goldReward, slots 1..3 status=0
        b.U8(1).I32(0).I32(goldReward);
        b.U8(0).U8(0).U8(0);
        b.I32(0); // receiverValue
        b.I32(0).I32(0).I32(0).I32(0); // 4 reserved
        // 4 premium slots: all status=0
        b.U8(0).U8(0).U8(0).U8(0);
        b.U8(85).U8(1).U8(0).U8(1);
        return b.ToArray();
    }

    public static byte[] BuildScoreScrollReply72()
    {
        return new byte[] { 1 };
    }

    public static byte[] BuildCardSelectRightReply73(short[] slots = null)
    {
        short[] s = slots ?? new short[] { 1, 0, -1, -1 };
        var b = new NoticePayloadBuilder().U8(1);
        for (int i = 0; i < 4; i++)
        {
            short v = i < s.Length ? s[i] : (short)-1;
            b.U16(unchecked((ushort)v));
        }
        return b.ToArray();
    }

    public static byte[] BuildEplpReply75(bool succeeded, byte flagOrError, EplpCommand command)
    {
        return new byte[] { succeeded ? (byte)1 : (byte)0, flagOrError, (byte)command };
    }

    public static byte[] BuildUserArea23(ushort uid, byte village, byte area, ushort x = 400, ushort y = 300)
    {
        return new NoticePayloadBuilder()
            .U16(uid).U8(village).U8(area).U16(x).U16(y).U8(1).U8(1)
            .ToArray();
    }

    public static byte[] BuildAreaUsers24(byte village, byte area, ushort uid, ushort x = 400, ushort y = 300)
    {
        return new NoticePayloadBuilder()
            .U8(village).U8(area).U16(1)
            .U16(uid).U16(x).U16(y).U8(1).U8(1)
            .ToArray();
    }

    public static async Task<FakeReceivedCommand> ReadClientFrameAsync(Stream stream, SessionCipher cipher, CancellationToken ct)
    {
        byte[] header = new byte[13];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }
        PacketClass cls = (PacketClass)header[0];
        ushort packetId = (ushort)(header[1] | (header[2] << 8));
        int totalLen = BitConverter.ToInt32(header, 3);
        if (totalLen < 13 || totalLen > FrameLayout.PacketBufferSize)
        {
            return null;
        }
        int bodyLen = totalLen - 13;
        byte[] body = bodyLen > 0 ? new byte[bodyLen] : Array.Empty<byte>();
        if (bodyLen > 0 && !await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
        {
            return null;
        }
        byte[] plain = bodyLen > 0 ? cipher.Decrypt(packetId, body) : Array.Empty<byte>();
        return new FakeReceivedCommand(cls, packetId, plain.Length, plain);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    private static uint ComputeIeeeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    private static void WriteU16Le(byte[] buf, int offset, ushort v)
    {
        buf[offset] = (byte)v;
        buf[offset + 1] = (byte)(v >> 8);
    }

    private static void WriteU32Le(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)v;
        buf[offset + 1] = (byte)(v >> 8);
        buf[offset + 2] = (byte)(v >> 16);
        buf[offset + 3] = (byte)(v >> 24);
    }

    private static void WriteI32Le(byte[] buf, int offset, int v)
    {
        WriteU32Le(buf, offset, unchecked((uint)v));
    }

    private static void WriteU32Be(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)(v >> 24);
        buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8);
        buf[offset + 3] = (byte)v;
    }
}
