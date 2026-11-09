namespace Dnf.Net.Tests;

/// <summary>
/// 手工拼合成通知载荷用的小写入器:只做小端定宽整数与原样字节,显式移位,独立于产品代码
/// (不用 PacketWriter,也不用 BitConverter),免得读写两边同错而互相掩盖。
/// 取值超出宽度直接抛异常:防止测试自己写错被静默截断。
/// </summary>
internal sealed class NoticePayloadBuilder
{
    private readonly List<byte> _bytes = new List<byte>();

    /// <summary>当前已拼的字节数。</summary>
    public int Length => _bytes.Count;

    /// <summary>追加 1 个字节(0..255)。</summary>
    public NoticePayloadBuilder U8(int value)
    {
        if (value < 0 || value > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        _bytes.Add((byte)value);
        return this;
    }

    /// <summary>追加小端无符号 16 位整数(0..65535)。</summary>
    public NoticePayloadBuilder U16(int value)
    {
        if (value < 0 || value > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        _bytes.Add((byte)value);
        _bytes.Add((byte)(value >> 8));
        return this;
    }

    /// <summary>追加小端有符号 32 位整数。</summary>
    public NoticePayloadBuilder I32(int value)
    {
        _bytes.Add((byte)value);
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)(value >> 16));
        _bytes.Add((byte)(value >> 24));
        return this;
    }

    /// <summary>原样追加字节。</summary>
    public NoticePayloadBuilder Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    /// <summary>取出拼好的字节(新数组)。</summary>
    public byte[] ToArray()
    {
        return _bytes.ToArray();
    }
}
