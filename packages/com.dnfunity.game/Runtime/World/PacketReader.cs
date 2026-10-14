using System;

namespace Dnf.Game
{
    /// <summary>
    /// 随"创建被动对象 / 切换状态 / 改变技能特效"等消息一起发出的二进制数据的读取端:
    /// 脚本里的 reciveData.readDword() 等就是它。写入端是 sq_BinaryStartWrite / sq_BinaryWriteDword(或 obj.sq_StartWrite / obj.sq_WriteDword……),
    /// 字节序为小端;读取时越界返回 0 且位置不动。各整数宽度:Byte 1、Word 2、Dword 4、Float 4(单精度)。
    /// 符号:Dword 按有符号 32 位(脚本里用它传坐标,y 可为负,见证据 E-037),Word 与 Byte 按无符号——后两者是推断,没有找到负数写入的反例。
    /// </summary>
    public sealed class PacketReader : NativeObject
    {
        private readonly byte[] _data;

        public PacketReader(byte[] data)
        {
            _data = data ?? Array.Empty<byte>();
        }

        public override string TypeName => "PacketReader";

        /// <summary>数据总字节数(脚本里的 getSize())。</summary>
        public int Size => _data.Length;

        /// <summary>已读取的字节数(脚本里的 sq_BinaryGetReadSize())。</summary>
        public int Position { get; private set; }

        public bool TryRead(int width, out ulong raw)
        {
            raw = 0;
            if (Position + width > _data.Length)
            {
                return false;
            }
            for (int i = 0; i < width; i++)
            {
                raw |= (ulong)_data[Position + i] << (8 * i);
            }
            Position += width;
            return true;
        }

        public long ReadByte()
        {
            return TryRead(1, out ulong v) ? (long)v : 0;
        }

        public long ReadWord()
        {
            return TryRead(2, out ulong v) ? (long)v : 0;
        }

        public long ReadDword()
        {
            return TryRead(4, out ulong v) ? (int)(uint)v : 0;
        }

        public float ReadFloat()
        {
            return TryRead(4, out ulong v) ? BitConverter.ToSingle(BitConverter.GetBytes((uint)v), 0) : 0f;
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }
    }
}
