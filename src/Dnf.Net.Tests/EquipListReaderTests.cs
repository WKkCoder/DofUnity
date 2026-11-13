using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 装备子表读取器(<see cref="EquipListReader"/>)测试:紧凑记录 22 字节、打包值拆分、槽号约定、尾字节,以及详细记录列表。
/// 夹具全是手工拼的合成字节,物品号 900001 起,不含任何真实物品号或数值。
/// </summary>
public class EquipListReaderTests
{
    // ---- 紧凑记录 ----

    [Fact]
    public void CompactRecordFieldsAreReadFromTheirLiteralOffsets()
    {
        byte[] bytes =
        {
            0x01,                   // 0      个数 1
            0x05,                   // 1      槽号 5
            0xA1, 0xBB, 0x0D, 0x00, // 2..5   物品号 900001
            0x04, 0x00, 0x00, 0x00, // 6..9   扩展信息长度 4
            0x11, 0x22, 0x33, 0x44, // 10..13 扩展信息 4 字节
            0x2B,                   // 14     packed:高 7 位 21,最低位 1
            0x78, 0x56, 0x34, 0x12, // 15..18 清除装扮
            0x0C, 0x00, 0x00, 0x00, // 19..22 附魔索引 12
            0x77,                   // 23     列表之后的尾字节
        };
        Assert.Equal(1 + 22 + 1, bytes.Length);
        PacketReader reader = new PacketReader(bytes);
        Assert.True(EquipListReader.TryReadCompact(reader, true, out EquipCompactRecord[] records, out byte trailing));
        Assert.Equal(bytes.Length, reader.Position);
        EquipCompactRecord r = Assert.Single(records);
        Assert.Equal(5, r.Slot);
        Assert.Equal(900001u, r.ItemId);
        Assert.Equal(0x44332211u, r.ExpansionInfo);
        Assert.Equal(0x2B, r.Packed);
        Assert.Equal(21, r.UpgradeLevel);
        Assert.True(r.IsAmplified);
        Assert.Equal(0x12345678u, r.ClearAvatar);
        Assert.Equal(12u, r.EnchantIndex);
        Assert.Equal(0x77, trailing);
    }

    [Theory]
    [InlineData(0x00, 0, false)]
    [InlineData(0x01, 0, true)]
    [InlineData(0x02, 1, false)]
    [InlineData(0x03, 1, true)]
    [InlineData(0x2A, 21, false)]
    [InlineData(0xFE, 127, false)]
    [InlineData(0xFF, 127, true)]
    public void CompactPackedValueSplitsTheHighSevenBitsFromTheLowestBit(int packed, int level, bool amplified)
    {
        byte[] bytes = new NoticePayloadBuilder().U8(1).AppendCompact(3, ItemA, 0, packed).ToArray();
        Assert.True(EquipListReader.TryReadCompact(new PacketReader(bytes), false, out EquipCompactRecord[] records, out _));
        Assert.Equal(level, records[0].UpgradeLevel);
        Assert.Equal(amplified, records[0].IsAmplified);
    }

    [Fact]
    public void CompactListAcceptsEveryOfTheTwelveSlotsZeroToEleven()
    {
        (int, long, int)[] all = Enumerable.Range(0, 12).Select(s => (s, ItemA + s, s * 2)).ToArray();
        byte[] bytes = new NoticePayloadBuilder().AppendCompactList(0, all).ToArray();
        Assert.Equal(1 + 12 * 22 + 1, bytes.Length);
        Assert.True(EquipListReader.TryReadCompact(new PacketReader(bytes), true, out EquipCompactRecord[] records, out _));
        Assert.Equal(12, records.Length);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(i, records[i].Slot);
            Assert.Equal(i, records[i].UpgradeLevel);
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(25)]
    [InlineData(255)]
    public void CompactListRejectsSlotsAboveEleven(int slot)
    {
        byte[] bytes = new NoticePayloadBuilder().U8(1).AppendCompact(slot, ItemA).U8(0).ToArray();
        Assert.False(EquipListReader.TryReadCompact(new PacketReader(bytes), true, out EquipCompactRecord[] records, out _));
        Assert.Null(records);
    }

    [Fact]
    public void CompactTrailingByteIsOnlyReadWhenAskedFor()
    {
        byte[] bytes = new NoticePayloadBuilder().AppendCompactList(0x55, (2, ItemA, 0)).ToArray();
        PacketReader withTail = new PacketReader(bytes);
        Assert.True(EquipListReader.TryReadCompact(withTail, true, out _, out byte trailing));
        Assert.Equal(0x55, trailing);
        Assert.Equal(0, withTail.Remaining);

        PacketReader withoutTail = new PacketReader(bytes);
        Assert.True(EquipListReader.TryReadCompact(withoutTail, false, out _, out byte none));
        Assert.Equal(0, none);
        Assert.Equal(1, withoutTail.Remaining);
    }

    [Fact]
    public void CompactListMissingItsTrailingByteIsRejectedWhenOneIsExpected()
    {
        byte[] bytes = new NoticePayloadBuilder().AppendCompactList(null, (2, ItemA, 0)).ToArray();
        Assert.False(EquipListReader.TryReadCompact(new PacketReader(bytes), true, out _, out _));
        Assert.True(EquipListReader.TryReadCompact(new PacketReader(bytes), false, out _, out _));
    }

    [Fact]
    public void EmptyCompactListIsJustTheCountByteAndTheTrailingByte()
    {
        byte[] bytes = { 0x00, 0x09 };
        PacketReader reader = new PacketReader(bytes);
        Assert.True(EquipListReader.TryReadCompact(reader, true, out EquipCompactRecord[] records, out byte trailing));
        Assert.Empty(records);
        Assert.Equal(9, trailing);
        Assert.Equal(2, reader.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(0x04000000)]
    public void CompactExpansionLengthThatIsNotFourIsRejected(int length)
    {
        byte[] bytes = new NoticePayloadBuilder().U8(1)
            .U8(2).U32(ItemA).U32(length).U32(0).U8(0).U32(0).U32(0).U8(0).ToArray();
        Assert.False(EquipListReader.TryReadCompact(new PacketReader(bytes), true, out _, out _));
    }

    [Fact]
    public void CompactCountThatLiesAboutTheRemainingBytesIsRejected()
    {
        byte[] bytes = new NoticePayloadBuilder().U8(12).Raw(new byte[100]).ToArray();
        Assert.False(EquipListReader.TryReadCompact(new PacketReader(bytes), true, out EquipCompactRecord[] records, out _));
        Assert.Null(records);
    }

    [Fact]
    public void EveryShorterPrefixOfACompactListIsRejected()
    {
        byte[] full = new NoticePayloadBuilder().AppendCompactList(1, (0, ItemA, 3), (4, ItemB, 8)).ToArray();
        Assert.True(EquipListReader.TryReadCompact(new PacketReader(full), true, out _, out _));
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(EquipListReader.TryReadCompact(new PacketReader(full.Take(length).ToArray()), true, out _, out _));
        }
    }

    [Fact]
    public void NullReaderIsRejectedWithoutException()
    {
        Assert.False(EquipListReader.TryReadCompact(null, true, out _, out _));
        Assert.False(EquipListReader.TryReadDetailed(null, null, 0, 25, out _));
    }

    // ---- 详细记录列表 ----

    [Fact]
    public void DetailedListReadsRecordsAndStopsRightAfterTheLastOne()
    {
        byte[] bytes = new NoticePayloadBuilder()
            .AppendDetailList(Item(10, ItemA), Item(12, ItemB), Item(25, ItemC))
            .U8(0xEE).ToArray();
        PacketReader reader = new PacketReader(bytes);
        Assert.True(EquipListReader.TryReadDetailed(reader, null, 0, 25, out ItemEntry[] entries));
        Assert.Equal(3, entries.Length);
        Assert.Equal(new[] { 10, 12, 25 }, entries.Select(e => (int)e.Slot).ToArray());
        Assert.Equal(1 + 3 * 27, reader.Position);
        // 详细列表没有尾字节:紧跟其后的字节原样留在流里。
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void EmptyDetailedListIsJustTheCountByte()
    {
        PacketReader reader = new PacketReader(new byte[] { 0x00 });
        Assert.True(EquipListReader.TryReadDetailed(reader, null, 0, 25, out ItemEntry[] entries));
        Assert.Empty(entries);
        Assert.Equal(1, reader.Position);
    }

    [Theory]
    [InlineData(0, 25, 26)]
    [InlineData(0, 25, 255)]
    [InlineData(10, 21, 9)]
    [InlineData(10, 21, 22)]
    [InlineData(10, 21, 0)]
    public void DetailedListRejectsSlotsOutsideTheAllowedRange(int min, int max, int slot)
    {
        byte[] bytes = new NoticePayloadBuilder().AppendDetailList(Item(slot, ItemA)).ToArray();
        Assert.False(EquipListReader.TryReadDetailed(new PacketReader(bytes), null, min, max, out ItemEntry[] entries));
        Assert.Null(entries);
    }

    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(0, 25, 25)]
    [InlineData(10, 21, 10)]
    [InlineData(10, 21, 21)]
    public void DetailedListAcceptsTheBoundarySlotsOfTheRange(int min, int max, int slot)
    {
        ItemSpec s = Item(slot, ItemA);
        if (slot <= 9)
        {
            // 槽 ≤ 9 缺省按装扮近似,要带上两个块。
            s.SocketBlock = Socket();
            s.ExpansionBlock = Expansion();
        }
        byte[] bytes = new NoticePayloadBuilder().AppendDetailList(s).ToArray();
        Assert.True(EquipListReader.TryReadDetailed(new PacketReader(bytes), null, min, max, out ItemEntry[] entries));
        Assert.Equal(slot, entries[0].Slot);
    }

    [Fact]
    public void DetailedCountThatLiesAboutTheRemainingBytesIsRejected()
    {
        byte[] bytes = new NoticePayloadBuilder().U8(26).Raw(new byte[100]).ToArray();
        Assert.False(EquipListReader.TryReadDetailed(new PacketReader(bytes), null, 0, 25, out ItemEntry[] entries));
        Assert.Null(entries);
    }

    [Fact]
    public void DetailedRecordsWithAvatarSlotApproximationAndPetSlotAreReadWithoutAResolver()
    {
        ItemSpec avatar = Item(3, ItemA);
        avatar.SocketBlock = Socket();
        avatar.ExpansionBlock = Expansion();
        ItemSpec ordinary = Item(12, ItemB);
        ItemSpec pet = Item(22, ItemC);
        pet.PetTime = 600;
        byte[] bytes = new NoticePayloadBuilder().AppendDetailList(avatar, ordinary, pet).ToArray();
        Assert.True(EquipListReader.TryReadDetailed(new PacketReader(bytes), null, 0, 25, out ItemEntry[] entries));
        Assert.True(entries[0].HasAvatarBlocks);
        Assert.False(entries[1].HasAvatarBlocks);
        Assert.True(entries[2].HasPetRemainTime);
        Assert.Equal(600u, entries[2].PetRemainTime);
    }

    [Fact]
    public void EveryShorterPrefixOfADetailedListIsRejected()
    {
        byte[] full = new NoticePayloadBuilder().AppendDetailList(Item(10, ItemA), Item(11, ItemB)).ToArray();
        Assert.True(EquipListReader.TryReadDetailed(new PacketReader(full), null, 0, 25, out _));
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(EquipListReader.TryReadDetailed(new PacketReader(full.Take(length).ToArray()), null, 0, 25, out _));
        }
    }

    // ---- 紧凑记录与详细记录的跨视图对应(合成数据) ----

    [Fact]
    public void ACompactRecordAndTheDetailedRecordOfTheSameSlotAgreeOnLevelAmplifyClearAvatarAndEnchant()
    {
        // 与笔记 §2.2.6 的交叉核对同形:packed >> 1 == ext1 & 0x1F、packed & 1 == (增幅类型 != 0)、清除装扮与附魔索引相等。
        ItemSpec detail = Item(11, ItemA);
        detail.Ext1 = 0x80 | 17;
        detail.AmpType = 6;
        detail.AmpValue = 99;
        detail.ClearAvatar = 424242;
        detail.Enchant = 31337;
        byte[] detailBytes = new NoticePayloadBuilder().AppendDetailList(detail).ToArray();
        byte[] compactBytes = new NoticePayloadBuilder().U8(1)
            .AppendCompact(11, ItemA, 0, (17 << 1) | 1, 424242, 31337).ToArray();
        Assert.True(EquipListReader.TryReadDetailed(new PacketReader(detailBytes), null, 0, 25, out ItemEntry[] d));
        Assert.True(EquipListReader.TryReadCompact(new PacketReader(compactBytes), false, out EquipCompactRecord[] c, out _));
        Assert.Equal(d[0].UpgradeLevel, c[0].UpgradeLevel);
        Assert.Equal(d[0].IsAmplified, c[0].IsAmplified);
        Assert.Equal(d[0].ClearAvatar, c[0].ClearAvatar);
        Assert.Equal(d[0].EnchantIndex, c[0].EnchantIndex);
        Assert.Equal(d[0].ItemId, c[0].ItemId);
    }
}
