using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 脚本前冲(客户端状态 0x2d 的模式 1)在房间会话里的接线。数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):动作 2 是 400 毫秒(四帧各 100)、动作 3 是 300 毫秒(三帧各 100);
/// [static data] 是 (200, −400, 100, −100, 2)。主角站在 (300, 100),按 10 毫秒一步推进,第 k 步结束时状态计时器是 10·k 毫秒(施放那一步算第 1 步)。
/// 位移按"距离 × 已过 / 落地时刻"手算(整数线性插值向零取整)。
/// </summary>
public class SkillLeapRoomTests
{
    private static SkillStageSpec Plain(int motion)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0);
    }

    private static SkillStageSpec Leaping(int motion, int distanceIndex, int landFrame)
    {
        return Plain(motion).WithLeap(new SkillLeapSpec(distanceIndex, 0, landFrame));
    }

    private static SkillRecipes One(SkillStageSpec[] stages, bool autoChain = false, SkillBodyHitSpec body = null)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill, stages, slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue,
                slideFollowsHeldDirection: false, autoChain: autoChain, bodyHit: body),
        });
    }

    private static HeroSkills Load(GameData data, SkillRecipes recipes)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 1));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    /// <summary>没有怪物、主角有一大片可走区域(或自己给一块)。</summary>
    private static RoomCombatTests.Arena Open(SkillRecipes recipes, bool facingLeft = false, List<WorldRect> walk = null)
    {
        var arena = new RoomCombatTests.Arena(new (int X, int Y)[0], hp: 100000, facingLeft: facingLeft, chr: AvatarFixture.ActionChrPath,
            walkArea: walk ?? new List<WorldRect> { new WorldRect(0, 0, 4000, 400) }, skillsFactory: data => Load(data, recipes));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    /// <summary>推进 <paramref name="steps"/> 步(每步 10 毫秒);第一步按下 <paramref name="firstStepKeys"/>。</summary>
    private static void Run(RoomCombatSession s, int steps, SkillKeys firstStepKeys = SkillKeys.None)
    {
        for (int i = 0; i < steps; i++)
        {
            s.Update(10, 0, 0, false, false, i == 0 ? firstStepKeys : SkillKeys.None);
        }
    }

    [Fact]
    public void TheLeapFollowsTheLinearOffsetAndStopsAtTheLandingFrameWithoutSnappingToTheFullDistance()
    {
        // 距离 S2 = 100;落地帧 2 → 落地时刻 = 前两帧 = 200 毫秒;第 k 步偏移 = 100 × 10k / 200 = 5k
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        Run(s, 1, SkillKeys.A);
        Assert.Equal(305.0, s.Hero.X, 6);                    // 10 毫秒:5
        Run(s, 9);
        Assert.Equal(350.0, s.Hero.X, 6);                    // 100 毫秒:50
        Run(s, 9);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 190 毫秒:95
        Run(s, 1);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 200 毫秒 = 落地时刻:这一步不摆位置,不补满距离(客户端落地块不写水平位置)
        Run(s, 18);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 之后在动画里不再动
        Assert.Equal(19, s.LeapSteps);                       // 第 1 到 19 步(已过 10 到 190 毫秒)在空中
    }

    [Fact]
    public void TheLeapIsNotTheClampedLinearTravelTheOldTravelGivesTheFullDistanceAtTheEnd()
    {
        // 新旧对照:同一段 200 毫秒、距离 100,按时长取数的线性位移(夹到满距离)到 400,脚本前冲停在 395
        using (var old = Open(One(new[] { Plain(2) }, body: new SkillBodyHitSpec(travels: new[] { new SkillTravelSpec(0, 2, 0) }))))
        {
            Run(old.Session, 20, SkillKeys.A);
            Assert.Equal(400.0, old.Session.Hero.X, 6);
            Assert.Equal(0, old.Session.LeapSteps);
        }
        using (var leap = Open(One(new[] { Leaping(2, 2, 2) })))
        {
            Run(leap.Session, 20, SkillKeys.A);
            Assert.Equal(395.0, leap.Session.Hero.X, 6);
        }
    }

    [Fact]
    public void ALeftFacingHeroLeapsLeftAndANegativeDistanceGoesTheOtherWay()
    {
        using (var arena = Open(One(new[] { Leaping(2, 2, 2) }), facingLeft: true))
        {
            Run(arena.Session, 19, SkillKeys.A);
            Assert.Equal(205.0, arena.Session.Hero.X, 6);    // 300 − 95
            Run(arena.Session, 10);
            Assert.Equal(205.0, arena.Session.Hero.X, 6);
        }
        using (var arena = Open(One(new[] { Leaping(2, 3, 2) })))                    // 距离 S3 = −100:朝右的主角往左走,100 毫秒时 −50
        {
            Run(arena.Session, 10, SkillKeys.A);
            Assert.Equal(250.0, arena.Session.Hero.X, 6);
        }
    }

    [Fact]
    public void AFasterAttackSpeedMakesTheHeroLandEarlierInWallClockTime()
    {
        // 动画快一倍:前两帧 200 动画毫秒 = 100 墙钟毫秒落地;90 毫秒时偏移 100 × 90 / 100 = 90,100 毫秒起不再动
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }));
        RoomCombatSession s = arena.Session;
        s.Hero.AttackAnimationRate = 2.0;
        Run(s, 9, SkillKeys.A);
        Assert.Equal(390.0, s.Hero.X, 6);
        Run(s, 1);
        Assert.Equal(390.0, s.Hero.X, 6);
        Run(s, 20);
        Assert.Equal(390.0, s.Hero.X, 6);
    }

    [Fact]
    public void TheLandingFrameCountsTheFramesBeforeIt()
    {
        // 落地帧 3 → 落地时刻 300 毫秒:100 毫秒时 100 × 100 / 300 = 33.3 → 33;290 毫秒时 96.7 → 96;300 毫秒起不动
        using var arena = Open(One(new[] { Leaping(2, 2, 3) }));
        RoomCombatSession s = arena.Session;
        Run(s, 10, SkillKeys.A);
        Assert.Equal(333.0, s.Hero.X, 6);
        Run(s, 19);
        Assert.Equal(396.0, s.Hero.X, 6);
        Run(s, 1);
        Assert.Equal(396.0, s.Hero.X, 6);
    }

    [Fact]
    public void OnlyTheStageWithTheLeapMovesAndItStartsFromWhereTheStageBegan()
    {
        // 两段自动接续,只有第 1 段(动作 3,300 毫秒)带前冲、落地帧 2 → 200 毫秒;第 0 段(400 毫秒)原地不动
        using var arena = Open(One(new[] { Plain(2), Leaping(3, 2, 2) }, autoChain: true));
        RoomCombatSession s = arena.Session;
        Run(s, 39, SkillKeys.A);
        Assert.Equal((0, 300.0), (s.Hero.SkillStage, s.Hero.X));
        Assert.Equal(0, s.LeapSteps);
        Run(s, 10);                                          // 第 40 步换段,新一段的计时器从 0 起:第 49 步是 90 毫秒
        Assert.Equal((1, 345.0), (s.Hero.SkillStage, s.Hero.X));      // 100 × 90 / 200 = 45
        Run(s, 10);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 第 59 步:190 毫秒 → 95
        Run(s, 1);
        Assert.Equal(395.0, s.Hero.X, 6);                    // 第 60 步:200 毫秒,落地
        Run(s, 30);
        Assert.Equal(395.0, s.Hero.X, 6);
    }

    [Fact]
    public void ASkillWithoutALeapStaysPutAndCountsNoLeapSteps()
    {
        using var arena = Open(One(new[] { Plain(2) }));
        Run(arena.Session, 30, SkillKeys.A);
        Assert.Equal(300.0, arena.Session.Hero.X, 6);
        Assert.Equal(0, arena.Session.LeapSteps);
    }

    [Fact]
    public void TheLeapStopsAtTheWalkAreaEdge()
    {
        using var arena = Open(One(new[] { Leaping(2, 2, 2) }), walk: new List<WorldRect> { new WorldRect(250, 90, 80, 20) });     // 右边界 330(含)
        Run(arena.Session, 30, SkillKeys.A);
        Assert.Equal(330.0, arena.Session.Hero.X, 6);
    }
}
