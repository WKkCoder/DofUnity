using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网多房间副本的宿主房间托管(r18-hostmulti,<see cref="HostedRoomRun"/> 的多房间部分):出口什么时候才算开、触发时回调什么、等待与被拒的节奏、
/// 换房(<see cref="HostedRoomRun.EnterRoom"/>)之后会话/计数/观察者/主角怎么变、放弃(<see cref="HostedRoomRun.Abort"/>)的语义。
/// 房间是带出口的合成房间(<see cref="HostedGatedRoomFixture"/>,走生产路径 <see cref="RoomCombatSession.Create"/>),不连任何联网层;
/// 单房间的老行为由 <see cref="HostedRoomRunTests"/> 与 <see cref="HostedRoomBotTests"/> 原样把关(这里不动它们)。
/// </summary>
public class HostedRoomMultiRunTests
{
    private static RoomMonsterSpawn[] Templates(int count)
    {
        return Enumerable.Range(0, count).Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 340 + 40 * i)).ToArray();
    }

    /// <summary>一局多房间托管:夹具 + 第一间房 + 记录用的两个回调(击杀交给谁、换房请求带了什么)。(自动机与走位的测试也用它。)</summary>
    internal sealed class Rig : IDisposable
    {
        public HostedGatedRoomFixture Fx { get; } = new HostedGatedRoomFixture();
        public HostedRoomRun Run { get; }
        public List<(int Room, int Id)> Kills { get; } = new List<(int Room, int Id)>();
        public List<(GateSide Side, int X, int Y)> Requests { get; } = new List<(GateSide Side, int X, int Y)>();
        public bool Accept { get; set; } = true;
        public bool Done { get; set; }

        public Rig(int monsters, GateSide? exit = GateSide.Right, GateSide[] gates = null, double heroX = 300, double heroY = 100, bool vitals = false, bool wire = true)
        {
            RoomCombatSession room = NewRoom(monsters, gates ?? new[] { GateSide.Left, GateSide.Right }, heroX, heroY, vitals);
            Run = new HostedRoomRun(room, id =>
            {
                Kills.Add((Run.RoomIndex, id));
                return true;
            }, () => Done, 0.0);
            if (wire)
            {
                Run.ExitRequest = (side, x, y) =>
                {
                    Requests.Add((side, x, y));
                    return Accept;
                };
            }
            Run.SetExit(exit);
        }

        public RoomCombatSession NewRoom(int monsters, IEnumerable<GateSide> gates, double heroX = 300, double heroY = 100, bool vitals = false)
        {
            return Fx.Create(Templates(monsters), map => RoomSpawnPlan.Fill(monsters, map.Monsters), gates, vitals, heroX, heroY);
        }

        public void Step(int steps, int dirX = 0, int dirY = 0)
        {
            for (int i = 0; i < steps; i++)
            {
                Run.Update(10, dirX, dirY, false, false);
            }
        }

        /// <summary>只把当前房间里活着的怪都打死(不推进时间:观察者、清房判定都还没看见)。</summary>
        public void Slay()
        {
            foreach (MonsterActor monster in Run.Room.Monsters)
            {
                if (monster.IsAlive)
                {
                    HostedRoomFixture.Kill(monster);
                }
            }
        }

        /// <summary>打死当前房间里所有的怪,并且不按键地推进到尸体都消失(清房成立);出口若已触发,请求就是在这一路上排的。</summary>
        public void KillAll()
        {
            Slay();
            for (int i = 0; i < 2000 && !Run.RoomCleared; i++)
            {
                Step(1);
            }
            Assert.True(Run.RoomCleared, "怪的尸体应当在 20 秒之内消失");
        }

        public void Dispose()
        {
            Fx.Dispose();
        }
    }

    // ------------------------------------------------------------------ 出口:什么时候触发、触发时带什么

    [Fact]
    public void The_exit_stays_shut_until_every_monster_is_gone_and_then_asks_once_with_the_side_and_the_hero_position()
    {
        using var rig = new Rig(2, heroX: 1090, heroY: 100);
        Assert.True(rig.Run.HasExitGate);

        rig.Step(30, 1, 0);                                              // 主角顶着门,怪还活着:不触发
        Assert.Empty(rig.Requests);
        Assert.False(rig.Run.RoomCleared);
        Assert.False(rig.Run.MovePending);

        rig.Slay();
        rig.Step(1);                                                     // 刚打死:尸体还在,清房还没成立,不触发
        Assert.Empty(rig.Requests);
        Assert.False(rig.Run.RoomCleared);

        rig.KillAll();                                                   // 尸体消失、清房成立的那一步,出口就触发
        (GateSide Side, int X, int Y) request = Assert.Single(rig.Requests);
        Assert.Equal(GateSide.Right, request.Side);
        Assert.Equal(((int)Math.Round(rig.Run.Hero.X), (int)Math.Round(rig.Run.Hero.Y)), (request.X, request.Y));   // 主角此刻的位置(取整)
        Assert.True(rig.Run.MovePending);
        Assert.Equal(1, rig.Run.MoveRequests);
    }

    [Fact]
    public void While_a_move_is_pending_the_exit_never_asks_again_however_long_the_hero_keeps_pushing()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.KillAll();
        rig.Step(500, 1, 0);                                             // 5 秒

        Assert.Single(rig.Requests);
        Assert.Equal(1, rig.Run.MoveRequests);
        Assert.True(rig.Run.MovePending);
        Assert.Equal(0, rig.Run.MoveRefusals);
    }

    [Fact]
    public void Only_the_planned_exit_side_asks()
    {
        // 主角站在左出口里,计划的出口是右:不触发
        using (var rig = new Rig(1, GateSide.Right, heroX: 30))
        {
            rig.KillAll();
            rig.Step(100, -1, 0);
            Assert.Empty(rig.Requests);
        }
        // 计划的出口换成左:同样的位置就触发,带的是左
        using (var rig = new Rig(1, GateSide.Left, heroX: 30))
        {
            rig.KillAll();
            rig.Step(1);
            Assert.Equal(GateSide.Left, Assert.Single(rig.Requests).Side);
        }
    }

    [Fact]
    public void A_hero_far_from_the_exit_does_not_ask_even_when_the_room_is_clear()
    {
        using var rig = new Rig(1, heroX: 300);
        rig.KillAll();
        rig.Step(100);

        Assert.Empty(rig.Requests);
        Assert.True(rig.Run.RoomCleared);
    }

    [Fact]
    public void A_refused_move_ends_the_wait_and_asks_again_only_after_the_cooldown()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.KillAll();
        rig.Step(1);
        Assert.True(rig.Run.MovePending);

        rig.Run.MoveRefused();
        Assert.False(rig.Run.MovePending);
        Assert.Equal(1, rig.Run.MoveRefusals);

        int cooldownSteps = (int)(HostedRoomRun.RetryCooldownMs / 10);
        rig.Step(cooldownSteps - 1, 1, 0);                               // 冷却还差一步:不再问
        Assert.Single(rig.Requests);
        rig.Step(2, 1, 0);                                               // 冷却走完:再问一次
        Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(2, rig.Run.MoveRequests);
        Assert.True(rig.Run.MovePending);
    }

    [Fact]
    public void MoveRefused_without_a_pending_move_changes_nothing()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.Run.MoveRefused();

        Assert.Equal(0, rig.Run.MoveRefusals);
        rig.KillAll();
        Assert.Single(rig.Requests);                                     // 没有被它吃掉冷却:清房那一步照常触发
    }

    [Fact]
    public void A_request_the_host_could_not_post_does_not_start_a_wait_and_is_retried_after_the_cooldown()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.Accept = false;
        rig.KillAll();
        rig.Step(1);

        Assert.Single(rig.Requests);
        Assert.False(rig.Run.MovePending);
        Assert.Equal(0, rig.Run.MoveRequests);                           // 没排上的不算"排过"
        rig.Step(50);
        Assert.Single(rig.Requests);                                     // 冷却中
        rig.Accept = true;
        rig.Step((int)(HostedRoomRun.RetryCooldownMs / 10) + 1);
        Assert.Equal(2, rig.Requests.Count);
        Assert.True(rig.Run.MovePending);
        Assert.Equal(1, rig.Run.MoveRequests);
    }

    [Fact]
    public void Without_an_exit_side_nothing_is_ever_asked_even_with_a_request_callback()
    {
        using var rig = new Rig(1, exit: null, heroX: 1090);
        rig.KillAll();
        rig.Step(100, 1, 0);

        Assert.Empty(rig.Requests);
        Assert.Null(rig.Run.ExitSide);
        Assert.False(rig.Run.HasExitGate);
        Assert.False(rig.Run.MovePending);
        Assert.Equal(1, rig.Run.RoomsEntered);
    }

    [Fact]
    public void Without_a_request_callback_the_exit_never_triggers_and_the_run_stays_a_plain_single_room_run()
    {
        using var rig = new Rig(1, GateSide.Right, heroX: 1090, wire: false);
        rig.KillAll();
        rig.Step(100, 1, 0);

        Assert.False(rig.Run.MovePending);
        Assert.Equal(0, rig.Run.MoveRequests);
        Assert.False(rig.Run.MultiRoom);                                 // 日志不多写换房字段
        Assert.Equal(GateSide.Right, rig.Run.ExitSide);
    }

    [Fact]
    public void A_run_is_multi_room_once_it_has_a_pump_a_request_callback_or_has_changed_rooms()
    {
        using var rig = new Rig(1, exit: null, wire: false);
        Assert.False(rig.Run.MultiRoom);
        rig.Run.Pump = r => { };
        Assert.True(rig.Run.MultiRoom);
        rig.Run.Pump = null;
        Assert.False(rig.Run.MultiRoom);
        rig.Run.ExitRequest = (s, x, y) => true;
        Assert.True(rig.Run.MultiRoom);
        rig.Run.ExitRequest = null;

        rig.Run.EnterRoom(rig.NewRoom(1, new[] { GateSide.Left }), 1, 905, GateSide.Left, null);
        Assert.True(rig.Run.MultiRoom);                                  // 换过房
    }

    [Fact]
    public void HasExitGate_follows_the_room_the_run_is_in()
    {
        using var rig = new Rig(1, GateSide.Right, new[] { GateSide.Left });          // 房间里只有左出口,计划的是右
        Assert.False(rig.Run.HasExitGate);
        rig.Run.SetExit(GateSide.Left);
        Assert.True(rig.Run.HasExitGate);
        rig.Run.SetExit(null);
        Assert.False(rig.Run.HasExitGate);
    }

    // ------------------------------------------------------------------ 出口的画面:清房才开,只开计划的那一扇

    [Fact]
    public void The_planned_exit_opens_when_the_room_is_cleared_and_no_other_gate_does()
    {
        using var rig = new Rig(1, GateSide.Right);
        RoomGate right = rig.Run.Room.Room.Content.Gates.Single(g => g.Side == GateSide.Right);
        RoomGate left = rig.Run.Room.Room.Content.Gates.Single(g => g.Side == GateSide.Left);

        rig.Run.Build(1000);
        Assert.Equal(GateState.Closed, right.State);                      // 怪还活着:关着

        rig.KillAll();
        rig.Run.Build(2000);
        Assert.Equal(GateState.Open, right.State);                        // 清房之后第一次出画面:计划的出口开
        Assert.Equal(GateState.Closed, left.State);                       // 别的出口原样
    }

    [Fact]
    public void A_room_that_is_empty_when_entered_shows_its_exit_open_from_the_start()
    {
        using var rig = new Rig(1, GateSide.Right);
        RoomCombatSession empty = rig.NewRoom(0, new[] { GateSide.Left, GateSide.Right });
        rig.Run.EnterRoom(empty, 1, 905, GateSide.Left, GateSide.Right);

        Assert.True(rig.Run.RoomCleared);
        rig.Run.Build(5000);
        RoomGate right = rig.Run.Room.Room.Content.Gates.Single(g => g.Side == GateSide.Right);
        Assert.Equal(GateState.Open, right.State);
        Assert.Equal(GateState.Open, right.State);
        Assert.Equal(RoomGate.SettledOpenedAtMs, right.OpenedAtMs);       // 本来就没有怪:出口是"开了很久"的样子(同离线)
    }

    // ------------------------------------------------------------------ Pump:在击杀交出去之后、出口检查之前

    [Fact]
    public void Pump_runs_after_the_kills_are_handed_out_and_before_the_exit_check()
    {
        using var rig = new Rig(2, heroX: 1090);
        var seen = new List<(int Reported, int Requests)>();
        rig.Run.Pump = r => seen.Add((r.Reported, rig.Requests.Count));
        rig.Slay();
        for (int i = 0; i < 2000 && rig.Requests.Count == 0; i++)
        {
            rig.Step(1);
        }
        rig.Step(1);

        Assert.Single(rig.Requests);
        Assert.Equal((2, 0), seen[0]);                                    // 第一步:两只怪的击杀已经交出去,Pump 看见的就是交完之后的数
        Assert.Equal(0, seen[seen.Count - 2].Requests);                   // 排请求的那一步:Pump 先于出口检查,还没看见请求
        Assert.Equal(1, seen[seen.Count - 1].Requests);                   // 下一步才看见
    }

    [Fact]
    public void Pump_is_called_once_per_step_and_never_after_an_abort()
    {
        using var rig = new Rig(1);
        int calls = 0;
        rig.Run.Pump = r => calls++;
        rig.Step(5);
        Assert.Equal(5, calls);

        rig.Run.Abort(HostedAbort.NetEnded);
        rig.Step(5);
        Assert.Equal(5, calls);
    }

    [Fact]
    public void Pump_may_enter_the_next_room_and_the_run_carries_on_there_without_asking_again()
    {
        using var rig = new Rig(1, heroX: 1090);
        RoomCombatSession next = rig.NewRoom(1, new[] { GateSide.Left });
        rig.Run.Pump = r =>
        {
            if (r.MovePending)
            {
                r.EnterRoom(next, 1, 905, GateSide.Left, null);
            }
        };
        rig.KillAll();
        Assert.True(rig.Run.MovePending);
        rig.Step(1);

        Assert.Equal(1, rig.Run.RoomIndex);
        Assert.Same(next, rig.Run.Room);
        Assert.False(rig.Run.MovePending);
        Assert.Single(rig.Requests);
        rig.Step(50);
        Assert.Single(rig.Requests);                                      // 新房间最后一间(没有出口):不再问
    }

    // ------------------------------------------------------------------ EnterRoom:换会话、清房间计数、留整局计数、绑新观察者

    [Fact]
    public void Entering_a_room_swaps_the_session_resets_the_room_counters_and_keeps_the_dungeon_totals()
    {
        using var rig = new Rig(2, heroX: 1090);
        rig.KillAll();
        rig.Step(1);
        Assert.Equal((2, 0, 2, 0), (rig.Run.Reported, rig.Run.Dropped, rig.Run.RoomReported, rig.Run.RoomDropped));
        RoomCombatSession first = rig.Run.Room;
        RoomCombatSession next = rig.NewRoom(3, new[] { GateSide.Left, GateSide.Right });

        rig.Run.EnterRoom(next, 1, 905, GateSide.Left, GateSide.Right);

        Assert.Same(next, rig.Run.Room);
        Assert.NotSame(first, rig.Run.Room);
        Assert.Equal(1, rig.Run.RoomIndex);
        Assert.Equal(2, rig.Run.RoomsEntered);
        Assert.Equal((2, 0), (rig.Run.Reported, rig.Run.Dropped));                    // 整局累计不清零
        Assert.Equal((0, 0), (rig.Run.RoomReported, rig.Run.RoomDropped));            // 房间计数清零
        Assert.False(rig.Run.MovePending);
        Assert.Equal(GateSide.Right, rig.Run.ExitSide);
        Assert.False(rig.Run.RoomCleared);                                            // 新房间有三只怪
    }

    [Fact]
    public void Kills_in_the_new_room_are_handed_out_once_each_under_the_new_room_index_and_counted_in_both_tallies()
    {
        using var rig = new Rig(2, heroX: 1090);
        rig.KillAll();
        rig.Step(1);
        rig.Run.EnterRoom(rig.NewRoom(3, new[] { GateSide.Left }), 1, 905, GateSide.Left, null);

        HostedRoomFixture.Kill(rig.Run.Room.Monsters[2]);                              // 先打死序号 2
        rig.Step(3);
        HostedRoomFixture.Kill(rig.Run.Room.Monsters[0]);
        rig.Step(3);
        rig.Step(3);

        Assert.Equal(new[] { (0, 0), (0, 1), (1, 2), (1, 0) }, rig.Kills.ToArray());   // 第一间两只在 0 号房、第二间两只在 1 号房,每只一次
        Assert.Equal(4, rig.Run.Reported);
        Assert.Equal(2, rig.Run.RoomReported);
    }

    [Fact]
    public void The_last_kills_of_the_old_room_are_handed_out_before_the_session_is_swapped()
    {
        using var rig = new Rig(2, heroX: 300);
        rig.Slay();                                                                   // 还没推进一步:观察者还没看见它们
        Assert.Empty(rig.Kills);

        rig.Run.EnterRoom(rig.NewRoom(1, new[] { GateSide.Left }), 1, 905, GateSide.Left, null);

        Assert.Equal(new[] { (0, 0), (0, 1) }, rig.Kills.ToArray());                  // 换房前交完,而且是老房间的序号
        Assert.Equal(2, rig.Run.Reported);
    }

    [Fact]
    public void Entering_a_room_places_the_hero_in_the_entry_gate_rectangle_facing_inward()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.KillAll();
        rig.Step(1);

        rig.Run.EnterRoom(rig.NewRoom(1, new[] { GateSide.Left, GateSide.Right }), 1, 905, GateSide.Left, GateSide.Right);

        WorldRect area = HostedGatedRoomFixture.GateRect(GateSide.Left);
        Assert.True(GateAreas.Contains(area, (int)Math.Floor(rig.Run.Hero.X), (int)Math.Floor(rig.Run.Hero.Y)), "落点在进来那扇门的识别矩形里");
        Assert.False(rig.Run.Hero.FacingLeft);                                         // 从左出口进来:朝右

        using var rig2 = new Rig(1, heroX: 30);
        rig2.KillAll();
        rig2.Run.EnterRoom(rig2.NewRoom(1, new[] { GateSide.Left, GateSide.Right }), 1, 905, GateSide.Right, null);
        Assert.True(GateAreas.Contains(HostedGatedRoomFixture.GateRect(GateSide.Right), (int)Math.Floor(rig2.Run.Hero.X), (int)Math.Floor(rig2.Run.Hero.Y)));
        Assert.True(rig2.Run.Hero.FacingLeft);                                         // 从右出口进来:朝左
    }

    [Fact]
    public void Entering_without_an_entry_side_leaves_the_hero_where_the_new_room_put_him()
    {
        using var rig = new Rig(1);
        RoomCombatSession next = rig.NewRoom(1, new[] { GateSide.Left }, heroX: 222, heroY: 111);
        rig.Run.EnterRoom(next, 1, 905, null, null);

        Assert.Equal((222.0, 111.0), (rig.Run.Hero.X, rig.Run.Hero.Y));
    }

    [Fact]
    public void Entering_a_room_carries_the_hero_state_the_commands_switch_and_the_revive_window()
    {
        using var rig = new Rig(1, vitals: true);
        RoomCombatSession old = rig.Run.Room;
        Assert.NotNull(old.Vitals);
        old.Vitals.Damage(7);
        int hpBefore = old.Vitals.Hp;
        var revive = new HeroRevive(null, new ReviveCoins(1));
        old.Revive = revive;
        rig.Run.CommandsEnabled = true;                                                // 新房间自己缺省是关的:开着的要带过去才分得出来
        RoomCombatSession next = rig.NewRoom(1, new[] { GateSide.Left }, vitals: true);
        int freshHp = next.Vitals.Hp;
        Assert.NotEqual(freshHp, hpBefore);                                            // 老房间被打过,新房间是满的:带过去才分得出来

        rig.Run.EnterRoom(next, 1, 905, GateSide.Left, null);

        Assert.Equal(hpBefore, next.Vitals.Hp);                                        // 生命值跟着人走
        Assert.Same(revive, next.Revive);                                              // 复活窗口全局只有一份
        Assert.True(rig.Run.CommandsEnabled);                                          // "接受指令"的开关带到新房间
    }

    [Fact]
    public void Entering_a_room_carries_the_buffs_the_skill_cooldowns_and_the_held_skill_keys()
    {
        using var rig = new Rig(1);
        RoomCombatSession old = rig.Run.Room;
        old.Buffs.Apply(AvatarFixture.JumpBuffSkill, BuffStat.JumpPower, 30, 30000);
        HeroSkillSlot upper = old.Skills.SlotOf(SkillKeys.S);
        Assert.NotNull(upper);                                                         // 夹具里的角色有快捷槽
        upper.CooldownLeftMs = 900.0;
        rig.Run.SkillsHeld = SkillKeys.S;
        RoomCombatSession next = rig.NewRoom(1, new[] { GateSide.Left });
        Assert.False(next.Buffs.Has(AvatarFixture.JumpBuffSkill));                    // 新房间是干净的:带过去才分得出来
        Assert.Equal(0.0, next.Skills.SlotOf(SkillKeys.S).CooldownLeftMs);
        Assert.Equal(SkillKeys.None, next.SkillsHeld);

        rig.Run.EnterRoom(next, 1, 905, GateSide.Left, null);

        Assert.True(next.Buffs.Has(AvatarFixture.JumpBuffSkill));                     // 增益跟着人走
        Assert.Equal(900.0, next.Skills.SlotOf(SkillKeys.S).CooldownLeftMs);          // 冷却不因换房清零
        Assert.Equal(SkillKeys.S, rig.Run.SkillsHeld);                                // 按着的技能键也带到新房间
    }

    [Fact]
    public void Entering_a_room_keeps_a_revive_window_the_new_room_already_has()
    {
        using var rig = new Rig(1);
        var oldRevive = new HeroRevive(null, new ReviveCoins(1));
        rig.Run.Room.Revive = oldRevive;
        RoomCombatSession next = rig.NewRoom(1, new[] { GateSide.Left });
        var ownRevive = new HeroRevive(null, new ReviveCoins(2));
        next.Revive = ownRevive;

        rig.Run.EnterRoom(next, 1, 905, null, null);

        Assert.Same(ownRevive, next.Revive);                                           // 新房间自己有的不覆盖
    }

    [Fact]
    public void Sounds_the_old_room_had_not_handed_over_yet_come_out_first_after_the_swap()
    {
        using var rig = new Rig(2, heroX: 300);
        for (int i = 0; i < 3; i++)
        {
            rig.Run.Update(10, 0, 0, i == 0, false);                                   // 按一下攻击:挥剑语音进老房间的声音队列(没人取)
        }
        RoomCombatSession old = rig.Run.Room;

        rig.Run.EnterRoom(rig.NewRoom(1, new[] { GateSide.Left }), 1, 905, GateSide.Left, null);

        var fromOld = new List<SoundCue>();
        old.DrainSounds(fromOld);
        Assert.Empty(fromOld);                                                         // 换房的时候已经把老房间的声音收走了
        var cues = new List<SoundCue>();
        rig.Run.DrainSounds(cues);
        Assert.Contains(cues, c => c.Kind == SoundCueKind.Voice && c.Key == SwordmanVoice.Attack);   // 下一次取声音先给它们
        var again = new List<SoundCue>();
        rig.Run.DrainSounds(again);
        Assert.DoesNotContain(again, c => c.Kind == SoundCueKind.Voice && c.Key == SwordmanVoice.Attack);   // 只给一次
    }

    [Fact]
    public void EnterRoom_rejects_a_null_room_and_the_room_the_run_is_already_in()
    {
        using var rig = new Rig(1);

        Assert.Throws<ArgumentNullException>(() => rig.Run.EnterRoom(null, 1, 905, null, null));
        Assert.Throws<ArgumentException>(() => rig.Run.EnterRoom(rig.Run.Room, 1, 905, null, null));
        Assert.Equal(0, rig.Run.RoomIndex);
        Assert.Equal(1, rig.Run.RoomsEntered);
    }

    [Fact]
    public void Entering_the_last_room_without_an_exit_leaves_nothing_to_trigger()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.KillAll();
        rig.Step(1);
        rig.Run.EnterRoom(rig.NewRoom(1, new[] { GateSide.Right }, heroX: 1090), 1, 905, null, null);
        rig.KillAll();
        rig.Step(100, 1, 0);

        Assert.Single(rig.Requests);
        Assert.Null(rig.Run.ExitSide);
        Assert.False(rig.Run.HasExitGate);
    }

    // ------------------------------------------------------------------ 放弃

    [Fact]
    public void Abort_fails_the_run_keeps_the_first_reason_and_stops_the_exit()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.KillAll();
        rig.Step(1);
        Assert.True(rig.Run.MovePending);
        Assert.False(rig.Run.Failed);
        Assert.False(rig.Run.Aborted);
        Assert.Equal(HostedAbort.None, rig.Run.AbortReason);

        rig.Run.Abort(HostedAbort.MoveTimeout);
        rig.Run.Abort(HostedAbort.NetEnded);                                           // 第二次不改原因

        Assert.True(rig.Run.Aborted);
        Assert.True(rig.Run.Failed);
        Assert.Equal(HostedAbort.MoveTimeout, rig.Run.AbortReason);
        Assert.False(rig.Run.MovePending);
        rig.Run.MoveRefused();
        Assert.Equal(0, rig.Run.MoveRefusals);                                         // 没有在等:不记
        rig.Step(500, 1, 0);
        Assert.Single(rig.Requests);                                                   // 放弃之后出口不再触发
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
    }

    [Fact]
    public void Abort_needs_a_reason()
    {
        using var rig = new Rig(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Run.Abort(HostedAbort.None));
        Assert.False(rig.Run.Aborted);
    }

    [Fact]
    public void Failed_is_still_the_revive_window_failure_for_a_run_that_was_never_aborted()
    {
        using var rig = new Rig(1);
        Assert.False(rig.Run.Failed);                                                  // 没有复活窗口又没人放弃:永远为假

        rig.Run.Room.Revive = new HeroRevive(null, new ReviveCoins(0));
        Assert.False(rig.Run.Failed);                                                  // 复活窗口没走完失败计时
        Assert.False(rig.Run.Aborted);
    }

    [Fact]
    public void A_finished_run_still_returns_to_town_after_the_delay_in_a_later_room()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.KillAll();
        rig.Step(1);
        rig.Run.EnterRoom(rig.NewRoom(1, new[] { GateSide.Left }), 1, 905, GateSide.Left, null);
        rig.KillAll();
        rig.Step(3);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);

        rig.Done = true;
        rig.Step(1);

        Assert.True(rig.Run.Finished);
        Assert.Equal(SettlementChoice.ReturnToTown, rig.Run.Choice);                   // 回城延迟 0:同一步就选定
        Assert.False(rig.Run.Failed);
    }

    [Fact]
    public void The_exit_stops_asking_once_the_run_is_finished()
    {
        using var rig = new Rig(1, heroX: 1090);
        rig.Done = true;
        rig.KillAll();
        rig.Step(10, 1, 0);

        Assert.Empty(rig.Requests);                                                    // 结算收齐了:没有换房的道理
        Assert.True(rig.Run.Finished);
    }
}
