using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// skill-check 的两项补充检查(M104,状态 0x21、0x25、0x27 这几个状态族,证据 E-271、E-272):
/// (1) 按成长类型数"能放"的技能:子职业的技能默认主角(成长类型 0)学不了,所以逐个成长类型各建一个新主角、转职、花光点数,
///     把已实现的、学会了的主动技能在真实的房间会话里各放一遍(先按自己的指令打,指令放不出来的换快捷键放),再按技能号去重汇总;
/// (2) 状态族的放法在真实数据里走通:段首瞬移(落点按可走区域逐像素探、被动对象留在出发点)、逐击连段(起手自己接打击、按键接下一击、击数等于 [static data] 里的数、
///     不按键只打一击)、逐段不滑行(不滑的段横坐标一动不动)。
/// 只打印汇总数,不打印技能号、距离、击数等具体的数。
/// </summary>
internal static partial class SkillCheck
{
    /// <summary>一个技能放出来的结果:没放出来、按自己的指令放出来、改按快捷键放出来。</summary>
    private enum CastHow
    {
        None,
        ByCommand,
        ByQuick,
    }

    /// <summary>
    /// 按成长类型数能放的技能(M104)。每个成长类型:新主角转职、花光点数,取"已实现、学会了的主动技能"(不含倒地起身),
    /// 在新会话里先打自己的指令,没放出自己的换成快捷栏第 1 格按键放;汇总每个成长类型能放几个、按技能号去重一共几个。
    /// 通过条件:每个成长类型至少放得出一个、而且配方表里新增状态族的技能(带瞬移、逐击连段、逐段不滑行标记的)在能学会它的成长类型里都放得出来。
    /// </summary>
    private static void ReportGrowtypeCasts(DnfAssets assets, Options opts, SkillCatalog catalog, int level, Action<bool, string> report)
    {
        var castableAll = new HashSet<int>();
        var perGrowtype = new List<string>();
        var problems = new List<string>();
        int convertible = 0;
        var familyIds = new HashSet<int>(SkillRecipes.Default.All.Where(IsStateFamilyRecipe).Select(r => r.SkillId));
        int familyLearnable = 0;
        int familyCastable = 0;
        var familySeen = new HashSet<int>();
        for (int g = 0; g < SkillRules.GrowtypeCount; g++)
        {
            HeroProgress? hero = HeroProgress.CreateForLocalHero(assets, level);
            if (hero == null || (g > 0 && !hero.ChangeGrowtype(g)))
            {
                perGrowtype.Add(g + ":没转成");
                continue;
            }
            convertible++;
            SkillPlanner.SpendAll(hero);
            int ready = 0;
            int byCommand = 0;
            int byQuick = 0;
            int failed = 0;
            foreach (SkillDef def in catalog.Skills)
            {
                if (!def.IsActive || def.Id == SkillIds.QuickStand || !HeroSkills.IsImplemented(def.Id) || hero.Skills.LevelOf(def.Id) <= 0)
                {
                    continue;
                }
                ready++;
                bool isFamily = familyIds.Contains(def.Id);
                if (isFamily && familySeen.Add(def.Id))
                {
                    familyLearnable++;
                }
                CastHow how = TryCast(assets, opts, hero, def);
                if (how == CastHow.ByCommand)
                {
                    byCommand++;
                }
                else if (how == CastHow.ByQuick)
                {
                    byQuick++;
                }
                else
                {
                    failed++;
                    if (isFamily)
                    {
                        problems.Add("成长类型 " + g + " 的状态族技能没放出来(序号 " + familyIds.ToList().IndexOf(def.Id) + ")");
                    }
                    continue;
                }
                if (castableAll.Add(def.Id) && isFamily)
                {
                    familyCastable++;
                }
            }
            perGrowtype.Add(g + ":学会且已实现 " + ready + "、放出 " + (byCommand + byQuick) + "(指令 " + byCommand + "、快捷键 " + byQuick + ")、没放出 " + failed);
        }
        foreach (string line in problems)
        {
            Console.WriteLine("    " + line);
        }
        report(convertible == SkillRules.GrowtypeCount && castableAll.Count > 0 && problems.Count == 0 && familyCastable == familyLearnable && familyLearnable == familyIds.Count,
            "各成长类型能放的技能(M104):成长类型 " + string.Join(";", perGrowtype) + ";按技能号去重一共能放 " + castableAll.Count + " 个;"
            + "其中状态族(瞬移、逐击连段、逐段不滑行)的技能 " + familyIds.Count + " 个,在能学会的成长类型里学会 " + familyLearnable + " 个、放出 " + familyCastable + " 个");
    }

    /// <summary>配方带 M104 的状态族标记:某段有瞬移、某段不滑行或自己接下一段、或是逐击连段。</summary>
    private static bool IsStateFamilyRecipe(SkillRecipe recipe)
    {
        return recipe.Repeat != null || recipe.Stages.Any(st => st.Warp != null || !st.Slides || st.AutoAdvance);
    }

    /// <summary>一个主角放一次这个技能:先打自己的指令(能靠指令放的话),放不出自己的就把它放进快捷栏第 1 格、按那个键。悬停俯冲类技能先起跳。</summary>
    private static CastHow TryCast(DnfAssets assets, Options opts, HeroProgress hero, SkillDef def)
    {
        bool dive = SkillRecipes.Default.Find(def.Id)?.Dive != null;
        if (!def.Command.IsEmpty && HeroSkills.CommandCasts(def.Command))
        {
            RoomCombatSession session = NewSession(assets, opts, hero);
            if (dive)
            {
                LiftOff(session, 60.0);
            }
            foreach (CommandStep step in CommandSteps.Of(def.Command, session.Hero.FacingLeft))
            {
                session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
            }
            if (session.Hero.Motion == HeroMotion.Skill && session.Hero.SkillId == def.Id)
            {
                return CastHow.ByCommand;
            }
        }
        if (!hero.Skills.AssignQuick(0, def.Id))
        {
            return CastHow.None;
        }
        RoomCombatSession quick = NewSession(assets, opts, hero);
        HeroSkillSlot? slot = quick.Skills.SlotOfSkill(def.Id);
        if (slot == null || slot.Key == SkillKeys.None)
        {
            return CastHow.None;
        }
        if (dive)
        {
            LiftOff(quick, 60.0);
        }
        quick.Update(10, 0, 0, false, false, slot.Key);
        return quick.Hero.Motion == HeroMotion.Skill && quick.Hero.SkillId == def.Id ? CastHow.ByQuick : CastHow.None;
    }

    /// <summary>
    /// 状态族的放法在真实数据里走通(M104)。配方表里带状态族标记的每个技能,找一个学会它的主角、放进快捷栏第 1 格、在真实房间里放:
    /// 带段首瞬移的:瞬移那一段开始后主角的横坐标 = 起点按朝向逐像素往前探到第一个站不住的点之前(检查里自己再走一遍探点),放出来的"出发点对象"
    /// 留在出发点(横纵坐标等于瞬移前主角的);逐击连段的:不按键只打起手与第一击就回站立,按键接满后段数 = 击数 × 2、接段 = 段数 − 1、只算一次施放、
    /// 魔法值只扣一次、播完回站立,每个打击段里只在动画播过接段帧之后才接;标了"不滑行"的段:这一段里主角的横坐标一动不动。
    /// </summary>
    private static void ReportStateFamilies(DnfAssets assets, Options opts, SkillCatalog catalog, int level, Action<bool, string> report)
    {
        int recipes = 0;
        int warps = 0;
        int warpsLanded = 0;
        int originObjects = 0;
        int repeats = 0;
        int repeatFull = 0;
        int repeatSingle = 0;
        int stillStages = 0;
        int stillHeld = 0;
        var problems = new List<string>();
        foreach (SkillRecipe template in SkillRecipes.Default.All)
        {
            if (!IsStateFamilyRecipe(template))
            {
                continue;
            }
            recipes++;
            string label = "状态族 #" + recipes;
            SkillDef? def = catalog.Find(template.SkillId);
            HeroProgress? caster = def == null ? null : CasterWhoKnows(assets, level, def.Id);
            if (def == null || caster == null || !caster.Skills.AssignQuick(0, def.Id))
            {
                problems.Add(label + ":技能不在目录里、没学会或放不进快捷栏");
                continue;
            }
            bool isRepeat = template.Repeat != null;
            int hits = isRepeat ? template.Repeat!.HitsOf(def) : 1;
            if (isRepeat)
            {
                repeats++;
            }
            // 第一遍:一路按键(逐击连段在每个打击段过了接段帧就按一下技能键),看完整走完;第二遍(只有逐击连段):不按键
            foreach (bool pressKeys in isRepeat ? new[] { true, false } : new[] { false })
            {
                RoomCombatSession session = NewSession(assets, opts, caster);
                HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
                TownHero hero = session.Hero;
                if (slot == null || slot.Recipe == null || slot.Key == SkillKeys.None)
                {
                    problems.Add(label + ":没排进槽里");
                    break;
                }
                SkillRecipe recipe = slot.Recipe;
                int expectedStages = isRepeat ? hits * 2 : template.Stages.Count;
                if (recipe.Stages.Count != expectedStages)
                {
                    problems.Add(label + ":槽里的段数 " + recipe.Stages.Count + " 不等于预期的 " + expectedStages);
                    break;
                }
                double x0 = hero.X;
                double y0 = hero.Y;
                bool left = hero.FacingLeft;
                int mpBefore = session.Vitals?.Mp ?? 0;
                session.Update(10, 0, 0, false, false, slot.Key);
                if (!(hero.Motion == HeroMotion.Skill && hero.SkillId == def.Id && session.SkillCasts == 1 && hero.SkillStage == 0))
                {
                    problems.Add(label + ":没放出第一段");
                    break;
                }
                int stage = 0;
                int stageSeen = 0;
                double stageStartX = hero.X;
                bool pressedInStage = false;
                int ms = 0;
                var stillMoves = new List<double>();
                bool warpChecked = false;
                int warpStage = Array.FindIndex(recipe.Stages.ToArray(), st => st.Warp != null);
                if (warpStage == 0)
                {
                    problems.Add(label + ":第一段就瞬移,检查没有出发点可比");
                    break;
                }
                while ((hero.Motion == HeroMotion.Skill || hero.Motion == HeroMotion.Jump) && ms < 30000)
                {
                    double before = hero.X;
                    SkillKeys keys = SkillKeys.None;
                    SkillStageSpec current = recipe.Stages[Math.Min(hero.SkillStage, recipe.Stages.Count - 1)];
                    if (pressKeys && !pressedInStage && current.HasOwnStrike && hero.CurrentFrame >= recipe.ChainFromFrame && hero.SkillStage < recipe.Stages.Count - 1)
                    {
                        keys = slot.Key;
                        pressedInStage = true;
                    }
                    session.Update(10, 0, 0, false, false, keys);
                    ms += 10;
                    if (hero.Motion == HeroMotion.Skill && hero.SkillStage != stage)
                    {
                        // 上一段结束:记它是否一动不动;新一段开始
                        SkillStageSpec ended = recipe.Stages[Math.Min(stage, recipe.Stages.Count - 1)];
                        if (!ended.Slides && ended.Warp == null)
                        {
                            stillMoves.Add(before - stageStartX);
                        }
                        stage = hero.SkillStage;
                        stageSeen = Math.Max(stageSeen, stage);
                        pressedInStage = false;
                        stageStartX = hero.X;      // 这一段第一次看到时的横坐标(这一步里前一段还可能滑了一点,不算这一段的)
                        if (stage == warpStage && !warpChecked)
                        {
                            warpChecked = true;
                            warps++;
                            // 瞬移那一段刚开始:横坐标 = 检查里自己逐像素往前探出来的落点
                            SkillWarpSpec warp = recipe.Stages[stage].Warp!;
                            int distance = def.GetStatic(warp.DistanceStaticIndex, false);
                            double landing = before;
                            int sign = left ? -1 : 1;
                            for (int step = 1; step <= Math.Abs(distance); step++)
                            {
                                double next = before + sign * step * (distance < 0 ? -1 : 1);
                                if (!hero.Walk.Contains(next, y0) && !hero.Walk.IsUnrestricted)
                                {
                                    break;
                                }
                                landing = next;
                            }
                            // 这一步里没有别的滑行(瞬移段没有起滑),主角此刻就停在落点上
                            if (Math.Abs(hero.X - landing) < 0.001 && Math.Abs(hero.X - before) > 0.0)
                            {
                                warpsLanded++;
                            }
                            else
                            {
                                problems.Add(label + ":瞬移的落点与逐像素探出来的不一样(距离 " + distance + ")");
                            }
                            // 出发点对象:横纵坐标等于瞬移前主角的
                            foreach (SkillObjectSpec o in recipe.Objects.Where(o => o.FromWarpOrigin))
                            {
                                bool placed = session.HeroObjects.Any(obj => Math.Abs(obj.X - before) < 0.001 && Math.Abs(obj.Y - y0) < 0.001);
                                if (placed)
                                {
                                    originObjects++;
                                }
                                else
                                {
                                    problems.Add(label + ":出发点对象不在瞬移前的位置");
                                }
                            }
                        }
                    }
                }
                SkillStageSpec last = recipe.Stages[Math.Min(stage, recipe.Stages.Count - 1)];
                if (!last.Slides && last.Warp == null)
                {
                    stillMoves.Add(hero.X - stageStartX);
                }
                int chainsAtEnd = session.SkillChains;
                foreach (double moved in stillMoves)
                {
                    stillStages++;
                    if (moved == 0.0)
                    {
                        stillHeld++;
                    }
                }
                bool mpOnce = session.Vitals == null || session.Vitals.Mp == mpBefore - slot.ConsumeMp;
                bool endedAtRest = hero.Motion == HeroMotion.Rest && hero.SkillId == 0;
                if (pressKeys && isRepeat)
                {
                    bool ok = stageSeen == expectedStages - 1 && chainsAtEnd == expectedStages - 1 && session.SkillCasts == 1 && mpOnce && endedAtRest && slot.CooldownLeftMs > 0;
                    if (ok)
                    {
                        repeatFull++;
                    }
                    else
                    {
                        problems.Add(label + ":按键接满没走完(见到的段 " + stageSeen + "、接段 " + chainsAtEnd + "、施放 " + session.SkillCasts + ")");
                    }
                }
                else if (isRepeat)
                {
                    bool ok = stageSeen == 1 && chainsAtEnd == 1 && session.SkillCasts == 1 && mpOnce && endedAtRest;
                    if (ok)
                    {
                        repeatSingle++;
                    }
                    else
                    {
                        problems.Add(label + ":不按键该只打起手加一击(见到的段 " + stageSeen + "、接段 " + chainsAtEnd + ")");
                    }
                }
                else if (!(stageSeen == expectedStages - 1 && chainsAtEnd == expectedStages - 1 && session.SkillCasts == 1 && mpOnce && endedAtRest))
                {
                    problems.Add(label + ":没有走完各段(见到的段 " + stageSeen + "、接段 " + chainsAtEnd + ")");
                }
            }
        }
        foreach (string line in problems)
        {
            Console.WriteLine("    " + line);
        }
        report(recipes > 0 && problems.Count == 0 && warpsLanded == warps && repeatFull == repeats && repeatSingle == repeats && stillHeld == stillStages,
            "状态族的放法(M104):带标记的配方 " + recipes + " 个;段首瞬移 " + warps + " 个,落点与逐像素探点一致 " + warpsLanded + " 个、出发点对象留在原地 " + originObjects
            + " 个;逐击连段 " + repeats + " 个,按键接满走完 " + repeatFull + " 个、不按键只打一击回站立 " + repeatSingle + " 个;不滑行的段 " + stillStages + " 段,横坐标一动不动 " + stillHeld + " 段(问题 " + problems.Count + " 条)");
    }
}
