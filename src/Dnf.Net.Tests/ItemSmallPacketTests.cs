using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 物品类小包解析器测试:点券 53、装扮币应答 587(类 1)、持续型物品 172/174、物品锁 251、装备等级上限 438、快捷键选项 452。
/// 夹具全是手工拼的合成字节,偏移写成字面量;取值是编造的小数,不含真实物品号、名字或数值。
/// 直连帧载荷的几何(正文 + 4 字节 PI 尾 + 补零到块长)与笔记 §1.1 注、§2.6 一致:块长取自 <c>CipherSlots.BlockSizes[包号 % 14]</c>。
/// </summary>
public class ItemSmallPacketTests
{
    // ---- 包号常量:写成字面量,产品常量被改动时测试要能发现 ----

    [Fact]
    public void PacketIdConstantsMatchTheWireNumbers()
    {
        Assert.Equal(53, CeraParser.PacketId);
        Assert.Equal(587, AvatarCoinParser.PacketId);
        Assert.Equal(172, ContinuousItemListParser.CoolTimePacketId);
        Assert.Equal(174, ContinuousItemListParser.EffectPacketId);
        Assert.Equal(251, ItemLockParser.PacketId);
        Assert.Equal(438, MaxEquipLevelParser.PacketId);
        Assert.Equal(452, HotkeyOptionParser.PacketId);
    }

    // ---- 点券 53:u8 标志 + u32 + u32 + u32 ----

    private static readonly byte[] CeraBody =
    {
        0x01,
        0x10, 0x00, 0x00, 0x00,
        0x20, 0x00, 0x00, 0x00,
        0x30, 0x00, 0x00, 0x00,
    };

    [Fact]
    public void CeraReadsTheFlagAndThreeValuesAtLiteralOffsets()
    {
        Assert.True(CeraParser.TryParse(CeraBody, out CeraNotice n));
        Assert.Equal(1, n.Flag);
        Assert.Equal(0x10u, n.Cera);
        Assert.Equal(0x20u, n.Mileage);
        Assert.Equal(0x30u, n.CeraPoint);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void CeraValuesAreFourByteLittleEndianUnsigned()
    {
        byte[] body = { 0x01, 0x04, 0x03, 0x02, 0xF1, 0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.True(CeraParser.TryParse(body, out CeraNotice n));
        Assert.Equal(0xF1020304u, n.Cera);
        Assert.Equal(0x80000000u, n.Mileage);
        Assert.Equal(uint.MaxValue, n.CeraPoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0xFF)]
    public void CeraDoesNotRejectOnTheFlagValue(int flag)
    {
        byte[] body = (byte[])CeraBody.Clone();
        body[0] = (byte)flag;
        Assert.True(CeraParser.TryParse(body, out CeraNotice n));
        Assert.Equal(flag, n.Flag);
        Assert.Equal(0x10u, n.Cera);
    }

    [Fact]
    public void CeraEveryProperPrefixIsRejected()
    {
        for (int length = 0; length < CeraBody.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(CeraBody, prefix, length);
            Assert.False(CeraParser.TryParse(prefix, out CeraNotice n), "前缀长度 " + length);
            Assert.Null(n);
        }
    }

    [Fact]
    public void CeraTailAfterTheLastFieldIsOnlyCounted()
    {
        // 直连帧:正文 13 + PI 尾 4 + 补零 7 = 24(53 % 14 = 11,块长 8)。
        byte[] payload = DirectFramePayload(CeraBody, 53);
        Assert.Equal(24, payload.Length);
        Assert.True(CeraParser.TryParse(payload, out CeraNotice n));
        Assert.Equal(0x30u, n.CeraPoint);
        Assert.Equal(11, n.TrailingByteCount);
        Assert.Equal(13 + n.TrailingByteCount, payload.Length);
    }

    [Fact]
    public void CeraNullPayloadIsRejected()
    {
        Assert.False(CeraParser.TryParse(null, out CeraNotice n));
        Assert.Null(n);
        Assert.False(CeraParser.TryParse(new byte[0], out n));
    }

    // ---- 装扮币应答 587(类 1) ----

    [Fact]
    public void CoinSuccessBodyIsTheFlagThenAFourByteValue()
    {
        byte[] body = { 0x01, 0x78, 0x56, 0x34, 0x12 };
        Assert.True(AvatarCoinParser.TryParse(body, out AvatarCoinNotice n));
        Assert.True(n.Success);
        Assert.Equal(1, n.Flag);
        Assert.Equal(0x12345678u, n.Coin);
        Assert.Equal(0, n.ErrorCode);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void CoinFailureBodyIsTheFlagThenAnErrorCodeAndNothingElse()
    {
        byte[] body = { 0x00, 0x07 };
        Assert.True(AvatarCoinParser.TryParse(body, out AvatarCoinNotice n));
        Assert.False(n.Success);
        Assert.Equal(0, n.Flag);
        Assert.Equal(7, n.ErrorCode);
        Assert.Equal(0u, n.Coin);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void CoinFailureDoesNotReadTheValueEvenWhenBytesFollow()
    {
        byte[] body = { 0x00, 0x07, 0x78, 0x56, 0x34, 0x12 };
        Assert.True(AvatarCoinParser.TryParse(body, out AvatarCoinNotice n));
        Assert.False(n.Success);
        Assert.Equal(0u, n.Coin);
        Assert.Equal(4, n.TrailingByteCount);
    }

    [Fact]
    public void CoinAnyNonZeroFlagMeansSuccess()
    {
        byte[] body = { 0x02, 0x01, 0x00, 0x00, 0x00 };
        Assert.True(AvatarCoinParser.TryParse(body, out AvatarCoinNotice n));
        Assert.True(n.Success);
        Assert.Equal(2, n.Flag);
        Assert.Equal(1u, n.Coin);
    }

    [Fact]
    public void CoinEveryProperPrefixIsRejected()
    {
        byte[] success = { 0x01, 0x78, 0x56, 0x34, 0x12 };
        for (int length = 0; length < success.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(success, prefix, length);
            Assert.False(AvatarCoinParser.TryParse(prefix, out AvatarCoinNotice n), "成功体前缀长度 " + length);
            Assert.Null(n);
        }
        Assert.False(AvatarCoinParser.TryParse(new byte[] { 0x00 }, out AvatarCoinNotice failure), "失败体缺错误码");
        Assert.Null(failure);
    }

    [Fact]
    public void CoinPayloadFromTheDecoderStillStartsWithTheSuccessFlag()
    {
        // 载荷约定的结论:DecodedFrame.Payload 是解密后、从包体第一个字段起的字节,类 1 应答的成功标志没有被剥掉
        // (SelectCharacterResultParser 同样自己从载荷第一个字节读标志)。这里走真实的解码器验证:载荷 = 标志 + u32,而不是只有 u32。
        byte[] body = { 0x01, 0x78, 0x56, 0x34, 0x12 };
        DecodedFrame frame = FrameOf(ClassCmd, 587, body);
        Assert.Equal(PacketClass.Cmd, frame.Header.Class);
        Assert.Equal(587, frame.Header.PacketId);
        Assert.Equal(body, frame.Payload);
        Assert.Equal(1, frame.Payload[0]);

        Assert.True(AvatarCoinParser.TryParse(frame.Payload, out AvatarCoinNotice n));
        Assert.True(n.Success);
        Assert.Equal(0x12345678u, n.Coin);
    }

    [Fact]
    public void CoinParserDoesNotAcceptTheFlaglessForm()
    {
        // 假如载荷里的标志已经被分发层吃掉,剩下的只有 4 字节数值:本解析器会把数值的第一个字节当标志,
        // 然后因为不够 4 字节而拒绝——不会悄悄读出一个错位的数。
        byte[] flagless = { 0x78, 0x56, 0x34, 0x12 };
        Assert.False(AvatarCoinParser.TryParse(flagless, out AvatarCoinNotice n));
        Assert.Null(n);
    }

    [Fact]
    public void CoinDirectFrameGeometryReconciles()
    {
        // 正文 5 + PI 尾 4 = 9,587 % 14 = 13,块长 16:载荷 16 字节,读完后剩 11。
        byte[] body = { 0x01, 0x78, 0x56, 0x34, 0x12 };
        byte[] payload = DirectFramePayload(body, 587);
        Assert.Equal(16, payload.Length);
        Assert.True(AvatarCoinParser.TryParse(payload, out AvatarCoinNotice n));
        Assert.Equal(0x12345678u, n.Coin);
        Assert.Equal(11, n.TrailingByteCount);
    }

    [Fact]
    public void CoinNullAndEmptyPayloadsAreRejected()
    {
        Assert.False(AvatarCoinParser.TryParse(null, out AvatarCoinNotice n));
        Assert.Null(n);
        Assert.False(AvatarCoinParser.TryParse(new byte[0], out n));
    }

    // ---- 持续型物品 172 / 174:u8 个数 + n × (u32, u32) ----

    private static readonly byte[] TwoPairs =
    {
        0x02,
        0x11, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00,
        0x33, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00,
    };

    [Fact]
    public void ContinuousListReadsPairsInWireOrder()
    {
        Assert.True(ContinuousItemListParser.TryParse(TwoPairs, out ContinuousItemListNotice n));
        Assert.Equal(2, n.Entries.Count);
        Assert.Equal(0x11u, n.Entries[0].First);
        Assert.Equal(0x22u, n.Entries[0].Second);
        Assert.Equal(0x33u, n.Entries[1].First);
        Assert.Equal(0x44u, n.Entries[1].Second);
        Assert.Equal(TwoPairs.Length, n.ConsumedByteCount);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void ContinuousListValuesAreUnsignedFourByteLittleEndian()
    {
        byte[] body = { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x80 };
        Assert.True(ContinuousItemListParser.TryParse(body, out ContinuousItemListNotice n));
        Assert.Equal(uint.MaxValue, n.Entries[0].First);
        Assert.Equal(0x80000000u, n.Entries[0].Second);
    }

    [Theory]
    [InlineData(172, 16)]
    [InlineData(174, 8)]
    public void ContinuousEmptyTableGeometryInDirectFrames(int packetId, int expectedLength)
    {
        // 空表正文 1 字节 + PI 尾 4 = 5;172 % 14 = 4(块长 16)、174 % 14 = 6(块长 8)。
        byte[] payload = DirectFramePayload(new byte[] { 0x00 }, packetId);
        Assert.Equal(expectedLength, payload.Length);
        Assert.True(ContinuousItemListParser.TryParse(payload, out ContinuousItemListNotice n));
        Assert.Empty(n.Entries);
        Assert.Equal(1, n.ConsumedByteCount);
        Assert.Equal(expectedLength - 1, n.TrailingByteCount);
    }

    [Fact]
    public void ContinuousListCountThatCannotFitIsRejected()
    {
        byte[] body = (byte[])TwoPairs.Clone();
        body[0] = 3;
        Assert.False(ContinuousItemListParser.TryParse(body, out ContinuousItemListNotice n));
        Assert.Null(n);

        // 最大个数 255 配空内容:先对剩余字节,不会分配。
        Assert.False(ContinuousItemListParser.TryParse(new byte[] { 0xFF }, out n));
    }

    [Fact]
    public void ContinuousListEveryProperPrefixIsRejected()
    {
        for (int length = 0; length < TwoPairs.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(TwoPairs, prefix, length);
            Assert.False(ContinuousItemListParser.TryParse(prefix, out ContinuousItemListNotice n), "前缀长度 " + length);
            Assert.Null(n);
        }
    }

    [Fact]
    public void ContinuousListTailIsOnlyCountedAndEntriesAreReadOnly()
    {
        byte[] payload = DirectFramePayload(TwoPairs, 172);
        Assert.True(ContinuousItemListParser.TryParse(payload, out ContinuousItemListNotice n));
        Assert.Equal(2, n.Entries.Count);
        Assert.Equal(payload.Length - TwoPairs.Length, n.TrailingByteCount);
        Assert.True(((ICollection<ContinuousItemEntry>)n.Entries).IsReadOnly);
    }

    [Fact]
    public void ContinuousListNullPayloadIsRejected()
    {
        Assert.False(ContinuousItemListParser.TryParse(null, out ContinuousItemListNotice n));
        Assert.Null(n);
        Assert.False(ContinuousItemListParser.TryParse(new byte[0], out n));
    }

    // ---- 物品锁 251:u16 个数 + n × (u8 空间, u16 槽, u8 锁类型, [u32 仅锁类型 2]) ----

    [Fact]
    public void ItemLockEmptyTableGeometryInADirectFrame()
    {
        // 空表正文 2 + PI 尾 4 + 补零 10 = 16(251 % 14 = 13,块长 16)。
        byte[] payload = DirectFramePayload(new byte[] { 0x00, 0x00 }, 251);
        Assert.Equal(16, payload.Length);
        Assert.True(ItemLockParser.TryParse(payload, out ItemLockNotice n));
        Assert.Empty(n.Entries);
        Assert.Equal(2, n.ConsumedByteCount);
        Assert.Equal(14, n.TrailingByteCount);
    }

    [Fact]
    public void ItemLockEntryWithoutTheExtraValueIsFourBytes()
    {
        byte[] body = { 0x01, 0x00, 0x00, 0x05, 0x00, 0x01 };
        Assert.True(ItemLockParser.TryParse(body, out ItemLockNotice n));
        Assert.Single(n.Entries);
        Assert.Equal(0, n.Entries[0].Space);
        Assert.Equal(5, n.Entries[0].Slot);
        Assert.Equal(1, n.Entries[0].LockType);
        Assert.False(n.Entries[0].HasExtra);
        Assert.Equal(0u, n.Entries[0].Extra);
        Assert.Equal(6, n.ConsumedByteCount);
    }

    [Fact]
    public void ItemLockTypeTwoCarriesAFourByteExtraValue()
    {
        byte[] body = { 0x01, 0x00, 0x07, 0x09, 0x00, 0x02, 0x78, 0x56, 0x34, 0x12 };
        Assert.True(ItemLockParser.TryParse(body, out ItemLockNotice n));
        Assert.Equal(7, n.Entries[0].Space);
        Assert.Equal(9, n.Entries[0].Slot);
        Assert.Equal(2, n.Entries[0].LockType);
        Assert.True(n.Entries[0].HasExtra);
        Assert.Equal(0x12345678u, n.Entries[0].Extra);
        Assert.Equal(10, n.ConsumedByteCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(0x82, false)]
    [InlineData(0xFF, false)]
    public void ItemLockExtraValueDependsOnTheLockTypeBeingExactlyTwo(int lockType, bool hasExtra)
    {
        // 后面故意跟 4 个字节:不该有额外值的类型读完 4 字节后剩下这 4 个字节。
        byte[] body = { 0x01, 0x00, 0x00, 0x01, 0x00, (byte)lockType, 0xA1, 0xA2, 0xA3, 0xA4 };
        Assert.True(ItemLockParser.TryParse(body, out ItemLockNotice n));
        Assert.Equal(hasExtra, n.Entries[0].HasExtra);
        Assert.Equal(hasExtra ? 10 : 6, n.ConsumedByteCount);
        Assert.Equal(hasExtra ? 0 : 4, n.TrailingByteCount);
    }

    private static readonly byte[] MixedLocks =
    {
        0x03, 0x00,
        0x00, 0x01, 0x00, 0x02, 0x11, 0x11, 0x11, 0x11,
        0x02, 0x02, 0x00, 0x01,
        0x07, 0x03, 0x00, 0x02, 0x22, 0x22, 0x22, 0x22,
    };

    [Fact]
    public void ItemLockMixedEntriesKeepTheCursorAligned()
    {
        Assert.True(ItemLockParser.TryParse(MixedLocks, out ItemLockNotice n));
        Assert.Equal(3, n.Entries.Count);
        Assert.Equal(0x11111111u, n.Entries[0].Extra);
        Assert.Equal(2, n.Entries[1].Space);
        Assert.Equal(2, n.Entries[1].Slot);
        Assert.False(n.Entries[1].HasExtra);
        Assert.Equal(7, n.Entries[2].Space);
        Assert.Equal(3, n.Entries[2].Slot);
        Assert.Equal(0x22222222u, n.Entries[2].Extra);
        Assert.Equal(MixedLocks.Length, n.ConsumedByteCount);
    }

    [Fact]
    public void ItemLockEveryProperPrefixIsRejected()
    {
        for (int length = 0; length < MixedLocks.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(MixedLocks, prefix, length);
            Assert.False(ItemLockParser.TryParse(prefix, out ItemLockNotice n), "前缀长度 " + length);
            Assert.Null(n);
        }
    }

    [Fact]
    public void ItemLockCountThatCannotFitIsRejected()
    {
        // 个数 0xFFFF 而只有 3 个字节:个数 × 4 先对剩余字节。
        Assert.False(ItemLockParser.TryParse(new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0x00 }, out ItemLockNotice n));
        Assert.Null(n);
        // 个数 2 而只有一项的字节。
        Assert.False(ItemLockParser.TryParse(new byte[] { 0x02, 0x00, 0x00, 0x01, 0x00, 0x01 }, out n));
    }

    [Fact]
    public void ItemLockTailIsOnlyCountedAndNullIsRejected()
    {
        byte[] payload = DirectFramePayload(MixedLocks, 251);
        Assert.True(ItemLockParser.TryParse(payload, out ItemLockNotice n));
        Assert.Equal(payload.Length - MixedLocks.Length, n.TrailingByteCount);
        Assert.True(((ICollection<ItemLockEntry>)n.Entries).IsReadOnly);

        Assert.False(ItemLockParser.TryParse(null, out n));
        Assert.Null(n);
        Assert.False(ItemLockParser.TryParse(new byte[0], out n));
        Assert.False(ItemLockParser.TryParse(new byte[] { 0x00 }, out n));
    }

    // ---- 装备等级上限 438:u16 ----

    [Fact]
    public void MaxEquipLevelReadsATwoByteValue()
    {
        Assert.True(MaxEquipLevelParser.TryParse(new byte[] { 0x34, 0x12 }, out MaxEquipLevelNotice n));
        Assert.Equal(0x1234, n.Level);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void MaxEquipLevelGeometryInADirectFrame()
    {
        // 正文 2 + PI 尾 4 + 补零 10 = 16(438 % 14 = 4,块长 16)。
        byte[] payload = DirectFramePayload(new byte[] { 0x34, 0x12 }, 438);
        Assert.Equal(16, payload.Length);
        Assert.True(MaxEquipLevelParser.TryParse(payload, out MaxEquipLevelNotice n));
        Assert.Equal(0x1234, n.Level);
        Assert.Equal(14, n.TrailingByteCount);
    }

    [Fact]
    public void MaxEquipLevelShortAndNullPayloadsAreRejected()
    {
        Assert.False(MaxEquipLevelParser.TryParse(new byte[] { 0x34 }, out MaxEquipLevelNotice n));
        Assert.Null(n);
        Assert.False(MaxEquipLevelParser.TryParse(new byte[0], out n));
        Assert.False(MaxEquipLevelParser.TryParse(null, out n));
    }

    // ---- 快捷键选项 452:u8 类型 + u32 长度(必须 0xBC)+ 94 × u16 ----

    private static byte[] HotkeyBody(int type = 3, long length = 0xBC, int fill = -1, int count = 94)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(type).U32(length);
        for (int i = 0; i < count; i++)
        {
            b.U16(fill >= 0 ? fill : i + 1);
        }
        return b.ToArray();
    }

    [Fact]
    public void HotkeyOptionReadsTypeAndNinetyFourBindings()
    {
        byte[] body = HotkeyBody();
        Assert.Equal(1 + 4 + 188, body.Length);
        Assert.True(HotkeyOptionParser.TryParse(body, out HotkeyOptionNotice n));
        Assert.Equal(3, n.Type);
        Assert.Equal(94, n.Bindings.Count);
        Assert.Equal(1, n.Bindings[0]);
        Assert.Equal(2, n.Bindings[1]);
        Assert.Equal(94, n.Bindings[93]);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void HotkeyOptionBindingsAreTwoByteLittleEndianWithEmptyMarkerKept()
    {
        byte[] body = HotkeyBody(fill: 0xFFFF);
        body[5] = 0x34;
        body[6] = 0x12;
        Assert.True(HotkeyOptionParser.TryParse(body, out HotkeyOptionNotice n));
        Assert.Equal(0x1234, n.Bindings[0]);
        Assert.Equal(0xFFFF, n.Bindings[1]);
        Assert.Equal(0xFFFF, n.Bindings[93]);
    }

    [Theory]
    [InlineData(0xBBL)]
    [InlineData(0xBDL)]
    [InlineData(0L)]
    [InlineData(0xBC00L)]
    [InlineData(0x1BCL)]
    [InlineData(0xFFFFFFFFL)]
    public void HotkeyOptionAnyLengthOtherThanBcRejectsTheWholePacket(long length)
    {
        Assert.False(HotkeyOptionParser.TryParse(HotkeyBody(length: length), out HotkeyOptionNotice n));
        Assert.Null(n);
    }

    [Fact]
    public void HotkeyOptionEveryProperPrefixIsRejected()
    {
        byte[] body = HotkeyBody();
        for (int length = 0; length < body.Length; length++)
        {
            byte[] prefix = new byte[length];
            Array.Copy(body, prefix, length);
            Assert.False(HotkeyOptionParser.TryParse(prefix, out HotkeyOptionNotice n), "前缀长度 " + length);
            Assert.Null(n);
        }
    }

    [Fact]
    public void HotkeyOptionGeometryInADirectFrame()
    {
        // 正文 193 + PI 尾 4 + 补零 11 = 208(452 % 14 = 4,块长 16)。
        byte[] payload = DirectFramePayload(HotkeyBody(), 452);
        Assert.Equal(208, payload.Length);
        Assert.True(HotkeyOptionParser.TryParse(payload, out HotkeyOptionNotice n));
        Assert.Equal(15, n.TrailingByteCount);
        Assert.Equal(193 + n.TrailingByteCount, payload.Length);
    }

    [Fact]
    public void HotkeyOptionBindingsAreACopyAndReadOnly()
    {
        byte[] body = HotkeyBody();
        Assert.True(HotkeyOptionParser.TryParse(body, out HotkeyOptionNotice n));
        body[5] = 0x77;
        Assert.Equal(1, n.Bindings[0]);
        Assert.True(((ICollection<ushort>)n.Bindings).IsReadOnly);
    }

    [Fact]
    public void HotkeyOptionNullAndEmptyPayloadsAreRejected()
    {
        Assert.False(HotkeyOptionParser.TryParse(null, out HotkeyOptionNotice n));
        Assert.Null(n);
        Assert.False(HotkeyOptionParser.TryParse(new byte[0], out n));
    }
}
