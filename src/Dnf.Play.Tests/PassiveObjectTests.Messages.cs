using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 消息族行为落到怪物与被动对象身上:[SEND DO BEHAVIOR] 把"执行第 n 组"交给目标当前动作的运行器(下一次更新执行)、
/// [CAMOUFLAGE] 在怪物身上记状态与时限、[SEND MESSAGE] 只有带 AI 的怪物才接而且只计数、[APPEND HIT] 经会话打到主角。
/// 数据沿用本类的合成素材:被动对象 <see cref="AvatarFixture.ActSpawnerId"/>(出生时放一个 <see cref="AvatarFixture.ActGraveId"/>,是它唯一的一组)、
/// <see cref="AvatarFixture.ActShotId"/>(第 0 组起 X 速度 200,第 1 组切自定义动作 0);怪物 shoot(帧 1 放 <see cref="AvatarFixture.ActShotId"/> 的是它的第 1 组)。
/// </summary>
public partial class PassiveObjectTests
{
    private static readonly IWalkArea MsgFree = new RectUnionArea(new List<WorldRect>());

    private static void MsgRun(MonsterActor m, int ms)
    {
        for (int t = 0; t < ms; t += 10)
        {
            m.Update(10, 0, 0, false, MsgFree);
        }
    }

    private static ScriptLine ML(string tag, params object[] values) => new ScriptLine(tag, values
        .Select(v => v is string s ? new ScriptValue(ScriptValueKind.String, 0, s) : new ScriptValue(ScriptValueKind.Int, Convert.ToInt32(v), null)).ToArray());

    /// <summary>触发器第 0 帧对 [ALL ENEMY] 做第 0 组的程序,组里是 <paramref name="behavior"/>。</summary>
    private static ActProgram MsgProgram(params ScriptLine[] behavior)
    {
        var lines = new List<ScriptLine>
        {
            ML("[TRIGGER]"), ML("[FRAME]", 0, 0), ML("[WHICH]"), ML("[ALL ENEMY]"), ML("[DO BEHAVIOR]"), ML("[CHECKUP OBJECT]", 0), ML("[/TRIGGER]"), ML("[BEHAVIOR]"),
        };
        lines.AddRange(behavior);
        lines.Add(ML("[/BEHAVIOR]"));
        return ActProgram.Compile("t.act", ActParser.Parse(lines.ToArray()));
    }

    /// <summary>把 <see cref="Targets"/> 当作 [ALL ENEMY] 的宿主;其余能力都是空的。</summary>
    private sealed class MsgSetHost : IActHost
    {
        public List<IActEntity> Targets { get; } = new();

        public int Random(int n) => 0;

        public long NowMs => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            if (selector == ActSelector.AllEnemy)
            {
                into.AddRange(Targets);
            }
        }
    }

    // ------------------------------------------------------------------ [SEND DO BEHAVIOR] 的收件:被动对象

    [Fact]
    public void APassiveObjectRunsAMessagedGroupOnItsNextUpdateNotOnTheSpot()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActSpawnerId);
        o.Update(10);                                                  // 出生那次 [ON SET ACTION]:放一个
        Assert.Equal(new[] { AvatarFixture.ActGraveId }, rig.Host.Spawns.Select(s => s.Index).ToArray());

        Assert.True(((IActGroupReceiver)o).PostActGroup(0));
        Assert.Single(rig.Host.Spawns);                                // 收下了,还没执行
        o.Update(10);
        Assert.Equal(new[] { AvatarFixture.ActGraveId, AvatarFixture.ActGraveId }, rig.Host.Spawns.Select(s => s.Index).ToArray());
        Assert.Equal((o.X, o.Y, o.Z), (rig.Host.Spawns[1].X, rig.Host.Spawns[1].Y, rig.Host.Spawns[1].Z));   // 偏移全 0:落在对象自己的位置
        Assert.Equal((1, 0), (o.ActGroupMessagesPosted, o.ActGroupMessagesDropped));
    }

    [Fact]
    public void AMessagedGroupIsTheScriptsGroupWithThatNumber()
    {
        using var rig = new Rig();
        PassiveObject shot = rig.Spawn(AvatarFixture.ActShotId);
        shot.Update(10);                                               // 第 0 组:起速度
        Assert.Equal(-200.0, shot.VelocityX);
        Assert.True(shot.PostActGroup(1));                             // 第 1 组:切自定义动作 0(与被打中同一个效果)
        Assert.Same(shot.Template.Basic, shot.Action);
        shot.Update(10);
        Assert.Same(shot.Template.Etc(0), shot.Action);
    }

    [Fact]
    public void APassiveObjectDropsAMessageWithoutARunnerOrAfterItsGone_AndIgnoresAnOutOfRangeGroup()
    {
        using var rig = new Rig();
        PassiveObject spawner = rig.Spawn(AvatarFixture.ActSpawnerId);
        spawner.Update(10);
        Assert.True(spawner.PostActGroup(5));                          // 组号越界:收下,什么都不执行
        Assert.True(spawner.PostActGroup(-1));
        spawner.Update(10);
        Assert.Single(rig.Host.Spawns);

        PassiveObject forever = rig.Spawn(AvatarFixture.ActForeverId); // 没有触发器 → 没有运行器
        Assert.False(forever.PostActGroup(0));
        Assert.Equal((0, 1), (forever.ActGroupMessagesPosted, forever.ActGroupMessagesDropped));

        PassiveObject shot = rig.Spawn(AvatarFixture.ActShotId);
        Advance(shot, 1100);                                           // 限时 1000 毫秒,到点消失
        Assert.True(shot.Finished);
        Assert.False(shot.PostActGroup(1));
        Assert.Equal(1, shot.ActGroupMessagesDropped);
    }

    // ------------------------------------------------------------------ [SEND DO BEHAVIOR] 的收件:怪物

    [Fact]
    public void AMessageToAnAttackingMonsterRunsTheGroupOnTheAttackRunnerAtTheNextStep()
    {
        using var arena = new ActArena("shoot", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(0, arena.Session.PassivesSpawned);                // 脚本自己的放射在帧 1(80 毫秒),还没到

        var receiver = (IActGroupReceiver)m.ActSelf;
        Assert.True(receiver.PostActGroup(1));                         // 第 1 组:放射一个 ActShotId
        Assert.Equal(0, arena.Session.PassivesSpawned);                // 不当场执行
        arena.Steps(1);
        Assert.Equal(1, arena.Session.PassivesSpawned);
        PassiveObject first = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ActShotId, first.Template.Index);
        Assert.Same(m, first.Owner);

        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 1) > 0);   // 帧 1 到了,脚本自己又放一个:消息没有顶掉它
        Assert.Equal(2, arena.Session.PassivesSpawned);
        Assert.Equal((1, 0), (m.ActGroupMessagesPosted, m.ActGroupMessagesDropped));
    }

    [Fact]
    public void AMessageToAMonsterThatIsIdleOrWhoseAttackHasNoTriggersIsDropped()
    {
        using var idle = new ActArena("shoot", useVitals: false);
        Assert.Equal(MonsterPhase.Wait, idle.Monster.Phase);
        Assert.False(((IActGroupReceiver)idle.Monster.ActSelf).PostActGroup(1));     // 待机,没有站立族程序
        Assert.Equal((0, 1), (idle.Monster.ActGroupMessagesPosted, idle.Monster.ActGroupMessagesDropped));
        idle.Steps(1);
        Assert.Equal(0, idle.Session.PassivesSpawned);

        using var plain = new ActArena("plain", useVitals: false);
        Assert.True(plain.Until(() => plain.Monster.Phase == MonsterPhase.Attack) > 0);
        Assert.False(((IActGroupReceiver)plain.Monster.ActSelf).PostActGroup(0));    // 出招了,但这一招没有触发器、没有运行器
        Assert.Equal(1, plain.Monster.ActGroupMessagesDropped);
    }

    [Fact]
    public void AMessageToAnIdleMonsterGoesToItsStandingActionRunner()
    {
        using var arena = new ActArena("f_stayfire", useVitals: false);               // 站立动作有触发器;它唯一的一组放一个 ActForeverId
        Assert.True(arena.Until(() => arena.Monster.ActiveFamily == MonsterActFamily.Stay) > 0);     // 先出一招,回到站立:站立族的运行器此刻是当前动作
        Assert.Equal(MonsterPhase.Wait, arena.Monster.Phase);
        Assert.True(((IActGroupReceiver)arena.Monster.ActSelf).PostActGroup(0));
        Assert.Equal(0, arena.Session.PassivesSpawned);
        arena.Steps(1);
        Assert.Equal(1, arena.Session.PassivesSpawned);                               // 条件(挨到火元素的打)没有成立,组照样执行:消息不经过触发器
        Assert.Equal(AvatarFixture.ActForeverId, Assert.Single(arena.Session.Passives).Template.Index);
    }

    // ------------------------------------------------------------------ [CAMOUFLAGE]

    [Fact]
    public void ACamouflagedMonsterRunsDownItsTimerAndEndsExactlyAtTheLimit()
    {
        using var rig = new Rig();
        MonsterActor m = rig.Owner;
        var target = (IActCamouflageTarget)m.ActSelf;
        Assert.False(m.Camouflaged);

        target.SetCamouflage(true, 300);
        Assert.Equal((true, 300.0, 1), (m.Camouflaged, m.CamouflageLeftMs, m.CamouflagesStarted));
        MsgRun(m, 290);
        Assert.Equal((true, 10.0), (m.Camouflaged, m.CamouflageLeftMs));               // 边界:差 10 毫秒
        MsgRun(m, 10);
        Assert.Equal((false, 0.0, 1), (m.Camouflaged, m.CamouflageLeftMs, m.CamouflagesExpired));
        MsgRun(m, 100);
        Assert.Equal(1, m.CamouflagesExpired);                                         // 只到点一次
    }

    [Fact]
    public void CamouflageOffClearsAtOnceAndTurningItOnAgainRestartsTheTimer_AndAZeroLimitNeverExpires()
    {
        using var rig = new Rig();
        MonsterActor m = rig.Owner;
        var target = (IActCamouflageTarget)m.ActSelf;

        target.SetCamouflage(true, 500);                                               // 第 1 次开
        MsgRun(m, 300);
        target.SetCamouflage(false, 0);
        Assert.Equal((false, 0.0), (m.Camouflaged, m.CamouflageLeftMs));
        MsgRun(m, 400);
        Assert.Equal((0, 1), (m.CamouflagesExpired, m.CamouflagesStarted));            // 关掉的不算到点,关也不算"开"

        target.SetCamouflage(true, 500);                                               // 第 2 次开
        MsgRun(m, 300);
        target.SetCamouflage(true, 500);                                               // 第 3 次:还开着又开,重新数 500
        MsgRun(m, 300);
        Assert.Equal((true, 200.0, 3), (m.Camouflaged, m.CamouflageLeftMs, m.CamouflagesStarted));

        target.SetCamouflage(true, 0);                                                 // 时限 0:不限时,一直开着
        MsgRun(m, 5000);
        Assert.Equal((true, 0.0), (m.Camouflaged, m.CamouflageLeftMs));
        Assert.Equal(0, m.CamouflagesExpired);
    }

    // ------------------------------------------------------------------ [APPEND HIT] 的拥有者是被动对象

    [Fact]
    public void AnAppendedHitFromAPassiveObjectUsesItsOwnAttackInfoAndTheChainsMonster()
    {
        using var arena = new ActArena("plain", useVitals: true);
        RoomCombatSession s = arena.Session;
        MonsterActor m = arena.Monster;
        PassiveTemplate template = s.Acts.Passive(AvatarFixture.ActShotId);
        var shot = new PassiveObject(template, m.ActSelf, m, s.ActHost, 0, 0, 0, false);

        ((IActMessageHost)s.ActHost).AppendHit(shot, s.HeroEntity, 5000f, 100f, 0f);      // 落点远在攻击框之外
        Assert.Equal(25L, s.HeroDamageTaken);                                              // 与同一个对象正常打中主角的伤害一样(那条测试已固定为 25)
        Assert.Equal((1, 0, 1), (s.AppendHitsLanded, s.AppendHitsSkipped, s.HeroHits));
        Assert.Equal(0, s.PassiveHits);                                                    // 强行命中不算对象自己的"打中过"
    }

    // ------------------------------------------------------------------ 脚本到真实怪物

    private static MonsterActor SecondMonster(Rig rig)
    {
        MonsterModel model = MonsterModel.Read(rig.Assets, AvatarFixture.BlobMobPath);
        return new MonsterActor(1, new RoomMonsterSpawn(1, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100, 222.5, 1500, 0);
    }

    [Fact]
    public void ACompiledCamouflageReachesARealMonsterAndAMessageNeedsTheMonsterToHaveAnAi()
    {
        using var rig = new Rig();
        MonsterActor target = SecondMonster(rig);
        var host = new MsgSetHost();
        host.Targets.Add(target.ActSelf);

        ActProgram program = MsgProgram(ML("[CAMOUFLAGE]", 1, 300), ML("[SEND MESSAGE]"), ML("[ATTACK_COMMAND]", 3));
        new ActRunner(program, rig.Owner.ActSelf, host).Update(0, 0.016f);
        Assert.Equal((true, 300.0), (target.Camouflaged, target.CamouflageLeftMs));
        Assert.Equal(0, target.AiMessagesReceived);                                    // 没有 AI 的怪物没有 AI 从属:消息不送

        target.SetDefaultAi(new MonsterBrain(200, 50, false, 2000, false, () => 0));
        new ActRunner(program, rig.Owner.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(1, target.AiMessagesReceived);
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 0 }, target.AiMessagesByFamily);           // 族 1 = 攻击命令
    }

    [Fact]
    public void NoMonsterIsAnAiCharacterSoChangeAiAndEtcCommandsFromMonstersDoNothing()
    {
        using var rig = new Rig();
        MonsterActor target = SecondMonster(rig);
        target.SetDefaultAi(new MonsterBrain(200, 50, false, 2000, false, () => 0));
        var host = new MsgSetHost();
        host.Targets.Add(target.ActSelf);

        var peer = (IActAiPeer)rig.Owner.ActSelf;
        Assert.False(peer.IsAiCharacter);
        Assert.False(peer.HasAiHost);
        Assert.False(peer.IsAiControlledCharacter);

        ActProgram program = MsgProgram(ML("[CHANGE AI]"), ML("[NORMAL]", 1, "synth_x.ai"), ML("[SEND MESSAGE]"), ML("[ETC_COMMAND]", 6, 1, 5000));
        new ActRunner(program, rig.Owner.ActSelf, host).Update(0, 0.016f);
        Assert.Equal(0, target.AiMessagesReceived);                                    // ETC 要求发送者是 AICharacter:发送者是普通怪物,整个目标跳过
    }
}
