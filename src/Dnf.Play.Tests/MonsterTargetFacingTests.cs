using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物身上的朝向开关与 AI 目标(M70,证据 E-169):"朝向跟着目标走"总开关关着时 AI 的转向全不做;"出招里逐帧面朝目标"开着时出招的每一步横向比较、
/// 目标在右边才朝右、一样朝左;AI 目标分缺省与指定两层(指定的优先、死了回缺省),脚本的软设置只补上缺失或已死的目标;造怪时两个开关取 .mob 里写的出生值。
/// 数据全是合成的(迷你怪物 + 一个假的目标对象)。
/// </summary>
public sealed class MonsterTargetFacingTests
{
    private static readonly IWalkArea Free = new RectUnionArea(new List<WorldRect>());
    private const double WalkSpeed = 222.5;

    private static MonsterActor Blob(DnfAssets assets, double x, double y)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, 100, WalkSpeed, 1500, 0);
    }

    /// <summary>只有位置与死活的目标对象。</summary>
    private sealed class Aim : IActEntity
    {
        public Aim(float x, float y = 100f)
        {
            X = x;
            Y = y;
        }

        public ActEntityKind Kind => ActEntityKind.Character;
        public int ObjectIndex => 0;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z => 0f;
        public bool FacingLeft => false;
        public bool IsAlive { get; set; } = true;
        public IActEntity Target => null;
        public IActEntity Master => null;
        public int MotionVersion => 0;
        public int Hp => 100;
        public int HpMax => 100;
        public int Level => 1;
        public void SetFacing(bool left) { }
        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz) { }
        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd) { }
        public void Destroy(bool force) { }
        public void AttackRect(ActAttackRectMode mode) { }
        public void SetFrame(int frame) { }
        public void SetDamageBox(ActDamageBoxMode mode) { }
        public void SetFloatingHeight(int height) { }
        public bool Teleport(float x, float y, float z) => false;
        public void SetHp(int hp) { }
    }

    // ------------------------------------------------------------------ 总开关

    [Fact]
    public void ADefaultMonsterFacesTheHeroWhenItStartsAnAttack()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        Assert.True(m.FacesTarget);                                    // 缺省开
        Assert.False(m.AttackFacing);                                  // 缺省关
        Assert.True(m.FacingLeft);                                     // 出生朝左
        m.Update(10, 420, 100, true, Free);                            // 主角在右边
        Assert.Equal((MonsterPhase.Attack, false), (m.Phase, m.FacingLeft));
    }

    [Fact]
    public void AMonsterWithTheTurnSwitchOffNeverTurnsToTheHero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.FacesTarget = false;
        m.Update(10, 420, 100, true, Free);                            // 主角在右边
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));      // 照样出招,但不转向主角
    }

    [Fact]
    public void TheTurnSwitchAlsoStopsAWaitingMonsterFromFacingTheHero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 主角在视野里(右边 240 像素)、够不着的地方:怪物不出招,朝主角走过去并转身(默认)与关着开关不转(对照)
        MonsterActor facing = Blob(assets, 100, 100);
        facing.Update(10, 340, 100, true, Free);
        Assert.NotEqual(MonsterPhase.Attack, facing.Phase);
        Assert.False(facing.FacingLeft);

        MonsterActor fixedFacing = Blob(assets, 100, 100);
        fixedFacing.FacesTarget = false;
        fixedFacing.Update(10, 340, 100, true, Free);
        Assert.NotEqual(MonsterPhase.Attack, fixedFacing.Phase);
        Assert.True(fixedFacing.FacingLeft);
    }

    // ------------------------------------------------------------------ 出招里逐帧面朝目标

    [Fact]
    public void AttackFacingTurnsWithTheHeroOnEveryStepOfAnAttack()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.AttackFacing = true;
        m.Update(10, 300, 100, true, Free);
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));

        m.Update(10, 420, 100, true, Free);                            // 主角跳到了右边
        Assert.Equal((MonsterPhase.Attack, false), (m.Phase, m.FacingLeft));
        m.Update(10, 280, 100, true, Free);                            // 又回到左边
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));
    }

    [Fact]
    public void WithoutAttackFacingTheFacingIsKeptThroughTheAttack()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.Update(10, 300, 100, true, Free);
        Assert.True(m.FacingLeft);
        m.Update(10, 420, 100, true, Free);
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));
    }

    [Fact]
    public void AttackFacingFacesLeftWhenTheHeroIsOnTheSameX()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.AttackFacing = true;
        m.Update(10, 420, 100, true, Free);                            // 主角在右边:出招朝右
        Assert.Equal((MonsterPhase.Attack, false), (m.Phase, m.FacingLeft));
        m.Update(10, m.X, 100, true, Free);                            // 正对着:一样大就朝左
        Assert.True(m.FacingLeft);
    }

    [Fact]
    public void AttackFacingFacesLeftWhenTheAiTargetIsOnTheSameX()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.AttackFacing = true;
        var decoy = new Aim(420);
        m.ActSelf.SetAiTarget(decoy, true);
        m.Update(10, 300, 100, true, Free);                            // 起手:出招前按主角转向(左)
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));
        m.Update(10, 300, 100, true, Free);                            // 出招的这一步:朝指定目标(在右边)
        Assert.False(m.FacingLeft);
        decoy.X = (float)m.X;                                          // 指定目标正对着:一样大就朝左
        m.Update(10, 300, 100, true, Free);
        Assert.True(m.FacingLeft);
    }

    [Fact]
    public void ANullTargetNeverChangesTheAiTarget()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        var designated = new Aim(100);
        var fallback = new Aim(200);
        m.ActSelf.SetAiTarget(fallback, false);
        m.ActSelf.SetAiTarget(designated, true);
        m.ActSelf.SetAiTarget(null, true);
        Assert.Same(designated, m.DesignatedTarget);
        Assert.Same(designated, m.AiTarget);
        m.ActSelf.SetAiTarget(null, false);
        Assert.Same(designated, m.AiTarget);
        Assert.Same(fallback, m.ActTarget);
    }

    [Fact]
    public void AttackFacingNeedsTheTurnSwitchToo()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.AttackFacing = true;
        m.FacesTarget = false;
        m.Update(10, 420, 100, true, Free);                            // 主角在右边:出招起手不转
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));
        m.Update(10, 420, 100, true, Free);
        Assert.True(m.FacingLeft);                                     // 总开关关着:逐帧面朝开着也不转
        m.Update(10, 280, 100, true, Free);
        Assert.True(m.FacingLeft);
    }

    [Fact]
    public void AttackFacingWithoutAHeroLeavesTheFacingAlone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.AttackFacing = true;
        m.Update(10, 300, 100, true, Free);
        Assert.True(m.FacingLeft);
        m.Update(10, 420, 100, false, Free);                           // 主角不在场:没有目标可面对
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));
    }

    [Fact]
    public void AttackFacingAimsAtTheDesignatedTargetWhileItLives()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.AttackFacing = true;
        var decoy = new Aim(500);                                      // 指定的目标在右边,主角在左边
        m.ActSelf.SetAiTarget(decoy, true);
        m.Update(10, 300, 100, true, Free);                            // 出招起手:AI 的转向面对主角(左)
        Assert.Equal((MonsterPhase.Attack, true), (m.Phase, m.FacingLeft));
        m.Update(10, 300, 100, true, Free);                            // 出招里逐帧面朝的是指定的目标(右),不是主角
        Assert.Equal((MonsterPhase.Attack, false), (m.Phase, m.FacingLeft));

        decoy.IsAlive = false;                                         // 指定目标死了:回到主角
        m.Update(10, 300, 100, true, Free);
        Assert.True(m.FacingLeft);
    }

    // ------------------------------------------------------------------ AI 目标

    [Fact]
    public void ADesignatedTargetWinsOverTheDefaultAndFallsBackWhenItDies()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        var hero = new Aim(300);
        var decoy = new Aim(500);
        m.ActTarget = hero;
        Assert.Same(hero, m.AiTarget);
        Assert.Same(hero, m.ActSelf.Target);                           // 脚本问的目标就是 AI 目标

        m.ActSelf.SetAiTarget(decoy, true);
        Assert.Same(decoy, m.DesignatedTarget);
        Assert.Same(decoy, m.AiTarget);
        Assert.Same(decoy, m.ActSelf.Target);
        Assert.Same(hero, m.ActTarget);                                // 缺省目标还在

        decoy.IsAlive = false;
        Assert.Same(hero, m.AiTarget);                                 // 指定的死了,回缺省
        decoy.IsAlive = true;
        Assert.Same(decoy, m.AiTarget);                                // 指定目标一直记着(死而复生不当事)
    }

    [Fact]
    public void ADesignatedTargetCanBeReplacedByAnotherOne()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        var a = new Aim(100);
        var b = new Aim(200);
        m.ActSelf.SetAiTarget(a, true);
        m.ActSelf.SetAiTarget(b, true);
        Assert.Same(b, m.AiTarget);
    }

    [Fact]
    public void ASoftTargetOnlyFillsAMissingOrDeadTarget()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        var hero = new Aim(300);
        var other = new Aim(500);

        m.ActSelf.SetAiTarget(other, false);                           // 还没有目标:补上
        Assert.Same(other, m.AiTarget);
        Assert.Null(m.DesignatedTarget);                               // 软设置动的是缺省目标

        m.ActTarget = hero;
        m.ActSelf.SetAiTarget(other, false);                           // 现有目标有效:不换
        Assert.Same(hero, m.AiTarget);

        hero.IsAlive = false;
        m.ActSelf.SetAiTarget(other, false);                           // 现有目标死了:换
        Assert.Same(other, m.AiTarget);
    }

    [Fact]
    public void ASoftTargetDoesNotReplaceALiveDesignatedTarget()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        var hero = new Aim(300);
        var decoy = new Aim(500);
        var third = new Aim(600);
        m.ActTarget = hero;
        m.ActSelf.SetAiTarget(decoy, true);
        m.ActSelf.SetAiTarget(third, false);
        Assert.Same(decoy, m.AiTarget);                                // 指定目标还活着:缺省目标有效与否都不换
        Assert.Same(hero, m.ActTarget);
    }

    [Fact]
    public void ANullTargetChangesNothing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        var hero = new Aim(300);
        m.ActTarget = hero;
        m.ActSelf.SetAiTarget(null, true);
        m.ActSelf.SetAiTarget(null, false);
        Assert.Same(hero, m.AiTarget);
        Assert.Null(m.DesignatedTarget);
    }

    // ------------------------------------------------------------------ 脚本接口与造怪

    [Fact]
    public void TheScriptSwitchesReachTheMonster()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        m.ActSelf.SetFacingFollowsTarget(false);
        m.ActSelf.SetAttackFacing(true);
        Assert.False(m.FacesTarget);
        Assert.True(m.AttackFacing);
        m.ActSelf.SetFacingFollowsTarget(true);
        m.ActSelf.SetAttackFacing(false);
        Assert.True(m.FacesTarget);
        Assert.False(m.AttackFacing);
    }

    [Fact]
    public void TheBuilderHandsTheTwoBirthValuesFromTheDefinitionToTheMonster()
    {
        using var arena = new RoomCombatTests.Arena(System.Array.Empty<(int X, int Y)>());
        var builder = new RoomMonsterBuilder(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf)), MonsterCatalog.Read(arena.Assets),
            arena.Hero, 1120, 1, 0, null, null);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");

        MonsterActor plain = builder.Build(0, spawn);                  // 编号 1 的 .mob 没写这两个标签:总开关开、逐帧面朝关
        Assert.True(plain.FacesTarget);
        Assert.False(plain.AttackFacing);

        var other = new RoomMonsterSpawn(2, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");
        MonsterActor written = builder.Build(1, other);                // 编号 2 的 .mob 写了 [change direction by target] 0 与 [is direction to move] 1
        Assert.False(written.FacesTarget);
        Assert.True(written.AttackFacing);
    }
}
