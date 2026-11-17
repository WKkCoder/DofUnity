namespace Dnf.Play.Tests;

/// <summary>
/// 服务端角色列表 → 选角界面名册(<see cref="OnlineCharacterRoster"/>):0、1、12、13 个以上的角色一个不丢且顺序不变、UTF-8 名字与非法序列、
/// X1 与待删除与状态字节拦住进入、等级 u8 边界、职业与成长类型的拆分和称号(含英文占位)、装备原样带过去、槽号是列表位置而不是 Uid。
/// 角色全是合成的。
/// </summary>
public class OnlineCharacterRosterTests
{
    // ------------------------------------------------------------------ 数量

    [Fact]
    public void ANullOrEmptyListIsAnEmptyRoster()
    {
        CharacterRoster none = OnlineCharacterRoster.From(null);
        Assert.Equal(0, none.Count);
        Assert.Equal(0, none.TotalRows);
        Assert.Equal(0, none.MaxFirstRow);
        Assert.False(none.IsScrollable);

        CharacterRoster empty = OnlineCharacterRoster.From(RosterKit.List());
        Assert.Equal(0, empty.Count);
    }

    [Fact]
    public void OneCharacterIsOneRowAndNotScrollable()
    {
        CharacterRoster roster = RosterKit.Roster(1);
        Assert.Equal(1, roster.Count);
        Assert.Equal(1, roster.TotalRows);
        Assert.Equal(0, roster.MaxFirstRow);
        Assert.False(roster.IsScrollable);
        Assert.Equal("角色1", roster[0].Name);
    }

    [Fact]
    public void TwelveCharactersFillTheWindowExactly()
    {
        CharacterRoster roster = RosterKit.Roster(12);
        Assert.Equal(12, roster.Count);
        Assert.Equal(2, roster.TotalRows);
        Assert.Equal(0, roster.MaxFirstRow);
        Assert.False(roster.IsScrollable);
    }

    [Theory]
    [InlineData(13, 3, 1)]
    [InlineData(18, 3, 1)]
    [InlineData(19, 4, 2)]
    [InlineData(24, 4, 2)]
    [InlineData(25, 5, 3)]
    [InlineData(60, 10, 8)]
    public void MoreThanTwelveCharactersAreAllKeptAndScroll(int count, int rows, int maxFirstRow)
    {
        CharacterRoster roster = RosterKit.Roster(count);
        Assert.Equal(count, roster.Count);                                 // 一个也不丢
        Assert.Equal(rows, roster.TotalRows);
        Assert.Equal(maxFirstRow, roster.MaxFirstRow);
        Assert.True(roster.IsScrollable);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal("角色" + (i + 1), roster[i].Name);               // 顺序不变
        }
    }

    [Fact]
    public void ThePublicRosterConstructorStillRefusesMoreThanTheCapacity()
    {
        var slots = Enumerable.Range(0, 13).Select(i => new CharacterSlot("n" + i, "j", 1)).ToList();
        Assert.Throws<ArgumentException>(() => new CharacterRoster(slots));
        Assert.Equal(13, CharacterRoster.Scrollable(slots).Count);
        Assert.Throws<ArgumentNullException>(() => CharacterRoster.Scrollable(null));
    }

    // ------------------------------------------------------------------ 槽号与顺序

    [Fact]
    public void TheSlotIsTheListPositionNotTheUid()
    {
        OnlineCharacterList list = RosterKit.List(
            RosterKit.Entry("甲", uid: 905), RosterKit.Entry("乙", uid: 901), RosterKit.Entry("丙", uid: 903), RosterKit.Entry("丁", uid: 901));
        CharacterRoster roster = OnlineCharacterRoster.From(list);

        Assert.Equal(new[] { "甲", "乙", "丙", "丁" }, roster.Slots.Select(s => s.Name));
        Assert.Equal(new[] { 0, 1, 2, 3 }, roster.Slots.Select(s => s.Online.Slot));
        Assert.Equal(new[] { 905, 901, 903, 901 }, roster.Slots.Select(s => (int)s.Online.Entry.Uid));
    }

    [Fact]
    public void TheOriginalEntryIsKeptByReference()
    {
        OnlineCharacterList list = RosterKit.Many(3);
        CharacterRoster roster = OnlineCharacterRoster.From(list);
        for (int i = 0; i < 3; i++)
        {
            Assert.Same(list.Entries[i], roster[i].Online.Entry);
        }
    }

    // ------------------------------------------------------------------ 名字

    [Fact]
    public void MultiByteNamesDecodeAndTheCaptionCarriesTheLevel()
    {
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(RosterKit.Entry("测试é角色", level: 17)));
        Assert.Equal("测试é角色", roster[0].Name);
        Assert.Equal(17, roster[0].Level);
        Assert.Equal("Lv17 测试é角色", CharacterSelectText.Caption(roster[0], true));
    }

    [Fact]
    public void InvalidNamesBecomeEmptyAndTheOtherCharactersAreUntouched()
    {
        var list = RosterKit.List(
            RosterKit.Entry("前一个"),
            RosterKit.EntryBytes(new byte[] { 0x41, 0xF0, 0x9F, 0x98, 0x80 }),                // 4 字节序列
            RosterKit.EntryBytes(new byte[] { 0xE6, 0xB5 }),                                  // 被截断
            RosterKit.Entry("后一个"));
        CharacterRoster roster = OnlineCharacterRoster.From(list);
        Assert.Equal(4, roster.Count);
        Assert.Equal("前一个", roster[0].Name);
        Assert.Equal(string.Empty, roster[1].Name);
        Assert.Equal(string.Empty, roster[2].Name);
        Assert.Equal("后一个", roster[3].Name);
        Assert.Equal("Lv1", CharacterSelectText.Caption(roster[1], true));                    // 空名字只剩等级,不留尾部空格
    }

    [Fact]
    public void AnEmptyNameUsesTheDefaultNameOfTheTitleSource()
    {
        var list = RosterKit.List(RosterKit.EntryBytes(new byte[0]));
        Assert.Equal("NONAME", OnlineCharacterRoster.From(list)[0].Name);                      // 没有称号来源:英文占位
        Assert.Equal("NONAME", OnlineCharacterRoster.From(list, PlaceholderTitles.Instance)[0].Name);
        Assert.Equal("默认名", OnlineCharacterRoster.From(list, new StubTitles())[0].Name);
    }

    [Fact]
    public void ANameLongerThanThirtyBytesUsesTheDefaultName()
    {
        var list = RosterKit.List(RosterKit.EntryBytes(Enumerable.Repeat((byte)0x41, 31).ToArray()));
        Assert.Equal("默认名", OnlineCharacterRoster.From(list, new StubTitles())[0].Name);
    }

    // ------------------------------------------------------------------ 等级

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(255, 255)]
    public void TheLevelIsTheByteAndZeroShowsAsLevelOne(int level, int shown)
    {
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(RosterKit.Entry("名", level: (byte)level)));
        Assert.Equal(shown, roster[0].Level);
        Assert.Equal("Lv" + shown + " 名", CharacterSelectText.Caption(roster[0], true));
        Assert.Equal("LV" + shown + " 名".ToUpperInvariant(), CharacterSelectText.Caption(roster[0], false));
    }

    // ------------------------------------------------------------------ 职业与成长类型

    [Theory]
    [InlineData(0x00, 0, 0)]
    [InlineData(0x01, 1, 0)]
    [InlineData(0x0F, 15, 0)]
    [InlineData(0x10, 0, 1)]
    [InlineData(0x21, 1, 2)]
    [InlineData(0x32, 2, 3)]
    [InlineData(0x70, 0, 7)]
    [InlineData(0x7F, 15, 7)]
    [InlineData(0x80, 0, 0)]                                                // 最高位不用
    [InlineData(0xFF, 15, 7)]
    public void TheGrowthByteSplitsIntoTheFirstGrowthAndTheAwakeningStage(int growthByte, int growType, int awakening)
    {
        var titles = new StubTitles();
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", job: 3, growTypeByte: (byte)growthByte), 0, titles);

        Assert.Equal((3, growType, awakening), (slot.Online.Job, slot.Online.GrowType, slot.Online.Awakening));
        Assert.Equal((3, growType, awakening), Assert.Single(titles.Calls));
        Assert.Equal("职3转" + growType + "觉" + awakening, slot.JobName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(255)]
    public void TheJobNumberReachesTheTitleSourceUnchanged(int job)
    {
        var titles = new StubTitles();
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", job: (byte)job), 0, titles);
        Assert.Equal(job, slot.Online.Job);
        Assert.Equal(job, titles.Calls[0].Job);
    }

    [Theory]
    [InlineData(0, 0, 0, "SWORDMAN")]
    [InlineData(1, 0, 0, "FIGHTER")]
    [InlineData(2, 1, 0, "GUNNER G1")]
    [InlineData(3, 1, 1, "MAGE G1 A1")]
    [InlineData(4, 0, 2, "PRIEST A2")]
    [InlineData(5, 2, 1, "AT GUNNER G2 A1")]
    [InlineData(6, 0, 0, "THIEF")]
    [InlineData(7, 0, 0, "AT FIGHTER")]
    [InlineData(8, 0, 0, "AT MAGE")]
    [InlineData(9, 0, 0, "DEMONIC SWORDMAN")]
    [InlineData(10, 0, 0, "CREATOR MAGE")]
    [InlineData(11, 0, 0, "JOB11")]
    [InlineData(255, 3, 0, "JOB255 G3")]
    public void WithoutATitleSourceTheClassIsAnEnglishPlaceholder(int job, int growType, int awakening, string expected)
    {
        Assert.Equal(expected, OnlineCharacterRoster.PlaceholderClassName(job, growType, awakening));
        byte growthByte = (byte)(growType | (awakening << 4));
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", job: (byte)job, growTypeByte: growthByte), 0);
        Assert.Equal(expected, slot.JobName);
    }

    [Fact]
    public void ATitleSourceThatKnowsNothingFallsBackToThePlaceholderPerCharacter()
    {
        var titles = new StubTitles { Name = (j, g, a) => g == 1 ? "转职名" : null };
        var list = RosterKit.List(RosterKit.Entry("甲", job: 3, growTypeByte: 1), RosterKit.Entry("乙", job: 3, growTypeByte: 2));
        CharacterRoster roster = OnlineCharacterRoster.From(list, titles);
        Assert.Equal("转职名", roster[0].JobName);
        Assert.Equal("MAGE G2", roster[1].JobName);
    }

    // ------------------------------------------------------------------ 不可进入

    [Fact]
    public void AnOrdinaryCharacterCanEnter()
    {
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名"), 0);
        Assert.Equal(CharacterBlock.None, slot.Online.Block);
        Assert.True(slot.Online.CanEnter);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(255, true)]
    public void OnlyANameFlagOfExactlyOneBlocksEntry(int x1, bool canEnter)
    {
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", x1: (byte)x1), 0);
        Assert.Equal(canEnter, slot.Online.CanEnter);
        Assert.Equal(canEnter ? CharacterBlock.None : CharacterBlock.NameFlag, slot.Online.Block);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(255, true)]
    public void OnlyAStatusByteOfExactlyEightBlocksEntry(int status, bool canEnter)
    {
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", status: (byte)status), 0);
        Assert.Equal(canEnter, slot.Online.CanEnter);
        Assert.Equal(canEnter ? CharacterBlock.None : CharacterBlock.Status, slot.Online.Block);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(300u, false)]
    [InlineData(uint.MaxValue, false)]
    public void AnyNonZeroDeleteCountdownBlocksEntry(uint d2, bool canEnter)
    {
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名", d2: d2), 0);
        Assert.Equal(canEnter, slot.Online.CanEnter);
        Assert.Equal(canEnter ? CharacterBlock.None : CharacterBlock.PendingDelete, slot.Online.Block);
    }

    [Fact]
    public void WhenSeveralBlocksApplyThePendingDeleteComesFirstThenTheNameFlagThenTheStatus()
    {
        Assert.Equal(CharacterBlock.PendingDelete, OnlineCharacterRoster.BlockOf(RosterKit.Entry("名", x1: 1, status: 8, d2: 5)));
        Assert.Equal(CharacterBlock.NameFlag, OnlineCharacterRoster.BlockOf(RosterKit.Entry("名", x1: 1, status: 8)));
        Assert.Equal(CharacterBlock.Status, OnlineCharacterRoster.BlockOf(RosterKit.Entry("名", status: 8)));
    }

    [Fact]
    public void TheUnnamedByteDoesNotBlockAnything()
    {
        // 条目里的字节 D 没读到含义,客户端"开始游戏"的拦截里也没有它
        Assert.Equal(CharacterBlock.None, OnlineCharacterRoster.BlockOf(RosterKit.Entry("名", d: 1)));
        Assert.Equal(CharacterBlock.None, OnlineCharacterRoster.BlockOf(RosterKit.Entry("名", d: 255)));
    }

    // ------------------------------------------------------------------ 装备

    [Fact]
    public void TheEquipmentRecordsPassThroughUntouched()
    {
        var equips = new[]
        {
            new OnlineCharacterEquip(10, 1001u, 2002u, 3, 4004u, 5005u),
            new OnlineCharacterEquip(3, 7u, 0u, 0, 0u, 0u),
            new OnlineCharacterEquip(255, uint.MaxValue, uint.MaxValue, 255, uint.MaxValue, uint.MaxValue),
        };
        OnlineCharacterEntry entry = RosterKit.Entry("名", equips: equips);
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(entry, 0);

        Assert.Equal(3, slot.Online.Equips.Count);
        for (int i = 0; i < equips.Length; i++)
        {
            Assert.Same(entry.Equips[i], slot.Online.Equips[i]);
            Assert.Equal(equips[i].Slot, slot.Online.Equips[i].Slot);
            Assert.Equal(equips[i].ItemId, slot.Online.Equips[i].ItemId);
            Assert.Equal(equips[i].Blob, slot.Online.Equips[i].Blob);
            Assert.Equal(equips[i].Packed, slot.Online.Equips[i].Packed);
            Assert.Equal(equips[i].ClearAvatar, slot.Online.Equips[i].ClearAvatar);
            Assert.Equal(equips[i].Extra, slot.Online.Equips[i].Extra);
        }
    }

    [Fact]
    public void ACharacterWithoutEquipmentHasAnEmptyList()
    {
        CharacterSlot slot = OnlineCharacterRoster.SlotOf(RosterKit.Entry("名"), 0);
        Assert.Empty(slot.Online.Equips);
    }

    // ------------------------------------------------------------------ 参数检查

    [Fact]
    public void NullEntriesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineCharacterRoster.SlotOf(null, 0));
        Assert.Throws<ArgumentNullException>(() => OnlineCharacterRoster.BlockOf(null));
        Assert.Throws<ArgumentNullException>(() => OnlineCharacterRoster.SelectedSlotOf(null));
        OnlineCharacterEntry entry = RosterKit.Entry("名");
        Assert.Throws<ArgumentNullException>(() => new CharacterSlotOnline(0, null, 0, 0, 0, CharacterBlock.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterSlotOnline(-1, entry, 0, 0, 0, CharacterBlock.None));
        Assert.Throws<ArgumentNullException>(() => new CharacterSlot("名", "职", 1, null));
    }

    [Fact]
    public void AnOfflineSlotHasNoOnlineInformation()
    {
        Assert.Null(new CharacterSlot("名", "职", 1).Online);
        Assert.Null(CharacterRoster.Local()[0].Online);
    }
}
