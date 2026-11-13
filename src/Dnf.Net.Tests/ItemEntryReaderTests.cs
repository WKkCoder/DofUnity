using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 物品条目共用读取器(<see cref="ItemEntryReader"/>)的单元测试:每个形状的主干读序、C3 块、随机选项块、增幅类型、可选块判定与各种拒绝。
/// 夹具全部手工拼合成字节(见 <see cref="ItemWire"/>),合成物品号 900001 起,不含任何真实物品号或数值。
/// </summary>
public class ItemEntryReaderTests
{
    private static ItemEntry ReadOk(ItemEntryShape shape, byte[] bytes, IItemKindResolver resolver = null)
    {
        PacketReader reader = new PacketReader(bytes);
        Assert.True(ItemEntryReader.TryRead(reader, shape, resolver, out ItemEntry entry, out ItemEntryFault fault), "拒绝原因 " + fault);
        Assert.Equal(ItemEntryFault.None, fault);
        Assert.Equal(bytes.Length, reader.Position);
        Assert.Equal(bytes.Length, entry.WireLength);
        return entry;
    }

    private static void AssertFault(ItemEntryShape shape, byte[] bytes, ItemEntryFault expected, IItemKindResolver resolver = null)
    {
        PacketReader reader = new PacketReader(bytes);
        Assert.False(ItemEntryReader.TryRead(reader, shape, resolver, out ItemEntry entry, out ItemEntryFault fault));
        Assert.Null(entry);
        Assert.Equal(expected, fault);
    }

    // ---- 背包形:主干 25 字节,偏移逐个钉死 ----

    [Fact]
    public void BagEntryFieldsAreReadFromTheirLiteralOffsets()
    {
        byte[] bytes =
        {
            0x07, 0x01,             // 0..1   槽号 0x0107
            0xA1, 0xBB, 0x0D, 0x00, // 2..5   物品号 900001
            0x44, 0x33, 0x22, 0x11, // 6..9   Data 0x11223344
            0xB3,                   // 10     ext1(低 5 位 19,高 3 位 5)
            0x0B, 0x0A,             // 11..12 耐久 0x0A0B
            0x01,                   // 13     封装字节
            0x02, 0x01, 0x00, 0x00, // 14..17 附魔 0x0102
            0x05,                   // 18     增幅类型
            0x04, 0x03,             // 19..20 增幅值 0x0304
            0x00,                   // 21     C3 个数
            0x00,                   // 22     随机选项个数
            0x07,                   // 23     单独强化
            0x01,                   // 24     交易限制
        };
        Assert.Equal(25, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.Bag, bytes);
        Assert.Equal(ItemEntryShape.Bag, e.Shape);
        Assert.Equal(0x0107, e.Slot);
        Assert.Equal(900001u, e.ItemId);
        Assert.Equal(0x11223344u, e.Data);
        Assert.False(e.DataIsRemainingTime);
        Assert.Equal(0xB3, e.Ext1);
        Assert.Equal(19, e.UpgradeLevel);
        Assert.Equal(5, e.Ext1HighBits);
        Assert.Equal(0x0A0B, e.Durability);
        Assert.True(e.HasSealedByte);
        Assert.Equal(1, e.SealedByte);
        Assert.False(e.HasClearAvatar);
        Assert.Equal(0x0102u, e.EnchantIndex);
        Assert.Equal(5, e.AmplifyType);
        Assert.Equal(0x0304, e.AmplifyValue);
        Assert.False(e.HasAvatarBlocks);
        Assert.False(e.HasPetRemainTime);
        Assert.Empty(e.C3Records);
        Assert.Empty(e.RandomOptionFields);
        Assert.Equal(7, e.SeparateUpgrade);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Fact]
    public void BagEntryMinimumLengthConstantsMatchTheHandBuiltEntries()
    {
        Assert.Equal(25, BagEntryBytes(Item(3)).Length);
        Assert.Equal(25, ItemEntryReader.MinLength(ItemEntryShape.Bag));
        Assert.Equal(25, ItemEntryReader.MinLength(ItemEntryShape.PetBag));
        Assert.Equal(25, ItemEntryReader.MinLength(ItemEntryShape.BodyEquip));
        Assert.Equal(67, ItemEntryReader.MinLength(ItemEntryShape.AvatarBag));
        Assert.Equal(27, ItemEntryReader.MinLength(ItemEntryShape.EquipDetail));
    }

    [Fact]
    public void SealedSeparateUpgradeAndTradeRestrictionBytesAreKeptAsIs()
    {
        ItemSpec s = Item(5);
        s.Sealed = 0xFE;
        s.Separate = 0x21;
        s.Trade = 0x01;
        ItemEntry e = ReadOk(ItemEntryShape.Bag, BagEntryBytes(s));
        Assert.Equal(0xFE, e.SealedByte);
        Assert.Equal(0x21, e.SeparateUpgrade);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Theory]
    [InlineData(0x00, 0, 0)]
    [InlineData(0x1F, 31, 0)]
    [InlineData(0x20, 0, 1)]
    [InlineData(0xFF, 31, 7)]
    [InlineData(0xA5, 5, 5)]
    public void Ext1SplitsTheLowFiveBitsFromTheHighThreeBits(int ext1, int level, int high)
    {
        ItemSpec s = Item(4);
        s.Ext1 = ext1;
        ItemEntry e = ReadOk(ItemEntryShape.Bag, BagEntryBytes(s));
        Assert.Equal(level, e.UpgradeLevel);
        Assert.Equal(high, e.Ext1HighBits);
    }

    // ---- 增幅类型字节:同时给出"是否增幅"与"是否已鉴定" ----

    [Theory]
    [InlineData(0x00, false, true)]
    [InlineData(0x80, true, false)]
    [InlineData(0x01, true, true)]
    [InlineData(0x1B, true, true)]
    [InlineData(0x7F, true, true)]
    public void AmplifyTypeByteYieldsTwoBooleans(int type, bool amplified, bool identified)
    {
        ItemSpec s = Item(4);
        s.AmpType = type;
        s.AmpValue = type == 0x80 || type == 0 ? 0 : 321;
        ItemEntry e = ReadOk(ItemEntryShape.Bag, BagEntryBytes(s));
        Assert.Equal(type, e.AmplifyType);
        Assert.Equal(amplified, e.IsAmplified);
        Assert.Equal(identified, e.IsIdentified);
    }

    [Fact]
    public void AmplifyTypeBytesAboveTheUnidentifiedMarkerCountAsUnidentified()
    {
        // 服务端 isIdentified 的口径是"最高位为 0";0x81 到 0xFF 服务端不会产生,按口径判为未鉴定而不是已鉴定。
        ItemSpec s = Item(4);
        s.AmpType = 0x85;
        ItemEntry e = ReadOk(ItemEntryShape.Bag, BagEntryBytes(s));
        Assert.True(e.IsAmplified);
        Assert.False(e.IsIdentified);
    }

    // ---- C3 块:每项 8 字节(一个 u32 + 四个 u8) ----

    [Fact]
    public void C3BlockWithOneItemTakesEightMoreBytes()
    {
        ItemSpec s = Item(6);
        s.C3 = new[] { (0x01020304L, 0x11, 0x12, 0x13, 0x14) };
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(25 + 8, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.Bag, bytes);
        Assert.Single(e.C3Records);
        Assert.Equal(0x01020304u, e.C3Records[0].Value);
        Assert.Equal(0x11, e.C3Records[0].Byte1);
        Assert.Equal(0x12, e.C3Records[0].Byte2);
        Assert.Equal(0x13, e.C3Records[0].Byte3);
        Assert.Equal(0x14, e.C3Records[0].Byte4);
        // C3 之后的字段仍在正确位置。
        Assert.Equal(0, e.SeparateUpgrade);
    }

    [Fact]
    public void C3BlockWithTwoItemsTakesSixteenMoreBytesAndKeepsOrder()
    {
        ItemSpec s = Item(6);
        s.C3 = new[] { (101L, 1, 2, 3, 4), (202L, 5, 6, 7, 8) };
        s.Separate = 9;
        s.Trade = 1;
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(25 + 16, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.Bag, bytes);
        Assert.Equal(2, e.C3Records.Count);
        Assert.Equal(101u, e.C3Records[0].Value);
        Assert.Equal(202u, e.C3Records[1].Value);
        Assert.Equal(8, e.C3Records[1].Byte4);
        Assert.Equal(9, e.SeparateUpgrade);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Fact]
    public void C3CountThatLiesAboutTheRemainingBytesIsRejected()
    {
        // 声明 255 项(2040 字节),实际后面只有几个字节:不按声明去读。
        byte[] bytes = new NoticePayloadBuilder()
            .U16(6).U32(ItemA).U32(0).U8(0).U16(0).U8(0).U32(0).U8(0).U16(0)
            .U8(255).U8(0).U8(0).U8(0).U8(0)
            .ToArray();
        AssertFault(ItemEntryShape.Bag, bytes, ItemEntryFault.Truncated);
    }

    // ---- 随机选项块 ----

    [Fact]
    public void RandomOptionWithZeroFieldsIsOneByteAndNoSelector()
    {
        ItemEntry e = ReadOk(ItemEntryShape.Bag, BagEntryBytes(Item(7)));
        Assert.Empty(e.RandomOptionFields);
        Assert.Equal(0, e.RandomOptionSelector);
        Assert.False(e.HasRandomOptionTail);
        Assert.Empty(e.RandomOptionTail);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomOptionWithoutTailIsThreeBytesPerFieldPlusSelectorAndMarker(int fields)
    {
        ItemSpec s = Item(7);
        s.RoFields = Enumerable.Range(0, fields).Select(i => (10 + i, 20 + i, 30 + i)).ToArray();
        s.RoSelector = 0x42;
        s.RoTailMarker = 0xFF;
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(25 + 3 * fields + 2, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.Bag, bytes);
        Assert.Equal(fields, e.RandomOptionFields.Count);
        for (int i = 0; i < fields; i++)
        {
            Assert.Equal(10 + i, e.RandomOptionFields[i].First);
            Assert.Equal(20 + i, e.RandomOptionFields[i].Second);
            Assert.Equal(30 + i, e.RandomOptionFields[i].Third);
        }
        Assert.Equal(0x42, e.RandomOptionSelector);
        Assert.Equal(0xFF, e.RandomOptionTailMarker);
        Assert.False(e.HasRandomOptionTail);
        Assert.Empty(e.RandomOptionTail);
    }

    [Theory]
    [InlineData(1, 0x00)]
    [InlineData(2, 0x03)]
    [InlineData(3, 0xFE)]
    public void RandomOptionWithANonFfMarkerCarriesFourMoreBytes(int fields, int marker)
    {
        ItemSpec s = Item(7);
        s.RoFields = Enumerable.Range(0, fields).Select(i => (1 + i, 2 + i, 3 + i)).ToArray();
        s.RoSelector = 0x09;
        s.RoTailMarker = marker;
        s.RoTail = new byte[] { 0xC1, 0xC2, 0xC3, 0xC4 };
        s.Separate = 0x31;
        s.Trade = 0x01;
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(25 + 3 * fields + 2 + 4, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.Bag, bytes);
        Assert.Equal(marker, e.RandomOptionTailMarker);
        Assert.True(e.HasRandomOptionTail);
        Assert.Equal(new byte[] { 0xC1, 0xC2, 0xC3, 0xC4 }, e.RandomOptionTail.ToArray());
        // 尾部之后的两个字节没有错位。
        Assert.Equal(0x31, e.SeparateUpgrade);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Fact]
    public void RandomOptionCountAboveThreeIsRejected()
    {
        byte[] bytes = new NoticePayloadBuilder()
            .U16(7).U32(ItemA).U32(0).U8(0).U16(0).U8(0).U32(0).U8(0).U16(0)
            .U8(0)
            .U8(4).Raw(new byte[64])
            .ToArray();
        AssertFault(ItemEntryShape.Bag, bytes, ItemEntryFault.InvalidRandomOption);
    }

    // ---- 装扮背包形 ----

    [Fact]
    public void AvatarBagEntryAlwaysCarriesTheTwoBlocksAndDataIsRemainingTime()
    {
        ItemSpec s = Item(11, ItemB);
        s.Data = 77;
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(67, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.AvatarBag, bytes);
        Assert.True(e.DataIsRemainingTime);
        Assert.Equal(77u, e.Data);
        Assert.True(e.HasAvatarBlocks);
        Assert.Equal(Socket().ToArray(), e.SocketBlock.ToArray());
        Assert.Equal(Expansion().ToArray(), e.ExpansionBlock.ToArray());
        Assert.False(e.HasPetRemainTime);
    }

    [Fact]
    public void AvatarBagEntryStillReadsAnInlineC3Block()
    {
        ItemSpec s = Item(12, ItemB);
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        s.C3 = new[] { (555L, 1, 2, 3, 4) };
        s.Trade = 1;
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(67 + 8, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.AvatarBag, bytes);
        Assert.Single(e.C3Records);
        Assert.Equal(555u, e.C3Records[0].Value);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(0)]
    [InlineData(0x1E0000)]
    public void AvatarBagSocketBlockLengthThatIsNotThirtyIsRejected(int length)
    {
        byte[] bytes = new NoticePayloadBuilder()
            .U16(11).U32(ItemB).U32(0).U8(0).U16(0).U8(0).U32(0).U8(0).U16(0)
            .U32(length).Raw(new byte[80])
            .ToArray();
        AssertFault(ItemEntryShape.AvatarBag, bytes, ItemEntryFault.InvalidBlockLength);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(0)]
    public void AvatarBagExpansionBlockLengthThatIsNotFourIsRejected(int length)
    {
        byte[] bytes = new NoticePayloadBuilder()
            .U16(11).U32(ItemB).U32(0).U8(0).U16(0).U8(0).U32(0).U8(0).U16(0)
            .U32(30).Raw(Socket())
            .U32(length).Raw(new byte[16])
            .ToArray();
        AssertFault(ItemEntryShape.AvatarBag, bytes, ItemEntryFault.InvalidBlockLength);
    }

    [Fact]
    public void AvatarBagEntryCutInsideTheSocketBlockIsTruncated()
    {
        ItemSpec s = Item(11, ItemB);
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        byte[] full = BagEntryBytes(s);
        // 25 字节主干里 C3/RO/尾两字节还没到,先截到镶嵌块中段:偏移 21 起是 u32 长度,再 30 字节。
        byte[] cut = full.Take(21 + 4 + 10).ToArray();
        AssertFault(ItemEntryShape.AvatarBag, cut, ItemEntryFault.Truncated);
    }

    // ---- 宠物栏形:宠物类条目多一个 u32,类型靠回调 ----

    [Fact]
    public void PetBagPetEntryReadsTheExtraU32WhenTheResolverSaysPet()
    {
        ItemSpec s = Item(2, ItemC);
        s.PetTime = 86400;
        s.Trade = 1;
        byte[] bytes = BagEntryBytes(s);
        Assert.Equal(25 + 4, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.PetBag, bytes, new DictionaryKindResolver().With(ItemC, ResolvedItemKind.Pet));
        Assert.True(e.HasPetRemainTime);
        Assert.Equal(86400u, e.PetRemainTime);
        Assert.False(e.HasAvatarBlocks);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Fact]
    public void PetBagOrdinaryEntryHasNoExtraBytes()
    {
        byte[] bytes = BagEntryBytes(Item(2, ItemC));
        ItemEntry e = ReadOk(ItemEntryShape.PetBag, bytes, new DictionaryKindResolver().With(ItemC, ResolvedItemKind.Other));
        Assert.False(e.HasPetRemainTime);
        Assert.Equal(0u, e.PetRemainTime);
    }

    [Fact]
    public void PetBagIgnoresTheAvatarVerdictBecauseTheServerWriterOnlyHasThePetBranch()
    {
        byte[] bytes = BagEntryBytes(Item(2, ItemC));
        ItemEntry e = ReadOk(ItemEntryShape.PetBag, bytes, new DictionaryKindResolver().With(ItemC, ResolvedItemKind.Avatar));
        Assert.False(e.HasAvatarBlocks);
        Assert.False(e.HasPetRemainTime);
    }

    [Fact]
    public void PetBagWithoutAResolverIsRejectedAsKindUnresolved()
    {
        AssertFault(ItemEntryShape.PetBag, BagEntryBytes(Item(2, ItemC)), ItemEntryFault.KindUnresolved);
    }

    [Fact]
    public void PetBagWithAResolverThatAnswersUnknownIsRejectedAsKindUnresolved()
    {
        DictionaryKindResolver resolver = new DictionaryKindResolver();
        AssertFault(ItemEntryShape.PetBag, BagEntryBytes(Item(2, ItemC)), ItemEntryFault.KindUnresolved, resolver);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public void PetBagDeleteMarkerEntryNeedsNoResolverAndHasNoOptionalBlocks()
    {
        ItemEntry e = ReadOk(ItemEntryShape.PetBag, BagEntryBytes(Item(9, DeleteMarker)));
        Assert.Equal(ItemEntryReader.DeleteMarker, e.ItemId);
        Assert.Equal(25, e.WireLength);
    }

    // ---- 身上装备形(增量包空间 3) ----

    [Fact]
    public void BodyEquipAvatarItemReadsBothBlocksButNoPetBytes()
    {
        ItemSpec s = Item(4, ItemB);
        s.SocketBlock = Socket(0x10);
        s.ExpansionBlock = Expansion(0x70);
        ItemEntry e = ReadOk(ItemEntryShape.BodyEquip, BagEntryBytes(s), new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Avatar));
        Assert.True(e.HasAvatarBlocks);
        Assert.False(e.HasPetRemainTime);
        Assert.Equal(Socket(0x10).ToArray(), e.SocketBlock.ToArray());
        Assert.Equal(67, e.WireLength);
    }

    [Fact]
    public void BodyEquipPetItemReadsTheExtraU32ButNoBlocks()
    {
        ItemSpec s = Item(22, ItemC);
        s.PetTime = 3600;
        ItemEntry e = ReadOk(ItemEntryShape.BodyEquip, BagEntryBytes(s), new DictionaryKindResolver().With(ItemC, ResolvedItemKind.Pet));
        Assert.True(e.HasPetRemainTime);
        Assert.Equal(3600u, e.PetRemainTime);
        Assert.False(e.HasAvatarBlocks);
    }

    [Fact]
    public void BodyEquipOrdinaryItemHasNoOptionalBlocks()
    {
        ItemEntry e = ReadOk(ItemEntryShape.BodyEquip, BagEntryBytes(Item(10, ItemA)), new DictionaryKindResolver().With(ItemA, ResolvedItemKind.Other));
        Assert.False(e.HasAvatarBlocks);
        Assert.False(e.HasPetRemainTime);
        Assert.Equal(25, e.WireLength);
    }

    [Fact]
    public void BodyEquipWithoutAResolverIsKindUnresolvedButDeleteMarkerIsNot()
    {
        AssertFault(ItemEntryShape.BodyEquip, BagEntryBytes(Item(10, ItemA)), ItemEntryFault.KindUnresolved);
        ItemEntry e = ReadOk(ItemEntryShape.BodyEquip, BagEntryBytes(Item(10, DeleteMarker)));
        Assert.Equal(ItemEntryReader.DeleteMarker, e.ItemId);
    }

    // ---- 装备详细记录形:u8 槽号、没有封装字节、多一个"清除装扮" ----

    [Fact]
    public void EquipDetailEntryFieldsAreReadFromTheirLiteralOffsets()
    {
        byte[] bytes =
        {
            0x0A,                   // 0      槽号 10(u8)
            0xA1, 0xBB, 0x0D, 0x00, // 1..4   物品号 900001
            0x10, 0x20, 0x30, 0x40, // 5..8   Data
            0x23,                   // 9      ext1(低 5 位 3,高 3 位 1)
            0x05, 0x00,             // 10..11 耐久 5
            0x99, 0x88, 0x77, 0x66, // 12..15 清除装扮
            0x0C, 0x00, 0x00, 0x00, // 16..19 附魔 12
            0x80,                   // 20     增幅类型(有增幅未鉴定)
            0x00, 0x00,             // 21..22 增幅值
            0x00,                   // 23     C3 个数
            0x00,                   // 24     随机选项个数
            0x02,                   // 25     单独强化
            0x01,                   // 26     交易限制
        };
        Assert.Equal(27, bytes.Length);
        ItemEntry e = ReadOk(ItemEntryShape.EquipDetail, bytes);
        Assert.Equal(10, e.Slot);
        Assert.Equal(900001u, e.ItemId);
        Assert.Equal(0x40302010u, e.Data);
        Assert.False(e.DataIsRemainingTime);
        Assert.Equal(3, e.UpgradeLevel);
        Assert.Equal(1, e.Ext1HighBits);
        Assert.Equal(5, e.Durability);
        Assert.False(e.HasSealedByte);
        Assert.Equal(0, e.SealedByte);
        Assert.True(e.HasClearAvatar);
        Assert.Equal(0x66778899u, e.ClearAvatar);
        Assert.Equal(12u, e.EnchantIndex);
        Assert.True(e.IsAmplified);
        Assert.False(e.IsIdentified);
        Assert.Equal(2, e.SeparateUpgrade);
        Assert.Equal(1, e.TradeRestriction);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(21, false)]
    [InlineData(25, false)]
    public void EquipDetailWithoutAResolverGuessesAvatarBlocksForSlotsUpToNineOnly(int slot, bool blocks)
    {
        ItemSpec s = Item(slot, ItemB);
        if (blocks)
        {
            s.SocketBlock = Socket();
            s.ExpansionBlock = Expansion();
        }
        ItemEntry e = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(s));
        Assert.Equal(blocks, e.HasAvatarBlocks);
        Assert.Equal(blocks, e.DataIsRemainingTime);
        Assert.Equal(blocks ? 27 + 34 + 8 : 27, e.WireLength);
    }

    [Fact]
    public void EquipDetailWithoutAResolverGuessesPetBytesForSlotTwentyTwoOnly()
    {
        ItemSpec pet = Item(22, ItemC);
        pet.PetTime = 4321;
        ItemEntry e = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(pet));
        Assert.True(e.HasPetRemainTime);
        Assert.Equal(4321u, e.PetRemainTime);
        Assert.False(e.HasAvatarBlocks);
        Assert.Equal(27 + 4, e.WireLength);

        ItemEntry other = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(Item(21, ItemC)));
        Assert.False(other.HasPetRemainTime);
        ItemEntry next = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(Item(23, ItemC)));
        Assert.False(next.HasPetRemainTime);
    }

    [Fact]
    public void EquipDetailResolverVerdictOverridesTheSlotGuess()
    {
        // 槽 3 按近似是装扮,但回调说它是普通物品:不读装扮块。
        DictionaryKindResolver ordinary = new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Other);
        ItemEntry a = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(Item(3, ItemB)), ordinary);
        Assert.False(a.HasAvatarBlocks);
        Assert.Equal(27, a.WireLength);

        // 槽 15 按近似不是装扮,但回调说它是装扮:读装扮块。
        ItemSpec avatar = Item(15, ItemB);
        avatar.SocketBlock = Socket();
        avatar.ExpansionBlock = Expansion();
        DictionaryKindResolver isAvatar = new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Avatar);
        ItemEntry b = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(avatar), isAvatar);
        Assert.True(b.HasAvatarBlocks);
    }

    [Fact]
    public void EquipDetailResolverThatAnswersUnknownFallsBackToTheSlotGuess()
    {
        ItemSpec avatar = Item(2, ItemB);
        avatar.SocketBlock = Socket();
        avatar.ExpansionBlock = Expansion();
        ItemEntry e = ReadOk(ItemEntryShape.EquipDetail, DetailEntryBytes(avatar), new DictionaryKindResolver());
        Assert.True(e.HasAvatarBlocks);
    }

    // ---- 拒绝与边界 ----

    [Fact]
    public void EveryShorterPrefixOfARichEntryIsRejectedAsTruncatedWithoutException()
    {
        ItemSpec s = Item(8, ItemA);
        s.C3 = new[] { (1L, 2, 3, 4, 5), (6L, 7, 8, 9, 10) };
        s.RoFields = new[] { (1, 2, 3), (4, 5, 6), (7, 8, 9) };
        s.RoSelector = 3;
        s.RoTailMarker = 0x01;
        s.RoTail = new byte[] { 1, 2, 3, 4 };
        s.Separate = 2;
        s.Trade = 1;
        byte[] full = BagEntryBytes(s);
        ReadOk(ItemEntryShape.Bag, full);
        for (int length = 0; length < full.Length; length++)
        {
            AssertFault(ItemEntryShape.Bag, full.Take(length).ToArray(), ItemEntryFault.Truncated);
        }
    }

    [Fact]
    public void EveryShorterPrefixOfAnAvatarBagEntryIsRejectedAsTruncated()
    {
        ItemSpec s = Item(8, ItemA);
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        byte[] full = BagEntryBytes(s);
        ReadOk(ItemEntryShape.AvatarBag, full);
        for (int length = 0; length < full.Length; length++)
        {
            AssertFault(ItemEntryShape.AvatarBag, full.Take(length).ToArray(), ItemEntryFault.Truncated);
        }
    }

    [Fact]
    public void TheReaderStopsAfterTheLastFieldAndLeavesFollowingBytesUntouched()
    {
        byte[] one = BagEntryBytes(Item(3));
        byte[] twice = one.Concat(one).ToArray();
        PacketReader reader = new PacketReader(twice);
        Assert.True(ItemEntryReader.TryRead(reader, ItemEntryShape.Bag, null, out ItemEntry first, out _));
        Assert.Equal(25, reader.Position);
        Assert.Equal(25, reader.Remaining);
        Assert.True(ItemEntryReader.TryRead(reader, ItemEntryShape.Bag, null, out ItemEntry second, out _));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(3, first.Slot);
        Assert.Equal(3, second.Slot);
    }

    [Fact]
    public void NullReaderAndUnknownShapeAreRejectedWithoutException()
    {
        Assert.False(ItemEntryReader.TryRead(null, ItemEntryShape.Bag, null, out ItemEntry entry, out ItemEntryFault fault));
        Assert.Null(entry);
        Assert.Equal(ItemEntryFault.Truncated, fault);
        Assert.False(ItemEntryReader.TryRead(new PacketReader(BagEntryBytes(Item(1))), (ItemEntryShape)99, null, out entry, out _));
        Assert.Null(entry);
    }

    [Fact]
    public void ReturnedListsAreReadOnlyViews()
    {
        ItemSpec s = Item(6);
        s.C3 = new[] { (1L, 2, 3, 4, 5) };
        s.RoFields = new[] { (1, 2, 3) };
        s.RoTailMarker = 0;
        ItemEntry e = ReadOk(ItemEntryShape.Bag, BagEntryBytes(s));
        Assert.True(((System.Collections.IList)e.C3Records).IsReadOnly);
        Assert.True(((System.Collections.IList)e.RandomOptionFields).IsReadOnly);
        Assert.True(((System.Collections.IList)e.RandomOptionTail).IsReadOnly);
        Assert.True(((System.Collections.IList)e.SocketBlock).IsReadOnly);
    }
}
