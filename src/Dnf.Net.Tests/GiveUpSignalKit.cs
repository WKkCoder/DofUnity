using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>0/24 载荷的三种形态(公开类型,好让公开的 MemberData 与用例参数用它)。</summary>
public enum AreaUsersShape
{
    /// <summary>
    /// 按"u8 村、u8 区、u16 个数、个数 × 8 字节"读不下来:声明 200 条记录,后面没有那么多字节——不管会话加密补零补多少都读不下来
    /// (真会话把载荷补零到块长的整数倍之后才交付)。
    /// </summary>
    Unparseable,

    /// <summary>读得下来:一条记录(12 字节)。</summary>
    Parseable,

    /// <summary>读得下来,而且线上总长正好是窗口里的 31(16 字节载荷:一条记录加 4 个多余字节)。</summary>
    ParseableTableSized,
}

/// <summary>
/// "放弃之后服务端推来的那一串帧"的合成夹具(r22-giveup-signal)。形状照真服观察到的窗口造,全是合成载荷(包号是协议常量,载荷字节与长度之外的内容都是造的):
/// 放弃命令(类 1、包号 45)发出之后,按到达顺序是五组,类 0 = NOTI,共 9 帧:
/// <list type="number">
/// <item>561 信封(内层是压缩帧 0/9,开不了),随后是会话附上的同内容普通帧 0/9(总长 32);</item>
/// <item>普通帧 0/24(没有信封包着);</item>
/// <item>561 信封(内层 0/3,可开,总长 23),随后是会话附上的内层副本(被判重复跳过);</item>
/// <item>同上形态的 0/36(总长 27);</item>
/// <item>561 信封(内层是压缩帧 0/2,开不了),随后是会话附上的同内容普通帧 0/2(总长 72)。</item>
/// </list>
/// 没有 0/23。帧总长 = 15 字节帧头(服务端到客户端方向)加载荷;压缩内层的载荷是手拼的 zlib 流(一个"不压缩"块,长度 = 明文长 + 11),
/// 所以能凑出指定的压缩态长度,真会话拆信封时会把它解成明文附在信封之后。
/// 同一份形状有两种交付方式:真会话用的整帧字节(<see cref="RealWire"/>),和假会话用的"帧单元"(<see cref="PushFake"/>)。
/// </summary>
internal static class GiveUpSignalKit
{
    /// <summary>服务端到客户端方向的帧头字节数。</summary>
    public const int HeaderLength = 15;

    /// <summary>窗口里各帧的总长(帧头加载荷)。</summary>
    public const int Total9 = 32;
    public const int Total24 = 31;
    public const int Total3 = 23;
    public const int Total36 = 27;
    public const int Total2 = 72;

    /// <summary>窗口里每个包号的明文载荷(可识别的合成字节);0/9 与 0/2 在线上是压缩态,明文是解压之后的样子。</summary>
    public static byte[] Plain9() => GatewayTestSupport.Pattern(Total9 - HeaderLength - 11, 0x31, 3);

    public static byte[] Payload3() => GatewayTestSupport.Pattern(Total3 - HeaderLength, 0x41, 5);

    public static byte[] Payload36() => GatewayTestSupport.Pattern(Total36 - HeaderLength, 0x51, 7);

    public static byte[] Plain2() => GatewayTestSupport.Pattern(Total2 - HeaderLength - 11, 0x61, 1);

    /// <summary>
    /// 一份 0/24 载荷。<see cref="AreaUsersShape.Unparseable"/> 的另一种尺寸(25 字节、声明 3 条记录只剩 21 字节)见 <see cref="Unparseable25"/>:
    /// 那个只能直接喂状态机或假会话(真会话会把它补零到块长的整数倍,补完就读得下来了)。
    /// </summary>
    public static byte[] AreaUsers(AreaUsersShape shape)
    {
        switch (shape)
        {
            case AreaUsersShape.Unparseable:
                return new NoticePayloadBuilder().U8(3).U8(7).U16(200).Raw(GatewayTestSupport.Pattern(8, 0x71, 1)).ToArray();
            case AreaUsersShape.Parseable:
                return GiveUpKit.AreaUsersPayload(3, 7, 952);
            case AreaUsersShape.ParseableTableSized:
                return new NoticePayloadBuilder().U8(3).U8(7).U16(1).U16(952).U16(100).U16(200).U8(0).U8(1).Raw(GatewayTestSupport.Pattern(4, 0x71, 1)).ToArray();
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    /// <summary>25 字节、按"4 + 8n"读不下来的 0/24 载荷(声明 3 条记录要 24 字节,头之后只剩 21 字节)。</summary>
    public static byte[] Unparseable25()
    {
        byte[] payload = new NoticePayloadBuilder().U8(3).U8(7).U16(3).Raw(GatewayTestSupport.Pattern(21, 0x71, 1)).ToArray();
        Assert.Equal(25, payload.Length);
        Assert.False(TownAreaPayloads.TryParseAreaUsers(payload, out _));
        return payload;
    }

    /// <summary>
    /// 一段 zlib 流,里面只有一个"不压缩"块(RFC 1950/1951):2 字节头、1 字节块头、长度与长度补码各 2 字节、明文、4 字节 Adler-32(大端)。
    /// 长度恰好是明文长度 + 11,所以想要多长的压缩态载荷就选多长的明文。
    /// </summary>
    public static byte[] ZlibStored(byte[] plain)
    {
        if (plain.Length > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(plain));
        }
        uint a = 1;
        uint b = 0;
        foreach (byte value in plain)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        uint adler = (b << 16) | a;
        var stream = new List<byte> { 0x78, 0x01, 0x01, (byte)plain.Length, (byte)(plain.Length >> 8), (byte)~plain.Length, (byte)(~plain.Length >> 8) };
        stream.AddRange(plain);
        stream.Add((byte)(adler >> 24));
        stream.Add((byte)(adler >> 16));
        stream.Add((byte)(adler >> 8));
        stream.Add((byte)adler);
        return stream.ToArray();
    }

    /// <summary>窗口里信封(561 帧)的整帧字节:内层是类 0 的 <paramref name="id"/>,载荷是 <paramref name="innerPayload"/>(压缩包号就传压缩态)。</summary>
    public static byte[] Envelope(ushort id, byte[] innerPayload)
    {
        return HostTestKit.RealEnvelope(CombatKit.Noti, id, innerPayload);
    }

    /// <summary>
    /// 真会话用的窗口:按到达顺序的整帧(信封、普通帧),默认五个。<paramref name="payload24"/> 是 0/24 的明文载荷;
    /// <paramref name="reverse2And24"/> 为 true 时把 0/2 那一组挪到 0/24 前面(顺序无关的检验);
    /// <paramref name="without24"/> / <paramref name="without2"/> 为 true 时分别去掉 0/24 / 0/2 那一组。
    /// 完整的窗口,真会话收下之后交付 9 帧(四个信封各附一份内层副本,外加那条普通 0/24)。
    /// </summary>
    public static byte[][] RealWire(byte[] payload24, bool reverse2And24 = false, bool without24 = false, bool without2 = false)
    {
        byte[] env9 = Envelope(9, ZlibStored(Plain9()));
        byte[] plain24 = DungeonEntryWire.Notice(24, payload24);
        byte[] env3 = Envelope(3, Payload3());
        byte[] env36 = Envelope(36, Payload36());
        byte[] env2 = Envelope(2, ZlibStored(Plain2()));
        var window = new List<byte[]> { env9 };
        if (reverse2And24 && !without2)
        {
            window.Add(env2);
        }
        if (!without24)
        {
            window.Add(plain24);
        }
        window.Add(env3);
        window.Add(env36);
        if (!reverse2And24 && !without2)
        {
            window.Add(env2);
        }
        return window.ToArray();
    }

    /// <summary>
    /// 假会话用的窗口:同样的几组,每组一个"帧单元"(信封加它的内层副本一起交付,普通帧单独一个)。开关同 <see cref="RealWire"/>。
    /// 压缩包号(0/9、0/2)的内层副本照真会话的口径造:按压缩态载荷拼帧再解码,于是帧头总长是压缩态的总长(32、72),载荷是解压后的明文。
    /// </summary>
    public static void PushFake(CombatFakeSession session, byte[] payload24, bool reverse2And24 = false, bool without24 = false, bool without2 = false)
    {
        GiveUpKit.PushEnveloped(new CombatKit.ServerFrame(CombatKit.Noti, 9, ZlibStored(Plain9())), session);
        if (reverse2And24 && !without2)
        {
            GiveUpKit.PushEnveloped(new CombatKit.ServerFrame(CombatKit.Noti, 2, ZlibStored(Plain2())), session);
        }
        if (!without24)
        {
            session.Push(new CombatKit.ServerFrame(CombatKit.Noti, TownAreaPayloads.AreaUsersPacketId, payload24));
        }
        GiveUpKit.PushEnveloped(new CombatKit.ServerFrame(CombatKit.Noti, 3, Payload3()), session);
        GiveUpKit.PushEnveloped(new CombatKit.ServerFrame(CombatKit.Noti, 36, Payload36()), session);
        if (!reverse2And24 && !without2)
        {
            GiveUpKit.PushEnveloped(new CombatKit.ServerFrame(CombatKit.Noti, 2, ZlibStored(Plain2())), session);
        }
    }
}
