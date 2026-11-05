using System;
using Dnf.Net.Crypto;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 会话密码层(E-204、E-262):包号 % 14 选槽,334 字节密钥块按 <see cref="CipherSlots.KeyLengths"/> 依次切给 14 个槽,
    /// 每个包独立做 ECB(没有初始向量、计数器,也没有跨包状态)。
    /// <list type="bullet">
    /// <item>加密:载荷长度向上补到该槽块长的整数倍,补的字节是 0x00;载荷为空则输出为空(不补一整块)。</item>
    /// <item>解密:不去补齐字节,密文长度必须是块长的整数倍,否则返回 null;空密文得到空数组。</item>
    /// <item>包号必须在 0..65535(线上是 16 位),越界返回 null。</item>
    /// </list>
    /// 槽的算法对象由工厂按需创建并设密钥(默认 <see cref="CipherSlots.Create"/>);某个槽的算法尚未实现时只有用到那个槽的包才会失败。
    /// 对象内部的块算法可能带临时缓冲,所以一个连接一个对象,不要跨线程共用。
    /// </summary>
    public sealed class SessionCipher : IPayloadCipher
    {
        /// <summary>包号的取值上限(线上 16 位)。</summary>
        public const int MaxPacketId = 0xFFFF;

        private readonly byte[] _keyBlock;
        private readonly Func<int, BlockCipher> _factory;
        private readonly BlockCipher[] _slots = new BlockCipher[CipherSlots.Count];

        /// <summary>用默认的各槽实现。</summary>
        public SessionCipher(byte[] keyBlock) : this(keyBlock, CipherSlots.Create)
        {
        }

        /// <summary>指定槽算法的工厂(测试替身或自带实现)。工厂返回的对象必须与槽表里的密钥长度、块长一致。</summary>
        public SessionCipher(byte[] keyBlock, Func<int, BlockCipher> factory)
        {
            if (keyBlock == null) throw new ArgumentNullException(nameof(keyBlock));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (keyBlock.Length < CipherSlots.TotalKeyLength)
                throw new ArgumentException("密钥块不足 " + CipherSlots.TotalKeyLength + " 字节", nameof(keyBlock));
            _keyBlock = new byte[CipherSlots.TotalKeyLength];
            Buffer.BlockCopy(keyBlock, 0, _keyBlock, 0, _keyBlock.Length);
            _factory = factory;
        }

        /// <summary>某个包号对应的槽;包号越界返回 -1。</summary>
        public static int SlotOf(int packetId)
        {
            return (uint)packetId > MaxPacketId ? -1 : CipherSlots.SlotForPacket(packetId);
        }

        /// <summary>某个包号的块长;包号越界返回 0。</summary>
        public static int BlockSizeOf(int packetId)
        {
            int slot = SlotOf(packetId);
            return slot < 0 ? 0 : CipherSlots.BlockSizes[slot];
        }

        /// <inheritdoc />
        public int PaddedLength(int packetId, int length)
        {
            int block = BlockSizeOf(packetId);
            if (block == 0 || length < 0) return -1;
            return (length + block - 1) / block * block;
        }

        /// <inheritdoc />
        public byte[] Encrypt(int packetId, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            int slot = SlotOf(packetId);
            if (slot < 0) return null;
            if (payload.Length == 0) return new byte[0];
            BlockCipher cipher = GetSlot(slot);
            int block = cipher.BlockSize;
            int padded = (payload.Length + block - 1) / block * block;
            byte[] buffer = new byte[padded];
            Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);
            for (int offset = 0; offset < padded; offset += block)
                cipher.EncryptBlock(buffer, offset, buffer, offset);
            return buffer;
        }

        /// <inheritdoc />
        public byte[] Decrypt(int packetId, byte[] cipherText)
        {
            if (cipherText == null) throw new ArgumentNullException(nameof(cipherText));
            int slot = SlotOf(packetId);
            if (slot < 0) return null;
            if (cipherText.Length == 0) return new byte[0];
            BlockCipher cipher = GetSlot(slot);
            int block = cipher.BlockSize;
            if (cipherText.Length % block != 0) return null;
            byte[] buffer = (byte[])cipherText.Clone();
            for (int offset = 0; offset < buffer.Length; offset += block)
                cipher.DecryptBlock(buffer, offset, buffer, offset);
            return buffer;
        }

        private BlockCipher GetSlot(int slot)
        {
            BlockCipher cipher = _slots[slot];
            if (cipher != null) return cipher;
            cipher = _factory(slot);
            if (cipher == null)
                throw new InvalidOperationException("槽 " + slot + " 没有算法对象");
            if (cipher.KeyLength != CipherSlots.KeyLengths[slot] || cipher.BlockSize != CipherSlots.BlockSizes[slot])
                throw new InvalidOperationException("槽 " + slot + " 的密钥长或块长与槽表不一致");
            cipher.SetKey(_keyBlock, CipherSlots.KeyOffset(slot));
            _slots[slot] = cipher;
            return cipher;
        }
    }
}
