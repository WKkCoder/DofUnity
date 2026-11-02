using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 出招指令的输入判定(M47,证据 E-140):扳机必须在这一步按下;前面的键依次在窗口内按过,或(方向键)按着不放;"&amp;"(同时)只认此刻按着;
/// 方向键按朝向翻转。窗口长度与"按着算不算"是假设(E-140 末尾),测试按这些假设写。全是合成指令。
/// </summary>
public sealed class CommandInputTests
{
    private const double Step = 16.0;

    private static void Tick(CommandInput input, double ms = Step, int dirX = 0, int dirY = 0, bool attack = false, bool jump = false, bool skill = false, bool buff = false)
    {
        input.Update(ms, dirX, dirY, attack, jump, skill, buff);
    }

    // ------------------------------------------------------------------ 单键与扳机

    [Fact]
    public void ASingleKeyMatchesOnTheStepItIsPressedAndNotAfter()
    {
        var input = new CommandInput();
        SkillCommand z = SkillCommand.Of(CommandKey.Skill);
        Tick(input);
        Assert.False(input.Matches(z, false));
        Tick(input, skill: true);
        Assert.True(input.Matches(z, false));
        Assert.True(input.JustPressed(CommandKey.Skill));
        Tick(input);                                                    // 下一步没有新按下:不再匹配(边沿)
        Assert.False(input.Matches(z, false));
        Assert.False(input.JustPressed(CommandKey.Skill));
    }

    [Fact]
    public void AnEmptyOrNullCommandNeverMatches()
    {
        var input = new CommandInput();
        Tick(input, skill: true, attack: true, jump: true, buff: true);
        Assert.False(input.Matches(SkillCommand.Empty, false));
        Assert.False(input.Matches(null, false));
    }

    [Fact]
    public void EachOfTheFourNonDirectionKeysCanBeTheTrigger()
    {
        var input = new CommandInput();
        Tick(input, attack: true);
        Assert.True(input.Matches(SkillCommand.Of(CommandKey.Attack), false));
        Assert.False(input.Matches(SkillCommand.Of(CommandKey.Jump), false));
        Tick(input, jump: true);
        Assert.True(input.Matches(SkillCommand.Of(CommandKey.Jump), false));
        Tick(input, buff: true);
        Assert.True(input.Matches(SkillCommand.Of(CommandKey.Buff), false));
        Assert.False(input.Matches(SkillCommand.Of(CommandKey.Skill), false));
    }

    // ------------------------------------------------------------------ 方向 + 扳机

    [Fact]
    public void AHeldDirectionOrARecentTapBothSatisfyADirectionThenTrigger()
    {
        SkillCommand forwardZ = SkillCommand.Of(CommandKey.Right, CommandKey.Skill);

        // 按住方向键再按扳机
        var held = new CommandInput();
        Tick(held, dirX: 1);
        Tick(held, dirX: 1);
        Tick(held, dirX: 1, skill: true);
        Assert.True(held.Matches(forwardZ, false));

        // 点一下方向键、松开、200 毫秒后按扳机:在窗口内
        var tap = new CommandInput();
        Tick(tap, dirX: 1);
        Tick(tap, 200.0);
        Tick(tap, skill: true);
        Assert.True(tap.Matches(forwardZ, false));

        // 点一下方向键、松开、400 毫秒后按扳机:超出窗口
        var late = new CommandInput();
        Tick(late, dirX: 1);
        Tick(late, 400.0);
        Tick(late, skill: true);
        Assert.False(late.Matches(forwardZ, false));

        // 只按了扳机、方向键根本没按:不匹配
        var none = new CommandInput();
        Tick(none, skill: true);
        Assert.False(none.Matches(forwardZ, false));

        // 按的是另一个方向:不匹配
        var wrong = new CommandInput();
        Tick(wrong, dirX: -1);
        Tick(wrong, dirX: -1, skill: true);
        Assert.False(wrong.Matches(forwardZ, false));
    }

    [Fact]
    public void ForwardAndBackwardFollowTheFacing()
    {
        SkillCommand forwardZ = SkillCommand.Of(CommandKey.Right, CommandKey.Skill);
        SkillCommand backZ = SkillCommand.Of(CommandKey.Left, CommandKey.Skill);

        var left = new CommandInput();                       // 朝左的主角按左键:朝前
        Tick(left, dirX: -1, skill: true);
        Assert.True(left.Matches(forwardZ, true));
        Assert.False(left.Matches(backZ, true));
        Assert.False(left.Matches(forwardZ, false));         // 朝右的主角同一串按键是朝后
        Assert.True(left.Matches(backZ, false));

        Assert.Equal(CommandKey.Left, CommandInput.Absolute(CommandKey.Right, true));
        Assert.Equal(CommandKey.Right, CommandInput.Absolute(CommandKey.Left, true));
        Assert.Equal(CommandKey.Right, CommandInput.Absolute(CommandKey.Right, false));
        Assert.Equal(CommandKey.Up, CommandInput.Absolute(CommandKey.Up, true));
        Assert.Equal(CommandKey.Skill, CommandInput.Absolute(CommandKey.Skill, true));
    }

    [Fact]
    public void UpAndDownAreNotMirrored()
    {
        var input = new CommandInput();
        Tick(input, dirY: -1, skill: true);
        Assert.True(input.Matches(SkillCommand.Of(CommandKey.Up, CommandKey.Skill), true));
        Assert.False(input.Matches(SkillCommand.Of(CommandKey.Down, CommandKey.Skill), true));
    }

    [Fact]
    public void ASequenceEndingInTheForwardKeyReadsTheSameWhicheverWayTheHeroStartedFacing()
    {
        // 左、右、技能:朝右的主角先按左(转向左)再按右(转回右)——参照的是打出扳机时的朝向(朝右)
        SkillCommand backForwardZ = SkillCommand.Of(CommandKey.Left, CommandKey.Right, CommandKey.Skill);
        var startedRight = new CommandInput();
        Tick(startedRight, dirX: -1);
        Tick(startedRight, 40.0);
        Tick(startedRight, dirX: 1);
        Tick(startedRight, skill: true, dirX: 1);
        Assert.True(startedRight.Matches(backForwardZ, false));
        Assert.False(startedRight.Matches(backForwardZ, true));

        // 朝左的主角打同一个指令:先按右(转向右)再按左(转回左),扳机时朝左,"后、前"就是"右、左"
        var startedLeft = new CommandInput();
        Tick(startedLeft, dirX: 1);
        Tick(startedLeft, 40.0);
        Tick(startedLeft, dirX: -1);
        Tick(startedLeft, skill: true, dirX: -1);
        Assert.True(startedLeft.Matches(backForwardZ, true));
        Assert.False(startedLeft.Matches(backForwardZ, false));
    }

    // ------------------------------------------------------------------ 同时与先后

    [Fact]
    public void TogetherNeedsTheDirectionHeldNowAndATapIsNotEnough()
    {
        SkillCommand together = SkillCommand.OfTogether(new[] { CommandKey.Up, CommandKey.Skill }, 0);

        var held = new CommandInput();
        Tick(held, dirY: -1);
        Tick(held, dirY: -1, skill: true);
        Assert.True(held.Matches(together, false));

        var sameStep = new CommandInput();                   // 同一步一起按下也算
        Tick(sameStep, dirY: -1, skill: true);
        Assert.True(sameStep.Matches(together, false));

        var tapped = new CommandInput();                     // 点一下松开再按扳机:按"先后"能匹配,按"同时"不行
        Tick(tapped, dirY: -1);
        Tick(tapped, 50.0);
        Tick(tapped, skill: true);
        Assert.False(tapped.Matches(together, false));
        Assert.True(tapped.Matches(SkillCommand.Of(CommandKey.Up, CommandKey.Skill), false));
    }

    [Fact]
    public void ASequenceMustBeInOrder()
    {
        SkillCommand upThenDown = SkillCommand.Of(CommandKey.Up, CommandKey.Down, CommandKey.Attack);

        var ordered = new CommandInput();
        Tick(ordered, dirY: -1);
        Tick(ordered, 60.0);
        Tick(ordered, dirY: 1);
        Tick(ordered, 60.0);
        Tick(ordered, attack: true);
        Assert.True(ordered.Matches(upThenDown, false));

        var reversed = new CommandInput();
        Tick(reversed, dirY: 1);
        Tick(reversed, 60.0);
        Tick(reversed, dirY: -1);
        Tick(reversed, 60.0);
        Tick(reversed, attack: true);
        Assert.False(reversed.Matches(upThenDown, false));
    }

    [Fact]
    public void AKeyPressedAndHeldAfterTheNextKeyCannotStandInForAnEarlierPlace()
    {
        SkillCommand upAttackSkill = SkillCommand.Of(CommandKey.Up, CommandKey.Attack, CommandKey.Skill);

        var inOrder = new CommandInput();                    // 上、攻击、技能,上按着不放
        Tick(inOrder, dirY: -1);
        Tick(inOrder, 100.0, dirY: -1, attack: true);
        Tick(inOrder, 100.0, dirY: -1, skill: true);
        Assert.True(inOrder.Matches(upAttackSkill, false));

        var wrongOrder = new CommandInput();                 // 攻击、然后才按下上并一直按着、技能:上是攻击之后才按下的,顶不了排在攻击前面的位置
        Tick(wrongOrder, attack: true);
        Tick(wrongOrder, 100.0, dirY: -1);
        Tick(wrongOrder, 100.0, dirY: -1, skill: true);
        Assert.False(wrongOrder.Matches(upAttackSkill, false));
    }

    [Fact]
    public void AHeldDirectionThatWasReleasedBeforeTheTriggerStillCountsIfItWasHeldWhenTheNextKeyWasPressed()
    {
        SkillCommand rightAttackSkill = SkillCommand.Of(CommandKey.Right, CommandKey.Attack, CommandKey.Skill);

        var released = new CommandInput();                   // 右按着,按攻击,松开右,再按技能
        Tick(released, dirX: 1);
        Tick(released, 100.0, dirX: 1);
        Tick(released, 100.0, dirX: 1, attack: true);
        Tick(released, 60.0);
        Tick(released, 60.0, skill: true);
        Assert.True(released.Matches(rightAttackSkill, false));

        var tappedEarly = new CommandInput();                // 右点一下就松开,隔了 400 毫秒才按攻击:攻击时没按着右,右的按下也过了窗口
        Tick(tappedEarly, dirX: 1);
        Tick(tappedEarly, 400.0);
        Tick(tappedEarly, 10.0, attack: true);
        Tick(tappedEarly, 60.0, skill: true);
        Assert.False(tappedEarly.Matches(rightAttackSkill, false));
    }

    [Fact]
    public void TogetherNeedsTheDirectionHeldWhenTheNextKeyIsPressedNotWhenTheTriggerIs()
    {
        // 右 & 攻击,技能:攻击按下那一刻右按着,之后右松开了再按技能
        SkillCommand c = SkillCommand.OfTogether(new[] { CommandKey.Right, CommandKey.Attack, CommandKey.Skill }, 0);

        var ok = new CommandInput();
        Tick(ok, dirX: 1);
        Tick(ok, 60.0, dirX: 1, attack: true);
        Tick(ok, 60.0);
        Tick(ok, 60.0, skill: true);
        Assert.True(ok.Matches(c, false));

        var tapped = new CommandInput();                     // 右点一下松开,再按攻击:攻击那一刻右不按着
        Tick(tapped, dirX: 1);
        Tick(tapped, 30.0);
        Tick(tapped, 30.0, attack: true);
        Tick(tapped, 60.0, skill: true);
        Assert.False(tapped.Matches(c, false));

        var late = new CommandInput();                       // 先按攻击,再按下右并按着,最后技能:右是攻击之后才按下的
        Tick(late, attack: true);
        Tick(late, 60.0, dirX: 1);
        Tick(late, 60.0, dirX: 1, skill: true);
        Assert.False(late.Matches(c, false));
    }

    [Fact]
    public void EachGapIsMeasuredFromTheNextKeyNotFromTheTrigger()
    {
        // 左、右、扳机:左在 0 毫秒、右在 250 毫秒、扳机在 500 毫秒——每一对相邻的键都在 300 毫秒之内,整串超过 300 毫秒也行
        SkillCommand c = SkillCommand.Of(CommandKey.Left, CommandKey.Right, CommandKey.Skill);
        var input = new CommandInput();
        Tick(input, 0.0, dirX: -1);
        Tick(input, 250.0, dirX: 1);
        Tick(input, 250.0, skill: true);
        Assert.Equal(500.0, input.NowMs, 6);
        Assert.True(input.Matches(c, false));

        // 左与右之间隔了 350 毫秒:不行
        var slow = new CommandInput();
        Tick(slow, 0.0, dirX: -1);
        Tick(slow, 350.0, dirX: 0);
        Tick(slow, 0.0, dirX: 1);
        Tick(slow, 100.0, skill: true);
        Assert.False(slow.Matches(c, false));
    }

    [Fact]
    public void DoubleTapNeedsTwoSeparatePressesUnlessTheKeyIsHeldOnce()
    {
        SkillCommand doubleDownAttack = SkillCommand.Of(CommandKey.Down, CommandKey.Down, CommandKey.Attack);

        var twice = new CommandInput();                      // 按、松、按、攻击
        Tick(twice, dirY: 1);
        Tick(twice, 80.0);
        Tick(twice, dirY: 1);
        Tick(twice, 80.0, dirY: 1, attack: true);
        Assert.True(twice.Matches(doubleDownAttack, false));

        // 很久以前按下一次、一直按着:第二个下只能靠"按着",第一个下没有记录可用——不行
        var heldOnly = new CommandInput();
        Tick(heldOnly, dirY: 1);
        for (int i = 0; i < 40; i++)
        {
            Tick(heldOnly, dirY: 1);
        }
        Tick(heldOnly, dirY: 1, attack: true);
        Assert.False(heldOnly.Matches(doubleDownAttack, false));
    }

    [Fact]
    public void ASingleRecentTapCannotSatisfyBothPositionsOfARepeatedKey()
    {
        // 点一下下、紧接着按攻击:只有一次按下记录,同一次按下不能既当第二个下又当"早就按着"的第一个下
        // (M66,E-164:否则每次"下 + 攻击"都成了"下、下、攻击"的防御姿态——攻击键同时是普攻)
        SkillCommand doubleDownAttack = SkillCommand.Of(CommandKey.Down, CommandKey.Down, CommandKey.Attack);
        var once = new CommandInput();
        Tick(once, dirY: 1);
        Tick(once, 80.0, dirY: 1, attack: true);
        Assert.False(once.Matches(doubleDownAttack, false));

        var sameStep = new CommandInput();                   // 下和攻击同一步按下
        Tick(sameStep, dirY: 1, attack: true);
        Assert.False(sameStep.Matches(doubleDownAttack, false));

        var released = new CommandInput();                   // 点一下、松开、过了窗口再按攻击:也不行
        Tick(released, dirY: 1);
        Tick(released, 400.0);
        Tick(released, attack: true);
        Assert.False(released.Matches(doubleDownAttack, false));

        // 真正的双击(按、松、按)不受影响,单个下的指令也不受影响
        var twice = new CommandInput();
        Tick(twice, dirY: 1);
        Tick(twice, 80.0);
        Tick(twice, dirY: 1);
        Tick(twice, 80.0, dirY: 1, attack: true);
        Assert.True(twice.Matches(doubleDownAttack, false));

        SkillCommand downAttack = SkillCommand.Of(CommandKey.Down, CommandKey.Attack);
        var single = new CommandInput();
        Tick(single, dirY: 1);
        Tick(single, 80.0, dirY: 1, attack: true);
        Assert.True(single.Matches(downAttack, false));
    }

    // ------------------------------------------------------------------ 历史

    [Fact]
    public void ClearDropsRecordedPressesAndFreshness()
    {
        SkillCommand c = SkillCommand.Of(CommandKey.Right, CommandKey.Skill);
        var input = new CommandInput();
        Tick(input, dirX: 1);
        Tick(input, 20.0);
        Tick(input, skill: true);
        Assert.True(input.Matches(c, false));
        input.Clear();
        Assert.False(input.Matches(c, false));               // 扳机的按下也被忘了
        Assert.False(input.JustPressed(CommandKey.Skill));

        // 清空之后重新按就从头算:右键的点按已经忘了
        Tick(input, 20.0);
        Tick(input, skill: true);
        Assert.False(input.Matches(c, false));

        // 方向键还按着的话照样能再放一次
        var heldDir = new CommandInput();
        Tick(heldDir, dirX: 1, skill: true);
        Assert.True(heldDir.Matches(c, false));
        heldDir.Clear();
        Tick(heldDir, dirX: 1, skill: true);
        Assert.True(heldDir.Matches(c, false));
    }

    [Fact]
    public void OldPressesAreForgottenAndTheClockOnlyAdvancesInUpdate()
    {
        var input = new CommandInput();
        Tick(input, dirX: 1);
        Assert.Equal(Step, input.NowMs, 6);
        Tick(input, 0.0, dirX: 1);                           // 时长 0 的步:时钟不动
        Tick(input, -5.0, dirX: 1);                          // 负的时长当作 0
        Assert.Equal(Step, input.NowMs, 6);

        // 记录上限与保留时长:大量按键之后最老的被丢掉,不会出错
        for (int i = 0; i < 100; i++)
        {
            Tick(input, 30.0, attack: true);
        }
        Tick(input, 5000.0);
        Tick(input, skill: true);
        Assert.False(input.Matches(SkillCommand.Of(CommandKey.Attack, CommandKey.Skill), false));   // 攻击键的记录早过了窗口
    }

    [Fact]
    public void JustPressedIsPerStepAndDirectionsAreEdges()
    {
        var input = new CommandInput();
        Tick(input, dirX: 1);
        Assert.True(input.JustPressed(CommandKey.Right));
        Tick(input, dirX: 1);
        Assert.False(input.JustPressed(CommandKey.Right));   // 按着不放不是新按下
        Tick(input, dirX: 0);
        Tick(input, dirX: 1);
        Assert.True(input.JustPressed(CommandKey.Right));    // 松开再按又是一次
        Tick(input, dirX: -1);                               // 直接从右换到左:左算新按下
        Assert.True(input.JustPressed(CommandKey.Left));
        Assert.False(input.JustPressed(CommandKey.Right));
        Tick(input, dirY: 1, dirX: -1);
        Assert.True(input.JustPressed(CommandKey.Down));
        Assert.False(input.JustPressed(CommandKey.Left));
    }
}
