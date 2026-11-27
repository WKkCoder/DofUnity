using System.Text;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳"创建/删除角色"流程在闸门上的补充测试(r20fix-createcharui,簇 3):
/// 已接线但后端不允许写时,按钮启用、开模态、提交、后端这几层闸门各自独立,任何一层单独被拿掉都要有测试报警。
/// 同一个类的另外两个部分文件放选中位置与超时(<c>.Selection.cs</c>,簇 4)和模态开着按空格(<c>.Keys.cs</c>,簇 5b),共用这里的夹具。
/// 全部走内存里的假后端与合成数据,名字都是编的,不触发任何真实创建或删除。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    private const string CreateDisabledLine = "DNF_ONLINE stage=charselect event=fail reason=create_char_disabled";
    private const string DeleteDisabledLine = "DNF_ONLINE stage=charselect event=fail reason=delete_char_disabled";
    private const string CreateNoReplyLine = "DNF_ONLINE stage=charselect event=fail reason=create_char_noreply";
    private const string DeleteNoReplyLine = "DNF_ONLINE stage=charselect event=fail reason=delete_char_noreply";
    private const string OpenCreateLine = "DNF_ONLINE stage=charselect event=open reason=create_modal";
    private const string OpenDeleteLine = "DNF_ONLINE stage=charselect event=open reason=delete_modal";

    private static void ClickCenter(OnlineShellFlow flow, HitRect rect)
    {
        flow.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    private sealed class Rig
    {
        public readonly FakeOnlineBackend Backend;
        public readonly List<string> Lines;
        public readonly OnlineShellFlow Flow;
        public long Now = 1000;

        public Rig(FakeOnlineBackend backend, List<string> lines, OnlineShellOptions options)
        {
            Backend = backend;
            Lines = lines;
            Flow = new OnlineShellFlow(backend, options);
        }

        public void Poll(long advanceMs = 0)
        {
            Now += advanceMs;
            Flow.Poll(Now);
        }

        public void Pump(int times = 4)
        {
            for (int i = 0; i < times; i++) Poll();
        }

        public void ClickCreate()
        {
            ClickCenter(Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
            Pump(2);
        }

        public void ClickDelete()
        {
            ClickCenter(Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Delete));
            Pump(2);
        }

        /// <summary>把选中项移到第 <paramref name="index"/> 个角色(从当前位置按方向键)。</summary>
        public void MoveSelectionTo(int index)
        {
            for (int i = 0; i < 12; i++)
            {
                Flow.Key(OnlineShellKey.Left);
            }
            for (int i = 0; i < index; i++)
            {
                Flow.Key(OnlineShellKey.Right);
            }
        }

        /// <summary>回车进游戏,返回后端最近一次收到的槽位。</summary>
        public int EnterGameAndReadSlot()
        {
            Flow.Key(OnlineShellKey.Enter);
            Pump(3);
            Assert.Equal(OnlineShellStage.InGame, Flow.Stage);
            return Backend.LastSlot;
        }
    }

    private static Rig CreateRig(bool allowWrite, bool wireManage, params (string Name, byte Job, byte Level)[] seed)
    {
        var backend = new FakeOnlineBackend { AllowCharacterWrite = allowWrite };
        backend.SeedManagedRoster(seed);
        var lines = new List<string>();
        var options = new OnlineShellOptions
        {
            Log = lines.Add,
            CharacterSelect = new OnlineCharacterSelectOptions(),
            CharacterManage = wireManage ? backend : null,
            Fonts = new SizedFonts(),
            Art = new CharacterSelectScene(new ShellFakeSprites()),
        };
        var rig = new Rig(backend, lines, options);
        rig.Flow.Type("player_01");
        rig.Flow.Key(OnlineShellKey.Tab);
        rig.Flow.Type("synthetic-pw");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        return rig;
    }

    private static OnlineCharacterEntry Entry(ushort uid, string name, byte job, byte level = 1)
    {
        return new OnlineCharacterEntry(
            uid, Encoding.UTF8.GetBytes(name), 0, job, 0, level, 0, 0,
            Array.Empty<OnlineCharacterEquip>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    // ------------------------------------------------------------------ 簇 3:已接线但后端不允许写——几层闸门各自独立

    [Fact]
    public void A_wired_backend_that_refuses_writes_keeps_both_buttons_locked_and_opens_no_modal()
    {
        Rig rig = CreateRig(allowWrite: false, wireManage: true, ("HeroA", 0, 10));

        Assert.False(rig.Flow.CanManageCharacters);
        rig.ClickCreate();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        rig.ClickDelete();
        Assert.False(rig.Flow.HasCharacterModalOpen);

        Assert.Null(rig.Flow.CreateModal);
        Assert.Null(rig.Flow.DeleteModal);
        Assert.Equal(0, rig.Backend.CreateCalls);
        Assert.Equal(0, rig.Backend.DeleteCalls);
        Assert.DoesNotContain(OpenCreateLine, rig.Lines);
        Assert.DoesNotContain(OpenDeleteLine, rig.Lines);
    }

    [Fact]
    public void The_same_wiring_with_writes_allowed_can_manage_characters_and_opens_both_modals()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));

        Assert.True(rig.Flow.CanManageCharacters);
        rig.ClickCreate();
        Assert.NotNull(rig.Flow.CreateModal);
        Assert.Single(rig.Lines, line => line == OpenCreateLine);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);

        rig.Flow.Key(OnlineShellKey.Right);
        rig.ClickDelete();
        Assert.NotNull(rig.Flow.DeleteModal);
        Assert.Equal((byte)1, rig.Flow.DeleteModal.TargetSlot);
        Assert.Single(rig.Lines, line => line == OpenDeleteLine);
    }

    [Fact]
    public void An_unwired_flow_cannot_manage_characters_even_when_the_backend_would_allow_writes()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: false, ("HeroA", 0, 10));

        Assert.False(rig.Flow.CanManageCharacters);
        rig.ClickCreate();
        rig.ClickDelete();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(0, rig.Backend.CreateCalls);
        Assert.Equal(0, rig.Backend.DeleteCalls);
    }

    /// <summary>
    /// 按钮是否启用只在进入选角时按 CanManageCharacters 设一次:运行期把后端的写开关关掉后按钮仍是启用的,
    /// 点击会走到模态入口那道闸——它自己必须再看一遍,不能全靠按钮启用位。
    /// </summary>
    [Fact]
    public void Switching_writes_off_at_runtime_stops_both_modal_entry_points_although_the_buttons_stay_enabled()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        Assert.True(rig.Flow.CanManageCharacters);

        rig.Backend.AllowCharacterWrite = false;
        Assert.False(rig.Flow.CanManageCharacters);

        rig.ClickCreate();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Null(rig.Flow.CreateModal);
        rig.ClickDelete();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Null(rig.Flow.DeleteModal);

        Assert.Equal(0, rig.Backend.CreateCalls);
        Assert.Equal(0, rig.Backend.DeleteCalls);
        Assert.DoesNotContain(OpenCreateLine, rig.Lines);
        Assert.DoesNotContain(OpenDeleteLine, rig.Lines);
    }

    /// <summary>
    /// 模态已开、名字合法,提交之前写开关被关掉:流程自己的闸门要把请求挡在后端之外。
    /// 假后端在检查允许位之前先给调用计数,所以"流程这一道闸"没了只会表现为调用数变成 1(日志与错误文字两种情形相同)。
    /// </summary>
    [Fact]
    public void A_create_submitted_after_writes_were_switched_off_is_refused_before_it_reaches_the_backend()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickCreate();
        rig.Flow.Type("HeroNew");

        rig.Backend.AllowCharacterWrite = false;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.True(rig.Flow.HasCharacterModalOpen);
        Assert.Equal("角色写操作未开启", rig.Flow.CreateModal.ErrorText);
        Assert.False(rig.Flow.CreateModal.Busy);
        Assert.False(rig.Flow.Busy);
        Assert.Single(rig.Lines, line => line == CreateDisabledLine);
        Assert.Equal(0, rig.Backend.CreateCalls);
        Assert.Single(rig.Flow.Characters.Entries);
    }

    [Fact]
    public void A_delete_submitted_after_writes_were_switched_off_is_refused_before_it_reaches_the_backend()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickDelete();
        Assert.NotNull(rig.Flow.DeleteModal);
        rig.Flow.Type("HeroA");

        rig.Backend.AllowCharacterWrite = false;
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.True(rig.Flow.HasCharacterModalOpen);
        Assert.Equal("角色写操作未开启", rig.Flow.DeleteModal.ErrorText);
        Assert.False(rig.Flow.DeleteModal.Busy);
        Assert.False(rig.Flow.Busy);
        Assert.Single(rig.Lines, line => line == DeleteDisabledLine);
        Assert.Equal(0, rig.Backend.DeleteCalls);
        Assert.Single(rig.Flow.Characters.Entries);
    }
}
