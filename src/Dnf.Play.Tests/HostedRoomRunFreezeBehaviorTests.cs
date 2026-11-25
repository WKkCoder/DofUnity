using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// r20-settlefreeze Play 层行为验证:
/// 当宿主将 <see cref="HostedRoomRun"/> 的 <c>CommandsEnabled</c> 置为 <c>false</c>、<c>SkillsHeld</c> 置为 <see cref="SkillKeys.None"/>
/// 并持续传入零输入时,站立中的主角位置与动作态保持不变,已经发动的技能与冷却照常自然走完,且结束后不会再次出招。
/// </summary>
public sealed class HostedRoomRunFreezeBehaviorTests
{
    private sealed class DummyOutside
    {
        public bool Post(int id) => true;
        public bool IsFinished() => false;
    }

    [Fact]
    public void Zero_inputs_with_commands_disabled_hold_idle_hero_position_and_state_while_clock_advances()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (700, 100) }, hp: 100, chr: AvatarFixture.ActionChrPath, withSkills: true);
        var outside = new DummyOutside();
        var run = new HostedRoomRun(arena.Session, outside.Post, outside.IsFinished);

        double startX = run.Hero.X;
        double startY = run.Hero.Y;
        double startZ = run.Hero.Z;
        bool startFacingLeft = run.Hero.FacingLeft;

        run.CommandsEnabled = false;
        run.SkillsHeld = SkillKeys.None;

        for (int i = 0; i < 60; i++)
        {
            run.Update(16, 0, 0, false, false, SkillKeys.None);
            Assert.Equal(startX, run.Hero.X);
            Assert.Equal(startY, run.Hero.Y);
            Assert.Equal(startZ, run.Hero.Z);
            Assert.Equal(startFacingLeft, run.Hero.FacingLeft);
            Assert.Equal(HeroMotion.Rest, run.Hero.Motion);
            Assert.Equal(0, run.Hero.SkillId);
        }

        Assert.Equal(960.0, run.ClockMs, 9);
        Assert.Equal(0, run.Room.SkillCasts);
        Assert.Equal(0, run.Room.Hits);
        Assert.Equal(SettlementChoice.None, run.Choice);
    }

    [Fact]
    public void Walking_hero_stops_and_holds_position_when_frozen_with_zero_inputs()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (700, 100) }, hp: 100, chr: AvatarFixture.ActionChrPath, withSkills: true);
        var outside = new DummyOutside();
        var run = new HostedRoomRun(arena.Session, outside.Post, outside.IsFinished);

        run.CommandsEnabled = true;
        run.SkillsHeld = SkillKeys.None;
        double initialX = run.Hero.X;
        for (int i = 0; i < 10; i++)
        {
            run.Update(10, 1, 0, false, false, SkillKeys.None);
        }
        Assert.True(run.Hero.X > initialX);
        Assert.Equal(HeroMotion.Move, run.Hero.Motion);

        // 模拟进入结算冻结:清掉方向/按键/SkillsHeld/CommandsEnabled
        run.CommandsEnabled = false;
        run.SkillsHeld = SkillKeys.None;
        run.Update(10, 0, 0, false, false, SkillKeys.None);
        double frozenX = run.Hero.X;
        double frozenY = run.Hero.Y;
        Assert.Equal(HeroMotion.Rest, run.Hero.Motion);

        for (int i = 0; i < 30; i++)
        {
            run.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal(frozenX, run.Hero.X);
            Assert.Equal(frozenY, run.Hero.Y);
            Assert.Equal(0.0, run.Hero.Z);
            Assert.Equal(HeroMotion.Rest, run.Hero.Motion);
        }
    }

    [Fact]
    public void Already_started_skill_finishes_naturally_under_zero_inputs_and_stays_rest_afterward()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, withSkills: true);
        var outside = new DummyOutside();
        var run = new HostedRoomRun(arena.Session, outside.Post, outside.IsFinished);

        double startX = run.Hero.X;
        double startY = run.Hero.Y;
        HeroSkillSlot slot = run.Room.Skills.SlotOf(SkillKeys.A);

        // 先在未冻结状态下发动技能 A(原地 600 毫秒招式)
        run.CommandsEnabled = true;
        run.SkillsHeld = SkillKeys.A;
        run.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal(HeroMotion.Skill, run.Hero.Motion);
        Assert.Equal(AvatarFixture.HardAttackSkill, run.Hero.SkillId);
        Assert.Equal(1, run.Room.SkillCasts);
        Assert.False(slot.Ready);

        // 技能进行中切换为冻结(零输入 + SkillsHeld=None + CommandsEnabled=false)
        run.CommandsEnabled = false;
        run.SkillsHeld = SkillKeys.None;

        // 前 100 毫秒仍在技能动作中,房间模拟照常推进
        for (int i = 0; i < 10; i++)
        {
            run.Update(10, 0, 0, false, false, SkillKeys.None);
        }
        Assert.Equal(HeroMotion.Skill, run.Hero.Motion);
        Assert.Equal(AvatarFixture.HardAttackSkill, run.Hero.SkillId);

        // 继续推进到 600 毫秒之后,已发动技能自然收招回到 Rest,且特效自动播完
        for (int i = 0; i < 55; i++)
        {
            run.Update(10, 0, 0, false, false, SkillKeys.None);
        }
        Assert.Equal(HeroMotion.Rest, run.Hero.Motion);
        Assert.Equal(0, run.Hero.SkillId);
        Assert.Equal(startX, run.Hero.X);
        Assert.Equal(startY, run.Hero.Y);
        Assert.Equal(1, run.Room.SkillCasts);
        Assert.Equal(1, run.Room.Hits);
        Assert.Empty(run.Room.Effects);
        Assert.InRange(slot.CooldownLeftMs, AvatarFixture.HardAttackCool - 651.0, AvatarFixture.HardAttackCool - 649.0);

        // 收招后再连跑 30 帧零输入,位置与站立态保持不变
        for (int i = 0; i < 30; i++)
        {
            run.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal(HeroMotion.Rest, run.Hero.Motion);
            Assert.Equal(0, run.Hero.SkillId);
            Assert.Equal(startX, run.Hero.X);
            Assert.Equal(startY, run.Hero.Y);
        }
        Assert.Equal(1, run.Room.SkillCasts);
    }
}
