namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳"创建/删除角色"流程的相邻点补充测试(r20fix-createcharui):取消在途的删除、提交之后又改名、
/// 记着的 0 号选中位、服务端回来的名册里找不到新角色时选谁、名字只差长度的角色不算新角色。
/// 夹具(Rig、CreateRig、Entry、日志常量)在 <c>OnlineShellFlowManageEdgeTests.cs</c>。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    private const string CreateInvalidLine = "DNF_ONLINE stage=charselect event=fail reason=create_char_invalid";
    private const string DeleteInvalidLine = "DNF_ONLINE stage=charselect event=fail reason=delete_char_invalid";
    private const string CreateOkLine = "DNF_ONLINE stage=charselect event=ok reason=create_char";
    private const string DeleteOkLine = "DNF_ONLINE stage=charselect event=ok reason=delete_char";
    private const string EnterCreatedLine = "DNF_ONLINE stage=charselect event=enter reason=char_created";
    private const string EnterDeletedLine = "DNF_ONLINE stage=charselect event=enter reason=char_deleted";
    private const string CreateCancelledLine = "DNF_ONLINE stage=charselect event=fail reason=create_char_cancelled";
    private const string DeleteCancelledLine = "DNF_ONLINE stage=charselect event=fail reason=delete_char_cancelled";

    // ------------------------------------------------------------------ 取消在途的操作:面板留着,显示"已取消"

    [Fact]
    public void Cancelling_an_in_flight_delete_keeps_the_modal_open_with_a_cancelled_message_and_drops_the_request()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        var never = new TaskCompletionSource<OnlineCharacterManageOutcome>();
        rig.Backend.OnDeleteCharacter = (slot, confirm, ct) => never.Task;
        rig.ClickDelete();
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.True(rig.Flow.Busy);
        CancellationToken token = rig.Backend.LastToken;
        Assert.False(token.IsCancellationRequested);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.True(token.IsCancellationRequested);
        Assert.False(rig.Flow.Busy);
        Assert.NotNull(rig.Flow.DeleteModal);
        Assert.False(rig.Flow.DeleteModal.Busy);
        Assert.Equal("操作已取消", rig.Flow.DeleteModal.ErrorText);
        Assert.Single(rig.Lines, line => line == DeleteCancelledLine);
        Assert.DoesNotContain(CreateCancelledLine, rig.Lines);
        Assert.Equal(1, rig.Backend.DeleteCalls);

        // 面板还开着,再按一次 Esc 才关掉
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.Null(rig.Flow.DeleteModal);
        Assert.False(rig.Flow.HasCharacterModalOpen);
    }

    [Fact]
    public void Cancelling_an_in_flight_create_keeps_the_modal_open_with_a_cancelled_message_and_drops_the_request()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        var never = new TaskCompletionSource<OnlineCharacterManageOutcome>();
        rig.Backend.OnCreateCharacter = (job, name, ct) => never.Task;
        rig.ClickCreate();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.True(rig.Flow.Busy);
        CancellationToken token = rig.Backend.LastToken;
        Assert.False(token.IsCancellationRequested);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.True(token.IsCancellationRequested);
        Assert.False(rig.Flow.Busy);
        Assert.NotNull(rig.Flow.CreateModal);
        Assert.False(rig.Flow.CreateModal.Busy);
        Assert.Equal("操作已取消", rig.Flow.CreateModal.ErrorText);
        Assert.Single(rig.Lines, line => line == CreateCancelledLine);
        Assert.DoesNotContain(DeleteCancelledLine, rig.Lines);
        Assert.Equal(1, rig.Backend.CreateCalls);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.Null(rig.Flow.CreateModal);
        Assert.False(rig.Flow.HasCharacterModalOpen);
    }

    // ------------------------------------------------------------------ 面板开着时,底下界面已经挂着的结果不被处理

    /// <summary>
    /// "创立角色"钮和"游戏开始"钮都在同一次轮询之前点下:这次轮询先开创建面板,"游戏开始"的结果还挂在选角界面上;
    /// 面板开着的每一次轮询都只处理面板自己的意图,不处理底下界面的结果,所以不会在面板底下进游戏。
    /// </summary>
    [Fact]
    public void A_start_already_pending_on_the_select_screen_is_not_acted_on_while_the_create_modal_is_open()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));

        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Start));
        rig.Pump(4);

        Assert.NotNull(rig.Flow.CreateModal);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
    }

    [Fact]
    public void A_start_already_pending_on_the_select_screen_is_not_acted_on_while_the_delete_modal_is_open()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));

        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Delete));
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Start));
        rig.Pump(4);

        Assert.NotNull(rig.Flow.DeleteModal);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
    }

    // ------------------------------------------------------------------ 槽位号是一个字节:255 能删,256 不能(256 截成字节是 0,会删错人)

    [Fact]
    public void The_delete_modal_opens_for_slot_255_but_not_for_slot_256_whose_byte_would_wrap_to_slot_0()
    {
        var seed = new (string Name, byte Job, byte Level)[257];
        for (int i = 0; i < seed.Length; i++)
        {
            seed[i] = ("Hero" + i, (byte)(i % 11), 1);
        }
        Rig rig = CreateRig(allowWrite: true, wireManage: true, seed);
        Assert.Equal(257, rig.Flow.Characters.Entries.Count);

        // 一行 6 个:往下 42 行是 252 号,再往右 3 格是 255 号
        for (int i = 0; i < 42; i++)
        {
            rig.Flow.Key(OnlineShellKey.Down);
        }
        for (int i = 0; i < 3; i++)
        {
            rig.Flow.Key(OnlineShellKey.Right);
        }
        rig.ClickDelete();
        Assert.NotNull(rig.Flow.DeleteModal);
        Assert.Equal((byte)255, rig.Flow.DeleteModal.TargetSlot);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);

        rig.Flow.Key(OnlineShellKey.Right);   // 256 号
        rig.ClickDelete();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Null(rig.Flow.DeleteModal);
        Assert.Equal(0, rig.Backend.DeleteCalls);
    }

    // ------------------------------------------------------------------ 提交之后、轮询之前又改了名字

    /// <summary>
    /// 回车时面板自己的检查通过、挂起了"提交";流程下一次轮询才取走它,在这之间又敲进一个禁用字符(尖括号):
    /// 流程自己的检查要重新验一遍名字,不合格就不发给后端。
    /// </summary>
    [Fact]
    public void A_name_made_invalid_after_the_modal_accepted_it_is_stopped_by_the_flows_own_check()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickCreate();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        Assert.Null(rig.Flow.CreateModal.ErrorText);

        rig.Flow.Type("<");
        rig.Poll();

        Assert.Equal(0, rig.Backend.CreateCalls);
        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.CreateModal.Busy);
        Assert.Equal("输入内容不符合规则", rig.Flow.CreateModal.ErrorText);
        Assert.Single(rig.Lines, line => line == CreateInvalidLine);
        Assert.DoesNotContain(DeleteInvalidLine, rig.Lines);
    }

    /// <summary>删除一侧同理:确认名在回车时对得上,轮询取走提交之前又多敲了一个字,流程自己的检查发现对不上,不发给后端。</summary>
    [Fact]
    public void A_confirmation_name_that_stops_matching_after_the_modal_accepted_it_is_stopped_by_the_flows_own_check()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickDelete();
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        Assert.Null(rig.Flow.DeleteModal.ErrorText);

        rig.Flow.Type("X");
        rig.Poll();

        Assert.Equal(0, rig.Backend.DeleteCalls);
        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.DeleteModal.Busy);
        Assert.Equal("输入内容不符合规则", rig.Flow.DeleteModal.ErrorText);
        Assert.Single(rig.Lines, line => line == DeleteInvalidLine);
        Assert.DoesNotContain(CreateInvalidLine, rig.Lines);
    }

    // ------------------------------------------------------------------ 成功之后的日志:整行逐字、顺序固定、各用各的原因

    [Fact]
    public void A_successful_create_logs_ok_and_then_enters_the_select_stage_again_for_the_created_character()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickCreate();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(2, rig.Flow.Characters.Entries.Count);

        string[] sinceOpen = rig.Lines.SkipWhile(line => line != OpenCreateLine).ToArray();
        Assert.Equal(new[] { OpenCreateLine, CreateOkLine, EnterCreatedLine }, sinceOpen);
    }

    [Fact]
    public void A_successful_delete_logs_ok_and_then_enters_the_select_stage_again_for_the_remaining_characters()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.ClickDelete();
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Single(rig.Flow.Characters.Entries);

        string[] sinceOpen = rig.Lines.SkipWhile(line => line != OpenDeleteLine).ToArray();
        Assert.Equal(new[] { OpenDeleteLine, DeleteOkLine, EnterDeletedLine }, sinceOpen);
    }

    // ------------------------------------------------------------------ 记着的选中位是 0

    [Fact]
    public void A_remembered_first_slot_stays_the_first_slot_after_a_delete_of_another_character()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnSelect = (slot, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.Unexpected, true));

        // 选 0 号进游戏失败 → 回选角,SelectedSlot 记着 0
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(4);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.SelectedSlot);

        // 删 1 号:名册剩 1 个,记着的 0 还是 0
        rig.MoveSelectionTo(1);
        rig.ClickDelete();
        Assert.Equal((byte)1, rig.Flow.DeleteModal.TargetSlot);
        rig.Flow.Type("HeroB");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Single(rig.Flow.Characters.Entries);
        Assert.Equal(0, rig.Flow.SelectedSlot);
    }

    [Fact]
    public void A_remembered_first_slot_stays_the_first_slot_after_a_create()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.Backend.OnSelect = (slot, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.Unexpected, true));

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(4);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.SelectedSlot);

        rig.ClickCreate();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(2, rig.Flow.Characters.Entries.Count);
        Assert.Equal(0, rig.Flow.SelectedSlot);
    }

    // ------------------------------------------------------------------ 服务端回来的名册里找不到新角色

    /// <summary>服务端把名字改了:名册里没有任何条目的职业与名字都对得上,就选最后一个条目。</summary>
    [Fact]
    public void When_no_roster_entry_matches_the_created_character_the_last_entry_is_selected()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20), ("HeroC", 2, 30));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Add(Entry(950, "ServerPicked", job));
            return Task.FromResult(OnlineCharacterManageOutcome.Ok(rig.Backend.SnapshotManagedRoster()));
        };

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down);    // 职业 3
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(4, rig.Flow.Characters.Entries.Count);
        Assert.Equal(3, rig.EnterGameAndReadSlot());
    }

    /// <summary>职业不对的同名条目不算新角色:同名但职业不同的旧条目排在前面,没有匹配项时仍选最后一个。</summary>
    [Fact]
    public void An_entry_with_the_same_name_but_another_job_does_not_count_as_the_created_character()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Clear();
            rig.Backend.ManagedRoster.Add(Entry(970, name, 5));            // 名字对、职业不对,新 uid
            rig.Backend.ManagedRoster.Add(Entry(901, "HeroB", 1, 20));
            rig.Backend.ManagedRoster.Add(Entry(971, "ServerPicked", job));
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

    /// <summary>
    /// 名字只差长度的条目不算匹配:创建的是 "HeroNew",排在前面的有名字更长的 "HeroNewer"(以它开头)和更短的 "Hero"(它的开头),
    /// 都是新 uid、职业相同;只有后面那个名字逐字相等的才是新角色。
    /// </summary>
    [Fact]
    public void Entries_whose_names_are_longer_or_shorter_than_the_created_name_are_not_the_created_character()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            rig.Backend.ManagedRoster.Clear();
            rig.Backend.ManagedRoster.Add(Entry(900, "HeroA", 0, 10));
            rig.Backend.ManagedRoster.Add(Entry(962, "Hero", job));          // 比创建的名字短,是它的开头
            rig.Backend.ManagedRoster.Add(Entry(961, name + "er", job));     // 比创建的名字长,以它开头
            rig.Backend.ManagedRoster.Add(Entry(960, name, job));            // 真正的新角色
            return Task.FromResult(OnlineCharacterManageOutcome.Ok(rig.Backend.SnapshotManagedRoster()));
        };

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down);    // 职业 3
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.Equal(4, rig.Flow.Characters.Entries.Count);
        Assert.Equal(3, rig.EnterGameAndReadSlot());
    }
}
