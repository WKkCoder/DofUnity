using System.Globalization;
using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// .act 触发器与行为运行期(ActProgram 编译 + ActRunner 执行)的测试。规则依据 E-130(客户端静态读码):
/// 逐帧运行器的三道闸、单次位、补帧、目标集合、各行为的执行语义。脚本内容全是合成的,不含原版内容。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ScriptValue I(int v) => new(ScriptValueKind.Int, v, null);

    private static ScriptValue S(string s) => new(ScriptValueKind.String, 0, s);

    private static ScriptLine L(string tag, params object[] values) =>
        new(tag, Array.ConvertAll(values, v => v switch
        {
            int i => I(i),
            string text => S(text),
            ScriptValue sv => sv,
            _ => throw new ArgumentException("不支持的值类型"),
        }));

    private static ActProgram P(params ScriptLine[] lines) => ActProgram.Compile("test.act", ActParser.Parse(lines));

    /// <summary>一个触发器块:[TRIGGER] 记录… [/TRIGGER]。</summary>
    private static ScriptLine[] Trigger(params ScriptLine[] body)
    {
        var list = new List<ScriptLine> { L("[TRIGGER]") };
        list.AddRange(body);
        list.Add(L("[/TRIGGER]"));
        return list.ToArray();
    }

    /// <summary>一个行为组:[BEHAVIOR] 行为… [/BEHAVIOR]。</summary>
    private static ScriptLine[] Behavior(params ScriptLine[] body)
    {
        var list = new List<ScriptLine> { L("[BEHAVIOR]") };
        list.AddRange(body);
        list.Add(L("[/BEHAVIOR]"));
        return list.ToArray();
    }

    private static ScriptLine[] Cat(params ScriptLine[][] parts) => parts.SelectMany(p => p).ToArray();

    private static ScriptLine[] Speed(int x) => new[] { L("[SET SPEED]"), L("[X AXIS]", x), L("[/SET SPEED]") };

    private class Entity : IActEntity
    {
        public ActEntityKind Kind { get; set; } = ActEntityKind.Monster;
        public int ObjectIndex { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public bool FacingLeft { get; set; }
        public bool IsAlive { get; set; } = true;
        public IActEntity Target { get; set; }
        public IActEntity Master { get; set; }
        public int MotionVersion { get; set; }
        public int Hp { get; set; }
        public int HpMax { get; set; }
        public List<string> Log { get; } = new();

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        public void SetFacing(bool left)
        {
            FacingLeft = left;
            Log.Add("face:" + (left ? "L" : "R"));
        }

        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
            Log.Add("speed:" + (hasX ? F(vx) : "-") + "," + (hasY ? F(vy) : "-") + "," + (hasZ ? F(vz) : "-"));
        }

        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
            Log.Add("action:" + kind + ":" + index + ":" + (now ? "now" : atEnd ? "end" : "-"));
            if (now)
            {
                MotionVersion++;
            }
        }

        public void Destroy(bool force)
        {
            Log.Add(force ? "destroy:force" : "destroy");
            IsAlive = false;
        }

        public void AttackRect(ActAttackRectMode mode) => Log.Add("rect:" + mode);

        public void SetFrame(int frame)
        {
            Log.Add("frame:" + frame);
            MotionVersion++;
        }

        public void SetDamageBox(ActDamageBoxMode mode) => Log.Add("box:" + mode);

        public void SetFloatingHeight(int height) => Log.Add("float:" + height);

        public void SetUseHoming(bool on) => Log.Add("homing:" + on);

        public bool Teleport(float x, float y, float z)
        {
            Log.Add("teleport:" + F(x) + "," + F(y) + "," + F(z));
            X = x;
            Y = y;
            Z = z;
            return true;
        }

        /// <summary>M58(E-152):[SET POS FORCE] 的"全程"毫秒数;缺省 0 = 不知道,插值直接取终点。</summary>
        public int MotionDurationMs { get; set; }

        /// <summary>[SET POS FORCE] 每帧摆过来的位置(落点合法,照单全收;<see cref="RejectForce"/> 为真时模拟"不合法只改高度")。</summary>
        public List<(float x, float y, float z)> Forced { get; } = new();

        public bool RejectForce { get; set; }

        public void ForcePosition(float x, float y, float z)
        {
            Log.Add("force:" + F(x) + "," + F(y) + "," + F(z));
            Forced.Add((x, y, z));
            if (!RejectForce)
            {
                X = x;
                Y = y;
            }
            Z = z;
        }

        public int Level { get; set; }

        public int ActAiState { get; set; }

        public int ActTargetStateFocus { get; set; } = -1;

        public int ActTargetStateHint { get; set; } = -1;

        public void SetHp(int hp)
        {
            Log.Add("sethp:" + hp);
            Hp = hp;
            if (hp <= 0)
            {
                IsAlive = false;
            }
        }

        public List<(ActiveStatusEntry entry, IActEntity source)> Statuses { get; } = new();

        public void ApplyStatus(ActiveStatusEntry entry, IActEntity source)
        {
            Log.Add("status:" + entry.Kind);
            Statuses.Add((entry, source));
        }

        /// <summary>M52(E-145):按动作族写的 [TRIGGER] 落到拥有者上的请求。</summary>
        public List<(ActTriggerPool pool, int slot, int trigger, ActTriggerCommand command)> ActionTriggers { get; } = new();

        public void SetActionTrigger(ActTriggerPool pool, int slot, int trigger, ActTriggerCommand command)
        {
            Log.Add("trigger:" + pool + ":" + slot + ":" + trigger + ":" + command);
            ActionTriggers.Add((pool, slot, trigger, command));
        }

        /// <summary>M61(E-156):[IS GRABABLE] 看的"能不能被抓";没设就取活着,与接口默认一致。</summary>
        public bool? Grabbable { get; set; }

        public bool IsGrabbable => Grabbable ?? IsAlive;

        /// <summary>M61(E-156):[TRIGGER CHECK] 读到的各族触发器开关;没登记的返回 null(没有这一族动作)。</summary>
        public Dictionary<(ActTriggerPool pool, int slot, int trigger), bool> ActionTriggerStates { get; } = new();

        public bool? GetActionTrigger(ActTriggerPool pool, int slot, int trigger)
            => ActionTriggerStates.TryGetValue((pool, slot, trigger), out bool on) ? (bool?)on : null;

        /// <summary>M61(E-157):[SAVE TARGET OBJECT] 的槽表(挂在拥有者身上,跨运行器保留)。</summary>
        public Dictionary<int, IActEntity> SavedTargets { get; } = new();

        public void SetSavedTarget(int slot, IActEntity target) => SavedTargets[slot] = target;

        public IActEntity GetSavedTarget(int slot) => SavedTargets.TryGetValue(slot, out IActEntity t) ? t : null;

        /// <summary>M63(E-161):[APPENDAGE] 挂上的属性改变附加物请求,按到达顺序。</summary>
        public List<(StatChange change, int durationMs, IActEntity source)> StatAdds { get; } = new();

        /// <summary>M63(E-161):[DELETE APPENDAGE] 的删除请求,按到达顺序。</summary>
        public List<(StatChange change, IActEntity source)> StatRemoves { get; } = new();

        public void AddStatAppendage(StatChange change, int durationMs, IActEntity source)
        {
            Log.Add("stat+:" + change + ":" + durationMs);
            StatAdds.Add((change, durationMs, source));
        }

        public void RemoveStatAppendage(StatChange change, IActEntity source)
        {
            Log.Add("stat-:" + change);
            StatRemoves.Add((change, source));
        }

        /// <summary>M71(E-171):脚本运行时类别 bitset。</summary>
        public HashSet<int> ActCategories { get; } = new();

        public bool HasActCategory(int categoryId) => ActCategories.Contains(categoryId);

        public void AddActCategory(int categoryId)
        {
            if ((uint)categoryId < (uint)ActObjectCategories.InvalidId)
            {
                ActCategories.Add(categoryId);
                Log.Add("cat+:" + categoryId);
            }
        }

        public void RemoveActCategory(int categoryId)
        {
            if (ActCategories.Remove(categoryId))
            {
                Log.Add("cat-:" + categoryId);
            }
        }

        /// <summary>M74(E-221):[SET TEAM] 写入覆盖队号。</summary>
        public int? OverrideActTeam { get; set; }

        public void SetActTeam(int teamId)
        {
            OverrideActTeam = teamId;
            Log.Add("team:" + teamId);
        }

        /// <summary>M74(E-222):[IS VISIBLE] 可见性字节。</summary>
        public int ActVisibilityByte { get; set; }

        /// <summary>M74(E-223):[IS DAMAGEBOX ENABLE] 伤害盒开关(被动对象 cast 失败,不参与)。</summary>
        public bool IsActDamageBoxEnabled { get; set; } = true;
    }

    private sealed class Host : IActHost
    {
        public Queue<int> Randoms { get; } = new();
        public long NowMs { get; set; }

        /// <summary>M68(E-166):舞台的宽与高([IS END MAP]、[IS END MAP VERTICALLY] 用);缺省 0 = 不知道,与接口默认一致。</summary>
        public int MapWidth { get; set; }

        public int MapHeight { get; set; }
        public List<(IActEntity owner, int index, int level, float x, float y, float z, bool left)> Spawns { get; } = new();
        public Dictionary<ActSelector, List<IActEntity>> Sets { get; } = new();

        /// <summary>M61:每次向宿主要随机数时给的上界(洗牌每一轮的范围),测试用。</summary>
        public List<int> RandomBounds { get; } = new();

        public int Random(int n)
        {
            RandomBounds.Add(n);
            return Randoms.Count > 0 ? Randoms.Dequeue() % n : 0;
        }

        /// <summary>M61:为真时宿主的选择结果里连死了的对象也给(测运行器自己的"活着"过滤),缺省与真实宿主一样只给活着的。</summary>
        public bool ListDeadToo { get; set; }

        /// <summary>M72:0x10d92a2 主角对象;null 表示取不到主角。</summary>
        public IActEntity PlayableCharacter { get; set; }

        public IActEntity ResolvePlayableCharacter(IActEntity owner) => PlayableCharacter;

        public List<(int strength, int ms)> Shakes { get; } = new();

        public void Shake(int strength, int durationMs) => Shakes.Add((strength, durationMs));

        public List<(IActEntity master, int index, int level, float x, float y, float z, bool left, int lifeMs)> Summons { get; } = new();

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
            => Summons.Add((master, index, level, x, y, z, facingLeft, lifetimeMs));

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
            => Spawns.Add((owner, index, level, x, y, z, facingLeft));

        /// <summary>M57(E-151):[CHANGE ACTION FILE] 交给宿主的请求。</summary>
        public List<(IActEntity target, ActFileSlot slot, int index, string path, string from)> FileChanges { get; } = new();

        public void ChangeActionFile(IActEntity target, ActFileSlot slot, int index, string path, string fromProgram)
            => FileChanges.Add((target, slot, index, path, fromProgram));

        /// <summary>M69(E-167):[CHANGE ATTACKINFO] 交给宿主的请求(拥有者、路径或 null 表示 [DEFAULT]、写这条行为的程序路径)。</summary>
        public List<(IActEntity owner, string path, string from)> AttackChanges { get; } = new();

        public void ChangeAttackInfo(IActEntity owner, string path, string fromProgram)
            => AttackChanges.Add((owner, path, fromProgram));

        /// <summary>M69b(E-168):[CASTING] 排上读条时宿主收到的通知(拥有者、读条毫秒、到期要跑的行为组号)。</summary>
        public List<(IActEntity owner, int ms, int group)> CastsArmed { get; } = new();

        /// <summary>M69b(E-168):读条到期、行为组真的执行时宿主收到的通知(拥有者、行为组号)。</summary>
        public List<(IActEntity owner, int group)> CastsFired { get; } = new();

        public void CastArmed(IActEntity owner, int milliseconds, int group) => CastsArmed.Add((owner, milliseconds, group));

        public void CastFired(IActEntity owner, int group) => CastsFired.Add((owner, group));

        /// <summary>M70(E-169):[MOVE TO TARGET] 冲出去时宿主收到的通知(冲的对象、速度 n、计时毫秒)。</summary>
        public List<(IActEntity mover, int speed, int ms)> MovesStarted { get; } = new();

        /// <summary>M70(E-169):[MOVE TO TARGET] 计时到了、平面速度被清零的对象。</summary>
        public List<IActEntity> MovesStopped { get; } = new();

        public void MoveToTargetStarted(IActEntity mover, int speed, int durationMs) => MovesStarted.Add((mover, speed, durationMs));

        public void MoveToTargetStopped(IActEntity mover) => MovesStopped.Add(mover);

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            if (Sets.TryGetValue(selector, out List<IActEntity> list))
            {
                into.AddRange(ListDeadToo ? list : list.Where(e => e.IsAlive));
            }
        }

        /// <summary>M73(E-173):[REALDUNGEON CHECKUP] 三张键值表。</summary>
        public Dictionary<(int Table, int Key), int> RealDungeon { get; } = new();

        public int RealDungeonLookup(int table, int key) => RealDungeon.TryGetValue((table, key), out int v) ? v : 0;

        /// <summary>M73(E-173):[IS OURTEAMATTACKED] 合成受击登记(受害对象、攻击者、攻击者 getState)。</summary>
        public List<(IActEntity victim, IActEntity attacker, int attackerState)> OurTeamAttackMarks { get; } = new();

        public bool MatchesOurTeamAttacked(IActEntity owner, IActEntity candidate)
        {
            for (int i = 0; i < OurTeamAttackMarks.Count; i++)
            {
                (IActEntity victim, IActEntity attacker, int attackerState) mark = OurTeamAttackMarks[i];
                if (!ReferenceEquals(mark.victim, candidate))
                {
                    continue;
                }
                if (ReferenceEquals(mark.attacker, owner))
                {
                    continue;
                }
                if (mark.attacker.Team == owner.Team)
                {
                    continue;
                }
                if (mark.attackerState != 3)
                {
                    continue;
                }
                if (mark.attacker.Target != candidate)
                {
                    continue;
                }
                return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void CompileReadsMotionsTriggersAndBehaviors()
    {
        ActProgram p = P(Cat(
            new[] { L("[MOTION]"), L("[BASE ANI]", S("a.ani")), L("[SUB ANI]", S("b.ani")), L("[/MOTION]") },
            Trigger(L("[ENABLE]"), L("[OFF]"), L("[LIMIT]", 3), L("[FRAME]", 2, 4), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[DESTROY]")),
            Behavior(L("[CREATE PASSIVEOBJECT]"), L("[INDEX]", 9), L("[POS]", 1, 2, 3), L("[/CREATE PASSIVEOBJECT]"))));

        Assert.Equal("a.ani", p.BaseAniPath);
        Assert.Equal(new[] { "b.ani" }, p.Motions[0].SubAnis.ToArray());
        ActTrigger t = Assert.Single(p.Triggers);
        Assert.False(t.Enabled);
        Assert.Equal(3, t.Limit);
        Assert.True(t.Supported);
        Assert.Equal(new[] { ActRecordKind.Frame, ActRecordKind.DoBehavior }, t.Records.Select(r => r.Kind).ToArray());
        Assert.Equal(2, t.Records[0].A);
        Assert.Equal(4, t.Records[0].B);
        Assert.Equal(ActSelector.Me, t.Records[1].Selector);
        Assert.Equal(1, t.Records[1].Behavior.First);
        Assert.Equal(2, p.Behaviors.Count);
        Assert.Equal(ActBehaviorKind.Destroy, p.Behaviors[0].Items[0].Kind);
        ActBehavior create = p.Behaviors[1].Items[0];
        Assert.Equal(ActBehaviorKind.CreatePassive, create.Kind);
        Assert.Equal(9, create.Index.First);
        Assert.Equal(3, create.PosZ.First);
    }

    [Fact]
    public void ATriggerWithAnUnreadableRecordNeverFires()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IF]", "x"), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(10))));

        Assert.False(p.Triggers[0].Supported);
        Assert.Contains(p.Triggers[0].Records, r => r.Kind == ActRecordKind.Unsupported && r.Tag == "[IF]");
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        runner.Update(0, 0.016f);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void UnsupportedBehaviorsAreRecordedButTheRestOfTheGroupStillRuns()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[DIM]"), L("[/DIM]"), L("[SET SPEED]"), L("[X AXIS]", 7), L("[/SET SPEED]"))));

        Assert.Contains("[DIM]", p.Behaviors[0].UnsupportedTags);
        var e = new Entity();
        new ActRunner(p, e, new Host()).Update(0, 0.016f);
        Assert.Equal(new[] { "speed:7,-,-" }, e.Log.ToArray());
    }

    [Fact]
    public void ValueExpressionsFollowTheClientRules()
    {
        Assert.Equal(5, ActValue.Literal(5).Eval(n => n - 1));
        // [RANDOM] a b:区间 [min, max),不含上界;方向写反一样
        Assert.Equal(10, ActValue.Range(10, 20).Eval(n => 0));
        Assert.Equal(19, ActValue.Range(10, 20).Eval(n => n - 1));
        Assert.Equal(19, ActValue.Range(20, 10).Eval(n => n - 1));
        Assert.Equal(7, ActValue.Range(7, 7).Eval(n => throw new InvalidOperationException("零宽度区间不该取随机数")));
        // [RANDOM SELECT]:等概率取一个
        ActValue pick = ActValue.Choice(new[] { 4, 8, 15 });
        Assert.Equal(4, pick.Eval(n => 0));
        Assert.Equal(15, pick.Eval(n => n - 1));
        Assert.False(pick.IsConstant);
        Assert.True(ActValue.Choice(new[] { 3 }).IsConstant);
    }

    [Fact]
    public void ComparisonOperatorsIncludeTheMisspelledAliases()
    {
        Assert.True(ActCompares.TryParse("[=>]", out ActCompare ge));
        Assert.Equal(ActCompare.GreaterEqual, ge);
        Assert.True(ActCompares.TryParse("[=<]", out ActCompare le));
        Assert.Equal(ActCompare.LessEqual, le);
        Assert.False(ActCompares.TryParse("[FRAME]", out _));
        Assert.True(ActCompares.Test(3, ActCompare.GreaterEqual, 3));
        Assert.False(ActCompares.Test(3, ActCompare.Greater, 3));
        Assert.True(ActCompares.Test(2, ActCompare.Less, 3));
        Assert.True(ActCompares.Test(3, ActCompare.LessEqual, 3));
        Assert.True(ActCompares.Test(3, ActCompare.NotEqual, 4));
        Assert.True(ActCompares.Test(3, ActCompare.Equal, 3));
    }

    // ------------------------------------------------------------------ 帧与单次位

    private static ActProgram SpeedAtFrame(int a, int b, int speed) =>
        P(Cat(Trigger(L("[FRAME]", a, b), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(Speed(speed))));

    [Fact]
    public void AFrameTriggerFiresOncePerCycleAndRearmsAtFrameZero()
    {
        var e = new Entity();
        var runner = new ActRunner(SpeedAtFrame(2, 2, 100), e, new Host());

        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        Assert.Empty(e.Log);
        runner.Update(2, 0.016f);
        runner.Update(2, 0.016f);     // 同一帧停了好几次更新:单次位挡住重复
        runner.Update(3, 0.016f);
        Assert.Single(e.Log);

        runner.Update(0, 0.016f);     // 动画绕回第 0 帧:复位
        runner.Update(2, 0.016f);
        Assert.Equal(2, e.Log.Count);
    }

    [Fact]
    public void AFrameZeroTriggerFiresOncePerArrivalAtFrameZeroNotOncePerUpdate()
    {
        var e = new Entity();
        var runner = new ActRunner(SpeedAtFrame(0, 0, 100), e, new Host());
        for (int i = 0; i < 5; i++)
        {
            runner.Update(0, 0.016f);     // 第 0 帧停了好几次更新:复位只在到达时做一次(客户端的"这一圈复位过"标志)
        }
        Assert.Single(e.Log);
        runner.Update(1, 0.016f);
        runner.Update(1, 0.016f);
        Assert.Single(e.Log);
        runner.Update(0, 0.016f);         // 离开过第 0 帧再回来:新的一圈
        runner.Update(0, 0.016f);
        Assert.Equal(2, e.Log.Count);
    }

    [Fact]
    public void ALoopThatWrapsWithoutLandingOnFrameZeroStillStartsANewCycle()
    {
        var e = new Entity();
        var runner = new ActRunner(SpeedAtFrame(1, 1, 100), e, new Host());
        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        runner.Update(3, 0.016f);
        Assert.Single(e.Log);
        runner.Update(1, 0.016f);         // 一次更新跨过了第 0 帧(帧号变小):同样算新的一圈(推断)
        Assert.Equal(2, e.Log.Count);
    }

    [Fact]
    public void AFrameRangeFiresOnlyOnceWhenTheFrameEntersIt()
    {
        var e = new Entity();
        var runner = new ActRunner(SpeedAtFrame(2, 4, 100), e, new Host());
        for (int f = 0; f <= 5; f++)
        {
            runner.Update(f, 0.016f);
        }
        Assert.Single(e.Log);
    }

    [Fact]
    public void SkippedFramesAreReplayedInOrderBeforeTheCurrentFrame()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 3, 3), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Trigger(L("[FRAME]", 6, 6), L("[DO BEHAVIOR]"), L("[ME]", 2)),
            Behavior(Speed(1)),
            Behavior(Speed(3)),
            Behavior(Speed(6))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());

        runner.Update(0, 0.016f);
        runner.Update(5, 0.2f);       // 一次跳了五帧
        Assert.Equal(new[] { "speed:1,-,-", "speed:3,-,-" }, e.Log.ToArray());
        runner.Update(7, 0.1f);
        Assert.Equal("speed:6,-,-", e.Log[^1]);
        Assert.Equal(3, e.Log.Count);
    }

    [Fact]
    public void TheFirstUpdateAlsoReplaysFramesFromTheStart()
    {
        ActProgram p = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(Speed(9))));
        var e = new Entity();
        new ActRunner(p, e, new Host()).Update(3, 0.3f);
        Assert.Single(e.Log);
    }

    [Fact]
    public void ALimitCapsATriggerThatHasNoFrameRecord()
    {
        ActProgram p = P(Cat(Trigger(L("[LIMIT]", 2), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        for (int i = 0; i < 5; i++)
        {
            runner.Update(0, 0.016f);      // 没有 [FRAME]:每次更新都满足,靠次数上限停下
        }
        Assert.Equal(2, e.Log.Count);
        Assert.Equal(2, runner.FiredCount(0));
    }

    [Fact]
    public void BeginRearmsCountsAndSwitches()
    {
        ActProgram p = P(Cat(Trigger(L("[LIMIT]", 1), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        Assert.Single(e.Log);
        runner.Begin();
        runner.Update(0, 0.016f);
        Assert.Equal(2, e.Log.Count);
    }

    [Fact]
    public void ABehaviorCanSwitchTriggersOnAndOff()
    {
        ActProgram p = P(Cat(
            Trigger(L("[ENABLE]"), L("[OFF]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[TRIGGER]", 0), L("[ON]")),
            Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());

        runner.Update(0, 0.016f);     // 触发器 0 先求值(关着),触发器 1 再把它打开
        Assert.Empty(e.Log);
        Assert.True(runner.IsEnabled(0));
        runner.Update(0, 0.016f);
        Assert.Single(e.Log);
    }

    [Fact]
    public void ResettingATriggerRearmsItsCountAndOneShot()
    {
        ActProgram p = P(Cat(
            Trigger(L("[LIMIT]", 1), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 2, 2), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(5)),
            Behavior(L("[TRIGGER]", 0), L("[RESET]"))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        runner.Update(0, 0.016f);
        Assert.Single(e.Log);
        runner.Update(2, 0.016f);     // 触发器 1 重置了触发器 0
        Assert.Equal(0, runner.FiredCount(0));
        runner.Update(0, 0.016f);
        Assert.Equal(2, e.Log.Count);
    }

    [Fact]
    public void SwitchingActionNowAbortsTheRestOfTheTickAndKeepsTheTriggerArmed()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET ACTION]"), L("[STAND]"), L("[NOW]")),
            Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());

        runner.Update(0, 0.016f);
        runner.Update(0, 0.016f);
        // 行为改变了动画:没有置单次位(第二次更新又触发),后面的触发器这一拍不再求值
        Assert.Equal(new[] { "action:Stand:0:now", "action:Stand:0:now" }, e.Log.ToArray());
    }

    [Fact]
    public void CheckTimeIsAPeriodicTimer()
    {
        ActProgram p = P(Cat(Trigger(L("[CHECK TIME]", 500), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        var fired = new List<int>();
        for (int i = 1; i <= 6; i++)
        {
            int before = e.Log.Count;
            runner.Update(0, 0.2f);
            if (e.Log.Count > before)
            {
                fired.Add(i);
            }
        }
        Assert.Equal(new[] { 3, 6 }, fired.ToArray());
    }

    // ------------------------------------------------------------------ 目标集合

    [Fact]
    public void WhichSelectsAndCheckedNoLetsAnEmptySetFire()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[TARGET]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Trigger(L("[WHICH]"), L("[TARGET]"), L("[CHECKED NO]"), L("[<]", 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(5)),
            Behavior(Speed(8))));
        var owner = new Entity();
        var runner = new ActRunner(p, owner, new Host());

        runner.Update(0, 0.016f);      // 没有目标:第一条目标集合为空不触发,第二条 [CHECKED NO] [<] 1 通过
        Assert.Equal(new[] { "speed:8,-,-" }, owner.Log.ToArray());

        var hero = new Entity { Kind = ActEntityKind.Character };
        owner.Target = hero;
        owner.Log.Clear();
        runner.Update(0, 0.016f);
        Assert.Empty(owner.Log);       // 第二条这回因 [CHECKED NO] 失败
        Assert.Equal(new[] { "speed:5,-,-" }, hero.Log.ToArray());
    }

    [Fact]
    public void DistanceAndHeightFiltersNarrowTheSet()
    {
        ActProgram p = P(Cat(
            Trigger(
                L("[WHICH]"), L("[CHARACTER]"),
                L("[CHECKUP]"), L("[DISTANCE]"), L("[<=]", 100), L("[ZPOS]"), L("[<=]", 10), L("[/CHECKUP]"),
                L("[CHECKED NO]"), L("[>=]", 1),
                L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(5))));
        var owner = new Entity { X = 0, Y = 0 };
        var near = new Entity { Kind = ActEntityKind.Character, X = 60, Y = 80 };           // 距离 100:刚好在内
        var far = new Entity { Kind = ActEntityKind.Character, X = 500 };
        var high = new Entity { Kind = ActEntityKind.Character, X = 20, Z = 50 };
        var host = new Host();
        host.Sets[ActSelector.Character] = new List<IActEntity> { near, far, high };

        new ActRunner(p, owner, host).Update(0, 0.016f);

        Assert.Single(near.Log);
        Assert.Empty(far.Log);
        Assert.Empty(high.Log);
        Assert.Empty(owner.Log);
    }

    [Fact]
    public void IndexAndObjectTypeFiltersKeepTheMatchingObjects()
    {
        ActProgram p = P(Cat(
            Trigger(
                L("[WHICH]"), L("[PASSIVE]"),
                L("[CHECKUP]"), L("[IS INDEX]", 3, 5), L("[/IS INDEX]"), L("[IS NOT INDEX]", 5), L("[/IS NOT INDEX]"), L("[/CHECKUP]"),
                L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Trigger(
                L("[WHICH]"), L("[ALL ENEMY]"),
                L("[CHECKUP]"), L("[IS OBJECT TYPE]"), L("[CHARACTER]"), L("[MONSTER]"), L("[/IS OBJECT TYPE]"), L("[/CHECKUP]"),
                L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Behavior(Speed(5)),
            Behavior(Speed(6))));
        var owner = new Entity();
        var p3 = new Entity { Kind = ActEntityKind.Passive, ObjectIndex = 3 };
        var p5 = new Entity { Kind = ActEntityKind.Passive, ObjectIndex = 5 };
        var p4 = new Entity { Kind = ActEntityKind.Passive, ObjectIndex = 4 };
        var hero = new Entity { Kind = ActEntityKind.Character };
        var mob = new Entity { Kind = ActEntityKind.Monster };
        var host = new Host();
        host.Sets[ActSelector.Passive] = new List<IActEntity> { p3, p4, p5 };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { p3, hero, mob };

        new ActRunner(p, owner, host).Update(0, 0.016f);

        Assert.Equal(new[] { "speed:5,-,-" }, p3.Log.ToArray());
        Assert.Empty(p4.Log);
        Assert.Empty(p5.Log);
        Assert.Contains("speed:6,-,-", hero.Log);
        Assert.Contains("speed:6,-,-", mob.Log);
        Assert.DoesNotContain("speed:6,-,-", p3.Log);       // 被动对象不在 [CHARACTER] [MONSTER] 里
    }

    [Fact]
    public void LeftRightAndDirectionConditionsLookAtThePositionsAndTheFacing()
    {
        ActProgram p = P(Cat(
            Trigger(L("[IS DIRECTION LEFT]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[IS DIRECTION RIGHT]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Trigger(
                L("[WHICH]"), L("[CHARACTER]"), L("[CHECKUP]"), L("[IS LEFT]"), L("[/CHECKUP]"),
                L("[CHECKED NO]"), L("[>=]", 1), L("[DO BEHAVIOR]"), L("[ME]", 2)),
            Behavior(Speed(1)),
            Behavior(Speed(2)),
            Behavior(Speed(3))));
        var owner = new Entity { X = 100, FacingLeft = true };
        var hero = new Entity { Kind = ActEntityKind.Character, X = 40 };
        var host = new Host();
        host.Sets[ActSelector.Character] = new List<IActEntity> { hero };

        new ActRunner(p, owner, host).Update(0, 0.016f);

        // 朝左:1 号触发;英雄在左侧:3 号触发;朝右的 2 号不触发
        Assert.Equal(new[] { "speed:-1,-,-", "speed:-3,-,-" }, owner.Log.ToArray());
    }

    [Fact]
    public void SummonMasterAndTargetSelectorsSkipDeadObjects()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[SUMMON MASTER]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(5))));
        var master = new Entity();
        var owner = new Entity { Master = master };
        var runner = new ActRunner(p, owner, new Host());

        runner.Update(0, 0.016f);
        Assert.Single(master.Log);

        master.IsAlive = false;
        master.Log.Clear();
        runner.Begin();
        runner.Update(0, 0.016f);
        Assert.Empty(master.Log);
    }

    // ------------------------------------------------------------------ 事件

    [Fact]
    public void EventTriggersOnlyRunWhenTheEventArrivesAndOnceUntilTheNextCycle()
    {
        ActProgram p = P(Cat(
            Trigger(L("[ON ATTACKSUCCESS]"), L("[WHICH]"), L("[LAST ATTACKSUCCESS]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(5))));
        var owner = new Entity();
        var hero = new Entity { Kind = ActEntityKind.Character };
        var runner = new ActRunner(p, owner, new Host());

        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        Assert.Empty(hero.Log);               // 逐帧运行器不碰带事件记录的触发器

        runner.OnAttackSuccess(hero);
        Assert.Single(hero.Log);
        runner.OnAttackSuccess(hero);
        Assert.Single(hero.Log);              // 事件运行器入口检查单次位

        runner.Update(0, 0.016f);             // 回到第 0 帧后复位
        runner.OnAttackSuccess(hero);
        Assert.Equal(2, hero.Log.Count);
    }

    [Fact]
    public void OnSetActionAndOnDamageRunTheirOwnTriggersOnly()
    {
        ActProgram p = P(Cat(
            Trigger(L("[ON SET ACTION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ON DAMAGE]"), L("[WHICH]"), L("[LAST ATTACKER]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Trigger(L("[ON CREATE OBJECT]"), L("[DO BEHAVIOR]"), L("[ME]", 2)),
            Behavior(Speed(1)),
            Behavior(Speed(2)),
            Behavior(Speed(3))));
        var owner = new Entity();
        var attacker = new Entity { Kind = ActEntityKind.Character };
        var runner = new ActRunner(p, owner, new Host());

        runner.OnSetAction();
        Assert.Equal(new[] { "speed:1,-,-" }, owner.Log.ToArray());
        runner.OnDamage(attacker);
        Assert.Equal(new[] { "speed:2,-,-" }, attacker.Log.ToArray());
        runner.Update(0, 0.016f);
        Assert.Single(owner.Log);              // [ON CREATE OBJECT] 没有接线,永远不会触发
    }

    // ------------------------------------------------------------------ 行为

    private static ScriptLine[] CreateBehavior(params ScriptLine[] extra)
    {
        var body = new List<ScriptLine> { L("[CREATE PASSIVEOBJECT]"), L("[INDEX]", 7), L("[LEVEL]", 2), L("[POS]", 30, 10, 5) };
        body.AddRange(extra);
        body.Add(L("[/CREATE PASSIVEOBJECT]"));
        return Behavior(body.ToArray());
    }

    private static (Entity owner, Host host) Run(ScriptLine[] behavior, Entity owner, string selector = "[ME]", Action<Host> setup = null)
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L(selector, 0)),
            behavior));
        var host = new Host();
        setup?.Invoke(host);
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return (owner, host);
    }

    [Fact]
    public void CreatePassiveObjectMirrorsXWhenFacingLeft()
    {
        var right = new Entity { X = 100, Y = 50, Z = 0, FacingLeft = false };
        var (_, hostR) = Run(CreateBehavior(), right);
        var s = Assert.Single(hostR.Spawns);
        Assert.Equal((7, 2, 130f, 60f, 5f, false), (s.index, s.level, s.x, s.y, s.z, s.left));
        Assert.Same(right, s.owner);

        var left = new Entity { X = 100, Y = 50, FacingLeft = true };
        var (_, hostL) = Run(CreateBehavior(), left);
        var sl = Assert.Single(hostL.Spawns);
        Assert.Equal((70f, 60f, true), (sl.x, sl.y, sl.left));
    }

    [Fact]
    public void CreatePassiveObjectFlagsChangeTheBaseDirectionAndHeight()
    {
        // 作用于 [TARGET]:基准是被作用对象;朝向默认取被作用对象的
        var owner = new Entity { X = 0, FacingLeft = false };
        var hero = new Entity { Kind = ActEntityKind.Character, X = 300, Y = 20, Z = 40, FacingLeft = true };
        owner.Target = hero;

        var (_, a) = Run(CreateBehavior(), owner, "[TARGET]");
        var sa = Assert.Single(a.Spawns);
        Assert.Equal((270f, 30f, 5f, true), (sa.x, sa.y, sa.z, sa.left));

        // [USE MY DIRECTION]:朝向取创建者的(朝右),X 不取反
        owner = new Entity { X = 0, FacingLeft = false };
        owner.Target = hero;
        var (_, b) = Run(CreateBehavior(L("[USE MY DIRECTION]")), owner, "[TARGET]");
        var sb = Assert.Single(b.Spawns);
        Assert.Equal((330f, false), (sb.x, sb.left));

        // [USE MY BASEPOS]:基准是创建者自己;[USE OBJECT ZPOS]:Z 加基准对象的高度;朝向仍取被作用对象的(朝左),所以 X 向左偏
        owner = new Entity { X = 10, Y = 4, Z = 7, FacingLeft = false };
        owner.Target = hero;
        var (_, c) = Run(CreateBehavior(L("[USE MY BASEPOS]"), L("[USE OBJECT ZPOS]")), owner, "[TARGET]");
        var sc = Assert.Single(c.Spawns);
        Assert.Equal((10f - 30f, 14f, 12f, true), (sc.x, sc.y, sc.z, sc.left));
    }

    [Fact]
    public void CreatePassiveObjectMapPositionAndFixedDirectionAreAbsolute()
    {
        var owner = new Entity { X = 500, Y = 500, Z = 99, FacingLeft = true };
        var (_, a) = Run(CreateBehavior(L("[USE MAP POS]")), owner);
        var sa = Assert.Single(a.Spawns);
        Assert.Equal((30f, 10f, 5f), (sa.x, sa.y, sa.z));

        owner = new Entity { X = 100, FacingLeft = true };
        var (_, b) = Run(CreateBehavior(L("[FIX DIRECTION]"), L("[RIGHT]")), owner);
        var sb = Assert.Single(b.Spawns);
        Assert.Equal((130f, false), (sb.x, sb.left));

        owner = new Entity { X = 100, FacingLeft = false };
        var (_, c) = Run(CreateBehavior(L("[FIX DIRECTION]"), L("[LEFT]")), owner);
        var sc = Assert.Single(c.Spawns);
        Assert.Equal((70f, true), (sc.x, sc.left));
    }

    [Fact]
    public void CreatePassiveObjectReadsMissingPositionValuesAsZero()
    {
        static ScriptLine[] Create(params ScriptLine[] pos) => Behavior(
            new[] { L("[CREATE PASSIVEOBJECT]"), L("[INDEX]", 7) }.Concat(pos).Append(L("[USE MY DIRECTION]")).Append(L("[/CREATE PASSIVEOBJECT]")).ToArray());

        var owner = new Entity { X = 100, Y = 50, Z = 3, FacingLeft = false };
        var (_, two) = Run(Create(L("[POS]", 30, 10)), owner);              // 两个值:Z 缺,当 0(绝对高度 0)
        Assert.Equal((130f, 60f, 0f), (two.Spawns[0].x, two.Spawns[0].y, two.Spawns[0].z));
        var (_, one) = Run(Create(L("[POS]", 30)), new Entity { X = 100, Y = 50 });
        Assert.Equal((130f, 50f, 0f), (one.Spawns[0].x, one.Spawns[0].y, one.Spawns[0].z));
        var (_, none) = Run(Create(), new Entity { X = 100, Y = 50 });         // 没有 [POS]:就在基准点上
        Assert.Equal((100f, 50f, 0f), (none.Spawns[0].x, none.Spawns[0].y, none.Spawns[0].z));
        // 没有对象编号的放不出任何东西
        ActProgram p = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[CREATE PASSIVEOBJECT]"), L("[POS]", 1, 2, 3), L("[/CREATE PASSIVEOBJECT]"))));
        Assert.Equal(ActBehaviorKind.Unsupported, p.Behaviors[0].Items[0].Kind);
    }

    [Fact]
    public void CreatePassiveObjectEvaluatesRandomOffsetsPerSpawn()
    {
        ScriptLine[] beh = Behavior(
            L("[CREATE PASSIVEOBJECT]"), L("[INDEX]", 1), L("[POS]", 0), L("[RANDOM]", -100, 100), L("[RANDOM SELECT]", 4, 9), L("[/RANDOM SELECT]"),
            L("[/CREATE PASSIVEOBJECT]"));
        var owner = new Entity { X = 0, Y = 0 };
        var (_, host) = Run(beh, owner, setup: h =>
        {
            h.Randoms.Enqueue(5);       // [RANDOM] -100 100:-100 + 5
            h.Randoms.Enqueue(1);       // [RANDOM SELECT]:第二项
        });
        var s = Assert.Single(host.Spawns);
        Assert.Equal((-95f, 9f), (s.y, s.z));
    }

    [Fact]
    public void SetSpeedFlipsOnlyTheXAxisByTheFacingOfTheAffectedObject()
    {
        ScriptLine[] beh = Behavior(L("[SET SPEED]"), L("[X AXIS]", 200), L("[Y AXIS]", 30), L("[Z AXIS]", 400), L("[USE MY DIRECTION]"), L("[/SET SPEED]"));
        var right = new Entity { FacingLeft = false };
        Run(beh, right);
        Assert.Equal(new[] { "speed:200,30,400" }, right.Log.ToArray());

        var left = new Entity { FacingLeft = true };
        Run(beh, left);
        Assert.Equal(new[] { "speed:-200,30,400" }, left.Log.ToArray());

        // 只写 Z 轴:X 与 Y 不动
        var zOnly = new Entity { FacingLeft = true };
        Run(Behavior(L("[SET SPEED]"), L("[Z AXIS]", 500), L("[/SET SPEED]")), zOnly);
        Assert.Equal(new[] { "speed:-,-,500" }, zOnly.Log.ToArray());
    }

    [Fact]
    public void SetDirectionModesMatchTheClientsNumbering()
    {
        // 加载函数的字符串顺序:[LEFT] 0、[RIGHT] 1、[TO TARGET] 2、[REVERSE] 3、[RANDOM] 4、[MY DIRECTION] 7、[MY OPPOSITE DIRECTION] 8
        static Entity With(bool ownerLeft, bool targetLeft, float targetX, Entity hero = null)
        {
            var owner = new Entity { X = 0, FacingLeft = ownerLeft };
            var t = new Entity { X = targetX, FacingLeft = targetLeft, Target = hero };
            owner.Master = t;
            return owner;
        }

        ScriptLine[] Dir(string mode) => Behavior(L("[SET DIRECTION]"), L(mode));

        // 目标朝右、创建者朝左,作用于 [SUMMON MASTER](= 被作用对象)
        foreach ((string mode, bool expectLeft) in new[]
        {
            ("[LEFT]", true), ("[RIGHT]", false), ("[REVERSE]", true), ("[MY DIRECTION]", true), ("[MY OPPOSITE DIRECTION]", false),
        })
        {
            Entity owner = With(ownerLeft: true, targetLeft: false, targetX: 100);
            Run(Dir(mode), owner, "[SUMMON MASTER]");
            Assert.True(((Entity)owner.Master).FacingLeft == expectLeft, mode);
        }

        // [TO TARGET]:参照物(被作用对象自己的目标)在右边就朝右,在左边就朝左
        var hero = new Entity { Kind = ActEntityKind.Character, X = 500 };
        Entity o2 = With(ownerLeft: false, targetLeft: true, targetX: 100, hero);
        Run(Dir("[TO TARGET]"), o2, "[SUMMON MASTER]");
        Assert.False(((Entity)o2.Master).FacingLeft);
        hero.X = -50;
        Entity o3 = With(ownerLeft: false, targetLeft: false, targetX: 100, hero);
        Run(Dir("[TO TARGET]"), o3, "[SUMMON MASTER]");
        Assert.True(((Entity)o3.Master).FacingLeft);

        // [RANDOM]:宿主的随机数 0 朝左,1 朝右
        var host = new Host();
        host.Randoms.Enqueue(1);
        Entity o4 = With(ownerLeft: true, targetLeft: true, targetX: 0);
        ActProgram p = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[SUMMON MASTER]", 0)), Dir("[RANDOM]")));
        new ActRunner(p, o4, host).Update(0, 0.016f);
        Assert.False(((Entity)o4.Master).FacingLeft);
    }

    [Fact]
    public void SetActionPassesTheFamilyIndexAndModifiers()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Trigger(L("[FRAME]", 2, 2), L("[DO BEHAVIOR]"), L("[ME]", 2)),
            Behavior(L("[SET ACTION]"), L("[ATTACK]"), L("[RANDOM SELECT]", 1, 3), L("[/RANDOM SELECT]"), L("[END]")),
            Behavior(L("[SET ACTION]"), L("[CUSTOM]", 2), L("[END]")),
            Behavior(L("[SET ACTION]"), L("[STAND]"), L("[NOW]"))));
        var e = new Entity();
        var host = new Host();
        host.Randoms.Enqueue(1);       // [RANDOM SELECT] 取第二项
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        runner.Update(2, 0.016f);

        Assert.Equal(new[] { "action:Attack:3:end", "action:Custom:2:end", "action:Stand:0:now" }, e.Log.ToArray());
    }

    [Fact]
    public void DestroyAttackRectAndSetFrameReachTheAffectedObjects()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(
                L("[ATTACKRECT]"), L("[RESET]"),
                L("[ATTACKRECT]"), L("[OFF]"),
                L("[SET FRAME]", 3),
                L("[DESTROY BY FORCE]"))));
        var e = new Entity();
        new ActRunner(p, e, new Host()).Update(0, 0.016f);
        Assert.Equal(new[] { "rect:Reset", "rect:Off", "frame:3", "destroy:force" }, e.Log.ToArray());
    }

    [Fact]
    public void ABehaviorGroupRunsItsItemsInOrderOnEveryTarget()
    {
        ActProgram p = P(Cat(
            Trigger(
                L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Cat(Speed(1), Speed(2)))));
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character };
        var b = new Entity { Kind = ActEntityKind.Character };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        new ActRunner(p, owner, host).Update(0, 0.016f);

        Assert.Equal(new[] { "speed:1,-,-", "speed:2,-,-" }, a.Log.ToArray());
        Assert.Equal(new[] { "speed:1,-,-", "speed:2,-,-" }, b.Log.ToArray());
    }

    [Fact]
    public void ARandomSelectBehaviorGroupIsChosenPerFiring()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]"), L("[RANDOM SELECT]", 0, 1), L("[/RANDOM SELECT]")),
            Behavior(Speed(10)),
            Behavior(Speed(20))));
        var host = new Host();
        host.Randoms.Enqueue(1);
        var e = new Entity();
        new ActRunner(p, e, host).Update(0, 0.016f);
        Assert.Equal(new[] { "speed:20,-,-" }, e.Log.ToArray());
    }

    [Fact]
    public void AGroupNumberOutsideTheFileDoesNothingButStillCounts()
    {
        ActProgram p = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 5)), Behavior(Speed(1))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());
        runner.Update(0, 0.016f);
        Assert.Empty(e.Log);
        Assert.Equal(1, runner.TotalFired);
    }

    [Fact]
    public void AProgramWithoutTriggersIsHarmless()
    {
        var e = new Entity();
        var runner = new ActRunner(ActProgram.Empty, e, new Host());
        runner.Update(3, 0.1f);
        runner.OnAttackSuccess(e);
        runner.OnDamage(e);
        runner.OnSetAction();
        Assert.Empty(e.Log);
    }

    // ------------------------------------------------------------------ 变量、条件、延迟与生命条件(M39,E-132)

    [Fact]
    public void ADimAssignsFromTheClockAndALaterIfComparesItAgainstTheClock()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[LIMIT]", 1), L("[IF]", "t"), L("[+]", 500), L("[<=]"), L("[GET TIME]"), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[DIM]", "t"), L("[=]"), L("[GET TIME]"), L("[/DIM]")),
            Behavior(Speed(9))));

        Assert.True(p.Triggers[0].Supported);
        Assert.True(p.Triggers[1].Supported);
        Assert.Equal(ActRecordKind.If, p.Triggers[1].Records.Single(r => r.Kind == ActRecordKind.If).Kind);
        Assert.Equal(ActBehaviorKind.SetVariable, p.Behaviors[0].Items[0].Kind);

        var e = new Entity();
        var host = new Host { NowMs = 1000 };
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0.016f);
        Assert.True(runner.TryGetVariable("t", out int t));
        Assert.Equal(1000, t);

        host.NowMs = 1400;
        runner.Update(1, 0.016f);
        host.NowMs = 1499;
        runner.Update(1, 0.016f);
        Assert.Empty(e.Log);
        host.NowMs = 1500;
        runner.Update(1, 0.016f);
        Assert.Equal(new[] { "speed:9,-,-" }, e.Log.ToArray());
        host.NowMs = 1600;
        runner.Update(1, 0.016f);
        Assert.Single(e.Log);     // [LIMIT] 1
    }

    [Fact]
    public void ExpressionsAddSubtractAndReadVariablesAndAnUnassignedVariableReadsZero()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(
                L("[DIM]", "a"), L("[=]", 5), L("[/DIM]"),
                L("[DIM]", "b"), L("[=]", "a"), L("[+]", 3), L("[/DIM]"),
                L("[DIM]", "c"), L("[=]", 10), L("[-]", "a"), L("[/DIM]"),
                L("[DIM]", "d"), L("[=]", "c"), L("[-]", "b"), L("[/DIM]"),
                L("[DIM]", "e"), L("[=]", "never assigned"), L("[+]", 2), L("[/DIM]"),
                L("[DIM]", "a"), L("[=]", "a"), L("[+]", "a"), L("[/DIM]"))));

        var runner = new ActRunner(p, new Entity(), new Host());
        runner.Update(0, 0.016f);

        int Var(string name)
        {
            Assert.True(runner.TryGetVariable(name, out int v), name);
            return v;
        }

        Assert.Equal(10, Var("a"));     // 最后一条 [DIM] 把 a 改成了 5 + 5
        Assert.Equal(8, Var("b"));
        Assert.Equal(5, Var("c"));
        Assert.Equal(-3, Var("d"));
        Assert.Equal(2, Var("e"));
        Assert.False(runner.TryGetVariable("zzz", out _));
    }

    [Fact]
    public void ClockComparisonsSurviveTheThirtyTwoBitWrapAround()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[IF]", "t"), L("[+]", 30), L("[<=]"), L("[GET TIME]"), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[DIM]", "t"), L("[=]"), L("[GET TIME]"), L("[/DIM]")),
            Behavior(Speed(1))));
        var e = new Entity();
        var host = new Host { NowMs = 0xFFFFFFF0L };      // 低 32 位是 -16
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0.016f);
        Assert.Empty(e.Log);
        host.NowMs = 0x100000010L;                          // 低 32 位是 16:过了 32 毫秒
        runner.Update(1, 0.016f);
        Assert.Single(e.Log);
    }

    [Fact]
    public void AnIfWithoutAComparatorOrAnOperandStaysUnsupportedAndNeverFires()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IF]", "x"), L("[=]"), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[IF]"), L("[=]", 3), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[IF]", "x"), L("[=]", 0), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(3))));

        Assert.False(p.Triggers[0].Supported);
        Assert.False(p.Triggers[1].Supported);
        Assert.True(p.Triggers[2].Supported);
        var e = new Entity();
        new ActRunner(p, e, new Host()).Update(0, 0.016f);
        Assert.Single(e.Log);       // 只有第三条(没赋过值的变量读 0,0 == 0)触发
    }

    [Fact]
    public void AnIfGatesTheWholeTriggerAndLeavesTheWorkingSetAlone()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[IF]", 1), L("[=<]", 2), L("[/IF]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[IF]", 3), L("[=>]", 4), L("[/IF]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Behavior(Speed(1)),
            Behavior(Speed(2))));
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character };
        var b = new Entity { Kind = ActEntityKind.Character };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        new ActRunner(p, owner, host).Update(0, 0.016f);

        Assert.Equal(new[] { "speed:1,-,-" }, a.Log.ToArray());      // 1 <= 2 成立:两个目标都被作用
        Assert.Equal(new[] { "speed:1,-,-" }, b.Log.ToArray());      // 3 >= 4 不成立:第二条什么也没做
    }

    [Fact]
    public void ADelayedBehaviorRunsOnceItsDelayHasPassedAndOnlyThenCountsAsDone()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DELAY DO BEHAVIOR]"), L("[ME]", 0, 300)),
            Behavior(Speed(5))));
        Assert.True(p.Triggers[0].Supported);
        var e = new Entity();
        var host = new Host { NowMs = 1000 };
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0.016f);
        Assert.Equal(1, runner.PendingDelayed);
        Assert.Equal(1, runner.TotalFired);
        Assert.Empty(e.Log);
        host.NowMs = 1299;
        runner.Update(1, 0.016f);
        Assert.Empty(e.Log);
        host.NowMs = 1300;
        runner.Update(1, 0.016f);
        Assert.Equal(new[] { "speed:5,-,-" }, e.Log.ToArray());
        Assert.Equal(0, runner.PendingDelayed);
        host.NowMs = 5000;
        runner.Update(1, 0.016f);
        Assert.Single(e.Log);       // 单次位:同一圈不会再排一次
    }

    [Fact]
    public void ANegativeDelayOrGroupNumberQueuesNothingButStillCounts()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DELAY DO BEHAVIOR]"), L("[ME]", 0, -5)),
            Trigger(L("[FRAME]", 0, 0), L("[DELAY DO BEHAVIOR]"), L("[ME]", -1, 5)),
            Behavior(Speed(5))));
        var e = new Entity();
        var host = new Host { NowMs = 0 };
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0.016f);
        Assert.Equal(0, runner.PendingDelayed);
        Assert.Equal(2, runner.TotalFired);
        host.NowMs = 100;
        runner.Update(1, 0.016f);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void ADelayedBehaviorKeepsTheTargetsOfTheMomentItWasQueuedAndSkipsTheDeadOnes()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DELAY DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0, 100)),
            Behavior(Speed(1))));
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character };
        var b = new Entity { Kind = ActEntityKind.Character };
        var late = new Entity { Kind = ActEntityKind.Character };
        var host = new Host { NowMs = 0 };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        var runner = new ActRunner(p, owner, host);
        runner.Update(0, 0.016f);

        b.IsAlive = false;
        host.Sets[ActSelector.AllEnemy].Add(late);
        host.NowMs = 100;
        runner.Update(1, 0.016f);

        Assert.Equal(new[] { "speed:1,-,-" }, a.Log.ToArray());
        Assert.Empty(b.Log);
        Assert.Empty(late.Log);
    }

    [Fact]
    public void RestartingTheActionDropsPendingDelaysButKeepsTheVariables()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Trigger(L("[FRAME]", 0, 0), L("[DELAY DO BEHAVIOR]"), L("[ME]", 0, 50)),
            Behavior(Speed(2)),
            Behavior(L("[DIM]", "n"), L("[=]", "n"), L("[+]", 1), L("[/DIM]"))));
        var e = new Entity();
        var host = new Host { NowMs = 0 };
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0.016f);
        Assert.Equal(1, runner.PendingDelayed);

        runner.Begin();
        Assert.Equal(0, runner.PendingDelayed);
        host.NowMs = 100;
        runner.Update(0, 0.016f);      // 重来后第 0 帧又排了一个
        Assert.Equal(1, runner.PendingDelayed);
        Assert.Empty(e.Log);
        Assert.True(runner.TryGetVariable("n", out int n));
        Assert.Equal(2, n);              // 变量跨过了动作重来(两次 [DIM] n = n + 1)
    }

    [Fact]
    public void ADelayedBehaviorThatSwitchesTheMotionEndsTheRestOfThatUpdate()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DELAY DO BEHAVIOR]"), L("[ME]", 0, 0)),
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET FRAME]", 3)),
            Behavior(Speed(7))));
        var e = new Entity();
        var host = new Host { NowMs = 0 };
        var runner = new ActRunner(p, e, host);
        runner.Update(0, 0.016f);          // 排队(延迟 0 毫秒,下一次更新开头执行)
        runner.Update(1, 0.016f);          // 开头先执行延迟行为:改了帧,这次更新的触发器不再求值
        Assert.Equal(new[] { "frame:3" }, e.Log.ToArray());
        runner.Update(1, 0.016f);          // 下一次更新照常
        Assert.Equal(new[] { "frame:3", "speed:7,-,-" }, e.Log.ToArray());
    }

    [Fact]
    public void AnHpConditionComparesPercentOfMaxAndPointsAndDropsObjectsWithoutHealth()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ME]"), L("[CHECKUP]"), L("[HP]"), L("[<=]", 50), L("[%]"), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Trigger(L("[WHICH]"), L("[ME]"), L("[CHECKUP]"), L("[HP]"), L("[>]", 30), L("[POINT]"), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Behavior(Speed(1)),
            Behavior(Speed(2))));
        Assert.True(p.Triggers[0].Supported);
        Assert.Equal(ActRecordKind.Hp, p.Triggers[0].Records[1].Kind);
        Assert.True(p.Triggers[0].Records[1].Percent);
        Assert.False(p.Triggers[1].Records[1].Percent);

        List<string> Run(int hp, int max)
        {
            var e = new Entity { Hp = hp, HpMax = max };
            new ActRunner(p, e, new Host()).Update(0, 0.016f);
            return e.Log;
        }

        Assert.Equal(new[] { "speed:1,-,-", "speed:2,-,-" }, Run(50, 100).ToArray());   // 50% 恰好满足 <= 50;50 点大于 30
        Assert.Equal(new[] { "speed:2,-,-" }, Run(51, 100).ToArray());                   // 51% 不满足
        Assert.Equal(new[] { "speed:1,-,-" }, Run(30, 100).ToArray());                   // 30 点不大于 30
        Assert.Empty(Run(10, 0));                                                        // 没有生命的对象:被剔除,两条都不触发
    }

    [Fact]
    public void ThePercentThresholdUsesA32BitMultiplyLikeTheClient()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ME]"), L("[CHECKUP]"), L("[HP]"), L("[<=]", 50), L("[%]"), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1))));
        // 最大生命 1 亿 × 50 在 32 位里回绕成 705032704,再除 100 得 7050327:4000 万不满足,700 万满足(客户端的乘法是 32 位的)
        var big = new Entity { Hp = 40_000_000, HpMax = 100_000_000 };
        new ActRunner(p, big, new Host()).Update(0, 0.016f);
        Assert.Empty(big.Log);
        var small = new Entity { Hp = 7_000_000, HpMax = 100_000_000 };
        new ActRunner(p, small, new Host()).Update(0, 0.016f);
        Assert.Single(small.Log);
    }

    [Fact]
    public void AnHpWordInsteadOfANumberIsStillUnsupported()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ME]"), L("[CHECKUP]"), L("[HP]"), L("[LOW]"), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1))));
        Assert.False(p.Triggers[0].Supported);
        Assert.Contains("[HP]", p.Triggers[0].UnsupportedTags);
    }

    [Fact]
    public void ADoProcBehaviorRunsLikeADoBehaviorInTheSameFile()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO PROC BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(4))));
        Assert.True(p.Triggers[0].Supported);
        var e = new Entity();
        new ActRunner(p, e, new Host()).Update(0, 0.016f);
        Assert.Equal(new[] { "speed:4,-,-" }, e.Log.ToArray());
    }

    [Fact]
    public void FrontSideKeepsOnlyTheTargetsThatFaceTheOwnerAlongTheXAxis()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKUP]"), L("[FRONTSIDE]"), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1))));
        Assert.True(p.Triggers[0].Supported);
        var owner = new Entity { X = 100 };
        var leftFacingRight = new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = false };     // 在拥有者左边、朝右:正面
        var leftFacingLeft = new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = true };       // 在左边、朝左:背对
        var rightFacingLeft = new Entity { Kind = ActEntityKind.Character, X = 150, FacingLeft = true };     // 在右边、朝左:正面
        var rightFacingRight = new Entity { Kind = ActEntityKind.Character, X = 150, FacingLeft = false };   // 在右边、朝右:背对
        var samePlaceFacingRight = new Entity { Kind = ActEntityKind.Character, X = 100, FacingLeft = false };
        var dead = new Entity { Kind = ActEntityKind.Character, X = 50, FacingLeft = false, IsAlive = false };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { leftFacingRight, leftFacingLeft, rightFacingLeft, rightFacingRight, samePlaceFacingRight, dead };
        new ActRunner(p, owner, host).Update(0, 0.016f);

        Assert.Single(leftFacingRight.Log);
        Assert.Empty(leftFacingLeft.Log);
        Assert.Single(rightFacingLeft.Log);
        Assert.Empty(rightFacingRight.Log);
        Assert.Single(samePlaceFacingRight.Log);      // 同一 X 算"在左边或同位置":要朝右
        Assert.Empty(dead.Log);
    }

    [Fact]
    public void IsInPosNeedsAllThreeIntegerCoordinatesToMatchExactly()
    {
        ActProgram p = P(Cat(
            Trigger(L("[WHICH]"), L("[ME]"), L("[CHECKUP]"), L("[IS IN POS]", 10, 20, 30), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Trigger(L("[WHICH]"), L("[ME]"), L("[CHECKUP]"), L("[IS IN POS]", 10, 20), L("[/CHECKUP]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(1))));
        Assert.True(p.Triggers[0].Supported);
        Assert.False(p.Triggers[1].Supported);       // 少一个坐标:读不懂

        int Fired(float x, float y, float z)
        {
            var e = new Entity { X = x, Y = y, Z = z };
            new ActRunner(p, e, new Host()).Update(0, 0.016f);
            return e.Log.Count;
        }

        Assert.Equal(1, Fired(10, 20, 30));
        Assert.Equal(1, Fired(10.9f, 20.5f, 30.99f));      // 取整数部分比较
        Assert.Equal(0, Fired(11, 20, 30));
        Assert.Equal(0, Fired(10, 20, 29));
    }

    // ------------------------------------------------------------------ 受击盒、浮空高度、震屏、瞬移(M40,证据 E-133)

    private static ScriptLine[] Me(params ScriptLine[] body) => Cat(
        Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
        Behavior(body));

    private static ScriptLine[] OnEnemies(params ScriptLine[] body) => Cat(
        Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
        Behavior(body));

    private static void Run(ActProgram p, IActEntity owner, Host host = null) => new ActRunner(p, owner, host ?? new Host()).Update(0, 0.016f);

    [Fact]
    public void SetDamageBoxHasOnOffAndResetAndABareTagMeansOff()
    {
        var on = new Entity();
        Run(P(Me(L("[SET DAMAGE BOX]"), L("[ON]"))), on);
        Assert.Equal(new[] { "box:On" }, on.Log.ToArray());

        var off = new Entity();
        Run(P(Me(L("[SET DAMAGE BOX]"), L("[OFF]"))), off);
        Assert.Equal(new[] { "box:Off" }, off.Log.ToArray());

        var reset = new Entity();
        Run(P(Me(L("[SET DAMAGE BOX]"), L("[RESET]"))), reset);
        Assert.Equal(new[] { "box:Reset" }, reset.Log.ToArray());

        // 没写档位:写成关,而且后面的行为照常读(档位标签是偷看的,不是吞掉下一个标签)
        var bare = new Entity();
        Run(P(Me(L("[SET DAMAGE BOX]"), L("[SET SPEED]"), L("[X AXIS]", 3), L("[/SET SPEED]"))), bare);
        Assert.Equal(new[] { "box:Off", "speed:3,-,-" }, bare.Log.ToArray());
    }

    [Fact]
    public void SetDamageBoxReachesEveryTarget()
    {
        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Character };
        var b = new Entity { Kind = ActEntityKind.Character };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        Run(P(OnEnemies(L("[SET DAMAGE BOX]"), L("[OFF]"))), owner, host);
        Assert.Equal(new[] { "box:Off" }, a.Log.ToArray());
        Assert.Equal(new[] { "box:Off" }, b.Log.ToArray());
        Assert.Empty(owner.Log);
    }

    [Fact]
    public void ChangeFloatingHeightGoesToAliveTargetsOnly()
    {
        var owner = new Entity();
        var alive = new Entity { Kind = ActEntityKind.Character };
        var dead = new Entity { Kind = ActEntityKind.Character, IsAlive = false };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { alive, dead };
        Run(P(OnEnemies(L("[CHANGE FLOATING HEIGHT]", 120))), owner, host);
        Assert.Equal(new[] { "float:120" }, alive.Log.ToArray());
        Assert.Empty(dead.Log);

        var self = new Entity();
        Run(P(Me(L("[CHANGE FLOATING HEIGHT]", 0))), self);
        Assert.Equal(new[] { "float:0" }, self.Log.ToArray());      // 0 与负数是结束悬停,由宿主处理
    }

    [Fact]
    public void ShakingStartsOneHostShakeNoMatterHowManyTargets()
    {
        var owner = new Entity();
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { new Entity { Kind = ActEntityKind.Character }, new Entity { Kind = ActEntityKind.Character } };
        Run(P(OnEnemies(L("[SHAKING]", 6, 300))), owner, host);
        Assert.Equal(new[] { (6, 300) }, host.Shakes.ToArray());
    }

    [Fact]
    public void ShakingWithANegativeDurationDoesNothing()
    {
        var host = new Host();
        Run(P(Me(L("[SHAKING]", 6, -1))), new Entity(), host);
        Assert.Empty(host.Shakes);
        Run(P(Me(L("[SHAKING]", 6, 0))), new Entity(), host);
        Assert.Equal(new[] { (6, 0) }, host.Shakes.ToArray());
    }

    private static string TeleportLog(Entity self, Entity other, bool targetOther, params ScriptLine[] teleport)
    {
        var lines = new List<ScriptLine> { L("[TELEPORT]") };
        lines.AddRange(teleport);
        lines.Add(L("[/TELEPORT]"));
        var host = new Host();
        if (other != null)
        {
            host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        }
        ActProgram p = P(targetOther ? OnEnemies(lines.ToArray()) : Me(lines.ToArray()));
        Run(p, self, host);
        return string.Join("|", self.Log.Concat(other?.Log ?? new List<string>()));
    }

    [Fact]
    public void TeleportOffsetsFromTheTargetAndMirrorsXWhenFacingLeft()
    {
        // 默认:以被作用的对象为基准,X 偏移在朝左时取反,Z 偏移加基准的高度
        var left = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = true };
        Assert.Equal("teleport:70,55,17", TeleportLog(left, null, false, L("[POS]", 30, 5, 7)));
        var right = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = false };
        Assert.Equal("teleport:130,55,17", TeleportLog(right, null, false, L("[POS]", 30, 5, 7)));
    }

    [Fact]
    public void TeleportMapPositionIsAbsolute()
    {
        var e = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = true };
        Assert.Equal("teleport:30,5,7", TeleportLog(e, null, false, L("[POS]", 30, 5, 7), L("[USE MAP POS]")));
    }

    [Fact]
    public void TeleportCanIgnoreTheBaseHeightAndTheDirection()
    {
        var e = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = true };
        Assert.Equal("teleport:70,55,7", TeleportLog(e, null, false, L("[POS]", 30, 5, 7), L("[NOT USE OBJECT ZPOS]")));

        // 没有 [USE MY BASEPOS] 却写了 [NOT USE OBJECT DIRECTION]:三个值原样当绝对坐标
        var f = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = true };
        Assert.Equal("teleport:30,5,7", TeleportLog(f, null, false, L("[POS]", 30, 5, 7), L("[NOT USE OBJECT DIRECTION]")));
    }

    [Fact]
    public void TeleportFromMyBasePositionUsesTheOwnerAsTheBase()
    {
        var owner = new Entity { X = 200, Y = 80, Z = 0, FacingLeft = true };
        var enemy = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 10, Z = 0, FacingLeft = false };
        // 目标是敌人(朝右),基准是拥有者:X 的取反看敌人的朝向(右 → 不取反);移动的是敌人
        string log = TeleportLog(owner, enemy, true, L("[POS]", 30, 5, 0), L("[USE MY BASEPOS]"));
        Assert.Equal("teleport:230,85,0", log);
        Assert.Equal(200, owner.X);
        Assert.Equal(230, enemy.X);
    }

    [Fact]
    public void TeleportUseMyDirectionTakesTheOwnersFacingForTheMirror()
    {
        var owner = new Entity { X = 200, Y = 80, Z = 0, FacingLeft = true };
        var enemy = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 10, Z = 0, FacingLeft = false };
        string log = TeleportLog(owner, enemy, true, L("[POS]", 30, 5, 0), L("[USE MY BASEPOS]"), L("[USE MY DIRECTION]"));
        Assert.Equal("teleport:170,85,0", log);
    }

    [Fact]
    public void TeleportMovesTheTargetByDefaultAndTheOwnerWithMoveMe()
    {
        var owner = new Entity { X = 200, Y = 80, FacingLeft = false };
        var enemy = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 10, FacingLeft = false };
        // 基准是被作用的敌人;默认移动敌人自己
        Assert.Equal("teleport:40,15,0", TeleportLog(owner, enemy, true, L("[POS]", 30, 5, 0)));
        Assert.Empty(owner.Log);

        var owner2 = new Entity { X = 200, Y = 80, FacingLeft = false };
        var enemy2 = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 10, FacingLeft = false };
        // [MOVE ME]:基准仍是被作用的敌人,被移动的是拥有者(瞬移到目标身边)
        string moved = TeleportLog(owner2, enemy2, true, L("[POS]", 30, 5, 0), L("[MOVE ME]"));
        Assert.Equal("teleport:40,15,0", moved);
        Assert.Equal(40, owner2.X);
        Assert.Empty(enemy2.Log);

        var owner3 = new Entity { X = 200, Y = 80, FacingLeft = false };
        var enemy3 = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 10, FacingLeft = false };
        // [MOVE TARGET] 把前面的 [MOVE ME] 取消
        TeleportLog(owner3, enemy3, true, L("[POS]", 30, 5, 0), L("[MOVE ME]"), L("[MOVE TARGET]"));
        Assert.Empty(owner3.Log);
        Assert.Equal(40, enemy3.X);
    }

    [Fact]
    public void TeleportWithAllowFixtureDoesNothingAndAMissingPosIsZero()
    {
        var e = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = false };
        Assert.Equal("", TeleportLog(e, null, false, L("[POS]", 30, 5, 7), L("[ALLOW FIXTURE]")));
        var f = new Entity { X = 100, Y = 50, Z = 10, FacingLeft = false };
        Assert.Equal("teleport:100,50,10", TeleportLog(f, null, false));
    }

    [Fact]
    public void TeleportReadsRandomValuesInThePosition()
    {
        var owner = new Entity { X = 100, Y = 50, Z = 0, FacingLeft = false };
        var host = new Host();
        host.Randoms.Enqueue(5);       // [RANDOM] 10 20:10 + 5
        host.Randoms.Enqueue(1);       // [RANDOM SELECT] 4 9:第二项
        Run(P(Me(L("[TELEPORT]"), L("[POS]", 0), L("[RANDOM]", 10, 20), L("[RANDOM SELECT]", 4, 9), L("[/RANDOM SELECT]"), L("[/TELEPORT]"))), owner, host);
        Assert.Equal(new[] { "teleport:100,65,9" }, owner.Log.ToArray());
    }
}
