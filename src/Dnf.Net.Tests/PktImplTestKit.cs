using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 副本许可(类 0、包号 5)与选角回包(类 1、包号 4)两个解析器的测试共用工具:合成载荷的独立写入器、合成帧、逐字段核对。
/// 写入器用 <see cref="NoticePayloadBuilder"/>(显式移位的小端写入,不用产品里的任何读写代码),读序与偏移取自 E-382 的读序表
/// (packetread_NOTES §2.2、§3.3),所以"构造一份、解析一份、两边对上"不会因为读写同错而互相掩盖。
/// 所有取值都是合成值(副本号、角色杂项里的 u16/u32 取 900 以上;u8 字段装不下 900,取互不相同的小值);不读任何真实帧文件,不含任何真实数值。
/// </summary>
internal static class PktImplTestKit
{
    public const byte Noti = 0;
    public const byte Cmd = 1;

    // ---- 合成帧(走真实的解码路径,同 TownEntryParserTests 的做法) ----

    /// <summary>手工拼一个服务端整帧:没有发送钩子,两个校验值都是载荷的 crc32N。</summary>
    public static byte[] Wire(byte cls, ushort id, byte[] body)
    {
        uint crc = Crc32N.Compute(body);
        byte[] f = new byte[15 + body.Length];
        f[0] = cls;
        f[1] = (byte)id;
        f[2] = (byte)(id >> 8);
        BitConverter.GetBytes((uint)f.Length).CopyTo(f, 3);
        BitConverter.GetBytes(crc).CopyTo(f, 7);
        BitConverter.GetBytes(crc).CopyTo(f, 11);
        body.CopyTo(f, 15);
        return f;
    }

    /// <summary>拼一帧并解码(不加密的替身密码)。别用包号 1、2、9、87 与类 1 的 21 字节包号 1,它们走特殊解码路径。</summary>
    public static DecodedFrame Frame(byte cls, ushort id, params byte[] body)
    {
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        Assert.Equal(FrameStatus.Ok, decoder.Decode(Wire(cls, id, body), out DecodedFrame frame));
        return frame;
    }

    // ---- (0,5) 副本许可 ----

    /// <summary>按读序拼 (0,5) 载荷:u16 个数 = 条数,每条 u16 副本号 + u8 档位,再接 <paramref name="tail"/>。</summary>
    public static byte[] Permission(IEnumerable<(int DungeonId, int Tier)> entries, params byte[] tail)
    {
        (int DungeonId, int Tier)[] list = entries.ToArray();
        NoticePayloadBuilder b = new NoticePayloadBuilder().U16(list.Length);
        foreach ((int dungeonId, int tier) in list)
        {
            b.U16(dungeonId).U8(tier);
        }
        return b.Raw(tail).ToArray();
    }

    /// <summary>按读序拼 (0,5) 载荷,但个数字段单独指定(用来造"个数声明与实际条数不符"的坏包)。</summary>
    public static byte[] PermissionWithDeclaredCount(int declaredCount, IEnumerable<(int DungeonId, int Tier)> entries, params byte[] tail)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U16(declaredCount);
        foreach ((int dungeonId, int tier) in entries)
        {
            b.U16(dungeonId).U8(tier);
        }
        return b.Raw(tail).ToArray();
    }

    public static IReadOnlyList<(int DungeonId, int Tier)> Pairs(DungeonPermissionNotice notice)
    {
        return notice.Entries.Select(e => ((int)e.DungeonId, (int)e.Tier)).ToArray();
    }

    public static IReadOnlyList<(int DungeonId, int Tier)> Pairs(DungeonPermissionSnapshot snapshot)
    {
        return snapshot.Entries.Select(e => ((int)e.DungeonId, (int)e.Tier)).ToArray();
    }

    // ---- (1,4) 选角回包 ----

    /// <summary>逐字段核对:解析出的载入字段与构造它的合成取值一致(列表个数与每个元素都核)。</summary>
    public static void AssertLoad(SelectCharacterSpec s, SelectCharacterLoadInfo l)
    {
        Assert.NotNull(l);
        Assert.Equal((uint)s.RegTime, l.RegTime);
        Assert.Equal((uint)s.CreateTime, l.CreateTime);
        Assert.Equal((ushort)s.UniqueId, l.UniqueId);
        Assert.Equal((ushort)s.TotalFatigue, l.TotalFatigue);
        Assert.Equal((ushort)s.TotalMaxFatigue, l.TotalMaxFatigue);
        Assert.Equal((ushort)s.UsedFatigue, l.UsedFatigue);

        Assert.Equal(s.Premiums.Length, l.Premiums.Count);
        for (int i = 0; i < s.Premiums.Length; i++)
        {
            Assert.Equal((byte)s.Premiums[i].Id, l.Premiums[i].Id);
            Assert.Equal(s.Premiums[i].RemainingSeconds, l.Premiums[i].RemainingSeconds);
        }

        Assert.Equal(s.Cera, l.Cera);

        Assert.Equal(20, l.QuestSlots.Count);
        for (int j = 0; j < 20; j++)
        {
            Assert.Equal((ushort)s.QuestSlots[j].Key, l.QuestSlots[j].Key);
            Assert.Equal((uint)s.QuestSlots[j].Value, l.QuestSlots[j].Value);
            Assert.Equal(s.QuestSlots[j].Key == 0xFFFF, l.QuestSlots[j].IsEmpty);
        }

        Assert.Equal(5, l.QuestNotifyValues.Count);
        for (int k = 0; k < 5; k++)
        {
            Assert.Equal((uint)s.QuestNotifyValues[k], l.QuestNotifyValues[k]);
        }

        Assert.Equal((byte)s.Village, l.Village);
        Assert.Equal((byte)s.TutorialSkipable, l.TutorialSkipable);
        Assert.Equal(s.TutorialFlagIndices.Length, l.TutorialFlagIndices.Count);
        for (int m = 0; m < s.TutorialFlagIndices.Length; m++)
        {
            Assert.Equal((byte)s.TutorialFlagIndices[m], l.TutorialFlagIndices[m]);
        }
        Assert.Equal((ushort)s.FatigueBatteryCharging, l.FatigueBatteryCharging);
        Assert.Equal((ushort)s.FatigueGrownUpBuff, l.FatigueGrownUpBuff);
        Assert.Equal((byte)s.TradePunishOverThirtyDays, l.TradePunishOverThirtyDays);
    }

    // ---- 只读列表 ----

    /// <summary>任何写操作都抛 <see cref="NotSupportedException"/>,且内容不变。</summary>
    public static void AssertListIsReadOnly<T>(IReadOnlyList<T> list)
    {
        Assert.NotNull(list);
        IList<T> asList = Assert.IsAssignableFrom<IList<T>>(list);
        Assert.True(asList.IsReadOnly);
        int count = list.Count;
        Assert.Throws<NotSupportedException>(() => asList.Add(default));
        Assert.Throws<NotSupportedException>(() => asList.Clear());
        if (count > 0)
        {
            Assert.Throws<NotSupportedException>(() => asList[0] = default);
            Assert.Throws<NotSupportedException>(() => asList.RemoveAt(0));
        }
        Assert.Equal(count, list.Count);
    }
}

/// <summary>
/// 一份合成的选角回包(类 1、包号 4)的字段取值,以及按 E-382 读序把它写成字节的写入器。
/// 读序(packetread_NOTES §3.3):u8 flag;u32,u32;u16 × 4;u8 N 与 N × {u8, u32};u32;20 × {u16, u32};5 × u32;u8;u8;u8 b 与 b × u8;u16;u16;u8。
/// 总长 = 1 + 169 + 5N + b。字段名与产品里的属性名同名,便于逐项对照。
/// </summary>
internal sealed class SelectCharacterSpec
{
    public int Flag = 1;
    public long RegTime;
    public long CreateTime;
    public int UniqueId;
    public int TotalFatigue;
    public int TotalMaxFatigue;
    public int UsedFatigue;
    public (int Id, int RemainingSeconds)[] Premiums = new (int, int)[0];
    public int Cera;
    public (int Key, long Value)[] QuestSlots = new (int, long)[20];
    public long[] QuestNotifyValues = new long[5];
    public int Village;
    public int TutorialSkipable;
    public int[] TutorialFlagIndices = new int[0];
    public int FatigueBatteryCharging;
    public int FatigueGrownUpBuff;
    public int TradePunishOverThirtyDays;

    /// <summary>不含尾巴的总长:1 + 169 + 5N + b。</summary>
    public int BodyLength => 170 + (5 * Premiums.Length) + TutorialFlagIndices.Length;

    /// <summary>按读序写出成功体(flag 取 <see cref="Flag"/>),再接 <paramref name="tail"/>。</summary>
    public byte[] Build(params byte[] tail)
    {
        Assert.Equal(20, QuestSlots.Length);
        Assert.Equal(5, QuestNotifyValues.Length);
        NoticePayloadBuilder b = new NoticePayloadBuilder()
            .U8(Flag)
            .U32(RegTime).U32(CreateTime)
            .U16(UniqueId).U16(TotalFatigue).U16(TotalMaxFatigue).U16(UsedFatigue)
            .U8(Premiums.Length);
        foreach ((int id, int remaining) in Premiums)
        {
            b.U8(id).I32(remaining);
        }
        b.I32(Cera);
        foreach ((int key, long value) in QuestSlots)
        {
            b.U16(key).U32(value);
        }
        foreach (long value in QuestNotifyValues)
        {
            b.U32(value);
        }
        b.U8(Village).U8(TutorialSkipable).U8(TutorialFlagIndices.Length);
        foreach (int index in TutorialFlagIndices)
        {
            b.U8(index);
        }
        b.U16(FatigueBatteryCharging).U16(FatigueGrownUpBuff).U8(TradePunishOverThirtyDays);
        return b.Raw(tail).ToArray();
    }

    /// <summary>失败体:u8 0、u8 失败码,再接 <paramref name="tail"/>。</summary>
    public static byte[] Failure(int failureCode, params byte[] tail)
    {
        return new NoticePayloadBuilder().U8(0).U8(failureCode).Raw(tail).ToArray();
    }

    /// <summary>
    /// 第 <paramref name="v"/> 个变体:每个字段的取值都随 v 变化且彼此不同(u16/u32 取 900 以上的合成值,u8 取互不相同的小值),
    /// 特权项带正、负、零三种剩余秒,金额型字段隔一个变体取负,20 个槽里混着空槽(键 0xFFFF,其中一部分的值非 0,验证"值照读")。
    /// </summary>
    public static SelectCharacterSpec Variant(int v, int premiumCount = 2, int tutorialCount = 3)
    {
        var s = new SelectCharacterSpec
        {
            RegTime = 9000001L + (7919L * v),
            CreateTime = 2900000001L + (1000003L * v),    // 大于 2^31,核对按无符号读
            UniqueId = 900 + ((v * 37) % 60000),
            TotalFatigue = 901 + v,
            TotalMaxFatigue = 1400 + (2 * v),
            UsedFatigue = 905 + ((v * 3) % 400),
            Cera = (v % 2 == 0) ? 90000 + (13 * v) : -(90000 + (13 * v)),
            Village = (0x31 + v) & 0xFF,
            TutorialSkipable = (v + 2) % 3,
            FatigueBatteryCharging = 901 + (3 * v),
            FatigueGrownUpBuff = 1500 + v,
            TradePunishOverThirtyDays = (v + 1) & 0xFF,
        };

        s.Premiums = new (int, int)[premiumCount];
        for (int i = 0; i < premiumCount; i++)
        {
            int id = (0x21 + v + (7 * i)) & 0xFF;
            int remaining;
            switch (i % 3)
            {
                case 0:
                    remaining = (86400 * (i + 1)) + v;
                    break;
                case 1:
                    remaining = -((3600 * (i + 1)) + v);
                    break;
                default:
                    remaining = 0;
                    break;
            }
            s.Premiums[i] = (id, remaining);
        }

        for (int j = 0; j < 20; j++)
        {
            if ((j + v) % 3 == 2)
            {
                // 空槽:键 0xFFFF;每隔一个空槽带非 0 的值(值照读,不能因为键是空槽就跳过或清零)
                s.QuestSlots[j] = (0xFFFF, (j % 2 == 0) ? 0 : 904 + j + v);
            }
            else
            {
                long c0 = (j + v) & 0x1FF;
                long c1 = (3 * j + 1) & 0x1FF;
                long c2 = (5 * j + v + 2) & 0x1FF;
                s.QuestSlots[j] = (910 + (11 * j) + v, c0 + (c1 << 9) + (c2 << 18));
            }
        }

        for (int k = 0; k < 5; k++)
        {
            s.QuestNotifyValues[k] = (k == 3 && v % 2 == 1) ? 0 : 9100 + (17 * k) + v;
        }

        s.TutorialFlagIndices = new int[tutorialCount];
        for (int m = 0; m < tutorialCount; m++)
        {
            s.TutorialFlagIndices[m] = ((5 * m) + v) & 0xFF;
        }
        return s;
    }
}
