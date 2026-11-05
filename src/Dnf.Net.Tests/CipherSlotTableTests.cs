using Dnf.Net.Crypto;

namespace Dnf.Net.Tests;

/// <summary>会话算法槽静态表的自检(E-204、E-262):长度、块长、选槽与对象工厂彼此一致。</summary>
public sealed class CipherSlotTableTests
{
    [Fact]
    public void KeyLengthsSumToTheChannelInfoKeyBlock()
    {
        Assert.Equal(CipherSlots.Count, CipherSlots.KeyLengths.Length);
        Assert.Equal(CipherSlots.Count, CipherSlots.BlockSizes.Length);
        Assert.Equal(CipherSlots.TotalKeyLength, CipherSlots.KeyLengths.Sum());
    }

    [Fact]
    public void FactoryObjectsMatchTheStaticTable()
    {
        for (int slot = 0; slot < CipherSlots.Count; slot++)
        {
            BlockCipher c = CipherSlots.Create(slot);
            Assert.Equal(slot, c.Slot);
            Assert.Equal(CipherSlots.KeyLengths[slot], c.KeyLength);
            Assert.Equal(CipherSlots.BlockSizes[slot], c.BlockSize);
        }
    }

    [Fact]
    public void KeyOffsetsPartitionTheKeyBlockWithoutGaps()
    {
        int expected = 0;
        for (int slot = 0; slot < CipherSlots.Count; slot++)
        {
            Assert.Equal(expected, CipherSlots.KeyOffset(slot));
            expected += CipherSlots.KeyLengths[slot];
        }
        Assert.Equal(CipherSlots.TotalKeyLength, expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(14, 0)]
    [InlineData(15, 1)]
    [InlineData(604, 2)]
    public void PacketIdSelectsTheSlotModulo14(int packetId, int slot)
    {
        Assert.Equal(slot, CipherSlots.SlotForPacket(packetId));
    }

    [Fact]
    public void OutOfRangeSlotsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CipherSlots.Create(14));
        Assert.Throws<ArgumentOutOfRangeException>(() => CipherSlots.Create(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CipherSlots.KeyOffset(14));
    }
}
