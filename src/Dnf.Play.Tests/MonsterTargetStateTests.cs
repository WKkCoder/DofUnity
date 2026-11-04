using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Play;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>Play 层 [IS TARGET STATE] 与主角 +0x36b0 分支(M72,E-172)。</summary>
public sealed class MonsterTargetStateTests
{
    [Fact]
    public void MinusOneParamKeepsCharacterWhenHeroFocusIsActive()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets);
        ActProgram p = CompileCharacterFilter(-1);
        var hero = new StubCharacter { ActAiState = 0, ActTargetStateFocus = 8 };
        var host = new Host(hero);
        new ActRunner(p, m.ActSelf, host).Update(0, 0.016f);
        Assert.Contains("speed:3,-,-", hero.Log);
    }

    private static MonsterActor Blob(DnfAssets assets, double x = 100, double y = 200)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, 100, 222.5, 1500, 0);
    }

    [Fact]
    public void HeroActEntityForwardsOperatingValueFromCallback()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero town = TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 200, false, TownHero.DungeonRestTag);
        IActEntity hero = new HeroActEntity(town, () => true, aiState: () => 8);
        Assert.Equal(8, hero.ActAiState);
        Assert.Equal(-1, hero.ActTargetStateFocus);
    }

    [Fact]
    public void TargetStateFilterUsesActAiStateOnCharacterEntities()
    {
        ActProgram p = CompileCharacterFilter(8);
        var hero = new StubCharacter { ActAiState = 8 };
        var host = new Host(hero);
        new ActRunner(p, new StubOwner(), host).Update(0, 0.016f);
        Assert.Contains("speed:5,-,-", hero.Log);
    }

    private static ActProgram CompileCharacterFilter(int stateParam)
    {
        return ActProgram.Compile("t.act", ActParser.Parse(new[]
        {
            new ScriptLine("[TRIGGER]", Array.Empty<ScriptValue>()),
            new ScriptLine("[FRAME]", new[] { new ScriptValue(ScriptValueKind.Int, 0, null), new ScriptValue(ScriptValueKind.Int, 0, null) }),
            new ScriptLine("[WHICH]", Array.Empty<ScriptValue>()),
            new ScriptLine("[CHARACTER]", Array.Empty<ScriptValue>()),
            new ScriptLine("[CHECKUP]", Array.Empty<ScriptValue>()),
            new ScriptLine("[IS TARGET STATE]", new[] { new ScriptValue(ScriptValueKind.Int, stateParam, null) }),
            new ScriptLine("[/CHECKUP]", Array.Empty<ScriptValue>()),
            new ScriptLine("[DO BEHAVIOR]", Array.Empty<ScriptValue>()),
            new ScriptLine("[CHECKUP OBJECT]", new[] { new ScriptValue(ScriptValueKind.Int, 0, null) }),
            new ScriptLine("[/TRIGGER]", Array.Empty<ScriptValue>()),
            new ScriptLine("[BEHAVIOR]", Array.Empty<ScriptValue>()),
            new ScriptLine("[SET SPEED]", Array.Empty<ScriptValue>()),
            new ScriptLine("[X AXIS]", new[] { new ScriptValue(ScriptValueKind.Int, stateParam == -1 ? 3 : 5, null) }),
            new ScriptLine("[/SET SPEED]", Array.Empty<ScriptValue>()),
            new ScriptLine("[/BEHAVIOR]", Array.Empty<ScriptValue>()),
        }));
    }

    private sealed class StubOwner : IActEntity
    {
        public ActEntityKind Kind => ActEntityKind.Monster;
        public int ObjectIndex => 0;
        public float X => 0;
        public float Y => 0;
        public float Z => 0;
        public bool FacingLeft => false;
        public bool IsAlive => true;
        public IActEntity Target => null;
        public IActEntity Master => null;
        public int MotionVersion => 0;
        public int Hp => 1;
        public int HpMax => 1;
        public int Level => 1;
        public int ActAiState => 0;
        public int ActTargetStateFocus => -1;
        public int ActTargetStateHint => -1;
        public bool IsGrabbable => false;
        public bool HasActiveStatus(int status) => false;
        public void SetFacing(bool left) { }
        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz) { }
        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd) { }
        public void Destroy(bool force) { }
        public void AttackRect(ActAttackRectMode mode) { }
        public void SetFrame(int frame) { }
        public void SetDamageBox(ActDamageBoxMode mode) { }
        public void SetFloatingHeight(int height) { }
        public void SetUseHoming(bool on) { }
        public void SetHp(int hp) { }
        public bool Teleport(float x, float y, float z) => true;
        public void AddActCategory(int categoryId) { }
        public void RemoveActCategory(int categoryId) { }
        public bool HasActCategory(int categoryId) => false;
    }

    private sealed class StubCharacter : IActEntity
    {
        public List<string> Log { get; } = new();
        public int ActAiState { get; set; }
        public int ActTargetStateFocus { get; set; } = -1;
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
        public int Hp => 100;
        public int HpMax => 100;
        public int Level => 1;
        public int ActTargetStateHint => -1;
        public bool IsGrabbable => true;
        public bool HasActiveStatus(int status) => false;
        public void SetFacing(bool left) { }
        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz) => Log.Add("speed:" + vx + ",-,-");
        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd) { }
        public void Destroy(bool force) { }
        public void AttackRect(ActAttackRectMode mode) { }
        public void SetFrame(int frame) { }
        public void SetDamageBox(ActDamageBoxMode mode) { }
        public void SetFloatingHeight(int height) { }
        public void SetUseHoming(bool on) { }
        public void SetHp(int hp) { }
        public bool Teleport(float x, float y, float z) => true;
        public void AddActCategory(int categoryId) { }
        public void RemoveActCategory(int categoryId) { }
        public bool HasActCategory(int categoryId) => false;
    }

    private sealed class Host : IActHost
    {
        private readonly IActEntity _hero;

        public Host(IActEntity hero) => _hero = hero;

        public long NowMs => 0;

        public int Random(int n) => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            if (selector == ActSelector.Character)
            {
                into.Add(_hero);
            }
        }

        public IActEntity ResolvePlayableCharacter(IActEntity owner) => _hero;

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }
    }
}
