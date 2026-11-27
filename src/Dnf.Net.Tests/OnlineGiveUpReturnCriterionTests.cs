using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 放弃回城判据(r22-giveup-signal)在状态机层的测试,全是合成数据,不连真服。
/// 判据:放弃命令发出之后,(0/23 且 0/24)或(0/24 且 0/2)任一成立就算回城,只认包号、与到达顺序无关、重复帧只算一次;
/// 放弃之前到的帧一概不算;0/2 的裁决始终是透传(透传计数、透传包号表的口径不变),0/23 与 0/24 的计数口径不变。
/// 判据矩阵用两种彼此独立的写法逐帧对拍:一个是"逐帧维护状态"的小模型(同时给出裁决与计数),一个是"已见包号的集合"的纯谓词。
/// </summary>
public class OnlineGiveUpReturnCriterionTests
{
    private const ushort N23 = TownAreaPayloads.UserAreaPacketId;
    private const ushort N24 = TownAreaPayloads.AreaUsersPacketId;
    private const ushort N2 = 2;
    private const ushort N3 = 3;
    private const ushort N9 = 9;
    private const ushort N36 = 36;

    private static readonly ushort[] AllIds = { N23, N24, N2, N3, N9, N36 };

    private static byte[] PayloadOf(ushort id)
    {
        switch (id)
        {
            case N23:
                return GiveUpKit.UserAreaPayload();
            case N24:
                return GiveUpKit.AreaUsersPayload();
            default:
                return GatewayTestSupport.Pattern(9, 0x10 + id, 3);
        }
    }

    private static CombatKit.ServerFrame Frame(ushort id, byte[] payload = null)
    {
        return new CombatKit.ServerFrame(CombatKit.Noti, id, payload ?? PayloadOf(id));
    }

    private static async Task<CombatRig> GivingUpRig()
    {
        var rig = new CombatRig(2);
        OnlineCombatSendResult sent = await rig.Machine.RequestGiveUpAsync();
        Assert.True(sent.Sent);
        Assert.True(rig.Machine.IsGivingUp);
        return rig;
    }

    private static int ReturnedEvents(CombatRig rig)
    {
        return rig.Machine.Events.Count(e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
    }

    /// <summary>事件序列的简写:G = 放弃已发、D&lt;包号&gt; = 作为重复帧忽略、R = 回城(建图时的 MonstersSpawned 不记)。</summary>
    private static string Trace(CombatRig rig)
    {
        var parts = new List<string>();
        foreach (OnlineCombatEvent e in rig.Machine.Events)
        {
            switch (e.Kind)
            {
                case OnlineCombatEventKind.MonstersSpawned:
                    break;
                case OnlineCombatEventKind.GiveUpSent:
                    parts.Add("G");
                    break;
                case OnlineCombatEventKind.FrameIgnored:
                    Assert.Equal(OnlineCombatFrameVerdict.Duplicate, e.Verdict);
                    Assert.Equal(PacketClass.Noti, e.PacketClass);
                    parts.Add("D" + e.PacketId);
                    break;
                case OnlineCombatEventKind.ReturnedToTown:
                    parts.Add("R");
                    break;
                default:
                    parts.Add("?" + e.Kind);
                    break;
            }
        }
        return string.Join(",", parts);
    }

    // ---------------- 判据谓词与小模型(两种独立写法) ----------------

    /// <summary>纯谓词:放弃之后见过哪些包号 → 回城是否成立。</summary>
    private static bool Satisfied(ISet<ushort> seenAfterGiveUp)
    {
        bool has23 = seenAfterGiveUp.Contains(N23);
        bool has24 = seenAfterGiveUp.Contains(N24);
        bool has2 = seenAfterGiveUp.Contains(N2);
        return (has23 && has24) || (has24 && has2);
    }

    /// <summary>逐帧小模型:给出每一帧的裁决、计数与事件序列。</summary>
    private sealed class Model
    {
        public bool GivingUp;
        public bool Returned;
        public int Duplicates;
        public int Passthrough;
        public readonly Dictionary<ushort, int> PassIds = new Dictionary<ushort, int>();
        public readonly HashSet<ushort> Seen = new HashSet<ushort>();
        public readonly List<string> Events = new List<string>();

        public void GiveUp()
        {
            GivingUp = true;
            Events.Add("G");
        }

        public OnlineCombatFrameVerdict Feed(ushort id)
        {
            if (GivingUp && (id == N23 || id == N24))
            {
                if (Returned || Seen.Contains(id))
                {
                    Duplicates++;
                    Events.Add("D" + id);
                    return OnlineCombatFrameVerdict.Duplicate;
                }
                Seen.Add(id);
                Complete();
                return OnlineCombatFrameVerdict.Accepted;
            }
            Passthrough++;
            PassIds[id] = PassIds.TryGetValue(id, out int n) ? n + 1 : 1;
            if (GivingUp && id == N2)
            {
                Seen.Add(id);
                Complete();
            }
            return OnlineCombatFrameVerdict.Passthrough;
        }

        private void Complete()
        {
            if (!Returned && Satisfied(Seen))
            {
                Returned = true;
                Events.Add("R");
            }
        }
    }

    private static async Task<string> Compare(ushort[] sequence, int giveUpBefore)
    {
        var rig = new CombatRig(2);
        var model = new Model();
        var seenAfterGiveUp = new HashSet<ushort>();
        string Describe() => "序列 [" + string.Join(",", sequence) + "],放弃发在第 " + giveUpBefore + " 帧之前";

        for (int i = 0; i <= sequence.Length; i++)
        {
            if (i == giveUpBefore)
            {
                OnlineCombatSendResult sent = await rig.Machine.RequestGiveUpAsync();
                if (!sent.Sent)
                {
                    return Describe() + ":放弃命令没发出去";
                }
                model.GiveUp();
            }
            if (i == sequence.Length)
            {
                break;
            }
            ushort id = sequence[i];
            OnlineCombatFrameVerdict actual = rig.Feed(Frame(id));
            OnlineCombatFrameVerdict expected = model.Feed(id);
            if (model.GivingUp && (id == N23 || id == N24 || id == N2))
            {
                seenAfterGiveUp.Add(id);                  // 0/3、0/9、0/36 放弃之后也只是透传,不进"已见集合"
            }
            if (actual != expected)
            {
                return Describe() + ":第 " + (i + 1) + " 帧(" + id + ")裁决 " + actual + ",模型 " + expected;
            }
            bool predicate = Satisfied(seenAfterGiveUp);
            if (rig.Machine.ReturnedToTown != predicate || rig.Machine.ReturnedToTown != model.Returned)
            {
                return Describe() + ":第 " + (i + 1) + " 帧(" + id + ")之后 ReturnedToTown=" + rig.Machine.ReturnedToTown
                    + ",谓词 " + predicate + ",模型 " + model.Returned;
            }
        }

        if (Trace(rig) != string.Join(",", model.Events))
        {
            return Describe() + ":事件序列 " + Trace(rig) + ",模型 " + string.Join(",", model.Events);
        }
        if (ReturnedEvents(rig) != (model.Returned ? 1 : 0))
        {
            return Describe() + ":ReturnedToTown 事件数 " + ReturnedEvents(rig);
        }
        if (rig.Machine.Duplicates != model.Duplicates || rig.Machine.PassthroughCount != model.Passthrough || rig.Machine.Malformed != 0)
        {
            return Describe() + ":计数 duplicates=" + rig.Machine.Duplicates + " passthrough=" + rig.Machine.PassthroughCount
                + " malformed=" + rig.Machine.Malformed + ",模型 " + model.Duplicates + "/" + model.Passthrough;
        }
        Dictionary<ushort, int> actualIds = rig.Machine.Passthrough.ToDictionary(c => (ushort)c.PacketId, c => c.Count);
        if (actualIds.Count != model.PassIds.Count || model.PassIds.Any(p => !actualIds.TryGetValue(p.Key, out int n) || n != p.Value))
        {
            return Describe() + ":透传包号表与模型不符";
        }
        if (rig.Machine.Passthrough.Any(c => c.PacketClass != PacketClass.Noti))
        {
            return Describe() + ":透传表里出现了非 NOTI 的项";
        }
        return null;
    }

    private static IEnumerable<ushort[]> AllSequences(ushort[] alphabet, int maxLength)
    {
        var frontier = new List<ushort[]> { new ushort[0] };
        for (int length = 1; length <= maxLength; length++)
        {
            var next = new List<ushort[]>();
            foreach (ushort[] prefix in frontier)
            {
                foreach (ushort id in alphabet)
                {
                    next.Add(prefix.Concat(new[] { id }).ToArray());
                }
            }
            foreach (ushort[] sequence in next)
            {
                yield return sequence;
            }
            frontier = next;
        }
    }

    private static IEnumerable<ushort[]> AllPermutations(ushort[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        for (int i = 0; i < items.Length; i++)
        {
            ushort[] rest = items.Where((_, index) => index != i).ToArray();
            foreach (ushort[] tail in AllPermutations(rest))
            {
                yield return new[] { items[i] }.Concat(tail).ToArray();
            }
        }
    }

    private static IEnumerable<ushort[]> Subsets(ushort[] items)
    {
        for (int mask = 1; mask < (1 << items.Length); mask++)
        {
            yield return items.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
        }
    }

    // ---------------- 判据矩阵(穷举) ----------------

    [Fact]
    public async Task The_criterion_matrix_matches_the_model_and_the_predicate_for_every_sequence_up_to_length_four_and_every_give_up_position()
    {
        var failures = new List<string>();
        int runs = 0;
        foreach (ushort[] sequence in AllSequences(AllIds, 4))
        {
            for (int giveUpBefore = 0; giveUpBefore <= sequence.Length; giveUpBefore++)
            {
                runs++;
                string problem = await Compare(sequence, giveUpBefore);
                if (problem != null && failures.Count < 12)
                {
                    failures.Add(problem);
                }
            }
        }
        Assert.True(failures.Count == 0, "不符(共跑 " + runs + " 次):\n" + string.Join("\n", failures));
        Assert.True(runs > 5000);
    }

    [Fact]
    public async Task The_criterion_matrix_matches_for_every_ordering_of_every_subset_of_the_six_frames()
    {
        var failures = new List<string>();
        int runs = 0;
        foreach (ushort[] subset in Subsets(AllIds))
        {
            foreach (ushort[] ordering in AllPermutations(subset))
            {
                for (int giveUpBefore = 0; giveUpBefore <= ordering.Length; giveUpBefore++)
                {
                    runs++;
                    string problem = await Compare(ordering, giveUpBefore);
                    if (problem != null && failures.Count < 12)
                    {
                        failures.Add(problem);
                    }
                }
            }
        }
        Assert.True(failures.Count == 0, "不符(共跑 " + runs + " 次):\n" + string.Join("\n", failures));
        Assert.True(runs > 9000);
    }

    [Fact]
    public async Task The_criterion_matrix_matches_for_every_length_six_sequence_over_the_three_signal_frames_with_duplicates()
    {
        var failures = new List<string>();
        int runs = 0;
        foreach (ushort[] sequence in AllSequences(new[] { N23, N24, N2 }, 6))
        {
            for (int giveUpBefore = 0; giveUpBefore <= sequence.Length; giveUpBefore++)
            {
                runs++;
                string problem = await Compare(sequence, giveUpBefore);
                if (problem != null && failures.Count < 12)
                {
                    failures.Add(problem);
                }
            }
        }
        Assert.True(failures.Count == 0, "不符(共跑 " + runs + " 次):\n" + string.Join("\n", failures));
        Assert.True(runs > 1500);
    }

    // ---------------- 判据矩阵的关键格子(逐条看得懂的断言) ----------------

    [Fact]
    public void The_new_signal_constant_is_the_user_info_notice_number_2()
    {
        Assert.Equal(2, OnlineDungeonCombat.GiveUpUserInfoNoticeId);
        Assert.Equal(23, TownAreaPayloads.UserAreaPacketId);
        Assert.Equal(24, TownAreaPayloads.AreaUsersPacketId);
    }

    [Theory]
    [InlineData(new[] { 23, 24 })]
    [InlineData(new[] { 24, 23 })]
    [InlineData(new[] { 24, 2 })]
    [InlineData(new[] { 2, 24 })]
    public async Task Either_pair_in_either_order_returns_to_town_once_after_the_give_up(int[] order)
    {
        CombatRig rig = await GivingUpRig();
        for (int i = 0; i < order.Length; i++)
        {
            Assert.False(rig.Machine.ReturnedToTown, "第 " + (i + 1) + " 帧之前不该回城");
            rig.Feed(Frame((ushort)order[i]));
        }
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Equal(1, ReturnedEvents(rig));
        Assert.Equal(0, rig.Machine.Duplicates);
        Assert.Equal(0, rig.Machine.Malformed);
    }

    [Theory]
    [InlineData(new[] { 23, 2 })]
    [InlineData(new[] { 2, 23 })]
    [InlineData(new[] { 23 })]
    [InlineData(new[] { 24 })]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 23, 2, 3, 9, 36 })]
    [InlineData(new[] { 3, 9, 36, 2, 23 })]
    public async Task Other_combinations_are_not_a_return(int[] order)
    {
        CombatRig rig = await GivingUpRig();
        foreach (int id in order)
        {
            rig.Feed(Frame((ushort)id));
        }
        Assert.False(rig.Machine.ReturnedToTown);
        Assert.Equal(0, ReturnedEvents(rig));
    }

    [Theory]
    [InlineData(new[] { 3 })]
    [InlineData(new[] { 9 })]
    [InlineData(new[] { 36 })]
    [InlineData(new[] { 3, 9 })]
    [InlineData(new[] { 3, 36 })]
    [InlineData(new[] { 9, 36 })]
    [InlineData(new[] { 3, 9, 36 })]
    [InlineData(new[] { 36, 9, 3, 2 })]
    [InlineData(new[] { 36, 9, 3, 23 })]
    public async Task The_0_3_0_9_and_0_36_frames_never_make_a_return_alone_or_in_any_combination_with_a_single_signal_frame(int[] order)
    {
        CombatRig rig = await GivingUpRig();
        foreach (int id in order)
        {
            rig.Feed(Frame((ushort)id));
        }
        Assert.False(rig.Machine.ReturnedToTown);
        Assert.Equal(0, ReturnedEvents(rig));
        Assert.Equal(order.Count(id => id != 23 && id != 24), rig.Machine.PassthroughCount);     // 放弃之后的 0/23、0/24 不计透传,其余都计
    }

    [Fact]
    public async Task The_0_3_0_9_and_0_36_frames_do_not_substitute_for_the_missing_half_of_a_pair()
    {
        // 真服放弃之后的窗口:0/9、0/24、0/3、0/36、0/2 里任何一帧(0/2 除外)都不能顶 0/2:
        // 去掉 0/2 只剩 0/24 加三个别的帧,不回城
        CombatRig withoutUserInfo = await GivingUpRig();
        foreach (ushort id in new[] { N9, N24, N3, N36 })
        {
            withoutUserInfo.Feed(Frame(id));
        }
        Assert.False(withoutUserInfo.Machine.ReturnedToTown);

        // 去掉 0/24 只剩 0/2 加三个别的帧,也不回城
        CombatRig without24 = await GivingUpRig();
        foreach (ushort id in new[] { N9, N3, N36, N2 })
        {
            without24.Feed(Frame(id));
        }
        Assert.False(without24.Machine.ReturnedToTown);

        // 五帧齐了才回城,并且是在 0/2 这一帧上回的
        CombatRig full = await GivingUpRig();
        foreach (ushort id in new[] { N9, N24, N3, N36 })
        {
            full.Feed(Frame(id));
            Assert.False(full.Machine.ReturnedToTown);
        }
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, full.Feed(Frame(N2)));
        Assert.True(full.Machine.ReturnedToTown);
    }

    [Fact]
    public async Task Duplicate_frames_count_once_and_never_make_a_return_early()
    {
        CombatRig rig = await GivingUpRig();
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(Frame(N24)));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(Frame(N24)));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(Frame(N24)));
        Assert.False(rig.Machine.ReturnedToTown);
        Assert.Equal(2, rig.Machine.Duplicates);

        // 重复的 0/24 没有另算一份:来一个 0/2 才与那一份 0/24 凑成一对
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Equal(1, ReturnedEvents(rig));

        CombatRig lone2 = await GivingUpRig();
        lone2.Feed(Frame(N2));
        lone2.Feed(Frame(N2));
        lone2.Feed(Frame(N2));
        Assert.False(lone2.Machine.ReturnedToTown);
        Assert.Equal(0, lone2.Machine.Duplicates);          // 0/2 不是"重复帧"的口径:它的裁决始终是透传
        Assert.Equal(3, lone2.Machine.PassthroughCount);

        CombatRig lone23 = await GivingUpRig();
        lone23.Feed(Frame(N23));
        lone23.Feed(Frame(N23));
        Assert.False(lone23.Machine.ReturnedToTown);
        Assert.Equal(1, lone23.Machine.Duplicates);
    }

    [Fact]
    public async Task The_signal_frames_that_arrive_before_the_give_up_do_not_count()
    {
        var rig = new CombatRig(2);
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N24)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N23)));
        Assert.False(rig.Machine.ReturnedToTown);
        Assert.Equal(3, rig.Machine.PassthroughCount);

        Assert.True((await rig.Machine.RequestGiveUpAsync()).Sent);
        Assert.False(rig.Machine.ReturnedToTown);

        // 放弃之前见过的 0/24 与 0/2 不能凑成一对:现在只来一个 0/2 不回城
        rig.Feed(Frame(N2));
        Assert.False(rig.Machine.ReturnedToTown);
        // 放弃之后的 0/24 与放弃之后的 0/2 才凑成一对
        rig.Feed(Frame(N24));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Equal(1, ReturnedEvents(rig));
    }

    [Fact]
    public async Task After_the_return_every_later_frame_leaves_the_event_alone()
    {
        CombatRig rig = await GivingUpRig();
        rig.Feed(Frame(N23));
        rig.Feed(Frame(N24));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Equal(1, ReturnedEvents(rig));

        // 回城之后又来 0/2(透传)、0/23 与 0/24(按重复忽略):不再发事件、不再改状态
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(Frame(N23)));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(Frame(N24)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));
        Assert.Equal(1, ReturnedEvents(rig));
        Assert.Equal("G,R,D23,D24", Trace(rig));

        // 先凑成 0/24 加 0/2 的一对,之后迟到的 0/23 按重复忽略,事件不重发、0/23 的快照不补记
        CombatRig second = await GivingUpRig();
        second.Feed(Frame(N24));
        second.Feed(Frame(N2));
        Assert.Equal(1, ReturnedEvents(second));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, second.Feed(Frame(N23)));
        Assert.Equal(1, ReturnedEvents(second));
        Assert.Null(second.Machine.UserArea);
        Assert.Equal("G,R,D23", Trace(second));
    }

    // ---------------- 0/2 的裁决与计数口径 ----------------

    [Fact]
    public async Task The_0_2_verdict_stays_passthrough_before_and_after_the_give_up_and_it_is_counted_as_passthrough()
    {
        var rig = new CombatRig(2);
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));          // 放弃之前
        Assert.True((await rig.Machine.RequestGiveUpAsync()).Sent);
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));          // 放弃之后(记下了,裁决不变)
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(Frame(N24)));            // 凑成一对的那一帧是 0/24
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));          // 回城之后

        Assert.Equal(3, rig.Machine.PassthroughCount);
        OnlineCombatPacketCount[] passthrough = rig.Machine.Passthrough.ToArray();
        Assert.Single(passthrough);
        Assert.Equal(PacketClass.Noti, passthrough[0].PacketClass);
        Assert.Equal(2, passthrough[0].PacketId);
        Assert.Equal(3, passthrough[0].Count);
        Assert.Equal(0, rig.Machine.Duplicates);
        Assert.Equal(0, rig.Machine.Malformed);
    }

    [Fact]
    public async Task The_0_23_and_0_24_accounting_is_unchanged_accepted_once_never_in_the_passthrough_table()
    {
        CombatRig rig = await GivingUpRig();
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(Frame(N23)));
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(Frame(N24)));
        Assert.Equal(OnlineCombatFrameVerdict.Duplicate, rig.Feed(Frame(N24)));
        Assert.Equal(0, rig.Machine.PassthroughCount);
        Assert.Empty(rig.Machine.Passthrough);
        Assert.Equal(1, rig.Machine.Duplicates);
    }

    // ---------------- 载荷读不读得下来不影响判据 ----------------

    [Fact]
    public async Task The_25_byte_unparseable_0_24_and_a_0_2_still_return_with_no_snapshots_and_no_malformed_count()
    {
        byte[] bad = GiveUpSignalKit.Unparseable25();
        CombatRig rig = await GivingUpRig();
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, rig.Feed(Frame(N24, bad)));
        Assert.Equal(OnlineCombatFrameVerdict.Passthrough, rig.Feed(Frame(N2)));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Null(rig.Machine.UserArea);
        Assert.Null(rig.Machine.AreaUsers);
        Assert.Equal(0, rig.Machine.Malformed);
        OnlineCombatEvent returned = rig.Machine.Events.Single(e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
        Assert.Null(returned.UserArea);
        Assert.Null(returned.AreaUsers);
    }

    [Fact]
    public async Task The_return_does_not_depend_on_whether_either_payload_parses()
    {
        // 0/23 太短、0/24 声明的记录数装不下:两个都读不下来,(0/23 且 0/24)照样成立
        CombatRig rig = await GivingUpRig();
        Assert.False(TownAreaPayloads.TryParseUserArea(new byte[] { 1, 2 }, out _));
        rig.Feed(Frame(N23, new byte[] { 1, 2 }));
        rig.Feed(Frame(N24, GiveUpSignalKit.Unparseable25()));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Null(rig.Machine.UserArea);
        Assert.Null(rig.Machine.AreaUsers);
        Assert.Equal(0, rig.Machine.Malformed);
        Assert.Equal(0, rig.Machine.Duplicates);
    }

    [Fact]
    public async Task A_parseable_0_24_with_a_0_2_returns_with_the_area_users_snapshot_and_without_the_user_area_one()
    {
        CombatRig rig = await GivingUpRig();
        rig.Feed(Frame(N2));
        rig.Feed(Frame(N24));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.Null(rig.Machine.UserArea);
        Assert.NotNull(rig.Machine.AreaUsers);
        Assert.Single(rig.Machine.AreaUsers.Entries);
        OnlineCombatEvent returned = rig.Machine.Events.Single(e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
        Assert.Null(returned.UserArea);
        Assert.Same(rig.Machine.AreaUsers, returned.AreaUsers);
    }

    [Fact]
    public async Task The_classic_pair_still_carries_both_snapshots_on_the_event()
    {
        CombatRig rig = await GivingUpRig();
        rig.Feed(Frame(N24));
        rig.Feed(Frame(N23));
        Assert.True(rig.Machine.ReturnedToTown);
        Assert.NotNull(rig.Machine.UserArea);
        Assert.NotNull(rig.Machine.AreaUsers);
        OnlineCombatEvent returned = rig.Machine.Events.Single(e => e.Kind == OnlineCombatEventKind.ReturnedToTown);
        Assert.Same(rig.Machine.UserArea, returned.UserArea);
        Assert.Same(rig.Machine.AreaUsers, returned.AreaUsers);
    }

    [Fact]
    public async Task The_return_event_is_raised_on_the_frame_that_completes_the_criterion_and_not_before()
    {
        CombatRig rig = await GivingUpRig();
        rig.Feed(Frame(N24));
        Assert.Equal("G", Trace(rig));
        rig.Feed(Frame(N3));
        rig.Feed(Frame(N9));
        Assert.Equal("G", Trace(rig));
        rig.Feed(Frame(N2));
        Assert.Equal("G,R", Trace(rig));
        OnlineCombatEvent returned = rig.Machine.Events.Last();
        Assert.Equal(OnlineCombatEventKind.ReturnedToTown, returned.Kind);
        Assert.Equal(OnlineCombatFrameVerdict.Accepted, returned.Verdict);
    }
}
