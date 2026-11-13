using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 两个新解析器与既有联网进城流程的衔接:选角判定(<see cref="SlotJudge"/>)把选角回包的解析结果作为附加信息一并送出,
/// 进城结果(<see cref="OnlineEntryResult"/>)在 Net 层快照出口新增两个并列的只读出口(<c>SelectCharacter</c>、<c>DungeonPermissions</c>)。
/// 重点是"不改选角流程语义":判定的种类与失败码、客户端发出的命令序列、<see cref="TownEntrySnapshot"/> 的计数都与接线前一致,
/// 这里有一份对原判定规则的独立复刻做随机对拍来钉死它。
/// 帧全是合成的:直接的会话加密帧与封在真信封里由会话层拆开的内层帧都用到,账号号 905,副本号 901 起,所有取值为合成编号。
/// 解析器本身的读序与边界见 <see cref="SelectCharacterResultParserTests"/> 与 <see cref="DungeonPermissionParserTests"/>。
/// </summary>
public class SelectCharacterWiringTests
{
    // ---- 助手 ----

    private static SlotFrameHint Hint(PacketClass cls, int id, params byte[] payload)
    {
        return new SlotFrameHint(cls, id, payload);
    }

    private static SlotFrameHint SelectHint(params byte[] payload)
    {
        return Hint(PacketClass.Cmd, SlotJudge.SelectPacketId, payload);
    }

    private static SlotFrameHint CompleteHint()
    {
        return Hint(PacketClass.Noti, SlotJudge.EnterCompletePacketId);
    }

    private static SelectCharacterSpec Spec(int variant)
    {
        return SelectCharacterSpec.Variant(variant, premiumCount: 2, tutorialCount: 3);
    }

    /// <summary>直接发来的选角回包帧:成功体或失败体后接折叠哈希(服务端对 1 到 0x190 字节的载荷追加),再经会话加密、补零。</summary>
    private static byte[] Reply4(byte[] payload)
    {
        return DungeonEntryWire.Reply(SlotJudge.SelectPacketId, PacketIntegrity.AppendFoldHash(payload));
    }

    private static byte[] Complete()
    {
        return DungeonEntryWire.Notice(SlotJudge.EnterCompletePacketId, new byte[] { 0 });
    }

    private static byte[] PermissionNotice(params (int DungeonId, int Tier)[] entries)
    {
        return DungeonEntryWire.Notice(DungeonPermissionParser.PacketId, Permission(entries));
    }

    private static OnlineTownWindows LongTail()
    {
        return new OnlineTownWindows(
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1200));
    }

    // ======== 帧摘要:SlotFrameHint.SelectResult ========

    [Fact]
    public void A_select_success_hint_carries_the_parsed_result_and_keeps_its_old_summary_fields()
    {
        SelectCharacterSpec spec = Spec(3);
        byte[] payload = spec.Build(0xA1, 0xA2);

        var hint = new SlotFrameHint(PacketClass.Cmd, 4, payload);

        Assert.NotNull(hint.SelectResult);
        Assert.True(hint.SelectResult.IsSuccess);
        AssertLoad(spec, hint.SelectResult.Load);
        Assert.Equal(2, hint.SelectResult.TrailingByteCount);
        Assert.Equal(PacketClass.Cmd, hint.PacketClass);
        Assert.Equal(4, hint.PacketId);
        Assert.Equal(payload.Length, hint.PayloadLength);
        Assert.Equal(payload[0], hint.FirstByte);
        Assert.Equal(payload[1], hint.SecondByte);
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(4)]
    [InlineData(0x9f)]
    [InlineData(8)]
    [InlineData(0x60)]
    [InlineData(0)]
    [InlineData(255)]
    public void A_select_failure_hint_carries_the_mapped_failure(int code)
    {
        SlotFrameHint hint = SelectHint(0, (byte)code);

        Assert.NotNull(hint.SelectResult);
        Assert.False(hint.SelectResult.IsSuccess);
        Assert.Equal((byte)code, hint.SelectResult.FailureCode);
        Assert.Equal(SelectCharacterResultParser.MapFailureCode((byte)code), hint.SelectResult.Failure);
        Assert.Equal(0, hint.FirstByte);
        Assert.Equal(code, hint.SecondByte);
    }

    [Fact]
    public void A_select_reply_that_cannot_be_read_has_no_result_but_keeps_its_summary()
    {
        byte[] almost = Spec(4).Build().Take(Spec(4).BodyLength - 1).ToArray();

        foreach (byte[] payload in new[] { new byte[] { 1, 9, 9 }, new byte[] { 0 }, new byte[] { 7, 0 }, new byte[0], null, almost })
        {
            var hint = new SlotFrameHint(PacketClass.Cmd, 4, payload);
            Assert.Null(hint.SelectResult);
            Assert.Equal(payload == null ? 0 : payload.Length, hint.PayloadLength);
        }
        Assert.Equal(1, new SlotFrameHint(PacketClass.Cmd, 4, new byte[] { 1, 9, 9 }).FirstByte);
        Assert.Equal(9, new SlotFrameHint(PacketClass.Cmd, 4, new byte[] { 1, 9, 9 }).SecondByte);
        Assert.Equal(-1, new SlotFrameHint(PacketClass.Cmd, 4, new byte[0]).FirstByte);
        Assert.Equal(-1, new SlotFrameHint(PacketClass.Cmd, 4, null).SecondByte);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(0, 5)]
    [InlineData(0, 124)]
    [InlineData(1, 3)]
    [InlineData(1, 5)]
    [InlineData(1, 124)]
    [InlineData(1, 2)]
    [InlineData(7, 4)]
    public void Only_class_cmd_packet_four_is_parsed_as_a_select_reply(int cls, int id)
    {
        byte[] readable = Spec(5).Build();
        Assert.True(SelectCharacterResultParser.TryParse(readable, out _));    // 载荷本身是读得通的,不读是因为包类与包号不对

        Assert.Null(new SlotFrameHint((PacketClass)cls, id, readable).SelectResult);
        Assert.Null(new SlotFrameHint((PacketClass)cls, id, new byte[] { 0, 8 }).SelectResult);
    }

    [Fact]
    public void A_default_hint_has_no_result()
    {
        SlotFrameHint hint = default;

        Assert.Null(hint.SelectResult);
        Assert.Equal(0, hint.PayloadLength);
    }

    // ======== 判定:SlotJudge.Judge 的附加结果 ========

    [Fact]
    public void Entered_by_a_success_reply_carries_that_replys_result()
    {
        SelectCharacterSpec spec = Spec(6);

        SlotOutcome outcome = SlotJudge.Judge(new[] { Hint(PacketClass.Noti, 183, 0, 0), SelectHint(spec.Build()) });

        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
        Assert.Equal(0, outcome.FailureCode);
        Assert.NotNull(outcome.SelectResult);
        Assert.True(outcome.SelectResult.IsSuccess);
        AssertLoad(spec, outcome.SelectResult.Load);
    }

    [Fact]
    public void A_short_success_shaped_reply_still_enters_and_has_no_result()
    {
        // 判定的口径不变:首字节为 1 就判进城;读不通的短回包没有解析结果
        SlotOutcome outcome = SlotJudge.Judge(new[] { SelectHint(1, 9, 9) });

        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
        Assert.Null(outcome.SelectResult);
    }

    [Fact]
    public void The_complete_notice_decides_and_the_result_is_taken_from_a_later_success_reply()
    {
        SelectCharacterSpec spec = Spec(7);

        SlotOutcome outcome = SlotJudge.Judge(new[] { CompleteHint(), SelectHint(spec.Build()) });

        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
        Assert.NotNull(outcome.SelectResult);
        AssertLoad(spec, outcome.SelectResult.Load);
    }

    [Fact]
    public void The_complete_notice_alone_gives_no_result()
    {
        SlotOutcome outcome = SlotJudge.Judge(new[] { Hint(PacketClass.Noti, 12, 2, 0), CompleteHint() });

        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
        Assert.Null(outcome.SelectResult);
    }

    [Fact]
    public void The_large_payload_form_enters_with_no_result_unless_a_readable_success_is_in_the_window()
    {
        byte[] big = new byte[SlotJudge.LargePayloadThreshold];
        SelectCharacterSpec spec = Spec(8);

        SlotOutcome alone = SlotJudge.Judge(new[] { new SlotFrameHint(PacketClass.Noti, 355, big) });
        Assert.Equal(SlotOutcomeKind.Entered, alone.Kind);
        Assert.Null(alone.SelectResult);

        // 大包与失败回包同窗:大包形态优先,判进城,失败回包的结果不当作进城的结果
        SlotOutcome withFailure = SlotJudge.Judge(new[] { SelectHint(0, 4), new SlotFrameHint(PacketClass.Noti, 355, big) });
        Assert.Equal(SlotOutcomeKind.Entered, withFailure.Kind);
        Assert.Null(withFailure.SelectResult);

        // 大包前面就带着一个读得通的成功回包:进城的结果是它(首字节为 1 就已经判进城,大包是否到达无关)
        SlotOutcome withSuccess = SlotJudge.Judge(new[] { new SlotFrameHint(PacketClass.Noti, 355, big), SelectHint(spec.Build()) });
        Assert.Equal(SlotOutcomeKind.Entered, withSuccess.Kind);
        AssertLoad(spec, withSuccess.SelectResult.Load);
    }

    [Fact]
    public void The_first_readable_success_wins_and_unreadable_ones_are_skipped()
    {
        SelectCharacterSpec first = Spec(9);
        SelectCharacterSpec second = Spec(10);

        SlotOutcome outcome = SlotJudge.Judge(new[] { SelectHint(1, 9, 9), SelectHint(first.Build()), SelectHint(second.Build()) });

        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);      // 第一帧 [1, 9, 9] 就已判进城
        AssertLoad(first, outcome.SelectResult.Load);              // 结果是窗口里第一个读得通的成功回包
    }

    [Fact]
    public void A_failure_carries_the_result_of_the_frame_that_gave_the_code_and_the_last_failure_wins()
    {
        SlotOutcome outcome = SlotJudge.Judge(new[]
        {
            SelectHint(0, 4),
            Hint(PacketClass.Noti, 183, 0, 0),
            SelectHint(0, 8, 1, 2, 3, 4),
        });

        Assert.Equal(SlotOutcomeKind.Failed, outcome.Kind);
        Assert.Equal((byte)8, outcome.FailureCode);
        Assert.NotNull(outcome.SelectResult);
        Assert.False(outcome.SelectResult.IsSuccess);
        Assert.Equal((byte)8, outcome.SelectResult.FailureCode);
        Assert.Equal(SelectCharacterFailure.NamePrefixForbidden, outcome.SelectResult.Failure);
        Assert.Equal(4, outcome.SelectResult.TrailingByteCount);
    }

    [Fact]
    public void Every_failure_code_comes_back_both_as_the_old_code_and_as_a_mapped_result()
    {
        for (int code = 0; code < 256; code++)
        {
            SlotOutcome outcome = SlotJudge.Judge(new[] { SelectHint(0, (byte)code) });

            Assert.Equal(SlotOutcomeKind.Failed, outcome.Kind);
            Assert.Equal((byte)code, outcome.FailureCode);
            Assert.Equal((byte)code, outcome.SelectResult.FailureCode);
            Assert.Equal(SelectCharacterResultParser.MapFailureCode((byte)code), outcome.SelectResult.Failure);
        }
    }

    [Fact]
    public void A_success_after_a_failure_enters_with_the_success_result()
    {
        SelectCharacterSpec spec = Spec(11);

        SlotOutcome afterFailure = SlotJudge.Judge(new[] { SelectHint(0, 8), SelectHint(spec.Build()) });

        Assert.Equal(SlotOutcomeKind.Entered, afterFailure.Kind);
        Assert.Equal(0, afterFailure.FailureCode);
        AssertLoad(spec, afterFailure.SelectResult.Load);
    }

    [Fact]
    public void No_verdict_never_carries_a_result()
    {
        Assert.Null(SlotJudge.Judge(new SlotFrameHint[0]).SelectResult);
        Assert.Null(SlotJudge.Judge((IReadOnlyList<SlotFrameHint>)null).SelectResult);
        Assert.Null(SlotJudge.Judge((IReadOnlyList<DecodedFrame>)null).SelectResult);

        foreach (SlotFrameHint hint in new[]
        {
            SelectHint(0),                                  // 失败形状但缺码字节
            SelectHint(7, 0),                               // 首字节既不是成功也不是失败形状
            SelectHint(),                                   // 空载荷
            Hint(PacketClass.Noti, 183, 1, 2, 3),
            new SlotFrameHint(PacketClass.Noti, 355, new byte[SlotJudge.LargePayloadThreshold - 1]),
        })
        {
            SlotOutcome outcome = SlotJudge.Judge(new[] { hint });
            Assert.Equal(SlotOutcomeKind.NoVerdict, outcome.Kind);
            Assert.Null(outcome.SelectResult);
        }
    }

    [Fact]
    public void A_full_success_body_whose_flag_is_not_one_is_still_no_verdict_because_the_judgement_is_unchanged()
    {
        // 登记的口径差异:客户端读成功标志是"非 0",服务端只写 1;选角判定一直是"等于 1"并保持原样,所以标志为 7 的完整成功体判不出结果,
        // 而解析器照客户端口径把它读成成功。这个差异只在服务端写别的非 0 值时才有后果,实网没有
        SelectCharacterSpec spec = Spec(12);
        spec.Flag = 7;
        SlotFrameHint hint = SelectHint(spec.Build());

        Assert.NotNull(hint.SelectResult);
        Assert.True(hint.SelectResult.IsSuccess);

        SlotOutcome outcome = SlotJudge.Judge(new[] { hint });
        Assert.Equal(SlotOutcomeKind.NoVerdict, outcome.Kind);
        Assert.Null(outcome.SelectResult);
    }

    [Fact]
    public void Judging_decoded_frames_gives_the_same_verdict_and_result_as_judging_their_hints()
    {
        SelectCharacterSpec spec = Spec(13);
        (byte Cls, ushort Id, byte[] Payload)[] script =
        {
            (Noti, 183, new byte[] { 0, 0 }),
            (Cmd, 4, new byte[] { 0, 4 }),
            (Cmd, 4, spec.Build(1, 2, 3, 4)),
            (Noti, 124, new byte[] { 0 }),
        };
        DecodedFrame[] frames = script.Select(s => PktImplTestKit.Frame(s.Cls, s.Id, s.Payload)).ToArray();
        SlotFrameHint[] hints = script.Select(s => new SlotFrameHint((PacketClass)s.Cls, s.Id, s.Payload)).ToArray();

        SlotOutcome fromFrames = SlotJudge.Judge(frames);
        SlotOutcome fromHints = SlotJudge.Judge(hints);

        Assert.Equal(fromHints.Kind, fromFrames.Kind);
        Assert.Equal(fromHints.FailureCode, fromFrames.FailureCode);
        Assert.NotNull(fromFrames.SelectResult);
        AssertLoad(spec, fromFrames.SelectResult.Load);
        Assert.Equal(4, fromFrames.SelectResult.TrailingByteCount);
        Assert.Equal(fromHints.SelectResult.TrailingByteCount, fromFrames.SelectResult.TrailingByteCount);

        // 失败的那一份窗口
        DecodedFrame[] failing = { PktImplTestKit.Frame(Noti, 183, 0, 0), PktImplTestKit.Frame(Cmd, 4, 0, 0x13) };
        SlotOutcome failed = SlotJudge.Judge(failing);
        Assert.Equal(SlotOutcomeKind.Failed, failed.Kind);
        Assert.Equal((byte)0x13, failed.FailureCode);
        Assert.Equal(SelectCharacterFailure.CreationLocked, failed.SelectResult.Failure);
    }

    // ---- 对原判定规则的独立复刻做随机对拍:判定的种类与失败码不得因接线改变 ----

    /// <summary>改动前判定规则的独立复刻:先进城完成与成功回包,其次大包形态,再次失败码,最后无判定。</summary>
    private static SlotOutcomeKind ReferenceKind(IReadOnlyList<SlotFrameHint> frames, out byte code)
    {
        code = 0;
        byte? failure = null;
        bool large = false;
        foreach (SlotFrameHint frame in frames)
        {
            if (frame.PacketClass == PacketClass.Noti && frame.PacketId == 124)
            {
                return SlotOutcomeKind.Entered;
            }
            if (frame.PacketClass == PacketClass.Cmd && frame.PacketId == 4 && frame.PayloadLength >= 1)
            {
                if (frame.FirstByte == 1)
                {
                    return SlotOutcomeKind.Entered;
                }
                if (frame.FirstByte == 0 && frame.PayloadLength >= 2)
                {
                    failure = (byte)frame.SecondByte;
                }
            }
            if (frame.PayloadLength >= 3000)
            {
                large = true;
            }
        }
        if (large)
        {
            return SlotOutcomeKind.Entered;
        }
        if (failure.HasValue)
        {
            code = failure.Value;
            return SlotOutcomeKind.Failed;
        }
        return SlotOutcomeKind.NoVerdict;
    }

    private sealed class Lcg
    {
        private uint _state;

        public Lcg(uint seed)
        {
            _state = seed;
        }

        public uint Next()
        {
            _state = (_state * 1664525u) + 1013904223u;
            return _state;
        }

        public int Below(int n)
        {
            return (int)((Next() >> 8) % (uint)n);
        }
    }

    private static SlotFrameHint RandomHint(Lcg rng)
    {
        switch (rng.Below(13))
        {
            case 0:
                return CompleteHint();
            case 1:
                return Hint(PacketClass.Noti, 183, (byte)rng.Below(256), (byte)rng.Below(256));
            case 2:
                return SelectHint(0, (byte)rng.Below(256));
            case 3:
                return SelectHint(1, 9, 9);
            case 4:
                return SelectHint(Spec(rng.Below(300)).Build());
            case 5:
                return SelectHint(0);
            case 6:
                return SelectHint(7, 0);
            case 7:
                return SelectHint();
            case 8:
                return new SlotFrameHint(PacketClass.Noti, 355, new byte[rng.Below(2) == 0 ? 3000 : 2999]);
            case 9:
                return Hint(PacketClass.Cmd, 5, 1, 0);
            case 10:
                {
                    byte[] noise = new byte[rng.Below(31)];
                    for (int i = 0; i < noise.Length; i++)
                    {
                        noise[i] = (byte)rng.Below(256);
                    }
                    return SelectHint(noise);
                }
            case 11:
                return SelectHint(Spec(rng.Below(300)).Build((byte)rng.Below(256), (byte)rng.Below(256), 0, 0));
            default:
                return SelectHint(0, (byte)rng.Below(256), 9, 9, 9);
        }
    }

    [Fact]
    public void Random_windows_keep_the_old_verdicts_and_the_attached_results_follow_their_rules()
    {
        var rng = new Lcg(0x5E1EC7ED);
        int[] seenKinds = new int[3];
        for (int round = 0; round < 4000; round++)
        {
            var hints = new List<SlotFrameHint>();
            int count = rng.Below(7);
            for (int i = 0; i < count; i++)
            {
                hints.Add(RandomHint(rng));
            }

            SlotOutcome outcome = SlotJudge.Judge(hints);

            SlotOutcomeKind expectedKind = ReferenceKind(hints, out byte expectedCode);
            Assert.Equal(expectedKind, outcome.Kind);
            Assert.Equal(expectedCode, outcome.FailureCode);
            seenKinds[(int)outcome.Kind]++;

            switch (outcome.Kind)
            {
                case SlotOutcomeKind.NoVerdict:
                    Assert.Null(outcome.SelectResult);
                    break;
                case SlotOutcomeKind.Failed:
                    Assert.NotNull(outcome.SelectResult);
                    Assert.False(outcome.SelectResult.IsSuccess);
                    Assert.Equal(outcome.FailureCode, outcome.SelectResult.FailureCode);
                    break;
                default:
                    SelectCharacterResult firstSuccess = hints
                        .Select(h => h.SelectResult)
                        .FirstOrDefault(r => r != null && r.IsSuccess);
                    Assert.Same(firstSuccess, outcome.SelectResult);
                    break;
            }
        }

        // 三种判定都真的出现过,对拍不是空转
        Assert.All(seenKinds, n => Assert.True(n > 100));
    }

    // ======== 进城结果:SelectCharacter 与 DungeonPermissions 两个出口(真会话接内存管道) ========

    [Fact]
    public async Task A_direct_success_reply_fills_the_select_character_exit()
    {
        SelectCharacterSpec spec = Spec(14);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.Frames(Reply4(spec.Build())));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Equal(OnlineFailure.None, result.Failure);
        Assert.Equal(0, result.Slot);
        Assert.NotNull(result.SelectCharacter);
        Assert.True(result.SelectCharacter.IsSuccess);
        AssertLoad(spec, result.SelectCharacter.Load);
        Assert.True(result.SelectCharacter.TrailingByteCount >= 4);      // 折叠哈希 4 字节,加上会话加密补的零
        Assert.Null(result.DungeonPermissions);
        Assert.Equal(new ushort[] { 1, 8, 4 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task A_success_reply_inside_a_real_envelope_is_read_from_the_unwrapped_inner_frame()
    {
        SelectCharacterSpec spec = Spec(15);
        byte[] onWire = PacketIntegrity.AppendFoldHash(spec.Build());
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.Frames(HostTestKit.RealEnvelope(1, 4, onWire)));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.NotNull(result.SelectCharacter);
        AssertLoad(spec, result.SelectCharacter.Load);
        Assert.Equal(4, result.SelectCharacter.TrailingByteCount);       // 内层帧不补零,只剩折叠哈希
    }

    [Fact]
    public async Task A_failed_slot_does_not_stop_the_next_one_and_the_exit_holds_the_entered_slots_success()
    {
        SelectCharacterSpec spec = Spec(16);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => slot == 0
                ? HostTestKit.Frames(Reply4(SelectCharacterSpec.Failure(8)))
                : HostTestKit.Frames(Reply4(spec.Build())));

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 3);

        Assert.True(result.Entered);
        Assert.Equal(1, result.Slot);
        Assert.NotNull(result.SelectCharacter);
        Assert.True(result.SelectCharacter.IsSuccess);
        AssertLoad(spec, result.SelectCharacter.Load);
        Assert.Equal(new ushort[] { 1, 8, 4, 4 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        Assert.Equal(new byte[] { 0, 1 }, pipe.ClientFrames.Where(f => f.Id == 4).Select(f => f.Plain[0]).ToArray());
    }

    [Fact]
    public async Task When_every_slot_fails_the_failed_result_carries_the_last_slots_failure()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        int[] codes = { 0x13, 4, 0x9f };
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot => HostTestKit.Frames(Reply4(SelectCharacterSpec.Failure(codes[slot]))));

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 3);

        Assert.False(result.Entered);
        Assert.Equal(OnlineStage.SelectSlot, result.Stage);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.Equal(-1, result.Slot);
        Assert.Null(result.Snapshot);
        Assert.Null(result.DungeonPermissions);
        Assert.NotNull(result.SelectCharacter);
        Assert.False(result.SelectCharacter.IsSuccess);
        Assert.Equal((byte)0x9f, result.SelectCharacter.FailureCode);
        Assert.Equal(SelectCharacterFailure.NameHasSlang, result.SelectCharacter.Failure);
        Assert.Equal(new ushort[] { 1, 8, 4, 4, 4 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task A_silent_slot_after_a_failed_slot_does_not_erase_the_last_failure_result()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => slot == 0 ? HostTestKit.Frames(Reply4(SelectCharacterSpec.Failure(4))) : HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 2);

        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.NotNull(result.SelectCharacter);
        Assert.Equal(SelectCharacterFailure.SlotOutOfRange, result.SelectCharacter.Failure);
    }

    [Fact]
    public async Task When_nothing_is_ever_said_about_selecting_the_failed_result_has_no_select_character()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 2);

        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.Null(result.SelectCharacter);
        Assert.Null(result.DungeonPermissions);
    }

    [Fact]
    public async Task Entering_by_the_complete_notice_alone_leaves_the_select_character_exit_null()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Null(result.SelectCharacter);
        Assert.Null(result.DungeonPermissions);
    }

    [Fact]
    public async Task A_short_success_shaped_reply_still_enters_and_leaves_the_select_character_exit_null()
    {
        // 选角流程语义不变:首字节为 1 的短回包照旧判进城;它读不通,出口为 null
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.Frames(DungeonEntryWire.Reply(4, new byte[] { 1, 9, 9 })));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Equal(0, result.Slot);
        Assert.Null(result.SelectCharacter);
    }

    [Fact]
    public async Task A_success_reply_that_arrives_after_the_select_window_is_picked_up_from_the_tail_window()
    {
        SelectCharacterSpec spec = Spec(17);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(450);
                    pipe.Push(Reply4(spec.Build()));
                });
                return HostTestKit.EnterComplete();
            });

        OnlineEntryResult result = await HostTestKit.EnterTown(session, windows: LongTail());

        Assert.True(result.Entered);
        Assert.Equal(3, result.Snapshot.FramesSeen);              // 登录回包、进城完成、晚到的选角回包:各一遍
        Assert.NotNull(result.SelectCharacter);
        AssertLoad(spec, result.SelectCharacter.Load);
    }

    [Fact]
    public async Task A_failure_reply_arriving_in_the_tail_window_is_not_taken_as_the_entered_slots_result()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(450);
                    pipe.Push(Reply4(SelectCharacterSpec.Failure(8)));
                });
                return HostTestKit.EnterComplete();
            });

        OnlineEntryResult result = await HostTestKit.EnterTown(session, windows: LongTail());

        Assert.True(result.Entered);
        Assert.Null(result.SelectCharacter);
    }

    [Fact]
    public async Task A_full_success_body_with_a_flag_other_than_one_is_no_verdict_at_session_level_too()
    {
        // 判定口径不变:标志为 7 的完整成功体,选角判定判不出结果,逐槽试完,没有可用的槽;失败结果出口里没有它(它不是失败)
        SelectCharacterSpec spec = Spec(18);
        spec.Flag = 7;
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.Frames(Reply4(spec.Build())));

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 2);

        Assert.False(result.Entered);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.Null(result.SelectCharacter);
    }

    [Fact]
    public async Task Failures_before_the_select_stage_carry_neither_exit()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] rejected = DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 });
        pipe.Script = HostTestKit.TownScript(HostTestKit.Frames(rejected), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.Equal(OnlineFailure.GameLoginRejected, result.Failure);
        Assert.Null(result.SelectCharacter);
        Assert.Null(result.DungeonPermissions);
    }

    // ---- 副本许可出口 ----

    [Fact]
    public async Task Permission_notices_from_every_town_window_merge_with_later_ones_overriding_earlier_ones()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.Frames(DungeonEntryWire.Reply(1, HostTestKit.LoginSuccess()), PermissionNotice((901, 1), (902, 2), (903, 3))),
            HostTestKit.Frames(),
            slot =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(450);
                    pipe.Push(PermissionNotice((901, 2)));
                });
                return HostTestKit.Frames(PermissionNotice((902, 3), (904, 1)), Complete());
            });

        OnlineEntryResult result = await HostTestKit.EnterTown(session, windows: LongTail());

        Assert.True(result.Entered);
        DungeonPermissionSnapshot permissions = result.DungeonPermissions;
        Assert.NotNull(permissions);
        Assert.Equal(new[] { (901, 2), (902, 3), (903, 3), (904, 1) }, Pairs(permissions));
        Assert.Equal(3, permissions.NoticeCount);
        Assert.Equal(0, permissions.MalformedNoticeCount);
        Assert.True(permissions.TryGetTier(901, out byte tier));
        Assert.Equal(2, tier);
    }

    [Fact]
    public async Task A_permission_notice_inside_a_real_envelope_is_merged_from_the_unwrapped_inner_frame()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 5, Permission(new[] { (905, 3), (906, 1) }))),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.NotNull(result.DungeonPermissions);
        Assert.Equal(new[] { (905, 3), (906, 1) }, Pairs(result.DungeonPermissions));
        Assert.Equal(1, result.DungeonPermissions.NoticeCount);
    }

    [Fact]
    public async Task An_empty_permission_table_is_a_snapshot_with_no_entries_not_null()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.Frames(PermissionNotice(), Complete()));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.NotNull(result.DungeonPermissions);
        Assert.Equal(0, result.DungeonPermissions.Count);
        Assert.Equal(1, result.DungeonPermissions.NoticeCount);
        Assert.Equal(0, result.DungeonPermissions.MalformedNoticeCount);
    }

    [Fact]
    public async Task A_malformed_permission_notice_is_counted_and_never_disturbs_the_entry()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] declaresTooMany = new byte[] { 0xFF, 0xFF, 1 };
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.Frames(DungeonEntryWire.Notice(5, declaresTooMany), Complete()));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.NotNull(result.DungeonPermissions);
        Assert.Equal(0, result.DungeonPermissions.Count);
        Assert.Equal(0, result.DungeonPermissions.NoticeCount);
        Assert.Equal(1, result.DungeonPermissions.MalformedNoticeCount);
        Assert.Equal(0, result.Snapshot.FramesMalformed);            // 城镇快照的错位计数不看 (0,5)
    }

    [Fact]
    public async Task Without_any_permission_notice_the_exit_is_null()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Null(result.DungeonPermissions);
    }

    // ---- 不改既有语义 ----

    [Fact]
    public async Task The_two_new_exits_do_not_change_the_town_snapshot_counts_or_the_client_command_sequence()
    {
        SelectCharacterSpec spec = Spec(19);
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot => HostTestKit.Frames(Reply4(spec.Build()), PermissionNotice((901, 1)), Complete()));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        // 登录回包、角色列表、选角回包 (1,4)、许可 (0,5)、进城完成:看过 5 帧;认得并读出的只有列表与进城完成,(1,4) 与 (0,5) 在这里照旧只计"看过"
        Assert.Equal(5, snapshot.FramesSeen);
        Assert.Equal(2, snapshot.FramesParsed);
        Assert.Equal(0, snapshot.FramesMalformed);
        Assert.True(snapshot.EnterComplete);
        Assert.Equal(61, snapshot.Level);
        Assert.Null(snapshot.AreaId);               // 区域编号的缺口照旧:选角回包里的村庄字节本版不回填
        Assert.NotNull(result.SelectCharacter);
        Assert.NotNull(result.DungeonPermissions);

        Assert.Equal(new ushort[] { 1, 8, 4 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        Assert.All(pipe.ClientFrames, f => Assert.Equal(PacketClass.Cmd, f.Class));
        Assert.Equal(0, pipe.ClientFrames[2].Plain[0]);
        Assert.Equal(session.Events.Count, result.FramesSeen);
    }
}
