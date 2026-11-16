using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// "个数与长度先对剩余字节、再分配"的可观察验证:谎报个数的包被拒绝时,解析器在这条路径上分配的字节数必须远小于按谎报个数分配的量。
/// 只看返回值测不出这条纪律——守卫之后每个字段的读取本来就带边界检查,去掉守卫结果也是"拒绝",区别只在于先分配了一个按谎报个数开出来的数组或列表。
/// 所以这里用 <see cref="GC.GetAllocatedBytesForCurrentThread"/> 量当前线程在单次调用里分配的字节(先预热一次,JIT 与静态初始化的分配不计入)。
/// 夹具全是手工拼的合成字节,个数写成最大值、后面只跟几个字节。
/// </summary>
public class ItemParserAllocationTests
{
    // 合法的拒绝路径只会分配读取器等少量对象;谎报个数一旦被拿去开数组(几 KB 到几百 KB)就远超这个线。
    private const long Budget = 1024;

    private static long Allocated(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void TheAllocationMeterSeesAnAllocation()
    {
        byte[] keep = null;
        long bytes = Allocated(() => keep = new byte[8192]);
        Assert.NotNull(keep);
        Assert.True(bytes >= 8192, "实测 " + bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(0x0C)]
    public void ASpacePacketWithTheLargestCountAndNoEntriesIsRejectedWithoutBuildingAList(int space)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(space);
        if (space == 0 || space == 2)
        {
            b.U16(8);
        }
        if (space == 0x0C)
        {
            b.U16(1).U32(2);
        }
        byte[] payload = b.U16(0xFFFF).Raw(1, 2, 3).ToArray();

        long bytes = Allocated(() => Assert.False(ItemListParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(0x0C)]
    public void AnUpdatePacketWithTheLargestCountAndNoEntriesIsRejectedWithoutBuildingAList(int space)
    {
        byte[] payload = new NoticePayloadBuilder().U8(space).U16(0xFFFF).Raw(1, 2, 3).ToArray();
        long bytes = Allocated(() => Assert.False(ItemUpdateParser.TryParse(payload, out _, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    // ---- "个数 × 最短条目长度"的守卫 ----
    // 上面那些夹具都是"个数 0xFFFF、后面只剩几个字节":个数本身就大于剩余字节,把守卫退化成"个数 > 剩余字节"也会拒绝,区分不出来。
    // 下面的夹具取 个数 N ≤ 剩余字节 R < N × 该形状的最短条目长度:正确的守卫在分配之前拒绝;退化成"N > R"的守卫会放行,
    // 随即按 N 开出列表(N = 1000 就是 8 KB 以上),再因为全零字节读不通而拒绝——返回值一样,只有分配量(宠物栏与身上装备还有回调次数)看得出差别。
    // 最短条目长度写成字面数字(与产品的 MinLength 分开写):背包 / 仓库 / 账号仓库 / 宠物栏 / 身上装备 25 字节;
    // 装扮背包 25 + (4 + 30) + (4 + 4) = 67 字节。

    [Fact]
    public void TheLiteralMinimumEntryLengthsMatchTheWireLayoutOfTheSyntheticEntries()
    {
        // 把夹具里用的 25 与 67 钉在独立夹具构建器写出的字节数上,而不是产品常量上。
        Assert.Equal(25, BagEntryBytes(Item(0)).Length);
        ItemSpec avatar = Item(0);
        avatar.SocketBlock = Socket();
        avatar.ExpansionBlock = Expansion();
        Assert.Equal(67, BagEntryBytes(avatar).Length);
    }

    private static byte[] SpacePacketWithCountAndZeroBytes(int space, int count, int remaining)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(space);
        if (space == 0 || space == 2)
        {
            b.U16(8);
        }
        if (space == 0x0C)
        {
            b.U16(1).U32(2);
        }
        return b.U16(count).Raw(new byte[remaining]).ToArray();
    }

    private static void AssertSpaceListRejectedWithoutBuildingAList(byte[] payload)
    {
        // 宠物栏的条目要问物品类型:守卫放行的话第一个条目就会去问回调。
        DictionaryKindResolver resolver = new DictionaryKindResolver();
        long bytes = Allocated(() => Assert.False(ItemListParser.TryParse(payload, resolver, out _, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
        Assert.Equal(0, resolver.CallCount);
    }

    private static void AssertUpdateRejectedWithoutBuildingAList(byte[] payload)
    {
        // 宠物栏与身上装备的条目要问物品类型:守卫放行的话第一个条目就会去问回调。
        DictionaryKindResolver resolver = new DictionaryKindResolver();
        long bytes = Allocated(() => Assert.False(ItemUpdateParser.TryParse(payload, resolver, out _, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData(0, 1500, 2000)]
    [InlineData(2, 1500, 2000)]
    [InlineData(7, 1500, 2000)]
    [InlineData(0x0C, 1500, 2000)]
    [InlineData(1, 1000, 2000)]
    public void ASpacePacketWhoseCountFitsTheBytesButNotTheMinimumEntriesIsRejectedWithoutBuildingAList(int space, int count, int remaining)
    {
        int minimumEntryLength = space == 1 ? 67 : 25;
        Assert.True(count <= remaining, "夹具自检:个数不超过剩余字节");
        Assert.True((long)count * minimumEntryLength > remaining, "夹具自检:个数 × 最短条目长度超过剩余字节");
        AssertSpaceListRejectedWithoutBuildingAList(SpacePacketWithCountAndZeroBytes(space, count, remaining));
    }

    [Theory]
    [InlineData(0, 1500, 2000)]
    [InlineData(2, 1500, 2000)]
    [InlineData(3, 1500, 2000)]
    [InlineData(7, 1500, 2000)]
    [InlineData(0x0C, 1500, 2000)]
    [InlineData(1, 1000, 2000)]
    public void AnUpdatePacketWhoseCountFitsTheBytesButNotTheMinimumEntriesIsRejectedWithoutBuildingAList(int space, int count, int remaining)
    {
        int minimumEntryLength = space == 1 ? 67 : 25;
        Assert.True(count <= remaining, "夹具自检:个数不超过剩余字节");
        Assert.True((long)count * minimumEntryLength > remaining, "夹具自检:个数 × 最短条目长度超过剩余字节");
        AssertUpdateRejectedWithoutBuildingAList(new NoticePayloadBuilder().U8(space).U16(count).Raw(new byte[remaining]).ToArray());
    }

    // 装扮背包专用的窗口:剩余字节够按 25 字节一条装下 N 条、但不够按 67 字节一条装下——
    // 守卫写成"全按 25 字节算"的话,装扮背包的包会被放行。
    [Fact]
    public void AnAvatarBagSpacePacketThatFitsTheBagMinimumButNotTheAvatarMinimumIsRejectedWithoutBuildingAList()
    {
        const int count = 400;
        const int remaining = 12000;
        Assert.True((long)count * 25 <= remaining, "夹具自检:按 25 字节一条装得下");
        Assert.True((long)count * 67 > remaining, "夹具自检:按 67 字节一条装不下");
        AssertSpaceListRejectedWithoutBuildingAList(SpacePacketWithCountAndZeroBytes(1, count, remaining));
    }

    [Fact]
    public void AnAvatarBagUpdatePacketThatFitsTheBagMinimumButNotTheAvatarMinimumIsRejectedWithoutBuildingAList()
    {
        const int count = 400;
        const int remaining = 12000;
        Assert.True((long)count * 25 <= remaining, "夹具自检:按 25 字节一条装得下");
        Assert.True((long)count * 67 > remaining, "夹具自检:按 67 字节一条装不下");
        AssertUpdateRejectedWithoutBuildingAList(new NoticePayloadBuilder().U8(1).U16(count).Raw(new byte[remaining]).ToArray());
    }

    // ---- 同形的守卫:装备子表、宠物表、持续型物品表、物品锁表 ----
    // 它们的守卫是同一种写法(个数 × 每条最短字节数 > 剩余字节),同样只有"个数 ≤ 剩余 < 个数 × 每条最短字节数"的夹具才能分辨它与"个数 > 剩余字节"。
    // 每条最短字节数写成字面数字:紧凑装备记录 22、详细装备记录 27、宠物表每只 16、持续型物品每项 8、物品锁每项 4。

    [Fact]
    public void ACompactListWhoseCountFitsTheBytesButNotTheRecordsIsRejectedWithoutBuildingTheArray()
    {
        const int count = 200;
        const int remaining = 300;
        Assert.True(count <= remaining && (long)count * 22 > remaining, "夹具自检");
        Assert.Equal(22, new NoticePayloadBuilder().AppendCompact(3, ItemA).ToArray().Length);
        byte[] payload = new NoticePayloadBuilder().U8(count).Raw(new byte[remaining]).ToArray();
        long bytes = Allocated(() => Assert.False(EquipListReader.TryReadCompact(new PacketReader(payload), true, out _, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void ADetailedListWhoseCountFitsTheBytesButNotTheRecordsIsRejectedWithoutBuildingTheArray()
    {
        const int count = 255;
        const int remaining = 300;
        Assert.True(count <= remaining && (long)count * 27 > remaining, "夹具自检");
        Assert.Equal(27, DetailEntryBytes(Item(10)).Length);
        byte[] payload = new NoticePayloadBuilder().U8(count).Raw(new byte[remaining]).ToArray();
        // 守卫放行的话第一条记录就会去问回调。
        DictionaryKindResolver resolver = new DictionaryKindResolver();
        long bytes = Allocated(() => Assert.False(EquipListReader.TryReadDetailed(new PacketReader(payload), resolver, 0, 25, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void ACreatureTableWhoseCountFitsTheBytesButNotTheEntriesIsRejectedWithoutBuildingTheArray()
    {
        const int count = 255;
        const int remaining = 300;
        Assert.True(count <= remaining && (long)count * 16 > remaining, "夹具自检");
        byte[] payload = new NoticePayloadBuilder().U8(count).Raw(new byte[remaining]).ToArray();
        long bytes = Allocated(() => Assert.False(CreatureAttributeParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void AContinuousItemListWhoseCountFitsTheBytesButNotTheEntriesIsRejectedWithoutBuildingTheArray()
    {
        const int count = 255;
        const int remaining = 300;
        Assert.True(count <= remaining && (long)count * 8 > remaining, "夹具自检");
        byte[] payload = new NoticePayloadBuilder().U8(count).Raw(new byte[remaining]).ToArray();
        long bytes = Allocated(() => Assert.False(ContinuousItemListParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void AnItemLockTableWhoseCountFitsTheBytesButNotTheEntriesIsRejectedWithoutBuildingTheArray()
    {
        const int count = 2000;
        const int remaining = 3000;
        Assert.True(count <= remaining && (long)count * 4 > remaining, "夹具自检");
        byte[] payload = new NoticePayloadBuilder().U16(count).Raw(new byte[remaining]).ToArray();
        long bytes = Allocated(() => Assert.False(ItemLockParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void ACompactListWithTheLargestCountIsRejectedWithoutBuildingTheArray()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0xFF).Raw(1, 2, 3, 4, 5).ToArray();
        long bytes = Allocated(() => Assert.False(EquipListReader.TryReadCompact(new PacketReader(payload), true, out _, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void ADetailedListWithTheLargestCountIsRejectedWithoutBuildingTheArray()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0xFF).Raw(1, 2, 3, 4, 5).ToArray();
        long bytes = Allocated(() => Assert.False(EquipListReader.TryReadDetailed(new PacketReader(payload), null, 0, 25, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void APublicViewWhoseCompactListCountLiesIsRejectedWithoutBuildingTheArray()
    {
        // 模式 0:个数 1、uid、名字长度 0、6 个头字节,然后紧凑列表个数 0xFF 而后面没有字节。
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(1).U16(0x0101).U32(0).Raw(1, 2, 3, 4, 5, 6).U8(0xFF).ToArray();
        long bytes = Allocated(() => Assert.False(UserInfoViewParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void AnExpandGroupListWhoseCountLiesIsRejectedWithoutBuildingTheArray()
    {
        byte[] payload = new NoticePayloadBuilder().U16(0x0101).U8(10).U8(0xFF).Raw(1, 2, 3).ToArray();
        long bytes = Allocated(() => Assert.False(ExpandEquipParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void ACreatureTableWithTheLargestCountIsRejectedWithoutBuildingTheArray()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0xFF).Raw(new byte[40]).ToArray();
        long bytes = Allocated(() => Assert.False(CreatureAttributeParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void AContinuousItemListWithTheLargestCountIsRejectedWithoutBuildingTheArray()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0xFF).Raw(new byte[100]).ToArray();
        long bytes = Allocated(() => Assert.False(ContinuousItemListParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Fact]
    public void AnItemLockTableWithTheLargestCountIsRejectedWithoutBuildingTheArray()
    {
        byte[] payload = new NoticePayloadBuilder().U16(0xFFFF).Raw(new byte[100]).ToArray();
        long bytes = Allocated(() => Assert.False(ItemLockParser.TryParse(payload, out _)));
        Assert.True(bytes < Budget, "实测 " + bytes);
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(1500)]
    [InlineData(2000)]
    public void AC3CountThatCannotFitInTheRemainingBytesIsRejectedWithoutBuildingTheArray(int fillerLength)
    {
        // 主干 21 字节(槽、物品号、Data、强化字节、耐久、封装、附魔、增幅类型、增幅值)后接 C3 个数 255:
        // 255 × 8 = 2040 超过剩余的 1200..2000 字节,必须在开数组之前拒绝(守卫写成每项 5 字节时 1275 以上的剩余量会放行并开出数组)。
        byte[] payload = new NoticePayloadBuilder()
            .U16(5).U32(ItemA).U32(0).U8(0).U16(0).U8(0).U32(0).U8(0).U16(0)
            .U8(255).Raw(new byte[fillerLength]).ToArray();

        long bytes = Allocated(() =>
        {
            Assert.False(ItemEntryReader.TryRead(new PacketReader(payload), ItemEntryShape.Bag, null, out ItemEntry entry, out ItemEntryFault fault));
            Assert.Null(entry);
            Assert.Equal(ItemEntryFault.Truncated, fault);
        });
        Assert.True(bytes < Budget, "实测 " + bytes);
    }
}
