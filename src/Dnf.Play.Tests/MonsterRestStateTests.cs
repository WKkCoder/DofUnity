using System;
using System.Collections.Generic;
using System.Linq;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Game.Ai;
using Dnf.Game.Tests;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;
using Xunit;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物未决簇 1 在 Play 宿主层(<see cref="MonsterActor"/>、<see cref="MonsterScript"/>、<see cref="PassiveObject"/>、
/// <see cref="RoomMonsterBuilder"/>)上的消费点与状态机测试。全用合成脚本与合成编号,不含原版数据。
/// </summary>
public sealed class MonsterRestStateTests
{
    private static ScriptLine L(string tag, params object[] values) =>
        new(tag, Array.ConvertAll(values, v => v is int i
            ? new ScriptValue(ScriptValueKind.Int, i, null)
            : new ScriptValue(ScriptValueKind.String, 0, (string)v)));

    private static ActProgram Compile(params ScriptLine[] lines) =>
        ActProgram.Compile("rest.act", ActParser.Parse(lines));

    private static AiScript CompileAi(params object[] items)
    {
        var b = new TokenStreamBuilder();
        foreach (object item in items)
        {
            switch (item)
            {
                case string s when s.StartsWith('['):
                    b.Tag(s);
                    break;
                case string s:
                    b.Str(s);
                    break;
                case int i:
                    b.Int(i);
                    break;
                default:
                    throw new ArgumentException("不支持的标记类型");
            }
        }
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        AiScript script = AiParser.Parse(tokens, strings, new AiConstants());
        Assert.True(script.Clean, string.Join(";", script.Errors));
        return script;
    }

    private sealed class MiniHost : IActHost
    {
        public long NowMs { get; set; }
        public List<IActEntity> Entities { get; } = new();

        public int Random(int upperBound) => 0;

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            foreach (IActEntity e in Entities)
            {
                if (selector == ActSelector.Passive && e.Kind == ActEntityKind.Passive)
                {
                    into.Add(e);
                }
                else if (selector == ActSelector.Monster && e.Kind == ActEntityKind.Monster)
                {
                    into.Add(e);
                }
                else if (selector == ActSelector.AllOurTeam && e.Team == owner.Team)
                {
                    into.Add(e);
                }
            }
        }

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }
    }

    private sealed class MiniScriptSource : IScriptSource
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Put(string path, byte[] bytes) => _files[path] = bytes;

        public bool TryRead(string path, out byte[] data) => _files.TryGetValue(path, out data);
    }

    private static GameData MakeMobData(int mobCode, string mobRelPath, int? grabable)
    {
        var pool = new List<string>();
        var list = new TokenStreamBuilder(pool).Int(mobCode).Str(mobRelPath);
        var mob = new TokenStreamBuilder(pool)
            .Tag("[waiting motion]").Str("Animation_Blob/Stay.ani")
            .Tag("[move motion]").Str("Animation_Blob/Move.ani")
            .Tag("[damage motion 1]").Str("Animation_Blob/Damage1.ani")
            .Tag("[damage motion 2]").Str("Animation_Blob/Damage2.ani")
            .Tag("[down motion]").Str("Animation_Blob/Down.ani")
            .Tag("[attack motion]").Str("Animation_Blob/Attack1.ani");
        if (grabable.HasValue)
        {
            mob.Tag("[is grabable]").Int(grabable.Value);
        }
        var src = new MiniScriptSource();
        src.Put("monster/monster.lst", list.Bytes());
        src.Put("monster/" + mobRelPath, mob.Bytes());
        src.Put("stringtable.bin", mob.StringTableBytes());
        return new GameData(src);
    }

    private static MonsterActor MakeMonster(DnfAssets assets, int hp = 1000, double x = 300, double y = 100)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 10, hp, 200.0, 1500, 0);
    }

    private static PassiveObject MakePassive(DnfAssets assets, MonsterActor owner, IActHost host, ActProgram basicProgram = null, double z = 0)
    {
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        Dnf.Game.PassiveObjectDef def = data.GetPassiveObject(AvatarFixture.ActHomeInstantId);
        AniFile ani = owner.CurrentAni;
        var basic = new PassiveAction(basicProgram, ani, "basic.ani");
        var template = new PassiveTemplate(901, def, basic, Array.Empty<PassiveAction>(), default);
        return new PassiveObject(template, owner.ActSelf, owner, host, owner.X, owner.Y, z, false);
    }

    // ------------------------------------------------------------------ 可见性与可抓取 + RoomMonsterBuilder

    [Fact]
    public void MonsterAndPassiveStartWithVisibilityByteOneAndToggleViaSetVisibility()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = MakeMonster(assets);
        var host = new MiniHost();
        PassiveObject p = MakePassive(assets, m, host);

        // CollisionObject::init1 (0x007fb7a7) 初值 1
        Assert.Equal(1, m.ActVisibilityByte);
        Assert.Equal(1, m.ActSelf.ActVisibilityByte);
        Assert.Equal(1, p.ActVisibilityByte);
        Assert.Equal(1, ((IActEntity)p).ActVisibilityByte);

        ActProgram hide = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET VISIBLITY]", 0), L("[/BEHAVIOR]"));
        ActProgram show = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET VISIBLITY]", 1), L("[/BEHAVIOR]"));

        new ActRunner(hide, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(0, m.ActVisibilityByte);
        Assert.Equal(0, m.ActSelf.ActVisibilityByte);

        new ActRunner(show, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(1, m.ActVisibilityByte);

        new ActRunner(hide, p, host).Update(0, 0.016f);
        Assert.Equal(0, p.ActVisibilityByte);
        new ActRunner(show, p, host).Update(0, 0.016f);
        Assert.Equal(1, p.ActVisibilityByte);

        // 被动对象其余接口默认/空操作属性
        var pRest = (IActRestStateEntity)p;
        Assert.False(pRest.IsActGhost);
        Assert.Equal(0, pRest.ActChargedTimeMs);
        pRest.SetActGrabbable(false);
        pRest.SetActDamageCheck(true, 100);
        pRest.SetActUnbeatable(true, 100);
        Assert.False(pRest.TryBeginActChangeTeam(2, 100, 10, p, out int pOrig));
        Assert.Equal(0, pOrig);
        Assert.False(pRest.TryEndActChangeTeam(out int pEndOrig, out int pEndPct, out IActEntity pEndSrc));
        Assert.Equal(0, pEndOrig);
        Assert.Equal(0, pEndPct);
        Assert.Null(pEndSrc);

        // 怪物其余只读属性
        var mRest = (IActRestStateEntity)m.ActSelf;
        Assert.False(mRest.IsActGhost);
        Assert.Equal(0, mRest.ActChargedTimeMs);
        mRest.SetActPassType(2); // 怪物忽略 [SET PASSTYPE]
    }

    [Fact]
    public void MonsterGrabbableRespectsInitScriptToggleAndDeathAndRoomMonsterBuilderTag()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = MakeMonster(assets, hp: 100);
        var host = new MiniHost();

        Assert.True(m.IsActGrabbable);
        Assert.True(m.ActSelf.IsGrabbable);

        m.InitActGrabbable(false);
        Assert.False(m.IsActGrabbable);
        Assert.False(m.ActSelf.IsGrabbable);

        ActProgram setOn = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET GRABABLE]", 1), L("[/BEHAVIOR]"));
        ActProgram setOff = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET GRABABLE]", 0), L("[/BEHAVIOR]"));

        new ActRunner(setOn, m.ActSelf, host).Update(0, 0.016f);
        Assert.True(m.IsActGrabbable);
        Assert.True(m.ActSelf.IsGrabbable);

        // 死亡后即便标记为可抓也返回 false
        m.ApplyHit(1000, false, 0, false);
        Assert.False(m.IsAlive);
        Assert.False(m.IsActGrabbable);
        Assert.False(m.ActSelf.IsGrabbable);

        // 存活怪物可关闭
        MonsterActor m2 = MakeMonster(assets, hp: 100);
        new ActRunner(setOff, m2.ActSelf, host).Update(0, 0.016f);
        Assert.False(m2.IsActGrabbable);

        // RoomMonsterBuilder 读 .mob [is grabable]:缺省 1 -> true, 写 0 -> false, 写 1 -> true, 写 2 -> true
        using var arena = new RoomCombatTests.Arena(Array.Empty<(int, int)>());
        var catalog = MonsterCatalog.Read(assets);
        var spawn = new RoomMonsterSpawn(AvatarFixture.CategorySeedMobCode, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");

        var defaultBuilder = new RoomMonsterBuilder(assets, MakeMobData(AvatarFixture.CategorySeedMobCode, "blob/category.mob", null), catalog, arena.Hero, 1120, 1, 0, null, null);
        Assert.True(defaultBuilder.Build(1, spawn).IsActGrabbable);

        var offBuilder = new RoomMonsterBuilder(assets, MakeMobData(AvatarFixture.CategorySeedMobCode, "blob/category.mob", 0), catalog, arena.Hero, 1120, 1, 0, null, null);
        Assert.False(offBuilder.Build(2, spawn).IsActGrabbable);

        var onBuilder = new RoomMonsterBuilder(assets, MakeMobData(AvatarFixture.CategorySeedMobCode, "blob/category.mob", 1), catalog, arena.Hero, 1120, 1, 0, null, null);
        Assert.True(onBuilder.Build(3, spawn).IsActGrabbable);

        var twoBuilder = new RoomMonsterBuilder(assets, MakeMobData(AvatarFixture.CategorySeedMobCode, "blob/category.mob", 2), catalog, arena.Hero, 1120, 1, 0, null, null);
        Assert.True(twoBuilder.Build(4, spawn).IsActGrabbable);
    }

    // ------------------------------------------------------------------ [SET DAMAGE CHECK] + MonsterScript.DamageCheck

    [Fact]
    public void DamageCheckAccumulatesActualDamageInsideWindowResetsOnThresholdAndWiresIntoMonsterScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = MakeMonster(assets, hp: 5000);
        var walk = new RectUnionArea(new List<WorldRect>());
        var host = new MiniHost();

        // 初始未开启:受击不累计(ApplyHit 返回是否打死:非致死返回 false)
        Assert.False(m.DamageCheckActive);
        Assert.Equal(0, m.DamageCheckDurationMs);
        Assert.Equal(0, m.DamageCheckAccumulated);
        Assert.False(m.ApplyHit(300, false, 0, false));
        Assert.Equal(4700, m.Hp);
        Assert.Equal(0, m.DamageCheckAccumulated);

        // 开启 1000ms 窗口
        ActProgram openWin = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET DAMAGE CHECK]", 1, 1000), L("[/BEHAVIOR]"));
        ActProgram closeWin = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET DAMAGE CHECK]", 0, 500), L("[/BEHAVIOR]"));

        new ActRunner(openWin, m.ActSelf, host).Update(0, 0.016f);
        Assert.True(m.DamageCheckActive);
        Assert.Equal(1000, m.DamageCheckDurationMs);
        Assert.Equal(0, m.DamageCheckAccumulated);

        // 0 伤害不累计;1 点伤害(<=0 与 <=1 边界)正常累计
        Assert.False(m.ApplyHit(0, false, 0, false));
        Assert.Equal(0, m.DamageCheckAccumulated);
        Assert.False(m.ApplyHit(1, false, 0, false));
        Assert.Equal(1, m.DamageCheckAccumulated);

        m.Update(999, 100, 100, false, walk);
        Assert.False(m.ApplyHit(119, false, 0, false));
        Assert.Equal(120, m.DamageCheckAccumulated);

        // 到达精确边界 1000ms (>= durMs):不再累计
        m.Update(1, 100, 100, false, walk);
        Assert.False(m.ApplyHit(80, false, 0, false));
        Assert.Equal(120, m.DamageCheckAccumulated);

        // 阈值检查:121 不够 -> false 且不清累计
        Assert.False(m.CheckDamageThreshold(121));
        Assert.Equal(120, m.DamageCheckAccumulated);

        // 阈值 120 恰好够 -> true, 清累计并把计时器归零(若错置为 1 则推进 999ms 后已达 1000ms 将不再累计!)
        Assert.True(m.CheckDamageThreshold(120));
        Assert.Equal(0, m.DamageCheckAccumulated);
        m.Update(999, 100, 100, false, walk);
        Assert.False(m.ApplyHit(55, false, 0, false));
        Assert.Equal(55, m.DamageCheckAccumulated);
        // 触发一次归零再走后续 MonsterScript 验证(使计时器重新归零后可继续累计)
        Assert.True(m.CheckDamageThreshold(55));
        Assert.False(m.ApplyHit(55, false, 0, false));
        Assert.Equal(55, m.DamageCheckAccumulated);

        // 接通 MonsterScript: .ai 的 "damage check()" 走 DamageCheckState
        AiScript ai = CompileAi(
            "[think]", "[void]", "damage check()", 200,
            "[true]", "[return]", 0,
            "[false]", "[return]", -1,
            "[/think]");
        var script = new MonsterScript(_ => null, new[] { null, null, "ai/act.ai", null }, new[] { null, null, ai, null }, null, 1, () => 0);
        // 未接线时 DamageCheckState == null -> 返回 false -> 动作结果 -1(无出招)
        var view = new MonsterAiView { X = 300, Y = 100, FacingLeft = true, Hp = 1000, HpMax = 5000, Level = 10, Free = true, HasTarget = true, TargetX = 200, TargetY = 100 };
        Assert.Equal(-1, script.DecideAttack(view));

        m.SetScript(script);
        Assert.NotNull(script.DamageCheckState);
        Assert.Equal(-1, script.DecideAttack(view));             // 当前 55 < 200 -> false

        Assert.False(m.ApplyHit(145, false, 0, false));          // 55 + 145 = 200
        Assert.Equal(200, m.DamageCheckAccumulated);
        Assert.Equal(0, script.DecideAttack(view));              // 200 >= 200 -> true(出招 0) 并清零
        Assert.Equal(0, m.DamageCheckAccumulated);

        // 关闭窗口:Active=false, Dur=0, Accum=0
        Assert.False(m.ApplyHit(90, false, 0, false));
        Assert.Equal(90, m.DamageCheckAccumulated);
        new ActRunner(closeWin, m.ActSelf, host).Update(0, 0.016f);
        Assert.False(m.DamageCheckActive);
        Assert.Equal(0, m.DamageCheckDurationMs);
        Assert.Equal(0, m.DamageCheckAccumulated);
        Assert.False(m.ApplyHit(90, false, 0, false));
        Assert.Equal(0, m.DamageCheckAccumulated);
    }

    [Fact]
    public void DamageCheckCapsAtActualHpLossAndSaturatesAtIntMaxValue()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 1. 溢杀只算实际掉血(prevHp - Hp)
        MonsterActor m1 = MakeMonster(assets, hp: 70);
        ((IActRestStateEntity)m1.ActSelf).SetActDamageCheck(true, 1000);
        Assert.True(m1.ApplyHit(500, false, 0, false));
        Assert.Equal(0, m1.Hp);
        Assert.Equal(70, m1.DamageCheckAccumulated);

        // 2. 接近 int.MaxValue 时饱和截断不溢出成负数
        MonsterActor m2 = MakeMonster(assets, hp: int.MaxValue);
        ((IActRestStateEntity)m2.ActSelf).SetActDamageCheck(true, 1000);
        Assert.False(m2.ApplyHit(1_500_000_000, false, 0, false));
        Assert.Equal(1_500_000_000, m2.DamageCheckAccumulated);
        Assert.True(m2.ApplyHit(700_000_000, false, 0, false));
        Assert.Equal(int.MaxValue, m2.DamageCheckAccumulated);
    }

    // ------------------------------------------------------------------ [SET UNBEATABLE]

    [Fact]
    public void SetUnbeatableBlocksHitsAndDamageBoxesUntilStrictlyPastExpiryWithoutTouchingIsDamageBoxEnable()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = MakeMonster(assets, hp: 1000);
        var walk = new RectUnionArea(new List<WorldRect>());
        var host = new MiniHost();

        Assert.False(m.IsActUnbeatable);
        Assert.True(m.HasDamageBox);
        Assert.NotEmpty(m.DamageBoxes());
        Assert.True(m.ActSelf.IsActDamageBoxEnabled);

        // 开启 500ms 无敌
        ActProgram on500 = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET UNBEATABLE]"), L("[ON]", 500), L("[/BEHAVIOR]"));
        new ActRunner(on500, m.ActSelf, host).Update(0, 0.016f);

        Assert.True(m.IsActUnbeatable);
        Assert.False(m.HasDamageBox);
        Assert.Empty(m.DamageBoxes());
        Assert.True(m.ActSelf.IsActDamageBoxEnabled);            // 不改 [IS DAMAGEBOX ENABLE]
        Assert.False(m.ApplyHit(250, false, 0, false));
        Assert.Equal(1000, m.Hp);

        // 推进恰好 500ms: expire == clock -> 仍然无敌(客户端 0x0081f913: expire < now 才清)
        m.Update(500, 100, 100, false, walk);
        Assert.True(m.IsActUnbeatable);
        Assert.False(m.HasDamageBox);
        Assert.False(m.ApplyHit(250, false, 0, false));

        // 再推进 1ms(总 501 > 500): 自动解除无敌
        m.Update(1, 100, 100, false, walk);
        Assert.False(m.IsActUnbeatable);
        Assert.True(m.HasDamageBox);
        Assert.NotEmpty(m.DamageBoxes());
        Assert.False(m.ApplyHit(250, false, 0, false));
        Assert.Equal(750, m.Hp);

        // 手动 [OFF] 立即解除
        new ActRunner(on500, m.ActSelf, host).Update(0, 0.016f);
        Assert.True(m.IsActUnbeatable);
        ActProgram off = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET UNBEATABLE]"), L("[OFF]"), L("[/BEHAVIOR]"));
        new ActRunner(off, m.ActSelf, host).Update(0, 0.016f);
        Assert.False(m.IsActUnbeatable);
        Assert.True(m.HasDamageBox);

        // 边界 1: 出生时刻 durationMs == 1 -> expire == 1(!= 0) -> 推进 2ms 必须过期(杀死 !=0 变 !=1 的变异)
        MonsterActor mOne = MakeMonster(assets, hp: 500);
        ((IActRestStateEntity)mOne.ActSelf).SetActUnbeatable(true, 1);
        mOne.Update(1, 100, 100, false, walk);
        Assert.True(mOne.IsActUnbeatable);
        mOne.Update(1, 100, 100, false, walk);
        Assert.False(mOne.IsActUnbeatable);

        // 边界 2: 出生时刻 durationMs == 0 -> expire == 0 -> 永不过期(客户端 0x0081f8ef)
        MonsterActor mPerm = MakeMonster(assets, hp: 500);
        ((IActRestStateEntity)mPerm.ActSelf).SetActUnbeatable(true, 0);
        mPerm.Update(5000, 100, 100, false, walk);
        Assert.True(mPerm.IsActUnbeatable);

        // 边界 3: 怪物已死但尚未 Gone(Dying 阶段)时 ApplyHit 必须直接拒收返回 false
        MonsterActor mDying = MakeMonster(assets, hp: 50);
        Assert.True(mDying.ApplyHit(50, false, 0, false));
        Assert.False(mDying.IsAlive);
        Assert.Equal(MonsterPhase.Dying, mDying.Phase);
        Assert.False(mDying.ApplyHit(10, false, 0, false));

        // 边界 4: 怪物在倒地动画里已死(Hp==0, Phase==Hurt != Gone 且 != Dying):DamageBoxes/HasDamageBox 必须由 !IsAlive 拦截
        MonsterModel boxedDown = MonsterModel.Read(assets, AvatarFixture.BlobBoxedDownMobPath);
        var mDownDead = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 300, 100, 0, 1, 0, "[fixed]", "[normal]"), boxedDown, 10, 50, 200.0, 1500, 0);
        var kd = new KnockdownInput(100, true, false, false, 1, 1);
        Assert.True(mDownDead.ApplyHit(50, true, 100, false, default, kd));
        Assert.False(mDownDead.IsAlive);
        Assert.Equal(MonsterPhase.Hurt, mDownDead.Phase);
        Assert.False(mDownDead.HasDamageBox);
        Assert.Empty(mDownDead.DamageBoxes());
    }

    // ------------------------------------------------------------------ [SET WIDTH] & [SET PASSTYPE]

    [Fact]
    public void SetWidthAndSetPassTypeStoreDistinctValuesOnMonsterAndPassiveObject()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = MakeMonster(assets);
        var host = new MiniHost();
        PassiveObject p = MakePassive(assets, m, host);

        ActProgram widthProg = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET WIDTH]", 137, 61), L("[/BEHAVIOR]"));
        ActProgram passProg = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET PASSTYPE]"), L("[pass only character]"), L("[/BEHAVIOR]"));

        new ActRunner(widthProg, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(137, m.ActWidthX);
        Assert.Equal(61, m.ActWidthY);

        new ActRunner(widthProg, p, host).Update(0, 0.016f);
        Assert.Equal(137, p.ActWidthX);
        Assert.Equal(61, p.ActWidthY);

        Assert.Equal(0, p.ActPassType);
        new ActRunner(passProg, p, host).Update(0, 0.016f);
        Assert.Equal(7, p.ActPassType);
    }

    // ------------------------------------------------------------------ [CHANGE TEAM] & [DEFAULT TEAM]

    [Fact]
    public void MonsterChangeTeamAppliesHpMaxPercentExpiresOnWallClockAndRestoresViaDefaultTeam()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = MakeMonster(assets, hp: 1000);
        var walk = new RectUnionArea(new List<WorldRect>());
        var host = new MiniHost();

        // 先设基础覆盖队 42,证明换队结束恢复到换队前队号 42 而非硬编码 100
        m.ActSelf.SetActTeam(42);
        Assert.Equal(42, m.ActSelf.Team);

        // 1. 定时换队: team=7, dur=500ms, hpMax=+25%
        ActProgram timedChange = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[CHANGE TEAM]", 7, 500, 25), L("[/BEHAVIOR]"));
        new ActRunner(timedChange, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(7, m.ActSelf.Team);
        Assert.Equal(1, m.StatAppendages.Count);
        Assert.Equal(new StatChange(StatChangeTypes.HpMax, true, 25), m.StatAppendages.Items[0].Change);

        // 有效期内再次换队被拒绝,且 TryBeginActChangeTeam 出参 originalTeam 严格为 0
        ActProgram secondChange = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[CHANGE TEAM]", 9, 500, 50), L("[/BEHAVIOR]"));
        new ActRunner(secondChange, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(7, m.ActSelf.Team);
        Assert.Equal(1, m.StatAppendages.Count);
        Assert.False(((IActRestStateEntity)m.ActSelf).TryBeginActChangeTeam(11, 200, 10, m.ActSelf, out int busyOrig));
        Assert.Equal(0, busyOrig);

        // 499ms 未到期
        m.Update(499, 100, 100, false, walk);
        Assert.Equal(7, m.ActSelf.Team);

        // 再 1ms(满 500ms):到期自动还原为 42,属性附加物也到期清零
        m.Update(1, 100, 100, false, walk);
        Assert.Equal(42, m.ActSelf.Team);
        Assert.Equal(0, m.StatAppendages.Count);

        // 边界: durationMs == 1(杀死 >0 变 >1 的变异): 0.5ms 仍保持 6, 满 1.0ms 后过期还原为 42
        ActProgram oneMsChange = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[CHANGE TEAM]", 6, 1, 10), L("[/BEHAVIOR]"));
        new ActRunner(oneMsChange, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(6, m.ActSelf.Team);
        m.Update(0.5, 100, 100, false, walk);
        Assert.Equal(6, m.ActSelf.Team);
        m.Update(0.5, 100, 100, false, walk);
        Assert.Equal(42, m.ActSelf.Team);

        // 2. 不限时换队(dur=0):推进 10 秒也不自动还原,靠 [DEFAULT TEAM] 立即还原并移除属性附加物
        ActProgram untimedChange = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[CHANGE TEAM]", 5, 0, 30), L("[/BEHAVIOR]"));
        ActProgram defaultTeam = Compile(
            L("[TRIGGER]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[DEFAULT TEAM]"), L("[/BEHAVIOR]"));

        new ActRunner(untimedChange, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(5, m.ActSelf.Team);
        Assert.Equal(1, m.StatAppendages.Count);

        m.Update(10000, 100, 100, false, walk);
        Assert.Equal(5, m.ActSelf.Team);
        Assert.Equal(1, m.StatAppendages.Count);

        new ActRunner(defaultTeam, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(42, m.ActSelf.Team);
        Assert.Equal(0, m.StatAppendages.Count);

        // 未激活时 [DEFAULT TEAM] 安全无副作用,且 TryEndActChangeTeam 出参全为 0/null
        m.ActSelf.SetActTeam(19);
        new ActRunner(defaultTeam, m.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(19, m.ActSelf.Team);
        Assert.False(((IActRestStateEntity)m.ActSelf).TryEndActChangeTeam(out int endOrig, out int endPct, out IActEntity endSrc));
        Assert.Equal(0, endOrig);
        Assert.Equal(0, endPct);
        Assert.Null(endSrc);

        // PassiveObject 队号覆盖
        PassiveObject p = MakePassive(assets, m, host);
        Assert.Null(((IActEntity)p).OverrideActTeam);
        Assert.Equal(19, ((IActEntity)p).Team);
        ((IActEntity)p).SetActTeam(8);
        Assert.Equal(8, ((IActEntity)p).OverrideActTeam);
        Assert.Equal(8, ((IActEntity)p).Team);
    }

    // ------------------------------------------------------------------ [ON MOVE COLLISION] 宿主事件分发

    [Fact]
    public void MonsterWallClampAndZLandingAndPassiveZLandingFireOnMoveCollision()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var host = new MiniHost();

        // 1. 怪物出招撞 X 墙(axis=0)与 Y 墙(axis=1)与落地(axis=2)分别唤醒对应 [AXIS INDEX] 触发器;
        //    另附默认无 [AXIS INDEX] 触发器(仅 X/Y 允许,Z 关闭,但非 0/1/2 会因 seen=true 误触 -> 区分 axis==2 与 axis==3!)
        ActProgram attackProg = Compile(
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 1), L("[DO BEHAVIOR]"), L("[ME]", 1), L("[/TRIGGER]"),
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 2), L("[DO BEHAVIOR]"), L("[ME]", 2), L("[/TRIGGER]"),
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 3), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET WIDTH]", 101, 1), L("[/BEHAVIOR]"),
            L("[BEHAVIOR]"), L("[SET WIDTH]", 102, 2), L("[/BEHAVIOR]"),
            L("[BEHAVIOR]"), L("[SET WIDTH]", 103, 3), L("[/BEHAVIOR]"),
            L("[BEHAVIOR]"), L("[SET VISIBLITY]", 0), L("[/BEHAVIOR]"));

        // 限制可行走区域为 [290..310] x [90..110](WorldRect 参数为 x, y, width, height)
        var boxWalk = new RectUnionArea(new List<WorldRect> { new WorldRect(290, 90, 20, 20) });

        // 无障碍精准移动 1.0px(stepX == 1 / stepY == 1):必须实际移动且绝不触发碰壁(杀死 hitX=true 与 stepX!=1/stepY!=1 变异)
        MonsterActor mFree = MakeMonster(assets, x: 300, y: 100);
        mFree.SetActs(new[] { attackProg }, host);
        mFree.ActSelf.SetAction(ActActionKind.Attack, 0, true, false);
        mFree.ActSelf.SetSpeed(true, 100f, true, 100f, false, 0f);
        mFree.Update(10, 100, 100, false, boxWalk);
        Assert.Equal(301.0, mFree.X, 4);
        Assert.Equal(101.0, mFree.Y, 4);
        Assert.Equal(0, mFree.ActWidthX);
        Assert.Equal(1, mFree.ActVisibilityByte);

        // X 墙碰撞:位于 309 向右走 stepX=5(309+5=314 出界,而 309-5=304 在界内 -> 杀死 X+stepX 变 X-stepX 变异)
        MonsterActor mX = MakeMonster(assets, x: 309, y: 100);
        mX.SetActs(new[] { attackProg }, host);
        mX.ActSelf.SetAction(ActActionKind.Attack, 0, true, false);
        Assert.Equal(MonsterPhase.Attack, mX.Phase);
        mX.ActSelf.SetSpeed(true, 100f, false, 0f, false, 0f);
        mX.Update(50, 100, 100, false, boxWalk);
        Assert.Equal(309.0, mX.X, 4);
        Assert.Equal(101, mX.ActWidthX);
        Assert.Equal(1, mX.ActWidthY);

        // Y 墙碰撞:位于 109 向下走 stepY=5(109+5=114 出界,而 109-5=104 在界内 -> 杀死 Y+stepY 变 Y-stepY 变异)
        MonsterActor mY = MakeMonster(assets, x: 300, y: 109);
        mY.SetActs(new[] { attackProg }, host);
        mY.ActSelf.SetAction(ActActionKind.Attack, 0, true, false);
        mY.ActSelf.SetSpeed(false, 0f, true, 100f, false, 0f);
        mY.Update(50, 100, 100, false, boxWalk);
        Assert.Equal(109.0, mY.Y, 4);
        Assert.Equal(102, mY.ActWidthX);
        Assert.Equal(2, mY.ActWidthY);

        // 怪物起跳后落回 Z=0 -> 严格为 axis=2([AXIS INDEX] 2 触发,默认 X/Y 触发器不触发 -> 可见性仍为 1)
        MonsterActor mZ = MakeMonster(assets, x: 300, y: 100);
        mZ.SetActs(new[] { attackProg }, host);
        mZ.ActSelf.SetAction(ActActionKind.Attack, 0, true, false);
        mZ.ActSelf.SetSpeed(false, 0f, false, 0f, true, 120f);
        mZ.Update(20, 100, 100, false, new RectUnionArea(new List<WorldRect>()));
        Assert.True(mZ.Z > 0);
        for (int i = 0; i < 60 && mZ.Z > 0; i++)
        {
            mZ.Update(20, 100, 100, false, new RectUnionArea(new List<WorldRect>()));
        }
        Assert.Equal(0, mZ.Z);
        Assert.Equal(103, mZ.ActWidthX);
        Assert.Equal(3, mZ.ActWidthY);
        Assert.Equal(1, mZ.ActVisibilityByte);

        // 2. 被动对象起跳后落地(Z <= 0)严格派发 axis=2([AXIS INDEX] 2 触发,默认 X/Y 触发器不触发 -> 可见性仍为 1)
        ActProgram passiveLandProg = Compile(
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 2), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 1), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET WIDTH]", 202, 22), L("[/BEHAVIOR]"),
            L("[BEHAVIOR]"), L("[SET VISIBLITY]", 0), L("[/BEHAVIOR]"));
        MonsterActor mOwner = MakeMonster(assets);
        PassiveObject p = MakePassive(assets, mOwner, host, passiveLandProg, z: 0);
        ((IActEntity)p).SetSpeed(false, 0f, false, 0f, true, 120f);
        p.Update(20);
        Assert.True(p.Z > 0);
        for (int i = 0; i < 60 && p.Z > 0; i++)
        {
            p.Update(20);
        }
        Assert.Equal(0, p.Z);
        Assert.Equal(202, p.ActWidthX);
        Assert.Equal(22, p.ActWidthY);
        Assert.Equal(1, p.ActVisibilityByte);
    }

    [Fact]
    public void FamilyActiveAndProcRunnersAlsoReceiveOnMoveCollisionFromMonster()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var host = new MiniHost();

        ActProgram stayColl = Compile(
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET VISIBLITY]", 0), L("[/BEHAVIOR]"));
        ActProgram procColl = Compile(
            L("[TRIGGER]"), L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0), L("[/TRIGGER]"),
            L("[BEHAVIOR]"), L("[SET WIDTH]", 303, 33), L("[/BEHAVIOR]"));

        // 用小行走盒卡住 X 轴,怪物处在 Wait 族时用脚本速度撞墙触发 FamilyOnMoveCollision(0)
        var actsField = typeof(MonsterFamilyActs).GetField("_actions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var anyProp = typeof(MonsterFamilyActs).GetProperty("Any");
        var famActs = (MonsterFamilyActs)Activator.CreateInstance(typeof(MonsterFamilyActs), true);
        var arr = (PassiveAction[])actsField.GetValue(famActs);
        arr[(int)MonsterActFamily.Stay] = new PassiveAction(stayColl, null, "stay.act");
        arr[(int)MonsterActFamily.Proc] = new PassiveAction(procColl, null, "proc.act");
        anyProp.SetValue(famActs, true);

        MonsterActor m2 = MakeMonster(assets, x: 309, y: 100);
        m2.SetFamilyActs(famActs, host);
        var boxWalk = new RectUnionArea(new List<WorldRect> { new WorldRect(290, 90, 20, 20) });
        m2.Update(16, 100, 100, false, boxWalk);                 // 进入 Stay 族并启动 Stay + Proc 运行器
        Assert.Equal(MonsterActFamily.Stay, m2.ActiveFamily);
        m2.ActSelf.SetSpeed(true, 500f, false, 0f, false, 0f);
        m2.Update(50, 100, 100, false, boxWalk);                 // X 撞墙 -> Stay 与 Proc 都收到 OnMoveCollision(0)

        Assert.Equal(0, m2.ActVisibilityByte);
        Assert.Equal(303, m2.ActWidthX);
        Assert.Equal(33, m2.ActWidthY);
    }
}
