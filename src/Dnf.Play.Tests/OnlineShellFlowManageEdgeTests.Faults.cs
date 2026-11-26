using System.Net.Sockets;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳"创建/删除角色"流程在后端任务<b>自己出错</b>时怎么收场(r20fix-createcharui,相邻点补测):
/// 后端回一个"断线"结局、后端任务抛连接类异常(IO 异常、套接字异常)、后端任务被取消、后端任务抛别的异常、后端回空结局。
/// 此前流程里处理"任务出错"的那一段(连接类异常退回登录、取消显示"已取消"、其它异常显示"服务器响应异常")没有任何用例走到过。
/// 夹具(Rig、CreateRig、日志常量)在 <c>OnlineShellFlowManageEdgeTests.cs</c>;全部走内存里的假后端与合成数据,不触发任何真实创建或删除。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    private const string DisconnectedFailLine = "DNF_ONLINE stage=charselect event=fail reason=disconnected";
    private const string LoginEnterDisconnectedLine = "DNF_ONLINE stage=login event=enter reason=disconnected";
    private const string CreateProtocolLine = "DNF_ONLINE stage=charselect event=fail reason=create_char_protocolerror";
    private const string DeleteProtocolLine = "DNF_ONLINE stage=charselect event=fail reason=delete_char_protocolerror";

    /// <summary>进选角、打开创建(或删除)面板、填一个合法名字、回车提交,让后端钩子照 <paramref name="reply"/> 回,再泵几帧。</summary>
    private static Rig SubmitWithBackendReply(bool create, Func<Task<OnlineCharacterManageOutcome>> reply)
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        if (create)
        {
            rig.Backend.OnCreateCharacter = (job, name, ct) => reply();
            rig.ClickCreate();
            rig.Flow.Type("HeroNew");
        }
        else
        {
            rig.Backend.OnDeleteCharacter = (slot, confirm, ct) => reply();
            rig.ClickDelete();
            rig.Flow.Type("HeroA");
        }
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump();
        return rig;
    }

    /// <summary>创建(或删除)面板是否还开着、是否在忙、当前的错误文字。</summary>
    private static (bool Open, bool Busy, string ErrorText) ModalState(Rig rig, bool create)
    {
        if (create)
        {
            CharacterCreateModal modal = rig.Flow.CreateModal;
            return (modal != null, modal != null && modal.Busy, modal?.ErrorText);
        }
        CharacterDeleteModal deleteModal = rig.Flow.DeleteModal;
        return (deleteModal != null, deleteModal != null && deleteModal.Busy, deleteModal?.ErrorText);
    }

    /// <summary>断线收场:退回登录、面板与名册全部清掉、给"连接已断开"提示、后端被通知断开恰好一次,日志从打开面板起恰好三行。</summary>
    private static void AssertBackToLogin(Rig rig, bool create, int disconnectCallsBefore)
    {
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Null(rig.Flow.CreateModal);
        Assert.Null(rig.Flow.DeleteModal);
        Assert.False(rig.Flow.Busy);
        Assert.NotNull(rig.Flow.Notice);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
        Assert.Equal(disconnectCallsBefore + 1, rig.Backend.DisconnectCalls);
        string[] sinceOpen = rig.Lines.SkipWhile(line => line != (create ? OpenCreateLine : OpenDeleteLine)).ToArray();
        Assert.Equal(new[] { create ? OpenCreateLine : OpenDeleteLine, DisconnectedFailLine, LoginEnterDisconnectedLine }, sinceOpen);
    }

    // ------------------------------------------------------------------ 断线:结局或连接类异常都退回登录

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_disconnected_outcome_from_the_backend_sends_the_player_back_to_login(bool create)
    {
        Rig rig = SubmitWithBackendReply(create, () => Task.FromResult(OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.Disconnected)));

        AssertBackToLogin(rig, create, disconnectCallsBefore: 0);
        Assert.Equal(1, create ? rig.Backend.CreateCalls : rig.Backend.DeleteCalls);
    }

    /// <summary>按名字造一个合成的连接类异常(显示名里不带平台相关的文字)。</summary>
    private static Exception MakeConnectionError(string kind)
    {
        switch (kind)
        {
            case "io": return new IOException("synthetic");
            case "end-of-stream": return new EndOfStreamException("synthetic");
            case "socket": return new SocketException();
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData(true, "io")]
    [InlineData(true, "end-of-stream")]
    [InlineData(true, "socket")]
    [InlineData(false, "io")]
    [InlineData(false, "end-of-stream")]
    [InlineData(false, "socket")]
    public void A_connection_error_from_the_backend_task_sends_the_player_back_to_login(bool create, string kind)
    {
        Exception error = MakeConnectionError(kind);
        Rig rig = SubmitWithBackendReply(create, () => Task.FromException<OnlineCharacterManageOutcome>(error));

        AssertBackToLogin(rig, create, disconnectCallsBefore: 0);
        Assert.Equal(1, create ? rig.Backend.CreateCalls : rig.Backend.DeleteCalls);
    }

    // ------------------------------------------------------------------ 取消与其它错误:面板留着,显示各自的文字

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cancelled_backend_task_keeps_the_modal_open_with_the_cancelled_message(bool create)
    {
        Rig rig = SubmitWithBackendReply(create, () => Task.FromCanceled<OnlineCharacterManageOutcome>(new CancellationToken(true)));

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
        (bool open, bool busy, string errorText) = ModalState(rig, create);
        Assert.True(open);
        Assert.False(busy);
        Assert.Equal("操作已取消", errorText);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        string[] sinceOpen = rig.Lines.SkipWhile(line => line != (create ? OpenCreateLine : OpenDeleteLine)).ToArray();
        Assert.Equal(new[] { create ? OpenCreateLine : OpenDeleteLine, create ? CreateCancelledLine : DeleteCancelledLine }, sinceOpen);
    }

    /// <summary>按名字造一个合成的、不算连接类的异常。</summary>
    private static Exception MakeOtherError(string kind)
    {
        switch (kind)
        {
            case "invalid-operation": return new InvalidOperationException("synthetic");
            case "timeout": return new TimeoutException("synthetic");
            case "argument": return new ArgumentException("synthetic");
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData(true, "invalid-operation")]
    [InlineData(true, "timeout")]
    [InlineData(true, "argument")]
    [InlineData(false, "invalid-operation")]
    [InlineData(false, "timeout")]
    [InlineData(false, "argument")]
    public void Any_other_error_from_the_backend_task_keeps_the_modal_open_with_the_protocol_error_message(bool create, string kind)
    {
        Exception error = MakeOtherError(kind);
        Rig rig = SubmitWithBackendReply(create, () => Task.FromException<OnlineCharacterManageOutcome>(error));

        AssertProtocolError(rig, create);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_empty_outcome_from_the_backend_keeps_the_modal_open_with_the_protocol_error_message(bool create)
    {
        Rig rig = SubmitWithBackendReply(create, () => Task.FromResult<OnlineCharacterManageOutcome>(null));

        AssertProtocolError(rig, create);
    }

    private static void AssertProtocolError(Rig rig, bool create)
    {
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Busy);
        (bool open, bool busy, string errorText) = ModalState(rig, create);
        Assert.True(open);
        Assert.False(busy);
        Assert.Equal("服务器响应格式异常", errorText);
        Assert.Equal(0, rig.Backend.DisconnectCalls);
        string[] sinceOpen = rig.Lines.SkipWhile(line => line != (create ? OpenCreateLine : OpenDeleteLine)).ToArray();
        Assert.Equal(new[] { create ? OpenCreateLine : OpenDeleteLine, create ? CreateProtocolLine : DeleteProtocolLine }, sinceOpen);
    }
}
