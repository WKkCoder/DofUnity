using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Ai;
using Dnf.Game.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物的 .ai 驱动(MonsterScript、MonsterActor.ThinkScripted;证据 E-052、E-053、E-128)。
/// 脚本、动画与怪物都是合成的(迷你怪物 + 手工拼的 .ai),数值检验的是我们写下的规则,不是原版数据。
/// </summary>
public sealed class MonsterScriptTests
{
    private const double WalkSpeed = 222.5;

    private static AiScript Script(params object[] items)
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

    /// <summary>无条件返回 <paramref name="value"/> 的脚本("get random()" 恒为真)。</summary>
    private static AiScript Always(int value)
    {
        return Script("[think]", "[void]", "get random()", 0, 1, "[true]", "[return]", value, "[false]", "[return]", value, "[/think]");
    }

    private static MonsterScript Make(AiScript action = null, AiScript destination = null, AiScript move = null, int[] cooltimes = null, int attacks = 1,
        Func<int> rand = null)
    {
        var scripts = new[] { null, destination, action, move };
        var paths = new[] { null, destination != null ? "ai/dest.ai" : null, action != null ? "ai/action.ai" : null, move != null ? "ai/move.ai" : null };
        return new MonsterScript(_ => null, paths, scripts, cooltimes, attacks, rand ?? (() => 0));
    }

    private static MonsterAiView View(double x = 500, double y = 100, double targetX = 300, double targetY = 100, int hp = 100, int hpMax = 100)
    {
        return new MonsterAiView { X = x, Y = y, FacingLeft = true, Hp = hp, HpMax = hpMax, Level = 10, State = 0, Free = true, HasTarget = true, TargetX = targetX, TargetY = targetY, TargetState = 0 };
    }

    // ------------------------------------------------------------------ 冷却、节拍、出招间隔

    [Fact]
    public void EveryAttackStartsCoolingForItsListedMillisecondsAndAnUnlistedOneIsReady()
    {
        MonsterScript s = Make(cooltimes: new[] { 1000, 0 }, attacks: 3);
        Assert.False(s.CanUse(0));
        Assert.True(s.CanUse(1));
        Assert.True(s.CanUse(2));                       // 清单比招式短:多出来的没有冷却
        Assert.Equal(1000, s.CoolingMs(0));
        s.Advance(400);
        Assert.Equal(600, s.CoolingMs(0));
        Assert.False(s.CanUse(0));
        s.Advance(600);
        Assert.True(s.CanUse(0));
        Assert.Equal(0, s.CoolingMs(0));
        s.AttackStarted(0);                             // 开始出招:这一招重新进入冷却
        Assert.False(s.CanUse(0));
        Assert.Equal(1000, s.CoolingMs(0));
        Assert.False(s.CanUse(-1));
        Assert.False(s.CanUse(3));
    }

    [Fact]
    public void AnAttackWithoutAnAnimationIsNeverUsable()
    {
        MonsterScript s = Make(cooltimes: new[] { 0, 0 }, attacks: 2);
        s.AttackExists = i => i == 1;
        Assert.False(s.CanUse(0));
        Assert.True(s.CanUse(1));
    }

    [Fact]
    public void ThinkAndDestinationTurnsComeAtTheirTermsAndRestart()
    {
        MonsterScript s = Make();
        s.ThinkTermMs = 200;
        s.DestinationTermMs = 500;
        s.Advance(199);
        Assert.False(s.TakeThinkTurn());
        s.Advance(1);
        Assert.True(s.TakeThinkTurn());
        Assert.False(s.TakeThinkTurn());               // 节拍清零:要再等一个周期
        s.Advance(200);
        Assert.True(s.TakeThinkTurn());
        Assert.False(s.TakeDestinationTurn());         // 目的地的间隔另算:才过了 400 毫秒
        s.Advance(100);
        Assert.True(s.TakeDestinationTurn());
        Assert.False(s.TakeDestinationTurn());
        // 没写(0)的间隔取缺省
        MonsterScript unset = Make();
        unset.ThinkTermMs = 0;
        unset.DestinationTermMs = 0;
        unset.Advance(MonsterScript.DefaultDestinationTermMs - 1);
        Assert.False(unset.TakeDestinationTurn());
        unset.Advance(1);
        Assert.True(unset.TakeDestinationTurn());
        unset.Advance(MonsterScript.DefaultThinkTermMs - MonsterScript.DefaultDestinationTermMs - 1);
        Assert.False(unset.TakeThinkTurn());
        unset.Advance(1);
        Assert.True(unset.TakeThinkTurn());
    }

    [Fact]
    public void TheVisionLooksOnlyAtTheHorizontalDistanceAndZeroMeansTheDefault()
    {
        MonsterScript s = Make();
        s.Vision = 300;
        Assert.True(s.Notices(300));
        Assert.True(s.Notices(-300));
        Assert.False(s.Notices(301));
        Assert.False(s.Notices(-301));
        s.Vision = 0;
        Assert.True(s.Notices(MonsterScript.DefaultVision));
        Assert.False(s.Notices(MonsterScript.DefaultVision + 1));
    }

    // ------------------------------------------------------------------ 动作角色

    [Fact]
    public void TheActionScriptPicksTheListedAttackOnlyWhenItIsReady()
    {
        MonsterScript s = Make(Always(0), cooltimes: new[] { 800 }, attacks: 1);
        MonsterAiView view = View();
        Assert.Equal(-1, s.DecideAttack(view));         // 出生时在冷却
        s.Advance(799);
        Assert.Equal(-1, s.DecideAttack(view));
        s.Advance(1);
        Assert.Equal(0, s.DecideAttack(view));
        s.AttackStarted(0);
        Assert.Equal(-1, s.DecideAttack(view));         // 刚出过:又在冷却
    }

    [Fact]
    public void AnOutOfRangeOrNegativeResultOrMissingScriptMeansNoAttack()
    {
        MonsterAiView view = View();
        Assert.Equal(-1, Make(Always(5), attacks: 2).DecideAttack(view));
        Assert.Equal(-1, Make(Always(-1), attacks: 2).DecideAttack(view));
        Assert.Equal(-1, Make(null, attacks: 2).DecideAttack(view));
        Assert.Equal(1, Make(Always(1), attacks: 2).DecideAttack(view));
    }

    [Fact]
    public void TheAttackDelayBlocksTheNextPickUntilItHasElapsed()
    {
        MonsterScript s = Make(Always(0), attacks: 1);
        s.AttackDelayMs = 500;
        MonsterAiView view = View();
        Assert.Equal(0, s.DecideAttack(view));
        s.AttackStarted(0);
        s.AttackFinished();
        Assert.Equal(-1, s.DecideAttack(view));
        s.Advance(499);
        Assert.Equal(-1, s.DecideAttack(view));
        s.Advance(1);
        Assert.Equal(0, s.DecideAttack(view));
    }

    [Fact]
    public void TheScriptReadsTheHealthPercentAndTheTargetDistanceFromTheView()
    {
        AiScript lowHp = Script("[think]", "[hp]", "%<=", 50, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        MonsterScript s = Make(lowHp, attacks: 2);
        Assert.Equal(1, s.DecideAttack(View(hp: 30)));
        Assert.Equal(0, s.DecideAttack(View(hp: 80)));
        Assert.Equal(1, s.DecideAttack(View(hp: 50)));

        AiScript near = Script("[think]", "[target distance]", "<=", 150, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        s = Make(near, attacks: 2);
        Assert.Equal(1, s.DecideAttack(View(x: 400, targetX: 300)));
        Assert.Equal(0, s.DecideAttack(View(x: 500, targetX: 300)));
        // 没有目标:目标距离写 0,条件仍然成立(原版填充函数的写法,没有目标时不报错)
        MonsterAiView noTarget = View();
        noTarget.HasTarget = false;
        Assert.Equal(1, s.DecideAttack(noTarget));
    }

    // ------------------------------------------------------------------ 选目的地、移动方式

    [Fact]
    public void ThePlanNumberTurnsIntoADestination()
    {
        MonsterAiView view = View(x: 500, targetX: 300, targetY: 120);
        AiDestination toTarget = Make(destination: Always(0)).DecideDestination(view);
        Assert.Equal((AiDestinationKind.MoveTo, 300f, 120f), (toTarget.Kind, toTarget.X, toTarget.Y));

        MonsterScript keep = Make(destination: Always(1));
        keep.KeepDistance = 120;
        AiDestination onMySide = keep.DecideDestination(view);
        Assert.Equal((AiDestinationKind.MoveTo, 420f, 120f), (onMySide.Kind, onMySide.X, onMySide.Y));   // 目标 x + 120,在自己这一侧
        AiDestination otherSide = keep.DecideDestination(View(x: 100, targetX: 300));
        Assert.Equal(180f, otherSide.X);                                                                  // 自己在左边:目标 x - 120

        Assert.Equal(AiDestinationKind.Hold, Make(destination: Always(3)).DecideDestination(view).Kind);
        Assert.Equal(AiDestinationKind.Hold, Make(destination: Always(33)).DecideDestination(view).Kind);
        Assert.Equal(AiDestinationKind.Keep, Make(destination: Always(-1)).DecideDestination(view).Kind);
        Assert.Equal(AiDestinationKind.Keep, Make().DecideDestination(view).Kind);                          // 没有这个脚本
        MonsterAiView noTarget = View();
        noTarget.HasTarget = false;
        Assert.Equal(AiDestinationKind.Keep, Make(destination: Always(0)).DecideDestination(noTarget).Kind);
    }

    [Fact]
    public void TheRandomPlansUseTheClientRandom()
    {
        MonsterAiView view = View(x: 500, targetX: 300, targetY: 100);
        AiDestination jitter = Make(destination: Always(20), rand: () => 7).DecideDestination(view);
        Assert.Equal((300f, 100f + (7 % 41) - 20), (jitter.X, jitter.Y));
        AiDestination left = Make(destination: Always(22), rand: () => 2).DecideDestination(view);
        AiDestination right = Make(destination: Always(22), rand: () => 3).DecideDestination(view);
        Assert.Equal(200f, left.X);
        Assert.Equal(400f, right.X);
    }

    [Fact]
    public void TheMoveScriptGivesTheWalkModeAndMinusOneMeansDontMove()
    {
        MonsterAiView view = View();
        Assert.Equal(0, Make().DecideMove(view));                    // 没有这个脚本:正常走
        Assert.Equal(-1, Make(move: Always(-1)).DecideMove(view));
        Assert.Equal(2, Make(move: Always(2)).DecideMove(view));
    }

    // ------------------------------------------------------------------ 脚本驱动的怪物(MonsterActor)

    private static MonsterActor Scripted(DnfAssets assets, MonsterScript script, double x = 600, double y = 100, int attackDelay = 0)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        var m = new MonsterActor(0, spawn, model, 3, 100, WalkSpeed, attackDelay, 0);
        script.AttackExists = i => i < model.Attacks.Count && model.Attacks[i] != null;
        m.SetScript(script);
        return m;
    }

    private static void Run(MonsterActor m, double ms, double heroX, double heroY, IWalkArea walk = null, double step = 10)
    {
        double left = ms;
        while (left > 0)
        {
            double d = left < step ? left : step;
            m.Update(d, heroX, heroY, true, walk ?? new RectUnionArea(new List<WorldRect>()));
            left -= d;
        }
    }

    [Fact]
    public void AScriptedMonsterWakesWhenTheHeroEntersItsVisionAndStaysAwake()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterScript script = Make(destination: Always(0), cooltimes: new[] { 100000 });
        script.Vision = 200;
        script.ThinkTermMs = 100;
        script.DestinationTermMs = 100;
        MonsterActor m = Scripted(assets, script, x: 600);
        Run(m, 500, 300, 100);                              // 水平 300 像素:在视野外
        Assert.False(m.IsAware);
        Assert.Equal((MonsterPhase.Wait, 600.0), (m.Phase, m.X));
        Run(m, 300, 450, 100);                              // 150 像素:发现了
        Assert.True(m.IsAware);
        Run(m, 300, 1500, 100);                             // 走远了也不放手
        Assert.True(m.IsAware);
    }

    [Fact]
    public void AScriptedMonsterWalksToTheDestinationTheScriptPicksAtTheScaledSpeed()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterScript script = Make(destination: Always(0), cooltimes: new[] { 100000 });
        script.ThinkTermMs = 100;
        script.DestinationTermMs = 100;
        MonsterActor m = Scripted(assets, script, x: 600, y: 100);
        Run(m, 100, 300, 100);                              // 第一个思考节拍(100 毫秒)到了:选了目的地、开始走
        Run(m, 500, 300, 100);
        Assert.Equal(MonsterPhase.Walk, m.Phase);
        Assert.True(m.FacingLeft);
        Assert.InRange(m.X, 600 - 222.5 * 0.6 - 1, 600 - 222.5 * 0.5 + 1);   // 走了大约 0.5 秒多一点
        Run(m, 3000, 300, 100);                             // 走到主角身边为止:不越过主角
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.InRange(m.X, 300, 300 + MonsterTuning.MinSeparationX + 5);
    }

    [Fact]
    public void AHoldPlanOrAMinusOneMoveModeKeepsAScriptedMonsterStanding()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        foreach (MonsterScript script in new[]
                 {
                     Make(destination: Always(3), cooltimes: new[] { 100000 }),
                     Make(destination: Always(0), move: Always(-1), cooltimes: new[] { 100000 }),
                 })
        {
            script.ThinkTermMs = 100;
            script.DestinationTermMs = 100;
            MonsterActor m = Scripted(assets, script, x: 600);
            Run(m, 1500, 300, 100);
            Assert.True(m.IsAware);
            Assert.Equal((MonsterPhase.Wait, 600.0), (m.Phase, m.X));
            Assert.True(m.FacingLeft);                       // 站着面朝主角
        }
    }

    [Fact]
    public void AScriptedMonsterAttacksWhenTheActionScriptSaysSoAndTheAttackIsReady()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterScript script = Make(Always(0), cooltimes: new[] { 2000 });
        script.ThinkTermMs = 100;
        MonsterActor m = Scripted(assets, script, x: 340);
        Run(m, 1900, 300, 100);                              // 出生时每一招在冷却:2 秒里不出招
        Assert.Equal(0, m.SwingNumber);
        Assert.NotEqual(MonsterPhase.Attack, m.Phase);
        Run(m, 200, 300, 100);                               // 冷却完的下一个思考节拍出招
        Assert.True(m.SwingNumber >= 1);
        Assert.Equal(0, m.AttackIndex);
        Assert.True(m.FacingLeft);                           // 出招前面朝主角(主角在左边)
        long swings = m.SwingNumber;
        Run(m, 600, 300, 100);
        Assert.Equal(swings, m.SwingNumber);                 // 刚出过的招又在冷却
    }

    [Fact]
    public void ANoAttackPickLeavesTheMonsterStandingEvenWhenTheHeroIsRightNextToIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterScript script = Make(Always(-1), cooltimes: new[] { 0 });
        script.ThinkTermMs = 100;
        MonsterActor m = Scripted(assets, script, x: 340);
        Run(m, 3000, 330, 100);
        Assert.Equal(0, m.SwingNumber);
    }

    [Fact]
    public void ThePlaceholderBehaviourIsUntouchedWithoutAScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 600, 100, 0, 1, 0, "[fixed]", "[normal]");
        var m = new MonsterActor(0, spawn, model, 3, 100, WalkSpeed, 1500, 0);
        Assert.Null(m.Script);
        Run(m, 500, 300, 100);
        Assert.Equal(MonsterPhase.Walk, m.Phase);            // 旧式:进了视野就直接走向主角
        Assert.Equal(600 - 222.5 * 0.5, m.X, 6);
    }

    [Fact]
    public void AScriptedMonsterOutsideTheWalkableAreaWalksBackInButALegacyOneStaysPut()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 可走区只有主角脚下和右边一片:怪物出生在区外的左边
        var area = new RectUnionArea(new List<WorldRect> { new WorldRect(300, 50, 400, 100) });
        MonsterScript script = Make(destination: Always(0), cooltimes: new[] { 100000 });
        script.ThinkTermMs = 100;
        script.DestinationTermMs = 100;
        MonsterActor scripted = Scripted(assets, script, x: 100, y: 100);
        Run(scripted, 2000, 400, 100, area);
        Assert.True(scripted.X > 250, "脚本驱动的怪物应该走回可走区,x = " + scripted.X);

        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 100, 100, 0, 1, 0, "[fixed]", "[normal]");
        var legacy = new MonsterActor(0, spawn, model, 3, 100, WalkSpeed, 1500, 0);
        Run(legacy, 2000, 400, 100, area);
        Assert.Equal(100.0, legacy.X);                       // 旧式怪物的每一步都落在区外:原地不动
    }

    // ------------------------------------------------------------------ 打得到与打不到(受击框)、房间清空

    [Fact]
    public void AMonsterWithoutAnyHurtBoxIsNeverHittableButANormalOneIsWhileItLives()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 340, 100, 0, 1, 0, "[fixed]", "[normal]");
        var ghost = new MonsterActor(0, spawn, MonsterModel.Read(assets, AvatarFixture.BlobGhostMobPath), 3, 100, WalkSpeed, 1500, 0);
        Assert.False(ghost.CanEverBeHit);
        Assert.False(ghost.HasDamageBox);
        Assert.Empty(ghost.DamageBoxes());

        var normal = new MonsterActor(1, spawn, MonsterModel.Read(assets, AvatarFixture.BlobMobPath), 3, 100, WalkSpeed, 1500, 0);
        Assert.True(normal.CanEverBeHit);
        Assert.True(normal.HasDamageBox);
        Assert.NotEmpty(normal.DamageBoxes());
        normal.ApplyHit(1000, false, 0, true, new HitStunInput(null, 1.0f));      // 打死:死亡的怪物没有受击框
        Assert.False(normal.HasDamageBox);
        Assert.True(normal.CanEverBeHit);                                         // 模型本身不变
    }

    [Fact]
    public void ARoomClearsWhenOnlyAMonsterThatCanNeverBeHitIsLeftAndTheBotIgnoresIt()
    {
        // 起点格:一只正常的弱怪物加一只没有任何受击框的怪物(占位);打死前者房间就算清空,自动机也不会对着后者挥一辈子
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(),
            (cx, cy) => cx == 0 && cy == 0 ? new[] { (340, 100), (380, 100) } : DungeonSessionTests.None(), hp: 10,
            mobPathOf: (cx, cy, i) => i == 1 ? AvatarFixture.BlobGhostMobPath : AvatarFixture.BlobMobPath);
        DungeonSession s = d.Session;
        var bot = new DungeonBot(false);
        for (int step = 0; step < 60 * 20 && s.RoomsCleared == 0; step++)
        {
            bool attack = bot.Next(s, out int dx, out int dy);
            s.Update(CombatScript.StepMs, dx, dy, attack);
        }
        Assert.Equal(1, s.RoomsCleared);
        Assert.True(s.IsCleared(0, 0));
        Assert.Equal(1, s.Kills);
        Assert.Equal(1, s.Current.AliveCount);                                    // 占位怪物还在
    }

    [Fact]
    public void TheBotKeepsItsTargetWhenTwoMonstersTakeTurnsBeingTheNearestOne()
    {
        // 两只怪物挤在一起(纵向差 6 像素),主角在它们之间来回挪:谁最近每一步都在换。自动机选定一只之后要一直盯着它
        // (寻路的状态按目标记,换人就清零,实测里会在原地来回晃一辈子)
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(),
            (cx, cy) => cx == 0 && cy == 0 ? new[] { (340, 100), (340, 106) } : DungeonSessionTests.None(), hp: 1_000_000);
        DungeonSession s = d.Session;
        var bot = new DungeonBot(false);
        var chosen = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            s.Hero.Place(300, i % 2 == 0 ? 102 : 104, false, HeroMotion.Rest, 0);
            bot.Next(s, out _, out _);
            string intent = bot.Intent ?? string.Empty;
            int at = intent.IndexOf("打怪 #", StringComparison.Ordinal);
            Assert.True(at >= 0, intent);
            chosen.Add(intent.Substring(at, 5));
        }
        Assert.Single(chosen.Distinct());
    }
}
