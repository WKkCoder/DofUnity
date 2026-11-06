using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// skill-check 的"逐成长类型放得出来"一节(M102):成长类型 0 到 5 各新建一个主角、转职、花光点数,
/// 把"已实现、已学会的主动技能"逐个在真实的房间会话里放一遍——优先打各自的出招指令,打不出来(或指令只有单个键、只能靠快捷槽)就放进快捷栏第 1 格按键放;
/// 放出来的标准是主角进入技能动作、技能号就是它自己。前面几节的"指令放技能"只覆盖成长类型 0 的主角,这一节把另外五个成长类型的技能也算进来。
/// 身体命中一族的配方(<see cref="SkillRecipe.BodyHit"/>)另外要求:每个都放得出来,整段放完回到站立,按配方应有的接段、重武装与起滑次数发生。
/// 只打印汇总数,不打印技能编号。
/// </summary>
internal static class SkillCheckBodyHit
{
    private enum Way
    {
        None,
        Command,
        Quick,
    }

    public static int Run(DnfAssets assets, Options opts, SkillCatalog catalog, int level)
    {
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        var castableIds = new HashSet<int>();
        var perGrowtype = new List<string>();
        int readyTotal = 0;
        int viaCommand = 0;
        int viaQuick = 0;
        var bodyProblems = new List<string>();
        int bodySkills = 0;
        int bodyCycles = 0;
        int hitSkills = 0;
        int hitSkipped = 0;
        var hitProblems = new List<string>();
        for (int g = 0; g < SkillRules.GrowtypeCount; g++)
        {
            HeroProgress? known = Caster(assets, level, g);
            if (known == null)
            {
                perGrowtype.Add("-");
                continue;
            }
            int ready = 0;
            int cast = 0;
            foreach (SkillDef def in catalog.Skills)
            {
                if (!def.IsActive || def.Id == SkillIds.QuickStand || !HeroSkills.IsImplemented(def.Id) || known.Skills.LevelOf(def.Id) <= 0)
                {
                    continue;
                }
                ready++;
                Way way = TryCast(assets, opts, level, g, def);
                if (way != Way.None)
                {
                    cast++;
                    castableIds.Add(def.Id);
                    if (way == Way.Command)
                    {
                        viaCommand++;
                    }
                    else
                    {
                        viaQuick++;
                    }
                }
                SkillRecipe? recipe = SkillRecipes.Default.Find(def.Id);
                if (recipe?.BodyHit != null)
                {
                    bodySkills++;
                    string label = "身体命中 #" + bodySkills;
                    string? problem = way == Way.None ? "没有放出来" : PlayThrough(assets, opts, level, g, def, recipe);
                    if (problem == null)
                    {
                        bodyCycles++;
                    }
                    else
                    {
                        bodyProblems.Add(label + ":" + problem);
                    }
                    switch (way == Way.None ? HitResult.NoHit : HitCycle(assets, opts, level, g, def, recipe))
                    {
                        case HitResult.Hit:
                            hitSkills++;
                            break;
                        case HitResult.Skipped:
                            hitSkipped++;
                            break;
                        default:
                            hitProblems.Add(label + ":放出来之后在怪物身边没有打中");
                            break;
                    }
                }
            }
            readyTotal += ready;
            perGrowtype.Add(cast + "/" + ready);
        }
        foreach (string line in bodyProblems)
        {
            Console.WriteLine("    " + line);
        }
        Report(readyTotal > 0 && castableIds.Count > 0,
            "逐成长类型放得出来(成长类型 0 到 5 各转职、花光点数,已实现且学会的主动技能逐个真放):各成长类型 放出来/已实现 = " + string.Join(" ", perGrowtype) + ";合计 放出来 "
            + (viaCommand + viaQuick) + "/" + readyTotal + "(靠指令 " + viaCommand + "、靠快捷键 " + viaQuick + "),不同的技能 " + castableIds.Count + " 个");
        int expectedBody = SkillRecipes.Default.All.Count(r => r.BodyHit != null);
        Report(bodySkills == expectedBody && bodyProblems.Count == 0 && bodyCycles == bodySkills,
            "身体命中一族配方 " + expectedBody + " 个:各自的成长类型里都放得出来,整段放完回到站立、接段/重武装/起滑次数按配方应有的数发生,走完 " + bodyCycles + " 个(问题 " + bodyProblems.Count + " 条)");
        foreach (string line in hitProblems)
        {
            Console.WriteLine("    " + line);
        }
        Report(hitProblems.Count == 0 && hitSkills + hitSkipped == expectedBody,
            "身体命中一族配方在有怪物的房间里放出来之后:打中怪物 " + hitSkills + " 个、房间里没有怪物而跳过 " + hitSkipped + " 个、没打中 " + hitProblems.Count + " 个");
        return bad;
    }

    private enum HitResult
    {
        Skipped,
        Hit,
        NoHit,
    }

    /// <summary>
    /// 真实数据里放出来有没有打到人:在有怪物的房间里把主角摆到活怪物身边(面朝它,最多试三只),放进快捷栏第 1 格按键放,
    /// 配方有接段表的就在每一步按它认的键(攻击键优先,否则"技能"键)直到接到目标段,放完看会话的命中数。房间里没有怪物返回 <see cref="HitResult.Skipped"/>。
    /// </summary>
    private static HitResult HitCycle(DnfAssets assets, Options opts, int level, int growtype, SkillDef def, SkillRecipe recipe)
    {
        bool sawMonster = false;
        for (int pick = 0; pick < 3; pick++)
        {
            HeroProgress? progress = Caster(assets, level, growtype);
            if (progress == null || !progress.Skills.AssignQuick(0, def.Id))
            {
                return HitResult.NoHit;
            }
            RoomCombatSession session = SkillCheck.NewSession(assets, opts, progress);
            HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
            var alive = session.Monsters.Where(m => m.IsAlive).ToList();
            if (slot == null || slot.Key == SkillKeys.None || pick >= alive.Count)
            {
                break;
            }
            sawMonster = true;
            TownHero hero = session.Hero;
            MonsterActor target = alive[pick];
            hero.Teleport(target.X + (hero.FacingLeft ? 50.0 : -50.0), target.Y);
            session.Update(10, 0, 0, false, false, slot.Key);
            SkillBodyHitSpec body = recipe.BodyHit!;
            int chainTo = body.Chains.Count > 0 ? body.Chains.Max(c => c.ToStage) : 0;
            SkillChainKeys chainKeys = body.Chains.Count > 0 ? body.Chains[0].Keys : SkillChainKeys.None;
            for (int ms = 10; hero.Motion == HeroMotion.Skill && ms < 20000; ms += 10)
            {
                bool wantChain = hero.SkillStage < chainTo;
                bool attack = wantChain && (chainKeys & SkillChainKeys.AttackKey) != SkillChainKeys.None;
                SkillKeys keys = wantChain && !attack && (chainKeys & SkillChainKeys.SkillKey) != SkillChainKeys.None ? SkillKeys.CommandSkill : SkillKeys.None;
                session.Update(10, 0, 0, attack, false, keys);
            }
            if (session.Hits > 0)
            {
                return HitResult.Hit;
            }
        }
        return sawMonster ? HitResult.NoHit : HitResult.Skipped;
    }

    /// <summary>新建一个转到成长类型 <paramref name="growtype"/>、花光点数的主角;转不了返回 null。</summary>
    private static HeroProgress? Caster(DnfAssets assets, int level, int growtype)
    {
        HeroProgress? progress = HeroProgress.CreateForLocalHero(assets, level);
        if (progress == null || (growtype > 0 && !progress.ChangeGrowtype(growtype)))
        {
            return null;
        }
        SkillPlanner.SpendAll(progress);
        return progress;
    }

    /// <summary>在一局新会话里放一遍这个技能:能靠指令放的先打指令,没放出来(或只能靠快捷槽)再放进快捷栏第 1 格按键;悬停俯冲类先起跳升空。</summary>
    private static Way TryCast(DnfAssets assets, Options opts, int level, int growtype, SkillDef def)
    {
        SkillRecipe? recipe = SkillRecipes.Default.Find(def.Id);
        if (HeroSkills.CommandCasts(def.Command))
        {
            HeroProgress? progress = Caster(assets, level, growtype);
            if (progress != null)
            {
                RoomCombatSession session = SkillCheck.NewSession(assets, opts, progress);
                if (recipe?.Dive != null)
                {
                    SkillCheck.LiftOff(session, 60.0);
                }
                foreach (SkillCheck.CommandStep step in SkillCheck.CommandSteps.Of(def.Command, session.Hero.FacingLeft))
                {
                    session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                }
                if (session.Hero.Motion == HeroMotion.Skill && session.Hero.SkillId == def.Id)
                {
                    return Way.Command;
                }
            }
        }
        HeroProgress? quick = Caster(assets, level, growtype);
        if (quick == null || !quick.Skills.AssignQuick(0, def.Id))
        {
            return Way.None;
        }
        RoomCombatSession quickSession = SkillCheck.NewSession(assets, opts, quick);
        HeroSkillSlot? slot = quickSession.Skills.SlotOfSkill(def.Id);
        if (slot == null || slot.Key == SkillKeys.None)
        {
            return Way.None;
        }
        if (recipe?.Dive != null)
        {
            SkillCheck.LiftOff(quickSession, 60.0);
        }
        quickSession.Update(10, 0, 0, false, false, slot.Key);
        return quickSession.Hero.Motion == HeroMotion.Skill && quickSession.Hero.SkillId == def.Id ? Way.Quick : Way.None;
    }

    /// <summary>
    /// 把身体命中一族的技能放进快捷栏第 1 格按键放出来,不再按键地放完(接段键由配方决定:没有按键的情况下只会走自动接续的那几段),
    /// 放完回到站立、魔法值冷却只在起手算一次。返回问题描述,没问题返回 null。
    /// </summary>
    private static string? PlayThrough(DnfAssets assets, Options opts, int level, int growtype, SkillDef def, SkillRecipe recipe)
    {
        HeroProgress? progress = Caster(assets, level, growtype);
        if (progress == null || !progress.Skills.AssignQuick(0, def.Id))
        {
            return "排不进快捷栏";
        }
        RoomCombatSession session = SkillCheck.NewSession(assets, opts, progress);
        HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
        TownHero hero = session.Hero;
        if (slot == null || slot.Key == SkillKeys.None)
        {
            return "没有排进快捷槽";
        }
        int mpBefore = session.Vitals?.Mp ?? 0;
        session.Update(10, 0, 0, false, false, slot.Key);
        if (hero.Motion != HeroMotion.Skill || hero.SkillId != def.Id || session.SkillCasts != 1 || hero.SkillStage != 0)
        {
            return "按键没有放出第一段";
        }
        int stageSeen = 0;
        int ms = 10;
        while (hero.Motion == HeroMotion.Skill && ms < 20000)
        {
            session.Update(10, 0, 0, false, false, SkillKeys.None);
            ms += 10;
            stageSeen = Math.Max(stageSeen, hero.SkillStage);
        }
        SkillBodyHitSpec body = recipe.BodyHit!;
        int autoStages = body.AutoChainLastFromStage >= 0 ? body.AutoChainLastFromStage + 1 : (recipe.AutoChain ? recipe.Stages.Count - 1 : 0);
        if (hero.Motion == HeroMotion.Skill)
        {
            return "放了 20 秒仍没结束";
        }
        if (stageSeen != autoStages || session.SkillChains != autoStages)
        {
            return "不按键时见到的段 " + stageSeen + "、接段 " + session.SkillChains + " 次,按配方应当各是 " + autoStages;
        }
        if (hero.Z != 0.0 || session.Vitals != null && session.Vitals.Mp != mpBefore - slot.ConsumeMp || slot.CooldownLeftMs <= 0)
        {
            return "放完后离地高度、魔法值或冷却不对";
        }
        if (body.Rearms.Count == 0 && session.BodyRearms != 0)
        {
            return "配方没有定间隔重武装,会话却响了 " + session.BodyRearms + " 次";
        }
        return null;
    }
}
