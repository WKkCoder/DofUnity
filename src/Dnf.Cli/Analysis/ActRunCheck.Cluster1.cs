using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

internal static partial class ActRunCheck
{
    /// <summary>
    /// 怪物未决簇 1(逻辑状态写入与触发器补全:[ON MOVE COLLISION]/[AXIS INDEX]/[CHECK GHOST]/[CHARGED TIME]/
    /// [SET VISIBLITY]/[SET GRABABLE]/[SET DAMAGE CHECK]/[SET UNBEATABLE]/[SET PASSTYPE]/[SET WIDTH]/
    /// [CHANGE TEAM]/[DEFAULT TEAM])的招式实跑统计(汇总数,本机,非 REA)。
    /// </summary>
    private sealed class LiveCluster1Tally
    {
        public const int MaxSteps = 3000;

        public int Monsters;
        public int NoModel;
        public int Attacks;
        public int NotEntered;
        public int Failed;
        public int StillRunning;
        public int StateChanged;
        public int VisibilityChanged;
        public int GrabableChanged;
        public int DamageCheckActivated;
        public int UnbeatableActivated;
        public int TeamChanged;
        public int PassivePassOrWidthSet;
        public readonly SortedDictionary<string, int> Errors = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Print()
        {
            Console.WriteLine("  未决簇 1 招式实跑(空场地、目标替身在右边 400 像素、每招最多 " + MaxSteps / 100
                + " 秒;含 [ON MOVE COLLISION]/[AXIS INDEX]/[CHECK GHOST]/[CHARGED TIME]/[SET VISIBLITY]/[SET GRABABLE]/[SET DAMAGE CHECK]/[SET UNBEATABLE]/[SET PASSTYPE]/[SET WIDTH]/[CHANGE TEAM]/[DEFAULT TEAM]):怪物 "
                + Monsters + " 只(模型读不出 " + NoModel + " 只),共 " + Attacks + " 招(没切进去 " + NotEntered
                + " 招,抛异常 " + Failed + " 招,到时间还在跑 " + StillRunning + " 招)");
            Console.WriteLine("    实跑观测到逻辑状态变更的招式 " + StateChanged + " 招(可见性 " + VisibilityChanged
                + ",可抓取 " + GrabableChanged + ",受击窗口 " + DamageCheckActivated + ",无敌 " + UnbeatableActivated
                + ",换队 " + TeamChanged + ",被动穿透或宽高 " + PassivePassOrWidthSet + ")"
                + (Errors.Count > 0 ? ";异常 " + Join(Errors, 6) : string.Empty));
        }

        public object ToJson() => new
        {
            Monsters, NoModel, Attacks, NotEntered, Failed, StillRunning,
            StateChanged, VisibilityChanged, GrabableChanged, DamageCheckActivated,
            UnbeatableActivated, TeamChanged, PassivePassOrWidthSet, Errors,
        };
    }

    private static bool IsCluster1Record(ActRecordKind kind)
    {
        return kind == ActRecordKind.OnMoveCollision
            || kind == ActRecordKind.AxisIndex
            || kind == ActRecordKind.CheckGhost
            || kind == ActRecordKind.ChargedTime;
    }

    private static bool IsCluster1Behavior(ActBehaviorKind kind)
    {
        return kind == ActBehaviorKind.SetVisibility
            || kind == ActBehaviorKind.SetGrabable
            || kind == ActBehaviorKind.SetDamageCheck
            || kind == ActBehaviorKind.SetUnbeatable
            || kind == ActBehaviorKind.SetPassType
            || kind == ActBehaviorKind.SetWidth
            || kind == ActBehaviorKind.ChangeTeam
            || kind == ActBehaviorKind.DefaultTeam;
    }

    private static bool HasCluster1Kind(ActProgram program)
    {
        foreach (ActTrigger t in program.Triggers)
        {
            foreach (ActRecord r in t.Records)
            {
                if (IsCluster1Record(r.Kind))
                {
                    return true;
                }
            }
        }
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (ActBehavior b in g.Items)
            {
                if (IsCluster1Behavior(b.Kind))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool ReachesCluster1Kind(ActLibrary library, ActProgram? program, Dictionary<int, bool> memo)
    {
        if (program == null)
        {
            return false;
        }
        if (HasCluster1Kind(program))
        {
            return true;
        }
        foreach (int index in SpawnIndices(program))
        {
            if (ObjectReachesCluster1Kind(library, index, memo))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ObjectReachesCluster1Kind(ActLibrary library, int index, Dictionary<int, bool> memo)
    {
        if (memo.TryGetValue(index, out bool known))
        {
            return known;
        }
        memo[index] = false;
        PassiveTemplate? template = library.Passive(index);
        bool found = false;
        if (template != null)
        {
            found = ReachesCluster1Kind(library, template.Basic.Program, memo);
            for (int n = 0; !found && n < template.EtcCount; n++)
            {
                PassiveAction? action = template.EtcAction(n);
                found = action != null && ReachesCluster1Kind(library, action.Program, memo);
            }
        }
        memo[index] = found;
        return found;
    }

    private static LiveCluster1Tally RunLiveCluster1(DnfAssets assets, GameData data, ActLibrary library)
    {
        var tally = new LiveCluster1Tally();
        var walk = new RectUnionArea(new List<WorldRect>());
        var target = new Stand(ActEntityKind.Character, 0, 700f, 100f);
        var memo = new Dictionary<int, bool>();
        foreach (int id in data.MonsterIds())
        {
            MonsterDef? def = data.GetMonster(id);
            if (def == null)
            {
                continue;
            }
            MonsterModel model;
            try
            {
                model = MonsterModel.Read(assets, def.Path);
            }
            catch (Exception)
            {
                tally.NoModel++;
                continue;
            }
            IReadOnlyList<string> paths = model.AttackActionPaths;
            var programs = new ActProgram?[paths.Count];
            var chosen = new List<int>();
            for (int i = 0; i < programs.Length; i++)
            {
                ActProgram? p = library.Program(paths[i]);
                if (p != null && p.Triggers.Count > 0)
                {
                    programs[i] = p;
                    if (ReachesCluster1Kind(library, p, memo))
                    {
                        chosen.Add(i);
                    }
                }
            }
            if (chosen.Count == 0)
            {
                continue;
            }
            tally.Monsters++;
            foreach (int a in chosen)
            {
                var actor = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 300, 100, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000000, 222.5, 1500, 0);
                actor.InitActGrabbable(def.Tags.IntOr("[is grabable]", 1) != 0);
                bool initGrab = actor.IsActGrabbable;
                int initTeam = actor.ActSelf.Team;
                var world = new LiveWorld(library, actor);
                actor.ActTarget = target;
                world.Heroes.Add(target);
                world.ResolvePassives = true;
                actor.SetActs(programs!, world);
                tally.Attacks++;
                bool sawVis = false;
                bool sawGrab = false;
                bool sawDmg = false;
                bool sawUnbeat = false;
                bool sawTeam = false;
                bool sawPassive = false;
                try
                {
                    actor.ActSelf.SetAction(ActActionKind.Attack, a, true, false);
                    if (actor.Phase != MonsterPhase.Attack)
                    {
                        tally.NotEntered++;
                        continue;
                    }
                    int steps = 0;
                    for (; steps < LiveCluster1Tally.MaxSteps; steps++)
                    {
                        bool attacking = actor.Phase == MonsterPhase.Attack;
                        if (attacking)
                        {
                            actor.Update(10, target.X, target.Y, true, walk);
                        }
                        world.Tick(10);
                        if (actor.ActVisibilityByte != 1) sawVis = true;
                        if (actor.IsActGrabbable != initGrab) sawGrab = true;
                        if (actor.DamageCheckActive) sawDmg = true;
                        if (actor.IsActUnbeatable) sawUnbeat = true;
                        if (actor.ActSelf.Team != initTeam) sawTeam = true;
                        foreach (PassiveObject o in world.All)
                        {
                            if (o.ActVisibilityByte != 1) sawVis = true;
                            if (o.ActPassType != 0 || o.ActWidthX != 0 || o.ActWidthY != 0) sawPassive = true;
                        }
                        if (!attacking && !AnyLive(world))
                        {
                            break;
                        }
                    }
                    if (steps >= LiveCluster1Tally.MaxSteps)
                    {
                        tally.StillRunning++;
                    }
                }
                catch (Exception e)
                {
                    tally.Failed++;
                    tally.Errors[e.GetType().Name] = tally.Errors.TryGetValue(e.GetType().Name, out int c) ? c + 1 : 1;
                    continue;
                }
                if (sawVis) tally.VisibilityChanged++;
                if (sawGrab) tally.GrabableChanged++;
                if (sawDmg) tally.DamageCheckActivated++;
                if (sawUnbeat) tally.UnbeatableActivated++;
                if (sawTeam) tally.TeamChanged++;
                if (sawPassive) tally.PassivePassOrWidthSet++;
                if (sawVis || sawGrab || sawDmg || sawUnbeat || sawTeam || sawPassive)
                {
                    tally.StateChanged++;
                }
            }
        }
        return tally;
    }
}
