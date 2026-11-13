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
