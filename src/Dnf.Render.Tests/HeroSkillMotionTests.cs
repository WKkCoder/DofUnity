using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 主角的两种技能动作(证据 E-076):把连招某一段整段当成技能打出来(上挑),与后跳(跳跃状态的特殊参数)。
/// 动画与角色脚本是合成的(<see cref="AvatarFixture.ActionChrPath"/>):三段普攻 400、300、400 毫秒,第三段两帧各 200 毫秒;
/// 跳跃动画的停留帧在 3 与 6(下落帧 4、落地帧 7);[move speed] 是 <see cref="AvatarFixture.MoveSpeedStat"/>。
/// </summary>
public class HeroSkillMotionTests
{
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ActionChrPath, IReadOnlyList<WorldRect> area = null,
        double x = 100, double y = 50, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, area ?? Unrestricted, x, y, facingLeft, TownHero.DungeonRestTag);
    }

    // ---- 把连招第 N 段当成技能打出来 ----

    [Fact]
    public void ASkillStrikePlaysTheChosenComboStageOnceWithoutChainingOrTurning()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, facingLeft: true);
        long swings = hero.SwingNumber;

        Assert.True(hero.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));
        Assert.Equal((HeroMotion.Skill, HeroAttackKind.Skill, true), (hero.Motion, hero.AttackKind, hero.IsAttacking));
        Assert.Equal((AvatarFixture.UpperSlashSkill, 2), (hero.SkillId, hero.AttackStage));
        Assert.Equal(swings + 1, hero.SwingNumber);                     // 起手算一次挥击(命中判定用它去重)
        Assert.Equal(AvatarFixture.Attack3Ani, hero.CurrentAnimationPath);
        Assert.Equal("attack3.ani", hero.AttackMotionFile);
        Assert.Equal(0, hero.CurrentFrame);

        // 第三段两帧各 200 毫秒:250 毫秒时在第 1 帧;挥击中按攻击键不接下一段,按右键不转身也不走
        hero.Update(250, 1, 0, true);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Assert.Equal(1, hero.CurrentFrame);
        Assert.True(hero.FacingLeft);
        Assert.Equal(100.0, hero.X);
        Assert.Equal(swings + 1, hero.SwingNumber);

        // 整段 400 毫秒:399 还在放,400 回到站立,技能编号清零,这一击没有再起新的挥击
        hero.Update(149, 0, 0);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        hero.Update(1, 0, 0);
        Assert.Equal((HeroMotion.Rest, false, 0, 0), (hero.Motion, hero.IsAttacking, hero.SkillId, hero.AttackStage));
        Assert.Equal(swings + 1, hero.SwingNumber);
    }

    [Fact]
    public void ASkillStrikeCanStartFromWalkingButNotWhileAttackingOrAirborneOrForAMissingStage()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        TownHero walker = NewHero(assets);
        walker.Update(50, 1, 0);
        Assert.Equal(HeroMotion.Move, walker.Motion);
        Assert.True(walker.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));

        TownHero swinging = NewHero(assets);
        swinging.Update(10, 0, 0, true);
        Assert.False(swinging.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));
        Assert.Equal((HeroMotion.Attack, 0), (swinging.Motion, swinging.AttackStage));

        TownHero jumper = NewHero(assets);
        jumper.Update(10, 0, 0, false, true);
        Assert.True(jumper.IsAirborne);
        Assert.False(jumper.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));

        TownHero idle = NewHero(assets);
        Assert.False(idle.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 3));    // 只有三段
        Assert.False(idle.BeginSkillStrike(AvatarFixture.UpperSlashSkill, -1));
        Assert.Equal((HeroMotion.Rest, 0), (idle.Motion, idle.SkillId));

        TownHero noAttacks = NewHero(assets, AvatarFixture.NoAttackChrPath);
        Assert.False(noAttacks.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 0));
    }

    // ---- 后跳 ----

    [Fact]
    public void ABackStepLiftsOffAtOnceAndRetreatsAtAConstantSpeedAgainstTheFacing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, x: 500, facingLeft: false);

        double power = AvatarFixture.BackStepPowerStat;
        double v0 = TownHero.TakeoffSpeed(power, hero.WeightStat, TownHero.BackStepJumpSpeedStat);   // 跳跃速度属性取 100,不用角色自己的
        double g = TownHero.BackStepJumpSpeedStat * TownHero.EngineGroundGravity / 100.0;
        double speed = TownHero.EngineDashX * AvatarFixture.MoveSpeedStat / 1000.0;                  // 奔跑表的水平值 × [move speed] 千分比
        Assert.Equal(speed, hero.BackStepSpeedX, 9);
        Assert.Equal(g, hero.BackStepGravity, 9);

        Assert.True(hero.BeginBackStep(AvatarFixture.BackStepSkill, power));
        Assert.Equal((HeroMotion.Jump, true, true, AvatarFixture.BackStepSkill), (hero.Motion, hero.IsBackStep, hero.IsAirborne, hero.SkillId));
        Assert.False(hero.IsAttacking);

        // 没有蹲姿:第一步就腾空;水平朝后(主角朝右,所以往左退),深度方向不动
        hero.Update(10, 0, 0);
        Assert.Equal(v0 * 0.01 - 0.5 * g * 0.0001, hero.Z, 6);
        Assert.Equal(500 - speed * 0.01, hero.X, 6);
        Assert.Equal(50.0, hero.Y);

        // 腾空中方向键不起作用(不转身、不改速度、不动深度),攻击键与跳跃键也不起作用
        double x1 = hero.X;
        hero.Update(10, 1, 1, true, true);
        Assert.Equal(x1 - speed * 0.01, hero.X, 6);
        Assert.Equal(50.0, hero.Y);
        Assert.False(hero.FacingLeft);
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.True(hero.IsBackStep);

        // 动画停在下落段的最后一个停留帧(落地帧的前一帧),整个腾空期间不变
        Assert.Equal(hero.JumpLandFrame - 1, hero.CurrentFrame);
        Assert.Equal(AvatarFixture.JumpAni, hero.CurrentAnimationPath);
    }

    [Fact]
    public void ABackStepFollowsTheBallisticArcAndLandsStraightIntoStanding()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, x: 500, facingLeft: true);   // 朝左,所以往右退
        double power = AvatarFixture.BackStepPowerStat;
        double v0 = TownHero.TakeoffSpeed(power, hero.WeightStat, TownHero.BackStepJumpSpeedStat);
        double g = TownHero.BackStepJumpSpeedStat * TownHero.EngineGroundGravity / 100.0;
        double speed = hero.BackStepSpeedX;

        Assert.True(hero.BeginBackStep(AvatarFixture.BackStepSkill, power));
        double apex = 0;
        int steps = 0;
        while (hero.Motion == HeroMotion.Jump && steps < 5000)
        {
            hero.Update(1, 0, 0);
            apex = Math.Max(apex, hero.Z);
            steps++;
        }
        // 腾空时间 = 2 v0 / g;最高点 = v0² / 2g;落地那一步不再水平移动,所以位移比 速度 × 腾空时间 至多少一步
        double airMs = 2 * v0 / g * 1000.0;
        Assert.InRange(steps, airMs - 1.5, airMs + 1.5);
        Assert.Equal(v0 * v0 / (2 * g), apex, 0);
        Assert.InRange(hero.X - 500, speed * (steps - 1) / 1000.0, speed * steps / 1000.0 + 1e-6);

        // 落地直接回到站立:没有落地段,技能编号与后跳标记清掉,脚着地
        Assert.Equal((HeroMotion.Rest, false, 0, 0.0), (hero.Motion, hero.IsBackStep, hero.SkillId, hero.Z));
        Assert.True(hero.FacingLeft);

        // 之后能正常起跳(带蹲姿、不是后跳),而且速度不再被后跳的参数影响
        hero.Update(10, 0, 0, false, true);
        Assert.Equal((HeroMotion.Jump, false), (hero.Motion, hero.IsBackStep));
        Assert.Equal(0.0, hero.Z);                                   // 蹲姿期间还在地面
    }

    [Fact]
    public void ABackStepRespectsTheWalkableAreaAndRefusesWhenBusy()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 退路被区域边界挡住:停在边界上(区域 x ∈ [90, 200]),朝右的主角往左退
        var area = new List<WorldRect> { new WorldRect(90, 0, 110, 100) };
        TownHero walled = NewHero(assets, area: area, x: 100, y: 50, facingLeft: false);
        Assert.True(walled.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        for (int i = 0; i < 1000 && walled.Motion == HeroMotion.Jump; i++)
        {
            walled.Update(1, 0, 0);
        }
        Assert.Equal((HeroMotion.Rest, 90.0), (walled.Motion, walled.X));

        // 正在挥击、腾空、放别的技能时不能放;没有跳跃动作的角色不能放;失败时状态不变
        TownHero swinging = NewHero(assets);
        swinging.Update(10, 0, 0, true);
        Assert.False(swinging.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        Assert.Equal(HeroMotion.Attack, swinging.Motion);

        TownHero jumper = NewHero(assets);
        jumper.Update(10, 0, 0, false, true);
        Assert.False(jumper.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        Assert.False(jumper.IsBackStep);

        TownHero skilling = NewHero(assets);
        Assert.True(skilling.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));
        Assert.False(skilling.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        Assert.Equal(HeroMotion.Skill, skilling.Motion);

        TownHero noJump = NewHero(assets, AvatarFixture.ChrPath);
        Assert.False(noJump.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        Assert.Equal(HeroMotion.Rest, noJump.Motion);
    }

    [Fact]
    public void PlacingTheHeroClearsABackStepInProgress()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        Assert.True(hero.BeginBackStep(AvatarFixture.BackStepSkill, AvatarFixture.BackStepPowerStat));
        hero.Update(50, 0, 0);
        Assert.True(hero.Z > 0);

        hero.Place(200, 60, false, HeroMotion.Rest, 0);
        Assert.Equal((HeroMotion.Rest, false, 0, 0.0), (hero.Motion, hero.IsBackStep, hero.SkillId, hero.Z));
        hero.Update(100, 0, 0);
        Assert.Equal((200.0, 60.0), (hero.X, hero.Y));               // 摆好之后不会继续往后退
    }

    // ---- 多段技能换段(证据 E-129) ----

    [Fact]
    public void ChainingSwitchesToTheNextEtcMotionFromTheStartWithFreshSlideAndANewSwing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        int a = hero.EtcMotionIndexOf("chaina.ani");
        int b = hero.EtcMotionIndexOf("chainb.ani");
        Assert.True(a >= 0 && b >= 0);

        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, a, 200, -400));
        Assert.Equal((HeroMotion.Skill, 0, "chaina.ani"), (hero.Motion, hero.SkillStage, hero.SkillMotionName));
        long swings = hero.SwingNumber;
        hero.Update(250, 0, 0);                                         // 第一段 400 毫秒,放到第 2 帧(200 到 300 毫秒)
        Assert.Equal(2, hero.CurrentFrame);
        double x = hero.X;
        Assert.Equal(100 + TownHero.SlideDistance(200, -400, 0, 250), x, 6);

        // 换段:新动画从头播,新一击(命中判定重新数),按新的速度重新起滑
        Assert.True(hero.ChainSkillMotion(AvatarFixture.ChainSkill, b, 300, -500));
        Assert.Equal((HeroMotion.Skill, 1, "chainb.ani", 0), (hero.Motion, hero.SkillStage, hero.SkillMotionName, hero.CurrentFrame));
        Assert.Equal((swings + 1, 0L, 300.0), (hero.SwingNumber, hero.MotionMs, hero.SkillSlideSpeed));
        Assert.Equal((AvatarFixture.ChainSkill, HeroAttackKind.Skill, true), (hero.SkillId, hero.AttackKind, hero.IsAttacking));
        hero.Update(100, 0, 0);
        Assert.Equal(1, hero.CurrentFrame);                             // 第二段 300 毫秒:100 毫秒时在第 1 帧
        Assert.Equal(x + TownHero.SlideDistance(300, -500, 0, 100), hero.X, 6);

        // 第二段 300 毫秒播完回到站立,段号清零,技能编号清零;再换段不行
        hero.Update(200, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0, 0), (hero.Motion, hero.SkillStage, hero.SkillId));
        Assert.False(hero.ChainSkillMotion(AvatarFixture.ChainSkill, b, 300, -500));
    }

    [Fact]
    public void ChainingNeedsTheSameSkillInItsMainPhaseAndAReadableMotion()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        int b = NewHero(assets).EtcMotionIndexOf("chainb.ani");

        // 没在放技能
        TownHero idle = NewHero(assets);
        Assert.False(idle.ChainSkillMotion(AvatarFixture.ChainSkill, b, 0, 0));
        Assert.Equal((HeroMotion.Rest, 0), (idle.Motion, idle.SkillStage));

        // 放的是别的技能(上挑借用普攻动画,起手阶段)
        TownHero other = NewHero(assets);
        Assert.True(other.BeginSkillStrike(AvatarFixture.UpperSlashSkill, 2));
        Assert.False(other.ChainSkillMotion(AvatarFixture.ChainSkill, b, 0, 0));
        Assert.False(other.ChainSkillMotion(AvatarFixture.UpperSlashSkill, b, 0, 0));    // 同一个技能,但它没有主体动画可换(整段借用普攻)
        Assert.Equal((HeroMotion.Skill, 0), (other.Motion, other.SkillStage));

        // 起手阶段(先挥普攻再换动画)里不换
        TownHero windup = NewHero(assets);
        Assert.True(windup.BeginSkillWindup(AvatarFixture.UpperSlashSkill, 0, 2, b, 100, -100));
        Assert.True(windup.SkillWindingUp);
        Assert.False(windup.ChainSkillMotion(AvatarFixture.UpperSlashSkill, b, 0, 0));

        // 放同一个技能,但目标动作读不到:不改状态
        TownHero hero = NewHero(assets);
        int a = hero.EtcMotionIndexOf("chaina.ani");
        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        long swings = hero.SwingNumber;
        Assert.False(hero.ChainSkillMotion(AvatarFixture.ChainSkill, 99, 0, 0));
        Assert.Equal((0, "chaina.ani", swings), (hero.SkillStage, hero.SkillMotionName, hero.SwingNumber));
    }

    [Fact]
    public void TheSkillStageIsZeroOutsideSkillsAndRestartsWithEachNewSkill()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        int a = hero.EtcMotionIndexOf("chaina.ani");
        int b = hero.EtcMotionIndexOf("chainb.ani");

        Assert.Equal(0, hero.SkillStage);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        Assert.True(hero.ChainSkillMotion(AvatarFixture.ChainSkill, b, 0, 0));
        Assert.Equal(1, hero.SkillStage);

        // 被打断(摆回站立)之后段号读出来是 0,下一次放同一个技能从第 0 段开始
        hero.Place(100, 50, false, HeroMotion.Rest, 0);
        Assert.Equal(0, hero.SkillStage);
        Assert.True(hero.BeginSkillMotion(AvatarFixture.ChainSkill, a, 0, 0));
        Assert.Equal((0, "chaina.ani"), (hero.SkillStage, hero.SkillMotionName));
    }
}
