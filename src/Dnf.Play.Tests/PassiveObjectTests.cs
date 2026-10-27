using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// .act 触发器进到房间里(M36-4,证据 E-130):怪物的出招 .act 放被动对象、设速度、切招、开关攻击框、拨帧、反转朝向;
/// 被动对象自己的 .act 起速度、打中主角后切自定义动作或销毁自己、再放别的对象、受重力、按时限与动画播完消失。数据全是合成的(见 <see cref="AvatarFixture.ActMobPath"/>):
/// 主角站在 (300, 100),站立时受击框是世界 x ∈ [288, 312]、y ∈ [94, 106]、z ∈ [0, 80];怪物站在 (340, 100) 朝左;
/// 被动对象的攻击框是以它为中心的 16 × 16 × 16(z 从它的高度起)。
/// </summary>
public class PassiveObjectTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    private static GameData DataOf(DnfAssets assets) => new GameData(new PvfScriptSource(assets.Pvf));

    private static readonly HitStunTable SyntheticStunTable = new HitStunTable(10, 330, 1000);

    private static DamageConstants Constants()
    {
        return new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
    }

    // ------------------------------------------------------------------ 没有会话:程序库与单个被动对象

    private sealed class FakeHost : IActHost
    {
        public List<(int Index, float X, float Y, float Z, bool Left)> Spawns { get; } = new();

        public int Random(int n) => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft) => Spawns.Add((index, x, y, z, facingLeft));

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
        }
    }

    private sealed class Victim : IActEntity
    {
        public ActEntityKind Kind => ActEntityKind.Character;
        public int ObjectIndex => 0;
        public float X => 0;
        public float Y => 0;
        public float Z => 0;
        public bool FacingLeft => false;
        public bool IsAlive => true;
        public IActEntity Target => null;
        public IActEntity Master => null;
        public int MotionVersion => 0;

        public void SetFacing(bool left)
        {
        }

        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
        }

        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
        }

        public void Destroy(bool force)
        {
        }

        public void AttackRect(ActAttackRectMode mode)
        {
        }

        public void SetFrame(int frame)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public ActLibrary Library { get; }
        public MonsterActor Owner { get; }
        public FakeHost Host { get; } = new();

        public Rig()
        {
            Assets = DnfAssets.Open(_fx.Root);
            Library = new ActLibrary(Assets, DataOf(Assets));
            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobMobPath);
            Owner = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100, 222.5, 1500, 0);
        }

        public PassiveObject Spawn(int id, bool facingLeft = true, double x = 100, double y = 50, double z = 0, IActEntity master = null)
        {
            PassiveTemplate template = Library.Passive(id);
            Assert.NotNull(template);
            return new PassiveObject(template, master ?? Owner.ActSelf, Owner, Host, x, y, z, facingLeft);
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    private static void Advance(PassiveObject o, int ms)
    {
        for (int t = 0; t < ms; t += 10)
        {
            o.Update(10);
        }
    }

    [Fact]
    public void TheLibraryCompilesAndCachesProgramsAndTemplates()
    {
        using var rig = new Rig();
        ActLibrary lib = rig.Library;
        Assert.Null(lib.Program(null));
        Assert.Null(lib.Program(""));
        Assert.Null(lib.Program("monster/blob/action/missing.act"));
        ActProgram shoot = lib.Program("monster/blob/action/actshoot.act");
        Assert.NotNull(shoot);
        Assert.Same(shoot, lib.Program("monster/blob/action/actshoot.act"));
        Assert.Equal(3, shoot.Triggers.Count);
        Assert.Equal(3, shoot.Behaviors.Count);
        Assert.Equal(1, lib.ProgramCount);

        Assert.Equal(0, lib.TemplateCount);
        PassiveTemplate shot = lib.Passive(AvatarFixture.ActShotId);
        Assert.NotNull(shot);
        Assert.Same(shot, lib.Passive(AvatarFixture.ActShotId));
        Assert.Equal(AvatarFixture.ActShotId, shot.Index);
        Assert.Equal(AvatarFixture.ShotAni, shot.Basic.AniPath);
        Assert.True(shot.Basic.HasTriggers);
        Assert.Equal(1, shot.EtcCount);
        Assert.NotNull(shot.Etc(0));
        Assert.Equal(AvatarFixture.ActBoomAni, shot.Etc(0).AniPath);
        Assert.Null(shot.Etc(1));
        Assert.Null(shot.Etc(-1));
        Assert.Equal(AvatarFixture.ShotDamageBonus, shot.Strike.DamageBonus);
        Assert.Equal(1000, shot.TimeLimitMs);
        Assert.True(shot.DestroyOnAnimationEnd);

        Assert.Null(lib.Passive(4242));            // 编号不存在
        Assert.Equal(1, lib.MissingTemplateCount);
        Assert.Equal(1, lib.TemplateCount);
        PassiveTemplate forever = lib.Passive(AvatarFixture.ActForeverId);
        Assert.Equal(-1, forever.TimeLimitMs);
        Assert.False(forever.DestroyOnAnimationEnd);
        Assert.False(forever.Basic.HasTriggers);   // 只有动画的 .act 没有触发器
    }

    [Fact]
    public void PreloadingAProgramReadsTheObjectsItCanSpawnAndTheirOwnSpawnsToALimitedDepth()
    {
        using var rig = new Rig();
        rig.Library.Preload(rig.Library.Program("monster/blob/action/actspawn9204.act"));
        // 帧 1 放 9204;9204 出生放 9203:两个模板都读好了
        Assert.Equal(2, rig.Library.TemplateCount);
        Assert.Equal(0, rig.Library.MissingTemplateCount);
        rig.Library.Preload(null);                  // 没有程序:什么也不做
        Assert.Equal(2, rig.Library.TemplateCount);
    }

    [Fact]
    public void ObjectValuesListEveryPossibleValueForPreloading()
    {
        Assert.Equal(new[] { 7 }, ActValue.Literal(7).Candidates().ToArray());
        Assert.Equal(new[] { 4, 8, 15 }, ActValue.Choice(new[] { 4, 8, 15 }).Candidates().ToArray());
        Assert.Equal(new[] { 10, 11, 12 }, ActValue.Range(10, 13).Candidates().ToArray());     // [min, max)
        Assert.Equal(new[] { 10, 11, 12 }, ActValue.Range(13, 10).Candidates().ToArray());
        Assert.Equal(new[] { 5 }, ActValue.Range(5, 5).Candidates().ToArray());
        Assert.Equal(64, ActValue.Range(0, 100000).Candidates().Count());
    }

    [Fact]
    public void AnObjectRunsItsSetActionTriggerOnTheFirstUpdateAndFliesAtTheScriptedSpeedMirroredByItsFacing()
    {
        using var rig = new Rig();
        PassiveObject left = rig.Spawn(AvatarFixture.ActShotId, facingLeft: true, x: 100, y: 50, z: 40);
        Assert.Same(left.Template.Basic, left.Action);
        Assert.Equal(0.0, left.VelocityX);
        left.Update(10);
        Assert.Equal(-200.0, left.VelocityX);        // X 轴速度随朝向取反
        Assert.Equal(98.0, left.X, 6);
        Assert.Equal(50.0, left.Y);
        Assert.Equal(40.0, left.Z);

        PassiveObject right = rig.Spawn(AvatarFixture.ActShotId, facingLeft: false, x: 100);
        right.Update(10);
        Assert.Equal(200.0, right.VelocityX);
        Assert.Equal(102.0, right.X, 6);
        Advance(right, 90);
        Assert.Equal(120.0, right.X, 4);
    }

    [Fact]
    public void AHitMakesTheObjectSwitchToItsCustomActionWhichStopsItAndEndsWithItsAnimation()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActShotId, x: 100);
        Advance(o, 30);
        Assert.Equal(-200.0, o.VelocityX);
        double xAtHit = o.X;
        o.NotifyHit(new Victim());                   // [ON ATTACKSUCCESS] → [SET ACTION] [CUSTOM] 0 [NOW]
        Assert.Same(o.Template.Etc(0), o.Action);
        Assert.Equal(0.0, o.ActionMs);               // 新动作从头放
        o.Update(10);                                // 新动作的 [ON SET ACTION] 把速度归零
        Assert.Equal(0.0, o.VelocityX);
        Advance(o, 100);
        Assert.Equal(xAtHit, o.X, 6);
        Assert.False(o.Finished);
        Advance(o, 200);                             // boom 三帧共 300 毫秒,播完(.obj 的 [on end of animation])就消失
        Assert.True(o.Finished);
        Assert.Empty(o.AttackBoxes());
    }

    [Fact]
    public void AnObjectHitsOnceUntilTheScriptResetsItsAttackRectAndTheRectCanBeSwitchedOff()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActRepeatId, facingLeft: true, x: 300, y: 100, z: 40);
        List<Aabb> boxes = o.AttackBoxes();
        Aabb box = Assert.Single(boxes);
        Assert.Equal((292, 308, 92, 108, 40, 56), (box.MinX, box.MaxX, box.MinY, box.MaxY, box.MinZ, box.MaxZ));
        o.NotifyHit(new Victim());
        Assert.Empty(o.AttackBoxes());               // 这一轮已经打中过
        var entity = (IActEntity)o;
        entity.AttackRect(ActAttackRectMode.Reset);
        Assert.Single(o.AttackBoxes());
        entity.AttackRect(ActAttackRectMode.Off);
        Assert.Empty(o.AttackBoxes());
        Assert.False(o.AttackRectOn);
        entity.AttackRect(ActAttackRectMode.On);
        Assert.Single(o.AttackBoxes());
        // 朝右:攻击框关于原点对称,换一个不对称的位置也是同一个 [292, 308](镜像不改变这个对称的框);高度跟着对象
        PassiveObject high = rig.Spawn(AvatarFixture.ActRepeatId, x: 300, y: 100, z: 100);
        Assert.Equal((100, 116), (high.AttackBoxes()[0].MinZ, high.AttackBoxes()[0].MaxZ));
    }

    [Fact]
    public void ARepeatObjectsScriptResetsItsAttackRectEveryTimeItsAnimationReturnsToFrameZero()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActRepeatId, x: 300, y: 100, z: 40);
        int ready = 0;
        for (int t = 0; t < 900; t += 10)
        {
            o.Update(10);
            if (o.AttackBoxes().Count > 0)
            {
                ready++;
                o.NotifyHit(new Victim());           // 每次能打就打一下
            }
        }
        // 动画两帧各 100 毫秒、一圈 200 毫秒;每圈回到第 0 帧时重置,所以 900 毫秒里能打 5 次(出生那圈,加 200、400、600、800)
        Assert.InRange(ready, 4, 5);
    }

    [Fact]
    public void ATimeLimitAndAnAnimationThatEndsBothEndAnObjectAndNothingElseDoesBeforeTheCap()
    {
        using var rig = new Rig();
        PassiveObject timed = rig.Spawn(AvatarFixture.ActRepeatId);
        Advance(timed, 990);
        Assert.False(timed.Finished);
        Advance(timed, 10);
        Assert.True(timed.Finished);                 // [time limite] 1000
        Assert.Empty(timed.AttackBoxes());

        PassiveObject forever = rig.Spawn(AvatarFixture.ActForeverId);
        Advance(forever, PassiveObject.MaxLifeMs - 10);
        Assert.False(forever.Finished);              // 没有任何消失条件的对象最长活 MaxLifeMs
        Advance(forever, 10);
        Assert.True(forever.Finished);
    }

    [Fact]
    public void AZAxisSpeedLiftsAnObjectAndGravityBringsItBackToTheGround()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActFallId, z: 0);
        o.Update(10);
        Assert.Equal(300.0 - PassiveObject.Gravity * 0.01, o.VelocityZ, 6);
        double top = 0;
        for (int t = 10; t < 600; t += 10)
        {
            o.Update(10);
            top = Math.Max(top, o.Z);
        }
        Assert.InRange(top, 29.0, 32.0);             // 抛起 300 像素/秒、重力 1500:连续解最高 30 像素,10 毫秒一步的离散积分是 31.5
        Assert.Equal(0.0, o.Z);                      // 落回地面就停
        Assert.Equal(0.0, o.VelocityZ);
        Assert.False(o.Finished);
    }

    [Fact]
    public void AnObjectBornInTheAirWithoutAZSpeedHangsThere()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActShotId, z: 60);
        Advance(o, 200);
        Assert.Equal(60.0, o.Z);                     // 没有给 Z 方向速度的对象不受重力
    }

    [Fact]
    public void AnObjectsScriptCanSpawnOtherObjectsThroughTheHostAtItsOwnPosition()
    {
        using var rig = new Rig();
        PassiveObject spawner = rig.Spawn(AvatarFixture.ActSpawnerId, facingLeft: false, x: 200, y: 80, z: 5);
        Assert.Empty(rig.Host.Spawns);
        spawner.Update(10);                          // [ON SET ACTION]:在自己的位置放一个 9203
        Assert.Equal(new[] { (AvatarFixture.ActGraveId, 200f, 80f, 0f, false) }, rig.Host.Spawns.ToArray());
    }

    [Fact]
    public void ADeferredActionWaitsForTheCurrentAnimationToEndAndTakesPrecedenceOverTheEndDestroy()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActShotId, x: 100);
        o.Update(10);
        o.NotifyHit(new Victim());
        Assert.Same(o.Template.Etc(0), o.Action);
        // 脚本在 boom 里要求"播完再回基础动作"
        ((IActEntity)o).SetAction(ActActionKind.Stand, 0, false, true);
        Advance(o, 200);
        Assert.Same(o.Template.Etc(0), o.Action);    // 还没播完
        Advance(o, 110);
        Assert.False(o.Finished);                    // 没有被 [on end of animation] 收走
        Assert.Same(o.Template.Basic, o.Action);
        Assert.Equal(10.0, o.ActionMs, 6);           // 切回基础动作那一步之后又走了一步
    }

    [Fact]
    public void SetFrameMovesTheAnimationClockAndClampsToTheLastFrame()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActShotId);
        var entity = (IActEntity)o;
        int before = entity.MotionVersion;
        entity.SetFrame(1);
        Assert.Equal(100.0, o.ActionMs);
        Assert.Equal(1, o.CurrentFrame);
        Assert.Equal(before + 1, entity.MotionVersion);
        entity.SetFrame(99);
        Assert.Equal(100.0, o.ActionMs);             // 越界按最后一帧
        entity.SetFrame(-3);
        Assert.Equal(0.0, o.ActionMs);
    }

    [Fact]
    public void AnObjectIgnoresActionKindsItDoesNotHaveAndAMissingCustomAction()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActShotId);
        var entity = (IActEntity)o;
        int before = entity.MotionVersion;
        entity.SetAction(ActActionKind.Move, 0, true, false);
        entity.SetAction(ActActionKind.Attack, 1, true, false);
        entity.SetAction(ActActionKind.Custom, 7, true, false);      // 没有第 7 个自定义动作
        Assert.Equal(before, entity.MotionVersion);
        Assert.Same(o.Template.Basic, o.Action);
        entity.Destroy(false);
        Assert.True(o.Finished);
        Assert.False(entity.IsAlive);
        o.Update(10);                                                // 已经销毁的对象不再动
        Assert.Equal(100.0, o.X);
    }

    [Fact]
    public void AnObjectKnowsItsMasterAndSharesItsTarget()
    {
        using var rig = new Rig();
        var hero = new Victim();
        rig.Owner.ActTarget = hero;
        PassiveObject o = rig.Spawn(AvatarFixture.ActShotId);
        Assert.Same(rig.Owner.ActSelf, o.Master);
        Assert.Same(hero, ((IActEntity)o).Target);                   // 盯着创建者盯着的目标
        PassiveObject child = rig.Spawn(AvatarFixture.ActGraveId, master: o);
        Assert.Same(o, child.Master);
        Assert.Same(rig.Owner, child.Owner);
        Assert.Same(rig.Owner, MonsterActor.From(child));
        Assert.Same(rig.Owner, MonsterActor.From(rig.Owner.ActSelf));
        Assert.Null(MonsterActor.From(hero));
        Assert.Null(MonsterActor.From(null));
    }

    // ------------------------------------------------------------------ 会话:怪物出招的触发器

    /// <summary>一个迷你房间、主角与一只用 .act 出招的怪物(<see cref="AvatarFixture.ActMobPath"/>),按需接上程序库。</summary>
    private sealed class ActArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }

        /// <param name="frozenHero">主角被钉在原地(可走区只有脚下一小块):推不动,每一击都打在同一处。</param>
        /// <param name="ranged">给了就装成怪物的 [attack kind](出生 3 秒后才抽签,射程里 100% 出招);缺省走"够近就出招"的占位。</param>
        public ActArena(string mob, int monsterX = 340, bool useVitals = true, int hp = 1000, bool ranged = false, bool attach = true, bool frozenHero = false)
        {
            Assets = DnfAssets.Open(_fx.Root);
            var tiles = new List<TileFile>();
            for (int i = 0; i < 5; i++)
            {
                tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]));
            }
            var map = new RoomMap("map/test/arena.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                new List<int[]> { null, null, null, null }, null);
            var content = new RoomContent(map, null, null, null, null);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            var area = frozenHero ? new List<WorldRect> { new WorldRect(HeroX - 5, HeroY - 5, 10, 10) } : new List<WorldRect>();
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, area, HeroX, HeroY, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.ActMobPath(mob));
            var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX, HeroY, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, 1, 100, 222.5, 1500, 0) { PhysicalAttack = 40 };
            if (ranged)
            {
                Monster.SetAttackKinds(new[] { new AttackKindRecord(100f, 0f, 0f, true, 400, 0, 20, 0, 30, 0, 30, 0) }, 1u);
            }
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, 1, default, default, null, 0, -1f, SyntheticStunTable,
                useVitals ? new HeroVitals(1, hp, 50, 100, 0, 0) : null, useVitals ? Constants() : null);
            if (attach)
            {
                Session.AttachActs(new ActLibrary(Assets, DataOf(Assets)));
            }
            Session.DrainSounds(new List<SoundCue>());
        }

        public void Steps(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Session.Update(10, 0, 0, false, false);
            }
        }

        /// <summary>每步 10 毫秒推进到 <paramref name="done"/> 为真为止,返回用了几步;<paramref name="max"/> 步内没有就返回 -1。</summary>
        public int Until(Func<bool> done, int max = 500)
        {
            for (int i = 1; i <= max; i++)
            {
                Session.Update(10, 0, 0, false, false);
                if (done())
                {
                    return i;
                }
            }
            return -1;
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void AnAttackScriptSpawnsItsObjectAtTheScriptedFrameAndTheMonsterMovesWithItsScriptedSpeed()
    {
        using var arena = new ActArena("shoot", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasActs);
        int steps = arena.Until(() => arena.Session.PassivesSpawned > 0);
        Assert.InRange(steps, 8, 12);                             // 帧 1 从 80 毫秒起
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ActShotId, o.Template.Index);
        Assert.True(o.FacingLeft);                                // 朝向取怪物的
        Assert.Equal(40.0, o.Z);
        Assert.Equal(100.0, o.Y);
        Assert.InRange(o.X, m.X - 31.0, m.X - 28.5);              // 起点偏移 30,朝左镜像
        Assert.Same(m, o.Owner);
        Assert.Same(m.ActSelf, o.Master);
        Assert.Equal(-100.0, m.ActSpeedX);                        // 出招一开始(ON SET ACTION)就设了 X 速度,朝左取反
        Assert.True(m.X < 340 && m.X > 320);                      // 已经按这个速度走了一段
        Assert.Equal(1, arena.Session.PassivesSpawned);
        arena.Until(() => m.ActSpeedX == 0);                      // 帧 2 速度归零
        double stopped = m.X;
        arena.Steps(5);
        Assert.Equal(stopped, m.X);                               // 速度归零后不再走
        arena.Until(() => m.Phase != MonsterPhase.Attack);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
    }

    [Fact]
    public void AMonsterWithoutAnActLibraryOrWithoutTriggersSpawnsNothing()
    {
        using var detached = new ActArena("shoot", useVitals: false, attach: false);
        Assert.False(detached.Monster.HasActs);
        Assert.Null(detached.Session.Acts);
        detached.Until(() => detached.Monster.Phase == MonsterPhase.Attack);
        detached.Steps(60);
        Assert.Equal(0, detached.Session.PassivesSpawned);

        using var plain = new ActArena("plain", useVitals: false);
        Assert.False(plain.Monster.HasActs);                      // .act 只有动画、没有触发器
        plain.Steps(60);
        Assert.Equal(0, plain.Session.PassivesSpawned);

        using var attached = new ActArena("shoot", useVitals: false);
        attached.Session.AttachActs(null);                        // 撤掉
        Assert.False(attached.Monster.HasActs);
        Assert.Null(attached.Monster.ActTarget);
        attached.Steps(60);
        Assert.Equal(0, attached.Session.PassivesSpawned);
    }

    [Fact]
    public void AShotFliesAtItsScriptedSpeedHurtsTheHeroOnceThenPlaysItsBoomAndDisappears()
    {
        using var arena = new ActArena("shoot", monsterX: 500, ranged: true);
        int spawned = arena.Until(() => arena.Session.PassivesSpawned > 0, 900);
        Assert.True(spawned > 300, "出生 3 秒后才抽签出招");
        PassiveObject o = arena.Session.Passives[0];
        Assert.Equal(0, arena.Session.HeroHits);
        int flight = arena.Until(() => arena.Session.PassiveHits > 0, 300);
        // 起点约在 x ≈ 455 到 470、以 200 像素/秒向左飞,攻击框左缘越过主角受击框右缘(312)就打中
        Assert.InRange(flight, 55, 90);
        Assert.Equal(1, arena.Session.HeroHits);
        Assert.Equal(1, arena.Session.PassiveHits);
        // 怪物攻击力 40、[damage bonus] 25 → 基础伤害 50;主角防御力 100、怪物等级 1 → 防御率 0.5 → 25(与 MonsterThrowTests 同一套合成常量)
        Assert.Equal(25, arena.Session.HeroDamageTaken);
        Assert.Same(o.Template.Etc(0), o.Action);                 // [ON ATTACKSUCCESS] 切到自定义动作 0
        arena.Steps(5);
        Assert.Equal(0.0, o.VelocityX);
        arena.Steps(40);                                          // boom 三帧 300 毫秒,播完消失
        Assert.Empty(arena.Session.Passives);
        Assert.Equal(1, arena.Session.HeroHits);                  // boom 没有攻击框
    }

    [Fact]
    public void ARepeatObjectHitsTheHeroOncePerAnimationCycleBecauseItsScriptResetsTheAttackRect()
    {
        using var arena = new ActArena("spawn9202", frozenHero: true);
        arena.Until(() => arena.Session.PassivesSpawned > 0);
        int hitsAtSpawn = arena.Session.PassiveHits;
        arena.Steps(105);                                         // 寿命 1000 毫秒
        Assert.Empty(arena.Session.Passives);
        // 出生后头一圈打一次,之后每 200 毫秒回到第 0 帧重置一次、再打一次:共 5 次左右
        Assert.InRange(arena.Session.PassiveHits, 4, 6);
        Assert.Equal(arena.Session.PassiveHits, arena.Session.HeroHits);
        Assert.True(hitsAtSpawn <= 1);
        Assert.Equal(arena.Session.PassiveHits * 25L, arena.Session.HeroDamageTaken);
    }

    [Fact]
    public void AGraveObjectDestroysItselfOnTheHitAndDoesNotHitAgain()
    {
        using var arena = new ActArena("spawn9203", frozenHero: true);
        arena.Until(() => arena.Session.PassiveHits > 0);
        Assert.Equal(1, arena.Session.HeroHits);
        arena.Steps(2);
        Assert.Empty(arena.Session.Passives);                     // [ON ATTACKSUCCESS] → [DESTROY]
        arena.Steps(150);
        Assert.Equal(1, arena.Session.HeroHits);
    }

    [Fact]
    public void AnObjectCanSpawnAnotherObjectAndTheChainKeepsTheFirstMonsterAsTheOwner()
    {
        using var arena = new ActArena("spawn9204", useVitals: false);
        arena.Until(() => arena.Session.PassivesSpawned >= 2);
        Assert.Equal(2, arena.Session.PassivesSpawned);
        PassiveObject spawner = arena.Session.Passives[0];
        PassiveObject grave = arena.Session.Passives[1];
        Assert.Equal(AvatarFixture.ActSpawnerId, spawner.Template.Index);
        Assert.Equal(AvatarFixture.ActGraveId, grave.Template.Index);
        Assert.Same(spawner, grave.Master);
        Assert.Same(arena.Monster, grave.Owner);
        Assert.Equal(spawner.X, grave.X, 6);                      // 偏移 0:在放出它的对象的位置
        Assert.Equal(spawner.Y, grave.Y, 6);
        arena.Steps(120);                                         // 限时 1000 毫秒,都消失
        Assert.Empty(arena.Session.Passives);
    }

    [Fact]
    public void AZSpeedObjectRisesAboveTheFloorAndComesBackDown()
    {
        using var arena = new ActArena("spawn9205", useVitals: false);
        arena.Until(() => arena.Session.PassivesSpawned > 0);
        PassiveObject o = arena.Session.Passives[0];
        double top = 0;
        for (int i = 0; i < 70 && !o.Finished; i++)
        {
            arena.Steps(1);
            top = Math.Max(top, o.Z);
        }
        Assert.InRange(top, 69.0, 72.0);                          // 出生高度 40,抛起 300、重力 1500:再升约 30(离散积分 31.5)
        Assert.Equal(0.0, o.Z);                                   // 落回地面
    }

    [Fact]
    public void ObjectsDoNotHurtAHeroWhoHasNoVitalsOrIsDead()
    {
        using var noVitals = new ActArena("spawn9202", useVitals: false);
        noVitals.Steps(120);
        Assert.Equal(0, noVitals.Session.PassiveHits);
        Assert.Equal(1, noVitals.Session.PassivesSpawned);

        using var fragile = new ActArena("spawn9202", hp: 1, frozenHero: true);
        fragile.Until(() => fragile.Session.HeroDead);
        int hits = fragile.Session.PassiveHits;
        fragile.Steps(100);
        Assert.Equal(hits, fragile.Session.PassiveHits);          // 死了的主角不再挨打
    }

    [Fact]
    public void TheSessionCapsTheNumberOfLiveObjects()
    {
        using var arena = new ActArena("shoot", useVitals: false);
        arena.Until(() => arena.Session.PassivesSpawned > 0);
        IActHost host = arena.Session.ActHost;
        for (int i = 0; i < RoomCombatSession.MaxPassives + 10; i++)
        {
            host.SpawnPassive(arena.Monster.ActSelf, AvatarFixture.ActForeverId, 0, 0, 0, 0, true);
        }
        Assert.Equal(RoomCombatSession.MaxPassives, arena.Session.Passives.Count);
        host.SpawnPassive(arena.Monster.ActSelf, 4242, 0, 0, 0, 0, true);   // 编号不存在:什么也不放
        Assert.Equal(RoomCombatSession.MaxPassives, arena.Session.Passives.Count);
    }

    [Fact]
    public void TheHostResolvesSelectorsTheRunnerDoesNotKnow()
    {
        using var arena = new ActArena("shoot", useVitals: true);
        IActHost host = arena.Session.ActHost;
        var into = new List<IActEntity>();
        host.Resolve(arena.Monster.ActSelf, ActSelector.AllEnemy, 0, into);
        IActEntity hero = Assert.Single(into);
        Assert.Same(arena.Session.HeroEntity, hero);
        Assert.Equal(ActEntityKind.Character, hero.Kind);
        Assert.Equal((float)HeroX, hero.X);
        Assert.Equal((float)HeroY, hero.Y);
        Assert.True(hero.IsAlive);
        into.Clear();
        host.Resolve(arena.Monster.ActSelf, ActSelector.AllMonsterTeam, 0, into);
        Assert.Same(arena.Monster.ActSelf, Assert.Single(into));
        into.Clear();
        host.Resolve(arena.Monster.ActSelf, ActSelector.Passive, 0, into);
        Assert.Empty(into);
        host.Resolve(arena.Monster.ActSelf, ActSelector.LoadTarget, 0, into);   // 没接的选择器:什么也不加
        Assert.Empty(into);
        // 主角的"作用"一律不执行
        hero.SetSpeed(true, 5, true, 5, true, 5);
        hero.SetAction(ActActionKind.Stand, 0, true, false);
        hero.Destroy(true);
        hero.AttackRect(ActAttackRectMode.Off);
        hero.SetFrame(3);
        hero.SetFacing(true);
        Assert.Equal(0, hero.MotionVersion);
        Assert.True(hero.IsAlive);
        Assert.Null(hero.Target);
        Assert.Null(hero.Master);
        Assert.Equal(0, hero.ObjectIndex);
        Assert.False(hero.FacingLeft);
        Assert.Equal(0f, hero.Z);
    }

    [Fact]
    public void ASetActionAttackNowSwitchesToTheOtherAttackAndTheSecondOnesTriggersStartOver()
    {
        using var arena = new ActArena("chain", useVitals: false);
        MonsterActor m = arena.Monster;
        int steps = arena.Until(() => m.AttackIndex == 1);        // 帧 1:切到第 1 招
        Assert.InRange(steps, 8, 12);
        Assert.Equal(2L, m.SwingNumber);                          // 又进了一次出招阶段
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(0, arena.Session.PassivesSpawned);
        Assert.NotNull(m.CurrentActRunner);
        // 第 1 招(shoot)自己的触发器:它的帧 1 又过了 80 毫秒才放对象
        int spawn = arena.Until(() => arena.Session.PassivesSpawned > 0);
        Assert.InRange(spawn, 7, 11);
    }

    [Fact]
    public void ASetActionAttackWithEndWaitsForTheCurrentAnimationBeforeSwitching()
    {
        using var arena = new ActArena("chainend", useVitals: false);
        MonsterActor m = arena.Monster;
        arena.Until(() => m.PhaseMs >= 150);
        Assert.Equal(0, m.AttackIndex);                           // 帧 1 要求了 [END],这时还在第 0 招
        Assert.Equal(1L, m.SwingNumber);
        int steps = arena.Until(() => m.AttackIndex == 1);
        Assert.True(steps > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);               // 播完(320 毫秒)接着第 1 招,没有回待机
        Assert.Equal(2L, m.SwingNumber);
        arena.Until(() => m.Phase != MonsterPhase.Attack);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
    }

    [Fact]
    public void ASetActionStandEndsTheAttackEarlyAndStillStartsTheCooldown()
    {
        using var plain = new ActArena("plain", useVitals: false);
        plain.Until(() => plain.Monster.Phase == MonsterPhase.Attack);
        plain.Steps(15);
        Assert.Equal(MonsterPhase.Attack, plain.Monster.Phase);

        using var arena = new ActArena("stand", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        int ended = arena.Until(() => m.Phase != MonsterPhase.Attack);
        Assert.InRange(ended, 7, 11);                             // 帧 1(80 毫秒)就回待机
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        arena.Steps(100);                                         // 冷却 1500 毫秒:这一秒里不会再出招
        Assert.Equal(1L, m.SwingNumber);
    }

    [Fact]
    public void ASetFrameCanSkipTheFrameThatHoldsTheMeleeStrikeBox()
    {
        using var plain = new ActArena("plain", frozenHero: true);
        plain.Steps(50);
        Assert.Equal(1, plain.Session.HeroHits);                  // 对照:动画第 2 帧的攻击框打中一次

        using var skip = new ActArena("skip", frozenHero: true);
        MonsterActor m = skip.Monster;
        skip.Steps(10);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        int ended = skip.Until(() => m.Phase != MonsterPhase.Attack);
        Assert.InRange(ended, 5, 9);                              // 前面已走 10 步(100 毫秒);帧 1(80 毫秒)拨到帧 3(240 毫秒),再播 80 毫秒就结束,而不是 320 毫秒
        skip.Steps(50);
        Assert.Equal(0, skip.Session.HeroHits);                   // 攻击框那一帧被跳过
    }

    [Fact]
    public void AttackRectOffSuppressesTheMeleeStrikeAndAResetAllowsASecondOne()
    {
        using var off = new ActArena("rectoff", frozenHero: true);
        off.Steps(50);
        Assert.True(off.Monster.AttackRectOff || off.Monster.Phase != MonsterPhase.Attack);
        Assert.Equal(0, off.Session.HeroHits);

        using var reset = new ActArena("rectreset", frozenHero: true);
        reset.Steps(60);
        // 帧 2 打一次;帧 3 时脚本重置攻击框并拨回帧 2(限一次),所以同一次出招里又打一次
        Assert.Equal(2, reset.Session.HeroHits);
    }

    [Fact]
    public void AMeleeStrikeThatHitsTheHeroWakesTheScriptsOnAttackSuccessTrigger()
    {
        using var arena = new ActArena("onhit", frozenHero: true);
        MonsterActor m = arena.Monster;
        arena.Until(() => m.Phase == MonsterPhase.Attack);
        arena.Steps(5);
        Assert.Equal(0, arena.Session.HeroHits);
        Assert.Equal(0.0, m.ActSpeedX);                           // 还没打中:触发器不跑
        arena.Until(() => arena.Session.HeroHits > 0);
        Assert.Equal(1, arena.Session.HeroHits);
        Assert.Equal(-100.0, m.ActSpeedX);                        // 打中那一刻 [ON ATTACKSUCCESS] 的行为起了 X 速度(朝左取反)
        arena.Until(() => m.Phase != MonsterPhase.Attack);
        Assert.Equal(0.0, m.ActSpeedX);                           // 出招结束,脚本给的速度撤掉
    }

    [Fact]
    public void ASetDirectionReverseFlipsTheMonsterAtItsFrame()
    {
        using var arena = new ActArena("reverse", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.FacingLeft);
        arena.Until(() => m.Phase == MonsterPhase.Attack);
        Assert.True(m.FacingLeft);
        arena.Until(() => !m.FacingLeft);
        Assert.False(m.FacingLeft);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
    }

    [Fact]
    public void AZSpeedLiftsAMonsterAndItIsBackOnTheGroundWhenTheAttackEnds()
    {
        using var arena = new ActArena("jump", useVitals: false);
        MonsterActor m = arena.Monster;
        double top = 0;
        bool seenAttack = false;
        for (int i = 0; i < 80; i++)
        {
            arena.Steps(1);
            seenAttack |= m.Phase == MonsterPhase.Attack;
            top = Math.Max(top, m.Z);
            if (seenAttack && m.Phase != MonsterPhase.Attack)
            {
                break;
            }
        }
        Assert.InRange(top, 29.0, 32.0);                          // 抛起 300、重力 1500:最高约 30 像素(离散积分 31.5)
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(0.0, m.Z);                                   // 出招动画(320 毫秒)比落地(400 毫秒)先结束:收招时落回地面
        Assert.Equal(0.0, m.ActSpeedZ);
        arena.Steps(100);
        Assert.Equal(0.0, m.Z);
    }

    [Fact]
    public void ABeatenMonsterLeavesTheAttackAndTheScriptStopsDrivingIt()
    {
        using var arena = new ActArena("shoot", useVitals: false);
        MonsterActor m = arena.Monster;
        arena.Until(() => m.CurrentActRunner != null && m.ActSpeedX != 0);
        Assert.True(m.ActSpeedX != 0);
        m.ApplyHit(1, false, 0, false);                           // 出招时挨打:进受击,脚本的速度清零
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Assert.Equal(0.0, m.ActSpeedX);
        Assert.Null(m.CurrentActRunner);
        arena.Steps(5);
        Assert.Equal(0, arena.Session.PassivesSpawned);           // 受击打断,帧 1 的触发器没机会放对象
    }

    [Fact]
    public void ObjectsAreDrawnWhileTheyLive()
    {
        using var arena = new ActArena("shoot", useVitals: false);
        arena.Until(() => arena.Session.PassivesSpawned > 0);
        PassiveObject o = arena.Session.Passives[0];
        TownProjection projection = arena.Session.Room.Projection;
        SceneSprite sprite = o.Sprite(arena.Assets, projection);
        Assert.NotNull(sprite);
        arena.Steps(130);                                         // 限时 1000 毫秒 → 到点消失,不再画
        Assert.True(o.Finished || arena.Session.Passives.Count == 0);
        Assert.Null(o.Sprite(arena.Assets, projection));
    }
}
