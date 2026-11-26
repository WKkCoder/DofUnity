namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳"创建/删除角色"流程在创建/删除之后选中谁、删除超时提示落在哪个面板上的补充测试
/// (r20fix-createcharui,簇 4;夹具与簇 3 的闸门测试在 <c>OnlineShellFlowManageEdgeTests.cs</c>)。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    // ------------------------------------------------------------------ 簇 4:删除之后选中谁

    [Theory]
    [InlineData(3, 2, 1)]      // [A,B,C] 删最后一个 C → 剩 [A,B],选中最后一个 B(1)
    [InlineData(3, 1, 1)]      // 删中间的 B → 剩 [A,C],原位置 1 现在是 C
    [InlineData(3, 0, 0)]      // 删第一个 A → 剩 [B,C],选中 0
    [InlineData(2, 1, 0)]      // [A,B] 删第二个 → 剩 [A],选中 0
    [InlineData(4, 3, 2)]      // [A,B,C,D] 删最后一个 → 选中新的最后一个(2)
    [InlineData(4, 1, 1)]
    public void After_a_delete_the_selection_stays_at_the_same_position_or_the_new_last_character(int count, int deleteIndex, int expectedSlot)
    {
        var seed = new (string Name, byte Job, byte Level)[count];
        for (int i = 0; i < count; i++)
        {
            seed[i] = ("Hero" + (char)('A' + i), (byte)i, (byte)(10 + i));
        }
        Rig rig = CreateRig(allowWrite: true, wireManage: true, seed);

        rig.MoveSelectionTo(deleteIndex);
        rig.ClickDelete();
        Assert.Equal((byte)deleteIndex, rig.Flow.DeleteModal.TargetSlot);
        rig.Flow.Type("Hero" + (char)('A' + deleteIndex));
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(count - 1, rig.Flow.Characters.Entries.Count);
        Assert.Equal(expectedSlot, rig.EnterGameAndReadSlot());
    }

    // ------------------------------------------------------------------ 簇 4:创建之后选中谁(服务端不一定把新角色追加在末尾)

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void After_a_create_the_selection_lands_on_the_new_character_wherever_the_server_put_it(int insertAt)
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20), ("HeroC", 2, 30));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Insert(insertAt, Entry(950, name, job));
            return Task.FromResult(OnlineCharacterManageOutcome.Ok(rig.Backend.SnapshotManagedRoster()));
        };

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down);    // 职业 3
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(4, rig.Flow.Characters.Entries.Count);
        Assert.Equal((byte)3, rig.Flow.Characters.Entries[insertAt].Job);
        Assert.Equal(insertAt, rig.EnterGameAndReadSlot());
    }

    /// <summary>
    /// 服务端复用了旧 uid:新角色(职业与名字都对得上)占了下标 0、uid 是旧名册里已有的,没有任何带新 uid 的匹配项。
    /// 此时仍选中那个对得上的(下标 0),而不是兜底的最后一个。
    /// </summary>
    [Fact]
    public void A_reused_uid_with_matching_job_and_name_is_still_found_as_the_created_character()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Clear();
            rig.Backend.ManagedRoster.Add(Entry(900, name, job));        // 900 是原来 HeroA 的 uid
            rig.Backend.ManagedRoster.Add(Entry(901, "HeroB", 1, 20));
            return Task.FromResult(OnlineCharacterManageOutcome.Ok(rig.Backend.SnapshotManagedRoster()));
        };

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down);    // 职业 3
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(2, rig.Flow.Characters.Entries.Count);
        Assert.Equal(0, rig.EnterGameAndReadSlot());
    }

    /// <summary>
    /// 名字只有首字节不同的条目不算匹配:下标 0 是对得上的旧 uid 条目,下标 1 是职业相同、名字首字节不同的旧 uid 条目。
    /// 比较要从第一个字节开始,选中 0。
    /// </summary>
    [Fact]
    public void A_name_that_differs_only_in_its_first_byte_does_not_count_as_the_created_character()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Clear();
            rig.Backend.ManagedRoster.Add(Entry(900, name, job));
            rig.Backend.ManagedRoster.Add(Entry(901, "KeroNew", job));   // 与 HeroNew 只差首字节
            return Task.FromResult(OnlineCharacterManageOutcome.Ok(rig.Backend.SnapshotManagedRoster()));
        };

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down);    // 职业 3
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(2, rig.Flow.Characters.Entries.Count);
        Assert.Equal(0, rig.EnterGameAndReadSlot());
    }

    /// <summary>带新 uid 的匹配项优先于复用旧 uid 的匹配项(即使后者的下标更靠前)。</summary>
    [Fact]
    public void A_match_with_a_new_uid_wins_over_an_earlier_match_that_reuses_an_old_uid()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Clear();
            rig.Backend.ManagedRoster.Add(Entry(900, name, job));         // 旧 uid,对得上
            rig.Backend.ManagedRoster.Add(Entry(901, "HeroB", 1, 20));
            rig.Backend.ManagedRoster.Add(Entry(960, name, job));         // 新 uid,对得上
            return Task.FromResult(OnlineCharacterManageOutcome.Ok(rig.Backend.SnapshotManagedRoster()));
        };

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down);    // 职业 3
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(3, rig.Flow.Characters.Entries.Count);
        Assert.Equal(2, rig.EnterGameAndReadSlot());
    }

    // ------------------------------------------------------------------ 簇 4:SelectedSlot 夹进新名册

    [Fact]
    public void A_remembered_selected_slot_is_clamped_into_the_roster_after_a_delete()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20), ("HeroC", 2, 30));
        rig.Backend.OnSelect = (slot, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.Unexpected, true));

        // 选 2 号槽位进游戏失败 → 回选角,SelectedSlot 记着 2
        rig.MoveSelectionTo(2);
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(4);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(2, rig.Flow.SelectedSlot);

        // 删 0 号槽位:名册剩 2 个,记着的 2 被夹到 1
        rig.MoveSelectionTo(0);
        rig.ClickDelete();
        Assert.Equal((byte)0, rig.Flow.DeleteModal.TargetSlot);
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(2, rig.Flow.Characters.Entries.Count);
        Assert.Equal(1, rig.Flow.SelectedSlot);
    }

    [Fact]
    public void A_remembered_selected_slot_becomes_minus_one_when_the_last_character_is_deleted()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.Backend.OnSelect = (slot, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.Unexpected, true));

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(4);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.SelectedSlot);

        rig.ClickDelete();
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Empty(rig.Flow.Characters.Entries);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
    }

    // ------------------------------------------------------------------ 簇 4:超时提示落在发起操作的那个面板上

    [Fact]
    public void A_delete_that_never_answers_times_out_on_the_delete_modal()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        var never = new TaskCompletionSource<OnlineCharacterManageOutcome>();
        rig.Backend.OnDeleteCharacter = (slot, confirm, ct) => never.Task;
        rig.ClickDelete();
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.True(rig.Flow.Busy);
        Assert.True(rig.Flow.DeleteModal.Busy);

        rig.Poll(60000);

        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.DeleteModal.Busy);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal("等待服务器响应超时", rig.Flow.DeleteModal.ErrorText);
        Assert.Null(rig.Flow.CreateModal);
        Assert.Single(rig.Lines, line => line == DeleteNoReplyLine);
        Assert.DoesNotContain(CreateNoReplyLine, rig.Lines);
    }

    [Fact]
    public void A_create_that_never_answers_times_out_on_the_create_modal()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        var never = new TaskCompletionSource<OnlineCharacterManageOutcome>();
        rig.Backend.OnCreateCharacter = (job, name, ct) => never.Task;
        rig.ClickCreate();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.True(rig.Flow.Busy);

        rig.Poll(60000);

        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.CreateModal.Busy);
        Assert.Equal("等待服务器响应超时", rig.Flow.CreateModal.ErrorText);
        Assert.Null(rig.Flow.DeleteModal);
        Assert.Single(rig.Lines, line => line == CreateNoReplyLine);
        Assert.DoesNotContain(DeleteNoReplyLine, rig.Lines);
    }
}
