namespace Dnf.Net.Tests;

/// <summary>
/// 结算两条下行通知(类 0、包号 0x22 通关结果与 0x23 通关奖励)合成载荷的独立写入器。
/// 按服务端写序(settlenoti_NOTES §2.3、§2.4)用 <see cref="NoticePayloadBuilder"/>(显式移位的小端写入,不用产品里的任何读写代码)拼字节,
/// 所以"构造一份、解析一份、两边对上"不会因为读写同错而互相掩盖。
/// 所有取值都是合成值(编号取 900 以上);不读任何真实帧文件,不含任何真实数值。
/// 字节字段一律以"线上字节"(0..255)给出,带符号字段的符号由解析器的取法决定,测试里另行断言。
/// </summary>
internal static class SettlementTestPayloads
{
    /// <summary>通关结果(0x22)里的一条最佳时间条目:u16 唯一号、i32 最佳用时、u8 新纪录标志。</summary>
    internal sealed class BestTimeSpec
    {
        public int UniqueId;
        public int BestTime;
        public int NewRecord;

        public BestTimeSpec(int uniqueId, int bestTime, int newRecord)
        {
            UniqueId = uniqueId;
            BestTime = bestTime;
            NewRecord = newRecord;
        }
    }

    /// <summary>通关结果(0x22)的合成规格;缺省值与测试里手写的字面载荷逐字节相同。</summary>
    internal sealed class ASpec
    {
        public int Rank = 0xE7;
        public int ClearTime = 0x12345678;
        public int TimeBonus = 0xF6;
        public int BaseScore = 0x9C;
        public int Maze = 1;
        public List<BestTimeSpec> Entries = new List<BestTimeSpec> { new BestTimeSpec(901, 98765, 1), new BestTimeSpec(40000, 123456, 0) };

        /// <summary>不为 null 时,个数字节写它而不是 <see cref="Entries"/> 的条数(造"声明与实际不符"的坏包)。</summary>
        public int? DeclaredCount;

        public byte[] Build(params byte[] tail)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder()
                .U8(Rank).I32(ClearTime).U8(TimeBonus).U8(BaseScore).U8(Maze).U8(DeclaredCount ?? Entries.Count);
            foreach (BestTimeSpec e in Entries)
            {
                b.U16(e.UniqueId).I32(e.BestTime).U8(e.NewRecord);
            }
            return b.Raw(tail).ToArray();
        }
    }

    /// <summary>通关奖励(0x23)里的一个奖励信息:状态决定线上形状(0 一个字节;1 再加保留位与数值;2 再加物品编号与物品附加信息)。</summary>
    internal sealed class RewardSpec
    {
        public int Status;
        public int Reserved;
        public int Value;
        public int ItemId;
        public int ItemData;

        public RewardSpec(int status, int reserved = 0, int value = 0, int itemId = 0, int itemData = 0)
        {
            Status = status;
            Reserved = reserved;
            Value = value;
            ItemId = itemId;
            ItemData = itemData;
        }

        public void WriteTo(NoticePayloadBuilder b)
        {
            b.U8(Status);
            if (Status >= 1)
            {
                b.I32(Reserved).I32(Value);
            }
            if (Status == 2)
            {
                b.I32(ItemId).I32(ItemData);
            }
        }

        /// <summary>这个奖励信息的线上长度(状态 0:1;状态 1:9;状态 2:17)。</summary>
        public int WireLength => Status == 0 ? 1 : Status == 1 ? 9 : 17;
    }

    /// <summary>通关奖励(0x23)里的一条任务物品:u16 槽位、i32 编号、i32 附加信息、u16 耐久、u8 增幅类型、u16 增幅值(15 字节)。</summary>
    internal sealed class ItemSpec
    {
        public int Slot;
        public int ItemId;
        public int Data;
        public int Durability;
        public int AmplifyType;
        public int AmplifyValue;

        public ItemSpec(int slot, int itemId, int data, int durability, int amplifyType, int amplifyValue)
        {
            Slot = slot;
            ItemId = itemId;
            Data = data;
            Durability = durability;
            AmplifyType = amplifyType;
            AmplifyValue = amplifyValue;
        }
    }

    /// <summary>通关奖励(0x23)的合成规格;缺省值与测试里手写的字面载荷逐字节相同(167 字节)。</summary>
    internal sealed class BSpec
    {
        public int[] Exp = { 1000, 250, 77, 12, 345, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 };
        public int Kind = 2;
        public List<ItemSpec> Items = new List<ItemSpec> { new ItemSpec(7, 901, 3, 100, 2, 15), new ItemSpec(258, 40000, 12, 50, 0, 0) };
        public List<RewardSpec> Rewards = new List<RewardSpec>
        {
            new RewardSpec(2, 0, 5000, 901, 4), new RewardSpec(1, 0, 6000), new RewardSpec(0), new RewardSpec(1, 0, 7000),
        };
        public int ReceiverValue = 424242;
        public int[] Reserved = { 0, 0, 0, 0 };
        public List<RewardSpec> Premium = new List<RewardSpec>
        {
            new RewardSpec(0), new RewardSpec(2, 0, 9000, 40000, 5), new RewardSpec(0), new RewardSpec(0),
        };
        public int Percent = 85;
        public int[] Flags = { 1, 0, 1 };

        /// <summary>不为 null 时,物品个数字节写它而不是 <see cref="Items"/> 的条数。</summary>
        public int? DeclaredItemCount;

        public byte[] Build(params byte[] tail)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder();
            for (int i = 0; i < 4; i++)
            {
                b.I32(Exp[i]);
            }
            b.U8(Kind);
            for (int i = 4; i < 16; i++)
            {
                b.I32(Exp[i]);
            }
            b.U8(DeclaredItemCount ?? Items.Count);
            foreach (ItemSpec it in Items)
            {
                b.U16(it.Slot).I32(it.ItemId).I32(it.Data).U16(it.Durability).U8(it.AmplifyType).U16(it.AmplifyValue);
            }
            foreach (RewardSpec r in Rewards)
            {
                r.WriteTo(b);
            }
            b.I32(ReceiverValue);
            foreach (int x in Reserved)
            {
                b.U8(x);
            }
            foreach (RewardSpec r in Premium)
            {
                r.WriteTo(b);
            }
            b.I32(Percent);
            foreach (int f in Flags)
            {
                b.U8(f);
            }
            return b.Raw(tail).ToArray();
        }

        /// <summary>按规格算出的线上总长:89 + 15 × 物品条数 + 各奖励信息超出 1 字节的部分。</summary>
        public int ExpectedLength()
        {
            int length = 65 + 1 + (15 * Items.Count) + 4 + 4 + 4 + 3;
            foreach (RewardSpec r in Rewards)
            {
                length += r.WireLength;
            }
            foreach (RewardSpec r in Premium)
            {
                length += r.WireLength;
            }
            return length;
        }
    }

    /// <summary>
    /// 只读包装的严格检查(比 <see cref="PktImplTestKit.AssertListIsReadOnly{T}"/> 多两点):底层不是数组,空列表上的写操作同样抛 NotSupportedException。
    /// </summary>
    public static void AssertReadOnlyWrapper<T>(IReadOnlyList<T> readOnly)
    {
        Assert.False(readOnly is Array);
        IList<T> list = Assert.IsAssignableFrom<IList<T>>(readOnly);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => { list.Add(default); });
        Assert.Throws<NotSupportedException>(() => { list.Insert(0, default); });
        Assert.Throws<NotSupportedException>(() => { list[0] = default; });
        Assert.Throws<NotSupportedException>(() => { list.RemoveAt(0); });
        Assert.Throws<NotSupportedException>(() => { list.Clear(); });
    }
}
