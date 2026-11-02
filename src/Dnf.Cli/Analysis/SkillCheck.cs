using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// skill-check:学技能的检查(M35,证据 E-127)。整库读主角职业的技能目录与白送技能,检查结构与规则的自洽:
/// 1. 目录:技能列表读得出来;每个技能每个能学的等级都有花费(补齐规则);前置技能都在目录里且没有环;
/// 2. 白送:每个成长类型的白送技能都在目录里;
/// 3. 学满与退光:对每个成长类型,给无限点数从白送状态逐个学到当前等级能学的上限,点数守恒(花掉的 = 各级花费之和);
///    再全部退回白送的等级,点数完全还回;重置后与新建的一致;
/// 4. 接线:本地主角的进度里快捷栏与旧的"初始技能表"排法一致、点数 = 等级对应的总额;存档往返不变。
/// 数量、等级、点数来自用户自己的数据,只打印在本机的终端,不要抄进入库的文件;通过与否只看结构。
/// </summary>
internal static class SkillCheck
{
    private const int Unlimited = 1_000_000_000;

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        CharacterDef? chr = data.GetCharacter(0);
        if (chr == null)
        {
            Console.WriteLine("  [失败] 读不到 0 号角色");
            return 1;
        }
        int level = opts.HeroLevel > 1 ? opts.HeroLevel : Leveling.MaxLevel;

        // 1. 目录
        SkillCatalog catalog = SkillCatalog.Load(data, chr.Job);
        int listed = data.GetSkillIds(chr.Job).Length;
        Report(catalog.Count > 0 && catalog.Count == listed, "职业 " + chr.Job + " 的技能列表有 " + listed + " 个技能,读出 " + catalog.Count + " 个");
        if (catalog.Count == 0)
        {
            return 1;
        }
        int active = catalog.Skills.Count(s => s.IsActive);
        int special = catalog.Skills.Count(s => s.IsSpecial);
        Console.WriteLine("  主动 " + active + " 个、特殊 " + special + " 个;界面分页:"
            + string.Join(" ", Enumerable.Range(0, SkillRules.UiGroupCount).Select(g => g + "=" + catalog.Skills.Count(s => SkillRules.UiGroup(s) == g))));

        int noCost = 0;
        int noGrowtypeLevels = 0;
        int raised = 0;
        int rangeZeroMulti = 0;
        foreach (SkillDef def in catalog.Skills)
        {
            int best = def.GrowtypeMaxLevels.Length == 0 ? 0 : def.GrowtypeMaxLevels.Max();
            if (best == 0)
            {
                noGrowtypeLevels++;
            }
            if (def.EffectiveMaxLevel > def.MaximumLevel)
            {
                raised++;
            }
            if (best > 1 && def.RequiredLevelRange == 0)
            {
                rangeZeroMulti++;
            }
            for (int l = 1; l <= best; l++)
            {
                if (SkillRules.CostOfLevel(def, l) < 0)
                {
                    noCost++;
                    break;
                }
            }
        }
        Report(noCost == 0, "每个技能到各成长类型的最高级都有花费(补齐后;缺花费的 " + noCost + " 个)");
        Console.WriteLine("  没有任何成长类型能学的技能 " + noGrowtypeLevels + " 个;[maximum level] 低于成长类型最高级(被抬高)的 " + raised + " 个;多级却没有等级间隔的 " + rangeZeroMulti + " 个");

        int missingPre = 0;
        foreach (SkillDef def in catalog.Skills)
        {
            foreach (SkillPrereq pre in def.PreRequired)
            {
                if (catalog.Find(pre.SkillId) == null || pre.SkillId == def.Id)
                {
                    missingPre++;
                }
            }
        }
        Report(missingPre == 0, "前置技能都在目录里、没有指向自己(不合格的 " + missingPre + " 条)");
        Report(!HasCycle(catalog), "前置技能没有环");

        // 冷却列(M65,证据 E-163):带 [cooltime level info] 的技能没有 [cool time]、列号落在 [level info] 的列数里、各级读出来的冷却都是正数;只数不打印具体数
        int coolColumnSkills = 0;
        int coolColumnBad = 0;
        foreach (SkillDef def in catalog.Skills)
        {
            if (def.CoolTimeColumn < 0)
            {
                continue;
            }
            coolColumnSkills++;
            // 表按 [maximum level] 排满,可学的最高级(成长类型的上限)可能低得多;冷却随等级递减,表尾可以降到零以下,所以只查学得到的那些级
            int reachable = def.GrowtypeMaxLevels.Length == 0 ? def.MaximumLevel : Math.Min(def.MaximumLevel, def.GrowtypeMaxLevels.Max());
            bool coolOk = def.CoolTime.Length == 0 && def.CoolTimeColumn < def.LevelInfo.Columns && def.LevelInfo.IsConsistent && def.LevelInfo.Levels >= reachable;
            for (int lv = 1; coolOk && lv <= reachable; lv++)
            {
                coolOk = def.GetLevel(lv, def.CoolTimeColumn, false) > 0;
            }
            if (!coolOk)
            {
                coolColumnBad++;
            }
        }
        Report(coolColumnBad == 0, "带 [cooltime level info] 的技能 " + coolColumnSkills + " 个:都没有 [cool time]、列号落在 [level info] 的列里、学得到的各级读出来的冷却是正数(不合格的 " + coolColumnBad + " 个)");

        // 2. 白送
        int badGrant = 0;
        for (int g = 0; g < SkillRules.GrowtypeCount; g++)
        {
            foreach (SkillGrant grant in SkillGrants.Read(chr.Tags.Lines, g))
            {
                if (catalog.Find(grant.SkillId) == null)
                {
                    badGrant++;
                }
            }
        }
        Report(badGrant == 0, "六个成长类型的白送技能都在目录里(不在的 " + badGrant + " 个)");
        Console.WriteLine("  白送技能个数(按成长类型 0 到 5):" + string.Join(" ", Enumerable.Range(0, SkillRules.GrowtypeCount).Select(g => SkillGrants.Read(chr.Tags.Lines, g).Count)));

        // 3. 学满与退光
        Console.WriteLine("  学满演习(主角 " + level + " 级,无限点数):");
        for (int g = 0; g < SkillRules.GrowtypeCount; g++)
        {
            List<SkillGrant> grants = SkillGrants.Read(chr.Tags.Lines, g);
            var book = new SkillBook(catalog);
            book.SetGiven(grants);
            book.SetPoints(Unlimited, Unlimited);
            int sp0 = book.Sp;
            int sfp0 = book.Sfp;
            // 反复扫描,每遍每个技能学一级,直到一遍里没有任何进展(前置技能先学满才开得出后面的)
            bool progress = true;
            int rounds = 0;
            while (progress && rounds < 200)
            {
                progress = false;
                rounds++;
                foreach (SkillDef def in catalog.Skills)
                {
                    if (book.Learn(def.Id, level, g, 1, out _) == SkillOpResult.Ok)
                    {
                        progress = true;
                    }
                }
            }
            long expectedSp = 0;
            long expectedSfp = 0;
            int atCap = 0;
            int learnable = 0;
            foreach (SkillDef def in catalog.Skills)
            {
                int cur = book.LevelOf(def.Id);
                int cap = SkillRules.MaxLearnable(def, level, g);
                if (cap > 0)
                {
                    learnable++;
                    if (cur >= cap)
                    {
                        atCap++;
                    }
                }
                long cost = SkillRules.NeedPoints(def, book.GivenLevelOf(def.Id), cur);
                if (def.IsSpecial)
                {
                    expectedSfp += Math.Max(0, cost);
                }
                else
                {
                    expectedSp += Math.Max(0, cost);
                }
            }
            bool conserved = sp0 - book.Sp == expectedSp && sfp0 - book.Sfp == expectedSfp;
            Report(conserved, "成长类型 " + g + ":学了 " + book.LearnedCount + " 个技能(当前等级能学的 " + learnable + " 个、学到上限的 " + atCap + " 个),花技能点 "
                + (sp0 - book.Sp) + "、特殊点 " + (sfp0 - book.Sfp) + ",与各级花费之和一致");

            // 退光:反复扫描,每遍每个技能退一级,直到没有进展;应回到白送的等级、点数全部还回
            progress = true;
            rounds = 0;
            while (progress && rounds < 400)
            {
                progress = false;
                rounds++;
                foreach (SkillDef def in catalog.Skills)
                {
                    if (book.Refund(def.Id, 1, out _) == SkillOpResult.Ok)
                    {
                        progress = true;
                    }
                }
            }
            bool restored = book.Sp == sp0 && book.Sfp == sfp0 && book.LearnedCount == grants.Count
                && grants.All(grant => book.LevelOf(grant.SkillId) == grant.Level);
            Report(restored, "成长类型 " + g + ":全部退回白送等级后点数完全还回、技能只剩白送的 " + grants.Count + " 个");

            // 重置:点数设成总额、技能回到白送
            book.Reset(grants, SkillRules.TotalSp(data.SpTable, level), SkillRules.TotalSfp(level));
            Report(book.LearnedCount == grants.Count && book.Sp == SkillRules.TotalSp(data.SpTable, level), "成长类型 " + g + ":重置后只剩白送技能,点数是 " + level + " 级的总额");
        }

        // 4. 接线
        HeroProgress? hero = HeroProgress.CreateForLocalHero(assets, level);
        if (hero == null)
        {
            Report(false, "读不到本地主角的进度");
            return 1;
        }
        Report(hero.HasSkillData && hero.SkillPoints == SkillRules.TotalSp(data.SpTable, level) && hero.AwakeningPoints == SkillRules.TotalSfp(level),
            "本地主角 " + level + " 级的技能点与觉醒点是该级的总额(" + hero.SkillPoints + "/" + hero.AwakeningPoints + ")");
        HeroSkills legacy = HeroSkills.Create(data, chr, chr.Job);
        HeroSkills fromBook = HeroSkills.Create(data, chr, chr.Job, hero.Skills);
        Report(legacy.Slots.Select(s => s.SkillId).SequenceEqual(fromBook.Slots.Select(s => s.SkillId))
            && legacy.Slots.Select(s => s.Key).SequenceEqual(fromBook.Slots.Select(s => s.Key))
            && legacy.Slots.Select(s => s.Level).SequenceEqual(fromBook.Slots.Select(s => s.Level)),
            "新建主角的快捷栏与旧的初始技能表排法一致(" + fromBook.Slots.Count + " 个槽)");

        // 学几个再存档往返
        int learned = 0;
        foreach (SkillDef def in catalog.Skills)
        {
            if (learned < 6 && !def.IsSpecial && hero.LearnSkill(def.Id, 1, out _) == SkillOpResult.Ok)
            {
                learned++;
            }
        }
        string text = hero.Snapshot().ToText();
        Report(HeroSave.TryParse(text, out HeroSave? parsed, out string error), "存档文本读得回来" + (error == null ? "" : "(" + error + ")"));
        if (parsed != null)
        {
            HeroProgress? other = HeroProgress.CreateForLocalHero(assets, 1);
            other?.Restore(parsed, _ => null);
            Report(other != null && other.Snapshot().ToText() == text, "还原后再拍的存档与原来一模一样(学了 " + learned + " 个技能的一级)");
        }

        // 5. 自动机策略(--learn):把点数花光
        HeroProgress? planned = HeroProgress.CreateForLocalHero(assets, level);
        if (planned != null)
        {
            int sp0 = planned.SkillPoints;
            int ap0 = planned.AwakeningPoints;
            int added = SkillPlanner.SpendAll(planned);
            long spentSp = 0;
            long spentAp = 0;
            int activeLearned = 0;
            foreach (SkillDef def in catalog.Skills)
            {
                int now = planned.Skills.LevelOf(def.Id);
                int cost = SkillRules.NeedPoints(def, planned.Skills.GivenLevelOf(def.Id), now);
                if (cost > 0)
                {
                    if (def.IsSpecial)
                    {
                        spentAp += cost;
                    }
                    else
                    {
                        spentSp += cost;
                    }
                }
                if (now > 0 && def.IsActive)
                {
                    activeLearned++;
                }
            }
            bool conservedPlan = sp0 - planned.SkillPoints == spentSp && ap0 - planned.AwakeningPoints == spentAp;
            int stillLearnable = SkillPlanner.Order(catalog, planned.Growtype)
                .Count(def => planned.Skills.CheckLearn(def.Id, planned.Level, planned.Growtype, 1, out _) == SkillOpResult.Ok);
            bool idempotent = SkillPlanner.SpendAll(planned) == 0;
            int filled = Enumerable.Range(0, SkillBook.QuickSlots).Count(i => planned.Skills.QuickAt(i) > 0);
            Report(added > 0 && conservedPlan && stillLearnable == 0 && idempotent && filled == Math.Min(SkillBook.QuickSlots, activeLearned),
                "自动机策略把点数花光:加了 " + added + " 级,技能点 " + sp0 + " → " + planned.SkillPoints + ",觉醒点 " + ap0 + " → " + planned.AwakeningPoints
                + ",花费与各技能加的级数对得上=" + conservedPlan + ",停下时还能再学一级的 " + stillLearnable + " 个,再跑一遍不再加=" + idempotent
                + ",快捷栏排了 " + filled + " 格(学会的主动技能 " + activeLearned + " 个)");
        }

        // 6. 技能配方(M37,证据 E-129;M42 补对象与蓄力,证据 E-135):配方表里的每个技能在真实数据里都读得通——技能在目录里且是主动技能、[static data] 与 [level info] 的列数够用、
        // 角色的 [etc motion] 与 [etc attack info] 的下标落在表内且文件在库里、特效列表够长且每个动画文件都在库里、要求的技能在目录里、1 级的逐段伤害数读得出;
        // 放被动对象的配方:.obj 读得到、基础动画在库里且有攻击框、攻击信息在库里、放出的帧不越界、两个伤害列读出来是正数;有蓄力的段:第 0 帧确实是长停留、各级的蓄力不为负
        SkillEffectTable effects = SkillEffectTable.Load(data, SkillEffectTable.SwordmanJob);
        bool InLibrary(string? path) => path != null && assets.Pvf.TryGetEntry(path, out _);
        int recipeCount = 0;
        int recipeStages = 0;
        int recipeObjects = 0;
        int recipeHolds = 0;
        int recipeEndFrames = 0;
        int recipeGates = 0;
        int recipeHops = 0;
        int recipeMultiHits = 0;
        int recipeDives = 0;
        int recipeJumpAttacks = 0;
        int recipeAutoChains = 0;
        int recipeFlagEvents = 0;
        int recipeRises = 0;
        var recipeProblems = new List<string>();
        foreach (SkillRecipe recipe in SkillRecipes.Default.All)
        {
            recipeCount++;
            recipeStages += recipe.Stages.Count;
            string label = "配方 #" + recipeCount;
            SkillDef? def = catalog.Find(recipe.SkillId);
            if (def == null || !def.IsActive)
            {
                recipeProblems.Add(label + ":技能不在目录里或不是主动技能");
                continue;
            }
            int needStatic = new[] { recipe.SlideSpeedIndex, recipe.SlideAccelIndex, recipe.ForwardSpeedIndex, recipe.ForwardAccelIndex }.Max() + 1;
            if (recipe.Hop != null)
            {
                SkillHopSpec hopSpec = recipe.Hop;
                needStatic = Math.Max(needStatic, new[] { hopSpec.HoldMinIndex, hopSpec.HoldMaxIndex, hopSpec.DistanceMinIndex, hopSpec.DistanceMaxIndex,
                    hopSpec.HeightMinIndex, hopSpec.HeightMaxIndex, hopSpec.SpeedMinIndex, hopSpec.SpeedMaxIndex, hopSpec.HitsMinIndex, hopSpec.HitsMaxIndex }.Max() + 1);
            }
            if (recipe.MultiHitPassiveSkillId > 0)
            {
                needStatic = Math.Max(needStatic, recipe.MultiHitCountIndex + 1);
            }
            if (def.StaticData.Length < needStatic)
            {
                recipeProblems.Add(label + ":[static data] 只有 " + def.StaticData.Length + " 个数,配方要 " + needStatic + " 个");
            }
            int needColumns = Math.Max(recipe.Stages.Max(st => Math.Max(st.DamageRateColumn, st.DamageFixedColumn)),
                recipe.Objects.Count == 0 ? -1 : recipe.Objects.Max(o => Math.Max(o.DamageRateColumn, o.DamageFixedColumn))) + 1;
            if (def.LevelInfo.Columns < needColumns || !def.LevelInfo.IsConsistent)
            {
                recipeProblems.Add(label + ":[level info] 只有 " + def.LevelInfo.Columns + " 列,配方要 " + needColumns + " 列");
            }
            IReadOnlyList<string> effectPaths = effects.EffectsOf(recipe.SkillId);
            for (int i = 0; i < recipe.Stages.Count; i++)
            {
                SkillStageSpec st = recipe.Stages[i];
                if (recipe.Dive == null && recipe.JumpAttack == null && !InLibrary(chr.CustomAnimationPath(st.MotionIndex)))      // 悬停俯冲没有 [etc motion] 的下标(动画是空中攻击动作,下面单独查)
                {
                    recipeProblems.Add(label + " 第 " + i + " 段:动作读不到");
                }
                if (st.HasOwnStrike && !InLibrary(chr.CustomAttackInfoPath(st.AttackInfoIndex)))
                {
                    recipeProblems.Add(label + " 第 " + i + " 段:攻击信息读不到");
                }
                if (!st.HasOwnStrike && recipe.Objects.Count == 0 && recipe.Hop == null && recipe.JumpAttack == null && !(recipe.AutoChain && recipe.Stages.Any(other => other.HasOwnStrike)))
                {
                    // (姿势→挥击类的姿势段:没有命中也不放对象,后面有一段自己的命中,M65,E-163)
                    recipeProblems.Add(label + " 第 " + i + " 段:没有自己的命中,配方却没有放对象");
                }
                foreach (int effectIndex in st.EffectIndices)
                {
                    if (effectIndex >= effectPaths.Count || !InLibrary(PvfPath.ResolveRelativeTo(chr.Path, effectPaths[effectIndex])))
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:特效读不到(预载表里有 " + effectPaths.Count + " 个)");
                    }
                }
                if (st.EndFrame >= 0)
                {
                    recipeEndFrames++;
                    AniFile? endMotion = assets.LoadAni(chr.CustomAnimationPath(st.MotionIndex) ?? "");
                    if (endMotion == null || st.EndFrame >= endMotion.Frames.Length)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:结束帧 " + st.EndFrame + " 超出动作的帧数 " + (endMotion == null ? 0 : endMotion.Frames.Length));
                    }
                }
                if (st.RequiresSkill != 0 && catalog.Find(st.RequiresSkill) == null)
                {
                    recipeProblems.Add(label + " 第 " + i + " 段:要求的技能不在目录里");
                }
                if (st.FlagEvents.Count > 0)
                {
                    // 关键帧标志事件(M65,证据 E-163):这一段的动画里确有带这个标志的帧(否则事件永远不触发);换命中的事件,攻击信息读得到、
                    // 伤害列在 [level info] 里、1 级的百分比是正数;这一段自己有命中(事件是在原来的命中上开新窗口);只数不打印标志之外的东西
                    AniFile? flagMotion = assets.LoadAni(chr.CustomAnimationPath(st.MotionIndex) ?? "");
                    if (!st.HasOwnStrike)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:带关键帧标志事件,但这一段没有自己的命中");
                    }
                    foreach (SkillFlagEvent ev in st.FlagEvents)
                    {
                        recipeFlagEvents++;
                        if (flagMotion == null || !flagMotion.Frames.Any(fr => fr.TryGetKeyFrameFlag(out int kf) && kf == ev.Flag))
                        {
                            recipeProblems.Add(label + " 第 " + i + " 段:动作里没有带标志 " + ev.Flag + " 的帧");
                        }
                        if (ev.AttackInfoIndex >= 0 && !InLibrary(chr.CustomAttackInfoPath(ev.AttackInfoIndex)))
                        {
                            recipeProblems.Add(label + " 第 " + i + " 段:标志 " + ev.Flag + " 的事件换的攻击信息读不到");
                        }
                        if (Math.Max(ev.DamageRateColumn, ev.DamageFixedColumn) >= def.LevelInfo.Columns)
                        {
                            recipeProblems.Add(label + " 第 " + i + " 段:标志 " + ev.Flag + " 的事件换的伤害列超出 [level info] 的 " + def.LevelInfo.Columns + " 列");
                        }
                        else if (ev.DamageRateColumn >= 0 && def.GetLevel(1, ev.DamageRateColumn, false) <= 0)
                        {
                            recipeProblems.Add(label + " 第 " + i + " 段:标志 " + ev.Flag + " 的事件换的百分比列 1 级读出来不是正数");
                        }
                    }
                }
                if (st.Rise != null)
                {
                    // 脚本升空(M65,证据 E-163):三个 [static data] 下标都在、升空高度与升空毫秒为正、后滑不为负;动作至少两帧(升空时间到了把动画拨到第 1 帧),
                    // 第 0 帧带攻击框(升空期间动画停在这一帧上,命中就是这一帧的框);这一段自己有命中。只数不打印具体的数
                    recipeRises++;
                    SkillRiseSpec rise = st.Rise;
                    int[] riseIndices = { rise.HeightStaticIndex, rise.DurationStaticIndex, rise.BackStaticIndex };
                    if (riseIndices.Any(ix => ix < 0 || ix >= def.StaticData.Length))
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:升空要的 [static data] 下标超出技能的 " + def.StaticData.Length + " 个数");
                    }
                    else if (def.GetStatic(rise.HeightStaticIndex, false) <= 0 || def.GetStatic(rise.DurationStaticIndex, false) <= 0 || def.GetStatic(rise.BackStaticIndex, false) < 0)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:升空高度、升空时间不是正数,或后滑距离是负数");
                    }
                    AniFile? riseMotion = assets.LoadAni(chr.CustomAnimationPath(st.MotionIndex) ?? "");
                    if (riseMotion == null || riseMotion.Frames.Length < 2 || !riseMotion.Frames[0].Boxes.Any(b => b.Type == AniBoxType.Attack))
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:升空段的动作不足两帧,或第 0 帧没有攻击框");
                    }
                    if (!st.HasOwnStrike)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:升空段没有自己的命中");
                    }
                }
                if (st.HasOwnStrike && def.GetLevel(1, st.DamageRateColumn, false) <= 0)
                {
                    recipeProblems.Add(label + " 第 " + i + " 段:1 级的百分比伤害读出来不是正数");
                }
                if (st.CastHoldFrames > 0)
                {
                    recipeHolds++;
                    AniFile? cast = assets.LoadAni(chr.CustomAnimationPath(st.MotionIndex) ?? "");
                    int frames = cast == null ? 0 : cast.Frames.Length;
                    int firstDelay = 0;
                    int following = 0;
                    if (cast != null && frames > st.CastHoldFrames)
                    {
                        cast.Frames[0].TryGetDelayMs(out firstDelay);
                        for (int f = 1; f <= st.CastHoldFrames; f++)
                        {
                            cast.Frames[f].TryGetDelayMs(out int d);
                            following += d;
                        }
                    }
                    if (cast == null || frames <= st.CastHoldFrames)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:蓄力要减 " + st.CastHoldFrames + " 帧,动画只有 " + frames + " 帧");
                    }
                    else if (firstDelay < 1000)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:有蓄力,第 0 帧的停留却只有 " + firstDelay + " 毫秒(不像蓄力占位)");
                    }
                    int negative = 0;
                    for (int lv = 1; lv <= Math.Max(1, def.MaximumLevel); lv++)
                    {
                        if (def.Interpolate(def.CastingTime, lv) - following < 0)
                        {
                            negative++;
                        }
                    }
                    if (negative > 0)
                    {
                        recipeProblems.Add(label + " 第 " + i + " 段:有 " + negative + " 个等级的施法时间不够减(蓄力会取 0)");
                    }
                }
            }
            if (recipe.Hop != null)
            {
                // 蓄力跳跃(M45,证据 E-138):十个 [static data] 都在、最长蓄力大于最短、距离、高度与窗口数的档位 0 一端不大于档位 100 一端(变速的两个数不要求前小后大);
                // 蓄力档位 0、50、100 下距离与高度不递减、变速为正、窗口数不小于 1;
                // 重武装窗口的攻击信息在库里;跳跃动作里四个关键帧都在动画里、先后合理,命中帧里确有攻击框(具体数不打印)
                recipeHops++;
                SkillHopSpec h = recipe.Hop;
                int Stat(int index) => def.GetStatic(index, false);
                if (Stat(h.HoldMaxIndex) <= Stat(h.HoldMinIndex))
                {
                    recipeProblems.Add(label + " 蓄力跳跃:最长蓄力不大于最短蓄力");
                }
                foreach ((int lo, int hi, string name) in new[]
                {
                    (h.DistanceMinIndex, h.DistanceMaxIndex, "距离"), (h.HeightMinIndex, h.HeightMaxIndex, "高度"), (h.HitsMinIndex, h.HitsMaxIndex, "窗口数"),
                })
                {
                    if (Stat(lo) > Stat(hi))
                    {
                        recipeProblems.Add(label + " 蓄力跳跃:" + name + "的档位 0 一端大于档位 100 一端");
                    }
                }
                int lastDistance = int.MinValue;
                int lastHeight = int.MinValue;
                foreach (int charge in new[] { 0, 50, 100 })
                {
                    int distance = SkillHopPlan.IntLerp(Stat(h.DistanceMinIndex), Stat(h.DistanceMaxIndex), charge, 100);
                    int height = SkillHopPlan.IntLerp(Stat(h.HeightMinIndex), Stat(h.HeightMaxIndex), charge, 100);
                    int speed = SkillHopPlan.IntLerp(Stat(h.SpeedMinIndex), Stat(h.SpeedMaxIndex), charge, 100);
                    int hits = SkillHopPlan.IntLerp(Stat(h.HitsMinIndex), Stat(h.HitsMaxIndex), charge, 100);
                    if (distance < lastDistance || height < lastHeight || distance <= 0 || height <= 0 || speed <= 0 || hits < 1)
                    {
                        recipeProblems.Add(label + " 蓄力跳跃:蓄力档位 " + charge + " 的距离、高度、变速或窗口数不合理(不递减、为正、窗口数不小于 1)");
                    }
                    lastDistance = distance;
                    lastHeight = height;
                }
                if (!InLibrary(chr.CustomAttackInfoPath(h.RepeatAttackInfoIndex)))
                {
                    recipeProblems.Add(label + " 蓄力跳跃:重武装窗口的攻击信息读不到");
                }
                AniFile? jump = assets.LoadAni(chr.CustomAnimationPath(recipe.Stages[1].MotionIndex) ?? "");
                int jumpFrames = jump == null ? 0 : jump.Frames.Length;
                if (jump == null || !(h.RiseFrame < h.LandFrame && h.LandFrame < jumpFrames && h.HitStartFrame < h.HitEndFrame && h.HitEndFrame < jumpFrames))
                {
                    recipeProblems.Add(label + " 蓄力跳跃:跳跃动作的帧数(" + jumpFrames + ")装不下上升、落地与命中的关键帧,或它们的先后不对");
                }
                else if (!Enumerable.Range(h.HitStartFrame, h.HitEndFrame - h.HitStartFrame).Any(f => jump.Frames[f].Boxes.Any(b => b.Type == AniBoxType.Attack)))
                {
                    recipeProblems.Add(label + " 蓄力跳跃:命中帧范围里没有任何攻击框");
                }
            }
            if (recipe.MultiHitPassiveSkillId > 0)
            {
                // 多段命中(M46,证据 E-139):要求的被动技能在目录里且确是被动技能、次数在 [static data] 里取得到且为正、整段动画时长不小于次数(间隔至少 1 毫秒);具体数不打印
                recipeMultiHits++;
                SkillDef? passive = catalog.Find(recipe.MultiHitPassiveSkillId);
                if (passive == null || !string.Equals(passive.Type, "[passive]", StringComparison.OrdinalIgnoreCase))
                {
                    recipeProblems.Add(label + " 多段命中:要求的被动技能不在目录里或不是被动技能");
                }
                int windows = def.GetStatic(recipe.MultiHitCountIndex, false);
                AniFile? hitMotion = assets.LoadAni(chr.CustomAnimationPath(recipe.Stages[0].MotionIndex) ?? "");
                int motionMs = 0;
                for (int f = 0; hitMotion != null && f < hitMotion.Frames.Length; f++)
                {
                    hitMotion.Frames[f].TryGetDelayMs(out int d);
                    motionMs += d;
                }
                if (windows < 1 || motionMs < windows)
                {
                    recipeProblems.Add(label + " 多段命中:次数不是正数,或动画时长装不下这些次数");
                }
            }
            if (recipe.Dive != null)
            {
                // 悬停俯冲(M55,证据 E-149):只有一段;悬停毫秒、水平速度为正、竖直速度为负(引擎常量);[static data] 里"落地放对象的最小离地高度"那个数读得出来且为正;
                // 预载表里有指定的特效且动画在库里;角色脚本里有空中攻击动作与坐姿动作([jumpattack motion]、[sit motion]),动画在库里且有帧(俯冲定在空中攻击动作的最后一帧);
                // 每一个"落地放"的对象都有:这里只数不打印具体的数
                recipeDives++;
                SkillDiveSpec dv = recipe.Dive;
                if (recipe.Stages.Count != 1)
                {
                    recipeProblems.Add(label + " 悬停俯冲:配方不止一段");
                }
                if (dv.HoverMs <= 0 || dv.SpeedX <= 0 || dv.SpeedZ >= 0)
                {
                    recipeProblems.Add(label + " 悬停俯冲:悬停毫秒、水平速度或竖直速度的符号不对");
                }
                if (def.GetStatic(dv.ImpactHeightIndex, false) <= 0)
                {
                    recipeProblems.Add(label + " 悬停俯冲:落地放对象的最小离地高度读出来不是正数");
                }
                if (dv.EffectIndex >= 0 && (dv.EffectIndex >= effectPaths.Count || !InLibrary(PvfPath.ResolveRelativeTo(chr.Path, effectPaths[dv.EffectIndex]))))
                {
                    recipeProblems.Add(label + " 悬停俯冲:俯冲特效读不到(预载表里有 " + effectPaths.Count + " 个)");
                }
                foreach (string tag in new[] { "[jumpattack motion]", "[sit motion]" })
                {
                    string? motionPath = null;
                    foreach (ScriptLine line in chr.Tags.Lines)
                    {
                        if (line.TagIs(tag))
                        {
                            string? rel = line.FirstString();
                            motionPath = string.IsNullOrEmpty(rel) ? null : PvfPath.ResolveRelativeTo(chr.Path, rel);
                            break;
                        }
                    }
                    AniFile? ani = motionPath == null ? null : assets.LoadAni(motionPath);
                    if (ani == null || ani.Frames.Length == 0)
                    {
                        recipeProblems.Add(label + " 悬停俯冲:角色脚本里的 " + tag + " 读不到或没有帧");
                    }
                }
                if (!recipe.Objects.Any(ob => ob.OnLanding))
                {
                    recipeProblems.Add(label + " 悬停俯冲:没有\"落地放\"的对象");
                }
            }
            if (recipe.AutoChain)
            {
                // 自动接续(M60,证据 E-154):每一段的动作读得到、有帧;配方说"身体打人"的段,动画里确有攻击框,说"身体不打人"的段,动画里一个攻击框也没有
                // (否则要么丢了真实的命中,要么多出不该有的命中);放对象的段下标落在配方里。只数不打印具体的动作与帧
                recipeAutoChains++;
                for (int i = 0; i < recipe.Stages.Count; i++)
                {
                    AniFile? stageAni = assets.LoadAni(chr.CustomAnimationPath(recipe.Stages[i].MotionIndex) ?? "");
                    if (stageAni == null || stageAni.Frames.Length == 0)
                    {
                        recipeProblems.Add(label + " 自动接续:第 " + i + " 段动作读不到或没有帧");
                        continue;
                    }
                    bool hasBox = stageAni.Frames.Any(fr => fr.Boxes.Any(b => b.Type == AniBoxType.Attack));
                    if (hasBox != recipe.Stages[i].HasOwnStrike)
                    {
                        recipeProblems.Add(label + " 自动接续:第 " + i + " 段动画里" + (hasBox ? "有" : "没有") + "攻击框,配方却标" + (recipe.Stages[i].HasOwnStrike ? "有" : "没有") + "自己的命中");
                    }
                }
                if (recipe.Objects.Any(o => o.Stage < 1))
                {
                    recipeProblems.Add(label + " 自动接续:有对象在第一段就放(状态码里它是接上第二段那一刻放的)");
                }
            }
            if (recipe.JumpAttack != null)
            {
                // 空中攻击强化(M56,证据 E-150):配方只有一段、不带自己的攻击信息(借空中攻击的,伤害取 [level info] 的百分比列);指令是单独一个攻击键;
                // 两种动作的 [etc motion] 下标落在表内、动画在库里、帧数够到特效帧(攻击框在武器图层里,由下面的真实会话检查);两种放的预载特效下标都在预载表里、动画在库里;1 级的百分比读出来是正数;具体数不打印
                recipeJumpAttacks++;
                SkillJumpAttackSpec ja = recipe.JumpAttack;
                if (recipe.Stages.Count != 1 || recipe.Stages[0].HasOwnStrike)
                {
                    recipeProblems.Add(label + " 空中攻击强化:配方不是借空中攻击信息的单段");
                }
                if (def.Command.IsEmpty || def.Command.Length != 1 || def.Command.Trigger != CommandKey.Attack)
                {
                    recipeProblems.Add(label + " 空中攻击强化:指令不是单独的攻击键");
                }
                foreach (int motionIndex in new[] { ja.MotionIndexA, ja.MotionIndexB })
                {
                    AniFile? m = assets.LoadAni(chr.CustomAnimationPath(motionIndex) ?? "");
                    if (m == null || m.Frames.Length <= ja.EffectFrame)
                    {
                        recipeProblems.Add(label + " 空中攻击强化:动作读不到,或帧数不到特效帧");
                    }
                }
                foreach (int effectIndex in ja.EffectsA.Concat(ja.EffectsB))
                {
                    if (effectIndex >= effectPaths.Count || !InLibrary(PvfPath.ResolveRelativeTo(chr.Path, effectPaths[effectIndex])))
                    {
                        recipeProblems.Add(label + " 空中攻击强化:特效读不到(预载表里有 " + effectPaths.Count + " 个)");
                    }
                }
                if (def.GetLevel(1, recipe.Stages[0].DamageRateColumn, false) <= 0)
                {
                    recipeProblems.Add(label + " 空中攻击强化:1 级的百分比伤害读出来不是正数");
                }
            }
            for (int i = 0; i < recipe.Objects.Count; i++)
            {
                SkillObjectSpec o = recipe.Objects[i];
                recipeObjects++;
                Dnf.Game.PassiveObjectDef? objectDef = data.GetPassiveObject(o.ObjectId);
                if (objectDef == null)
                {
                    recipeProblems.Add(label + " 对象 " + i + ":.obj 读不到");
                    continue;
                }
                if (objectDef.BasicActionPath != null)
                {
                    recipeProblems.Add(label + " 对象 " + i + ":是 .act 驱动的新式对象(这条路径只放老式对象)");
                }
                AniFile? objectAni = objectDef.BasicMotionPath == null ? null : assets.LoadAni(objectDef.BasicMotionPath);
                if (objectAni == null || objectAni.Frames.Length == 0)
                {
                    recipeProblems.Add(label + " 对象 " + i + ":基础动画读不到");
                }
                else if (!objectAni.Frames.Any(fr => fr.Boxes.Any(b => b.Type == AniBoxType.Attack)))
                {
                    recipeProblems.Add(label + " 对象 " + i + ":基础动画没有任何攻击框");
                }
                if (!InLibrary(objectDef.AttackInfoPath))
                {
                    recipeProblems.Add(label + " 对象 " + i + ":攻击信息读不到");
                }
                AniFile? motion = o.OnLanding ? null : assets.LoadAni(chr.CustomAnimationPath(recipe.Stages[Math.Min(o.Stage, recipe.Stages.Count - 1)].MotionIndex) ?? "");
                if (!o.OnLanding && (motion == null || o.Frame >= motion.Frames.Length))      // "落地放"的对象不按帧放(E-149)
                {
                    recipeProblems.Add(label + " 对象 " + i + ":放出的帧 " + o.Frame + " 超出施法动作的帧数 " + (motion == null ? 0 : motion.Frames.Length));
                }
                if (def.GetLevel(1, o.DamageRateColumn, false) <= 0 || def.GetLevel(1, o.DamageFixedColumn, false) <= 0)
                {
                    recipeProblems.Add(label + " 对象 " + i + ":1 级的两个伤害列读出来不是正数");
                }
                if (o.AfterEffect >= 0)
                {
                    // 等特效播到关键帧标志:特效在预载表里、动画在库里、动画里真有这个标志的帧,而且这一段确实放这个特效
                    recipeGates++;
                    SkillStageSpec objectStage = recipe.Stages[Math.Min(o.Stage, recipe.Stages.Count - 1)];
                    AniFile? gate = o.AfterEffect < effectPaths.Count
                        ? assets.LoadAni(PvfPath.ResolveRelativeTo(chr.Path, effectPaths[o.AfterEffect]))
                        : null;
                    if (!objectStage.EffectIndices.Contains(o.AfterEffect))
                    {
                        recipeProblems.Add(label + " 对象 " + i + ":等的特效这一段并不放");
                    }
                    else if (gate == null || !gate.Frames.Any(fr => fr.TryGetKeyFrameFlag(out int flag) && flag == o.AfterEffectFlag))
                    {
                        recipeProblems.Add(label + " 对象 " + i + ":等的特效读不到,或动画里没有关键帧标志 " + o.AfterEffectFlag);
                    }
                }
            }
        }
        foreach (string line in recipeProblems)
        {
            Console.WriteLine("    " + line);
        }
        Report(recipeCount == SkillRecipes.Default.Count && recipeProblems.Count == 0,
            "技能配方 " + recipeCount + " 个(共 " + recipeStages + " 段、放对象 " + recipeObjects + " 个、带蓄力 " + recipeHolds + " 段、等特效标志的对象 " + recipeGates
            + " 个、定结束帧 " + recipeEndFrames + " 段、蓄力跳跃 " + recipeHops + " 个、多段命中 " + recipeMultiHits + " 个、悬停俯冲 " + recipeDives + " 个、空中攻击强化 " + recipeJumpAttacks + " 个、自动接续 " + recipeAutoChains + " 个、关键帧标志事件 " + recipeFlagEvents + " 个、脚本升空 " + recipeRises + " 段)在真实数据里都读得通"
            + "(动作、攻击信息、特效、对象、蓄力、结束帧、[static data]、[level info] 列;问题 " + recipeProblems.Count + " 条)");

        // 7. 出招指令(M47,证据 E-140):整个职业技能的 [command] 在真实数据里读得通,而且输入层认得出来——
        // 有 [command] 的技能都解析出至少一个键、没有读不懂的括号词;把指令的键按顺序打一遍(朝右、朝左各一次),输入层认得出就是自己的指令;
        // 已实现的、能靠指令放的技能,在真实的房间会话里按各自的指令打一遍,放出来的就是它自己(或指令更长、同样被打出来的另一个技能);
        // 躺着的主角打出倒地起身的指令就起身。只打印汇总数,指令本身不打印。
        int withCommand = 0;
        int emptyParsed = 0;
        int unknownTokens = 0;
        int typedBad = 0;
        int togetherCommands = 0;
        int castableByCommand = 0;
        var triggerCounts = new int[8];
        var commandTexts = new Dictionary<string, int>();
        foreach (SkillDef def in catalog.Skills)
        {
            if (!def.HasCommand)
            {
                continue;
            }
            withCommand++;
            SkillCommand command = def.Command;
            if (command.IsEmpty)
            {
                emptyParsed++;
                continue;
            }
            unknownTokens += command.UnknownCount;
            triggerCounts[(int)command.Trigger]++;
            togetherCommands += command.HasTogether ? 1 : 0;
            castableByCommand += HeroSkills.CommandCasts(command) ? 1 : 0;
            commandTexts[command.ToString()] = commandTexts.TryGetValue(command.ToString(), out int seenBefore) ? seenBefore + 1 : 1;
            foreach (bool facingLeft in new[] { false, true })
            {
                var input = new CommandInput();
                foreach (CommandStep step in CommandSteps.Of(command, facingLeft))
                {
                    input.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Skill, step.Buff);
                }
                if (!input.Matches(command, facingLeft))
                {
                    typedBad++;
                }
            }
        }
        int sameCommand = commandTexts.Values.Count(n => n > 1);
        Report(withCommand > 0 && unknownTokens == 0 && typedBad == 0,
            "技能指令:" + withCommand + " 个技能带 [command](其中空的 " + emptyParsed + " 个,只能靠快捷槽),解析出键的 " + (withCommand - emptyParsed) + " 个,读不懂的括号词 " + unknownTokens + " 个,带\"同时\"的 " + togetherCommands
            + " 个;扳机键 上/下/左/右/攻击/跳跃/技能/增益 各 " + string.Join("/", triggerCounts) + ";能靠指令放的 " + castableByCommand + " 个;"
            + "按各自的键序列打一遍(朝右、朝左)输入层认不出的 " + typedBad + " 次;指令完全相同的有 " + sameCommand + " 组");

        HeroProgress? caster = HeroProgress.CreateForLocalHero(assets, level);
        if (caster != null)
        {
            SkillPlanner.SpendAll(caster);
            var ready = new List<SkillDef>();
            foreach (SkillDef def in catalog.Skills)
            {
                if (def.IsActive && def.Id != SkillIds.QuickStand && HeroSkills.IsImplemented(def.Id) && HeroSkills.CommandCasts(def.Command) && caster.Skills.LevelOf(def.Id) > 0)
                {
                    ready.Add(def);
                }
            }
            int selfCast = 0;
            int longerCast = 0;
            int missed = 0;
            int wrong = 0;
            foreach (SkillDef def in ready)
            {
                RoomCombatSession session = NewSession(assets, opts, caster);
                if (SkillRecipes.Default.Find(def.Id)?.Dive != null)
                {
                    LiftOff(session, 60.0);      // 悬停俯冲类技能只在腾空时放得出来(E-149):先跳起来、升到够高再打指令
                }
                foreach (CommandStep step in CommandSteps.Of(def.Command, session.Hero.FacingLeft))
                {
                    session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                }
                if (session.Hero.Motion != HeroMotion.Skill && !session.Hero.IsBackStep)
                {
                    missed++;      // 后跳是跳跃状态的特殊参数(动作是跳跃,不是技能动作),所以也要认
                }
                else if (session.Hero.SkillId == def.Id)
                {
                    selfCast++;
                }
                else if (catalog.Find(session.Hero.SkillId)?.Command.Length > def.Command.Length)
                {
                    longerCast++;
                }
                else
                {
                    wrong++;
                }
            }
            Report(ready.Count > 0 && missed == 0 && wrong == 0,
                "指令放技能:已实现、已学会、能靠指令放的 " + ready.Count + " 个技能,在房间会话里各打一遍自己的指令:放出来的就是它自己 " + selfCast + " 个、落到指令更长的另一个技能 " + longerCast
                + " 个、没放出来 " + missed + " 个、放成了别的 " + wrong + " 个");

            // 倒地起身:躺着的主角打出倒地起身的指令
            SkillDef? quick = catalog.Find(SkillIds.QuickStand);
            if (quick != null && caster.Skills.LevelOf(SkillIds.QuickStand) > 0 && !quick.Command.IsEmpty)
            {
                RoomCombatSession session = NewSession(assets, opts, caster);
                bool lying = false;
                if (session.HeroDown.Available && session.HeroDown.Begin(new KnockdownInput(300, false, false, false, 500, 500), 0, false, false))
                {
                    for (int t = 0; t < 4000 && session.HeroDown.Stage != 2; t += 10)
                    {
                        session.Update(10, 0, 0, false, false, SkillKeys.None);
                    }
                    lying = session.HeroDown.Stage == 2;
                }
                if (lying)
                {
                    foreach (CommandStep step in CommandSteps.Of(quick.Command, session.Hero.FacingLeft))
                    {
                        session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                    }
                }
                Report(lying && session.QuickStands == 1 && session.HeroDown.QuickStanding,
                    "倒地起身:躺着的主角打出倒地起身的指令,起身 " + session.QuickStands + " 次(躺着 " + (lying ? "是" : "否") + ",正在起身 " + (session.HeroDown.QuickStanding ? "是" : "否") + ")");
            }

            // 7b. 悬停俯冲类技能(M55,证据 E-149)的真实会话:表里的每个俯冲技能——地面上打它的指令放不出它(不进冷却);主角起跳升到够高后打指令:放出来的就是它自己、
            // 动画定在空中攻击动作的最后一帧;一路走完悬停、俯冲(放预载特效)、落地(放出时的离地高度够就放冲击对象,不够就不放)、坐姿、回到站立;
            // 魔法值按 [consume MP] 扣、冷却开始计;另外在跳起的最高点再放一次(起点最高,看高度判定与对象)。只打印汇总数,高度与阈值不打印。
            int diveSkills = 0;
            int diveCycles = 0;
            int diveGroundBlocked = 0;
            int diveImpacts = 0;
            int diveNoImpacts = 0;
            var diveProblems = new List<string>();
            foreach (SkillRecipe recipe in SkillRecipes.Default.All)
            {
                if (recipe.Dive == null)
                {
                    continue;
                }
                diveSkills++;
                string label = "俯冲 #" + diveSkills;
                SkillDef? def = catalog.Find(recipe.SkillId);
                if (def == null || def.Command.IsEmpty || caster.Skills.LevelOf(def.Id) <= 0)
                {
                    diveProblems.Add(label + ":技能不在目录里、没有指令或没学会");
                    continue;
                }
                int threshold = def.GetStatic(recipe.Dive.ImpactHeightIndex, false);

                RoomCombatSession ground = NewSession(assets, opts, caster);
                HeroSkillSlot? groundSlot = ground.Skills.SlotOfSkill(def.Id);
                foreach (CommandStep step in CommandSteps.Of(def.Command, ground.Hero.FacingLeft))
                {
                    ground.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                }
                if (groundSlot == null || ground.Hero.SkillId == def.Id || groundSlot.CooldownLeftMs > 0 || ground.Hero.DiveAirborne || ground.DiveStarts != 0)
                {
                    diveProblems.Add(label + ":地面上打指令不该放出俯冲(或没排进槽里)");
                }
                else
                {
                    diveGroundBlocked++;
                }

                foreach (bool atApex in new[] { false, true })
                {
                    RoomCombatSession session = NewSession(assets, opts, caster);
                    HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
                    TownHero diver = session.Hero;
                    bool lifted = slot != null && LiftOff(session, 6.0);
                    if (lifted && atApex)
                    {
                        double last = diver.Z;
                        for (int t = 0; t < 3000 && diver.Z >= last && diver.Motion == HeroMotion.Jump; t += 10)
                        {
                            last = diver.Z;
                            session.Update(10, 0, 0, false, false, SkillKeys.None);
                        }
                    }
                    if (!lifted || slot == null)
                    {
                        diveProblems.Add(label + ":起跳升不起来,或没排进槽里");
                        continue;
                    }
                    int mpBefore = session.Vitals?.Mp ?? 0;
                    foreach (CommandStep step in CommandSteps.Of(def.Command, diver.FacingLeft))
                    {
                        session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                    }
                    if (diver.Motion != HeroMotion.Skill || diver.SkillId != def.Id || !diver.DiveAirborne || !diver.FrameHeld || session.SkillCasts != 1)
                    {
                        diveProblems.Add(label + ":腾空时打指令没有放出俯冲(或动画没有定住)");
                        continue;
                    }
                    int ms = 0;
                    while (diver.Motion == HeroMotion.Skill && ms < 20000)
                    {
                        session.Update(10, 0, 0, false, false, SkillKeys.None);
                        ms += 10;
                    }
                    bool impactExpected = (int)diver.DiveStartHeight >= threshold;
                    bool ok = session.DiveLandings == 1 && session.DiveStarts <= 1 && diver.Motion == HeroMotion.Rest && diver.Z == 0.0
                        && diver.DiveLandingImpact == impactExpected && session.DiveImpacts == (impactExpected ? 1 : 0) && session.HeroObjectsSpawned == session.DiveImpacts
                        && slot.CooldownLeftMs > 0 && (session.Vitals == null || session.Vitals.Mp == mpBefore - slot.ConsumeMp);
                    if (!ok)
                    {
                        diveProblems.Add(label + (atApex ? "(最高点)" : "(刚离地)") + ":俯冲没按规则走完(落地 " + session.DiveLandings + " 次、俯冲 " + session.DiveStarts + " 次、冲击 "
                            + session.DiveImpacts + " 个、动作 " + diver.Motion + "、高度判定与对象不符、魔法值或冷却不对)");
                        continue;
                    }
                    diveCycles++;
                    if (impactExpected)
                    {
                        diveImpacts++;
                    }
                    else
                    {
                        diveNoImpacts++;
                    }
                }
            }
            foreach (string line in diveProblems)
            {
                Console.WriteLine("    " + line);
            }
            Report(diveSkills > 0 && diveProblems.Count == 0 && diveGroundBlocked == diveSkills && diveCycles == diveSkills * 2,
                "悬停俯冲类技能 " + diveSkills + " 个:地面上打指令放不出 " + diveGroundBlocked + " 个;腾空时(刚离地与最高点各一次)打指令放出自己、定住最后一帧、悬停、俯冲、落地、坐姿、回到站立走完 "
                + diveCycles + " 次,其中放了冲击对象 " + diveImpacts + " 次、因起点太低没放 " + diveNoImpacts + " 次(问题 " + diveProblems.Count + " 条)");

            // 7c. 空中攻击强化类技能(M56,证据 E-150)的真实会话:表里的每个这类技能(指令是单独一个攻击键)——地面上按攻击键是普通攻击(不放它、不扣蓝);
            // 出招指令关着(自动机的默认)时空中按攻击键是普通的空中攻击;开着时主角起跳升到最高点按攻击键:这一击换成它(技能号、[etc motion] 里这一次该放的那一种动作、
            // 扣魔法值、算一次接管、交替模式变成这一次的种类),动画播到特效帧放一次预载特效,挥完这一跳再按攻击键没有反应;落地后再跳一次放另一种动作(两种交替)。只打印汇总数。
            int jumpSkills = 0;
            int jumpGroundPlain = 0;
            int jumpPlainWhenOff = 0;
            int jumpCycles = 0;
            int jumpEffectCycles = 0;
            int jumpBoxCycles = 0;
            var jumpBoxes = new HeroStrikeBoxes(assets);
            var jumpProblems = new List<string>();
            foreach (SkillRecipe recipe in SkillRecipes.Default.All)
            {
                if (recipe.JumpAttack == null)
                {
                    continue;
                }
                jumpSkills++;
                string label = "空中攻击强化 #" + jumpSkills;
                SkillDef? def = catalog.Find(recipe.SkillId);
                if (def == null || def.Command.IsEmpty || def.Command.Length != 1 || def.Command.Trigger != CommandKey.Attack || caster.Skills.LevelOf(def.Id) <= 0)
                {
                    jumpProblems.Add(label + ":技能不在目录里、指令不是单独的攻击键或没学会");
                    continue;
                }

                RoomCombatSession ground = NewSession(assets, opts, caster);
                ground.Update(10, 0, 0, true, false, SkillKeys.None);
                if (ground.SkillCasts != 0 || ground.JumpSkillCasts != 0 || ground.Hero.JumpAttackSkillId != 0 || ground.Hero.Motion == HeroMotion.Skill
                    || (ground.Vitals != null && ground.Vitals.Mp != ground.Vitals.MpMax))
                {
                    jumpProblems.Add(label + ":地面上按攻击键不该放出它(或扣了魔法值)");
                }
                else
                {
                    jumpGroundPlain++;
                }

                RoomCombatSession off = NewSession(assets, opts, caster);
                off.CommandsEnabled = false;
                bool liftedOff = LiftOff(off, 6.0);
                off.Update(10, 0, 0, true, false, SkillKeys.None);
                if (!liftedOff || off.Hero.Motion != HeroMotion.JumpAttack || off.Hero.JumpAttackSkillId != 0 || off.SkillCasts != 0
                    || (off.Vitals != null && off.Vitals.Mp != off.Vitals.MpMax))
                {
                    jumpProblems.Add(label + ":关着出招指令时空中按攻击键应当是普通的空中攻击");
                }
                else
                {
                    jumpPlainWhenOff++;
                }

                RoomCombatSession session = NewSession(assets, opts, caster);
                HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
                TownHero jumper = session.Hero;
                for (int round = 0; slot != null && round < 2; round++)
                {
                    bool lifted = LiftOff(session, 20.0);
                    if (lifted)
                    {
                        double last = jumper.Z;
                        for (int t = 0; t < 3000 && jumper.Z >= last && jumper.Motion == HeroMotion.Jump; t += 10)
                        {
                            last = jumper.Z;
                            session.Update(10, 0, 0, false, false, SkillKeys.None);
                        }
                    }
                    int mpBefore = session.Vitals?.Mp ?? 0;
                    int effectsBefore = session.JumpSkillEffects;
                    session.Update(10, 0, 0, true, false, SkillKeys.None);
                    string? wantPath = chr.CustomAnimationPath(round == 0 ? recipe.JumpAttack.MotionIndexA : recipe.JumpAttack.MotionIndexB);
                    bool started = lifted && jumper.Motion == HeroMotion.JumpAttack && jumper.JumpAttackSkillId == def.Id
                        && session.JumpSkillCasts == round + 1 && session.SkillCasts == round + 1 && session.Skills.JumpAttackMode == round
                        && string.Equals(jumper.CurrentAnimationPath, wantPath, StringComparison.OrdinalIgnoreCase)
                        && (session.Vitals == null || session.Vitals.Mp == mpBefore - slot.ConsumeMp);
                    if (!started)
                    {
                        jumpProblems.Add(label + " 第 " + (round + 1) + " 跳:空中按攻击键没有接管成这个技能(动作、魔法值、计数或交替模式不对,或没跳起来)");
                        break;
                    }
                    int ms = 0;
                    var boxFrames = new HashSet<int>();
                    while (jumper.Motion == HeroMotion.JumpAttack && ms < 3000)
                    {
                        if (jumpBoxes.At(jumper, jumper.CurrentFrame).Count > 0)
                        {
                            boxFrames.Add(jumper.CurrentFrame);
                        }
                        session.Update(10, 0, 0, false, false, SkillKeys.None);
                        ms += 10;
                    }
                    bool effectOnce = session.JumpSkillEffects == effectsBefore + 1;
                    if (boxFrames.Count > 0)
                    {
                        jumpBoxCycles++;
                    }
                    int castsBefore = session.SkillCasts;
                    session.Update(10, 0, 0, true, false, SkillKeys.None);
                    bool noSecond = session.SkillCasts == castsBefore && jumper.Motion != HeroMotion.JumpAttack;
                    int rest = 0;
                    while (jumper.Motion != HeroMotion.Rest && rest < 5000)
                    {
                        session.Update(10, 0, 0, false, false, SkillKeys.None);
                        rest += 10;
                    }
                    if (!noSecond || jumper.Motion != HeroMotion.Rest || jumper.JumpAttackSkillId != 0)
                    {
                        jumpProblems.Add(label + " 第 " + (round + 1) + " 跳:同一跳里不该再接管、落地后应回到站立且技能痕迹清掉");
                        break;
                    }
                    jumpCycles++;
                    if (effectOnce)
                    {
                        jumpEffectCycles++;
                    }
                    else
                    {
                        jumpProblems.Add(label + " 第 " + (round + 1) + " 跳:动画播到特效帧该放一次预载特效(这一击放了 " + (session.JumpSkillEffects - effectsBefore) + " 次)");
                    }
                }
                if (slot == null)
                {
                    jumpProblems.Add(label + ":没排进技能槽");
                }
            }
            foreach (string line in jumpProblems)
            {
                Console.WriteLine("    " + line);
            }
            Report(jumpSkills > 0 && jumpProblems.Count == 0 && jumpGroundPlain == jumpSkills && jumpPlainWhenOff == jumpSkills && jumpCycles == jumpSkills * 2 && jumpEffectCycles == jumpCycles && jumpBoxCycles == jumpCycles,
                "空中攻击强化类技能 " + jumpSkills + " 个:地面上按攻击键是普通攻击 " + jumpGroundPlain + " 个;关着出招指令时空中按攻击键是普通空中攻击 " + jumpPlainWhenOff
                + " 个;开着时在最高点按攻击键接管、扣魔法值、两跳交替两种动作、每一击放一次特效、同一跳不再接管、落地回站立走完 " + jumpCycles + " 跳(放了特效 " + jumpEffectCycles + " 跳、武器图层有攻击框 " + jumpBoxCycles + " 跳;问题 " + jumpProblems.Count + " 条)");

            // 7d. 两段自动接续类技能(M60,证据 E-154)的真实会话:表里的每个这类技能——按各自的指令放出来;第一段播完不按任何键就自己接上第二段(算一次接段、不算新的施放),
            // 第二段一开始放出配方里的被动对象(.obj 在真实数据里读得到、放得出来),整个技能播完回到站立;魔法值与冷却只在起手那一刻算。只打印汇总数。
            int autoSkills = 0;
            int autoCycles = 0;
            int autoObjects = 0;
            int autoQuickCasts = 0;
            int autoRises = 0;
            var autoProblems = new List<string>();
            foreach (SkillRecipe recipe in SkillRecipes.Default.All)
            {
                if (!recipe.AutoChain)
                {
                    continue;
                }
                autoSkills++;
                string label = "自动接续 #" + autoSkills;
                SkillDef? def = catalog.Find(recipe.SkillId);
                // 子职业技能默认主角学不了:按成长类型 0 到 5 各试一个新主角、花光点数,取第一个学会它的(M65)
                HeroProgress? autoCaster = def == null ? null : CasterWhoKnows(assets, level, def.Id);
                if (def == null || def.Command.IsEmpty || autoCaster == null)
                {
                    autoProblems.Add(label + ":技能不在目录里、没有指令或没学会");
                    continue;
                }
                RoomCombatSession session = NewSession(assets, opts, autoCaster);
                HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
                TownHero caster1 = session.Hero;
                int mpBefore = session.Vitals?.Mp ?? 0;
                foreach (CommandStep step in CommandSteps.Of(def.Command, caster1.FacingLeft))
                {
                    session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                }
                bool started = slot != null && caster1.Motion == HeroMotion.Skill && caster1.SkillId == def.Id && session.SkillCasts == 1 && session.SkillChains == 0 && caster1.SkillStage == 0;
                if (!started && caster1.Motion == HeroMotion.Skill && caster1.SkillId != def.Id)
                {
                    // 同一条指令先落到了别的技能上(子职业技能与基础技能的指令常常撞车;指令层的取舍是快捷槽在前、编号小的在前,E-140):
                    // 换一局新会话,把这个技能放进快捷栏第 1 格,按那个键放
                    autoCaster = CasterWhoKnows(assets, level, def.Id);
                    if (autoCaster != null && autoCaster.Skills.AssignQuick(0, def.Id))
                    {
                        session = NewSession(assets, opts, autoCaster);
                        slot = session.Skills.SlotOfSkill(def.Id);
                        caster1 = session.Hero;
                        mpBefore = session.Vitals?.Mp ?? 0;
                        session.Update(10, 0, 0, false, false, slot?.Key ?? SkillKeys.None);
                        started = slot != null && slot.Key != SkillKeys.None && caster1.Motion == HeroMotion.Skill && caster1.SkillId == def.Id && session.SkillCasts == 1
                            && session.SkillChains == 0 && caster1.SkillStage == 0;
                        if (started)
                        {
                            autoQuickCasts++;
                        }
                    }
                }
                if (!started || slot == null)
                {
                    autoProblems.Add(label + ":没有放出这个技能的第一段(指令没放出来,或没排进槽里)");
                    continue;
                }
                int stageSeen = 0;
                int ms = 0;
                double maxHeight = 0.0;
                bool hasRise = recipe.Stages.Any(st => st.Rise != null);
                while ((caster1.Motion == HeroMotion.Skill || caster1.Motion == HeroMotion.Jump) && ms < 20000)    // 升空类技能结束在空中:等它落地回到站立
                {
                    session.Update(10, 0, 0, false, false, SkillKeys.None);
                    ms += 10;
                    stageSeen = Math.Max(stageSeen, caster1.SkillStage);
                    maxHeight = Math.Max(maxHeight, caster1.Z);
                }
                // 升空类技能:放的过程中真的升到了空中,而且最后落回地面(站立、离地高度 0);别的技能一直在地面
                bool ok = stageSeen == recipe.Stages.Count - 1 && session.SkillChains == recipe.Stages.Count - 1 && session.SkillCasts == 1
                    && session.HeroObjectsSpawned == recipe.Objects.Count && caster1.Motion == HeroMotion.Rest && caster1.SkillId == 0 && slot.CooldownLeftMs > 0
                    && (session.Vitals == null || session.Vitals.Mp == mpBefore - slot.ConsumeMp)
                    && caster1.Z == 0.0 && (hasRise ? maxHeight > 0.0 : maxHeight == 0.0);
                if (ok && hasRise)
                {
                    autoRises++;
                }
                if (!ok)
                {
                    autoProblems.Add(label + ":没有按规则自己接完各段(见到的段 " + stageSeen + "、接段 " + session.SkillChains + " 次、放对象 " + session.HeroObjectsSpawned + " 个、动作 " + caster1.Motion + "、魔法值或冷却不对)");
                    continue;
                }
                autoCycles++;
                autoObjects += session.HeroObjectsSpawned;
            }
            foreach (string line in autoProblems)
            {
                Console.WriteLine("    " + line);
            }
            Report(autoSkills > 0 && autoProblems.Count == 0 && autoCycles == autoSkills,
                "自动接续类技能 " + autoSkills + " 个:按各自的指令放出第一段(指令撞车的 " + autoQuickCasts + " 个改按快捷键放),不按任何键各段自己接上、放出对象 " + autoObjects + " 个、整个技能播完回到站立走完 " + autoCycles
                + " 个(其中升空出招 " + autoRises + " 个:升到空中、落回地面;魔法值与冷却只在起手算;问题 " + autoProblems.Count + " 条)");

            // 7e. 带关键帧标志事件的技能(M65,证据 E-163)的真实会话:表里每个这类技能放进快捷栏第 1 格、按键放出来、动画播完;
            // 会话触发的事件数等于"动作里带标志、而且这一段的事件表里有这个标志"的帧数(每帧一次),放完回到站立。只打印汇总数。
            int flagSkills = 0;
            int flagCycles = 0;
            int flagTriggers = 0;
            var flagProblems = new List<string>();
            foreach (SkillRecipe recipe in SkillRecipes.Default.All)
            {
                if (!recipe.Stages.Any(st => st.FlagEvents.Count > 0))
                {
                    continue;
                }
                flagSkills++;
                string label = "标志事件 #" + flagSkills;
                SkillDef? def = catalog.Find(recipe.SkillId);
                HeroProgress? flagCaster = def == null ? null : CasterWhoKnows(assets, level, def.Id);
                if (def == null || flagCaster == null || !flagCaster.Skills.AssignQuick(0, def.Id))
                {
                    flagProblems.Add(label + ":技能不在目录里、没学会或排不进快捷栏");
                    continue;
                }
                RoomCombatSession flagSession = NewSession(assets, opts, flagCaster);
                HeroSkillSlot? flagSlot = flagSession.Skills.SlotOfSkill(def.Id);
                if (flagSlot == null || flagSlot.Key == SkillKeys.None)
                {
                    flagProblems.Add(label + ":没有排进快捷槽");
                    continue;
                }
                flagSession.Update(10, 0, 0, false, false, flagSlot.Key);
                if (flagSession.Hero.Motion != HeroMotion.Skill || flagSession.Hero.SkillId != def.Id)
                {
                    flagProblems.Add(label + ":按键没有放出这个技能");
                    continue;
                }
                int flagMs = 10;
                while (flagSession.Hero.Motion == HeroMotion.Skill && flagMs < 20000)
                {
                    flagSession.Update(10, 0, 0, false, false, SkillKeys.None);
                    flagMs += 10;
                }
                int expectedEvents = 0;
                for (int i = 0; i < recipe.Stages.Count; i++)
                {
                    SkillStageSpec st = recipe.Stages[i];
                    AniFile? stageAni = assets.LoadAni(chr.CustomAnimationPath(st.MotionIndex) ?? "");
                    int frameLimit = stageAni == null ? 0 : (st.EndFrame >= 0 ? Math.Min(st.EndFrame, stageAni.Frames.Length) : stageAni.Frames.Length);
                    for (int f = 0; f < frameLimit; f++)
                    {
                        if (stageAni!.Frames[f].TryGetKeyFrameFlag(out int kf) && st.FlagEvents.Any(e => e.Flag == kf))
                        {
                            expectedEvents++;
                        }
                    }
                }
                if (flagSession.KeyFlagEvents != expectedEvents || expectedEvents == 0 || flagSession.Hero.Motion != HeroMotion.Rest)
                {
                    flagProblems.Add(label + ":触发了 " + flagSession.KeyFlagEvents + " 次事件,按动画上的标志帧应当是 " + expectedEvents + " 次,或放完没有回到站立");
                    continue;
                }
                flagCycles++;
                flagTriggers += flagSession.KeyFlagEvents;
            }
            foreach (string line in flagProblems)
            {
                Console.WriteLine("    " + line);
            }
            Report(flagSkills > 0 && flagProblems.Count == 0 && flagCycles == flagSkills,
                "带关键帧标志事件的技能 " + flagSkills + " 个:放进快捷栏按键放出来,动画播完触发的事件数与动画上的标志帧数一致(共 " + flagTriggers + " 次),放完回到站立走完 " + flagCycles
                + " 个(问题 " + flagProblems.Count + " 条)");

            // 8. 增益类技能(M48,证据 E-141):表里的每个增益在真实数据里都读得通——技能在目录里、是主动技能、指令的扳机是增益键、[static data] 与 [level info] 的列够用、
            // 每一级的持续时间与数值都是正数;真实会话里按各自的指令打一遍:施法动作是角色的 [buff motion](没有攻击框),施法正常结束那一步挂上增益,数值与持续时间等于槽里读出来的,
            // 到点失效,魔法值与冷却在放出来时就算了。只打印汇总数,时长与数值不打印。
            int buffSkills = 0;
            int buffLevelsChecked = 0;
            int buffCycles = 0;
            int buffMotionOk = 0;
            var buffProblems = new List<string>();
            foreach (BuffRecipe recipe in SkillRecipes.Default.AllBuffs)
            {
                buffSkills++;
                string label = "增益 #" + buffSkills;
                SkillDef? def = catalog.Find(recipe.SkillId);
                if (def == null || !def.IsActive)
                {
                    buffProblems.Add(label + ":技能不在目录里或不是主动技能");
                    continue;
                }
                if (def.Command.IsEmpty || def.Command.Trigger != CommandKey.Buff)
                {
                    buffProblems.Add(label + ":指令的扳机不是增益键");
                }
                int needColumns = Math.Max(recipe.DurationColumn, recipe.AmountStaticIndex >= 0 ? 0 : recipe.AmountColumn) + 1;
                if (def.LevelInfo.Columns < needColumns || !def.LevelInfo.IsConsistent || def.LevelInfo.Levels < def.MaximumLevel)
                {
                    buffProblems.Add(label + ":[level info] 列数或行数不够");
                }
                if (recipe.AmountStaticIndex >= 0 && def.StaticData.Length <= recipe.AmountStaticIndex)
                {
                    buffProblems.Add(label + ":[static data] 不够长");
                }
                for (int lv = 1; lv <= def.MaximumLevel; lv++)
                {
                    buffLevelsChecked++;
                    int duration = def.GetLevel(lv, recipe.DurationColumn, false);
                    int amount = recipe.AmountStaticIndex >= 0 ? def.GetStatic(recipe.AmountStaticIndex, false) : def.GetLevel(lv, recipe.AmountColumn, false);
                    if (duration <= 0 || amount <= 0)
                    {
                        buffProblems.Add(label + " " + lv + " 级:持续时间或数值不是正数");
                    }
                    if (recipe.Stat == BuffStat.SuperArmorChance && amount * recipe.AmountScale > 1.0)
                    {
                        buffProblems.Add(label + " " + lv + " 级:概率超过 100%(M49,E-142)");       // 概率类增益的数是千分比,换成 0 到 1 以后不会超过 1
                    }
                }

                RoomCombatSession session = NewSession(assets, opts, caster);
                HeroSkillSlot? slot = session.Skills.SlotOfSkill(def.Id);
                if (slot?.Buff == null || caster.Skills.LevelOf(def.Id) <= 0)
                {
                    buffProblems.Add(label + ":没有排进槽里(没学会或没有放法)");
                    continue;
                }
                if (!session.Hero.HasBuffMotion)
                {
                    buffProblems.Add(label + ":角色脚本没有 [buff motion]");
                    continue;
                }
                int mpBefore = session.Vitals?.Mp ?? 0;
                foreach (CommandStep step in CommandSteps.Of(def.Command, session.Hero.FacingLeft))
                {
                    session.Update(step.Ms, step.DirX, step.DirY, step.Attack, step.Jump, step.Keys);
                }
                if (session.BuffCasts != 1 || session.Hero.Motion != HeroMotion.Skill || session.Buffs.Count != 0)
                {
                    buffProblems.Add(label + ":按指令没有放出施法动作(或还没施完就挂上了增益)");
                    continue;
                }
                AniFile? motion = assets.LoadAni(session.Hero.CurrentAnimationPath ?? "");
                if (motion != null && motion.Frames.Length > 0 && !motion.Frames.Any(fr => fr.Boxes.Any(b => b.Type == AniBoxType.Attack)))
                {
                    buffMotionOk++;
                }
                else
                {
                    buffProblems.Add(label + ":施法动作读不到,或带攻击框");
                }
                int castMs = 0;
                while (session.Hero.Motion == HeroMotion.Skill && castMs < 20000)
                {
                    session.Update(10, 0, 0, false, false, SkillKeys.None);
                    castMs += 10;
                }
                HeroBuff? applied = session.Buffs.Find(def.Id);
                if (session.BuffsApplied != 1 || applied == null || applied.Stat != recipe.Stat || applied.Amount != slot.BuffAmount || applied.DurationMs != slot.BuffDurationMs)
                {
                    buffProblems.Add(label + ":施法结束时没有按槽里的数值挂上");
                    continue;
                }
                if (session.Vitals != null && session.Vitals.Mp != mpBefore - slot.ConsumeMp)
                {
                    buffProblems.Add(label + ":魔法值没按 [consume MP] 扣");
                }
                int livedMs = 0;
                while (session.Buffs.Has(def.Id) && livedMs < slot.BuffDurationMs + 2000)
                {
                    session.Update(10, 0, 0, false, false, SkillKeys.None);
                    livedMs += 10;
                }
                double expected = slot.BuffDurationMs - 10;      // 挂上那一步已经过去了一步,所以比标称时长短一个步长以内
                if (session.Buffs.Has(def.Id) || session.Buffs.Expired != 1 || Math.Abs(livedMs - expected) > 30)
                {
                    buffProblems.Add(label + ":持续时间走完的时间与槽里的时长对不上");
                    continue;
                }
                buffCycles++;
            }
            foreach (string line in buffProblems)
            {
                Console.WriteLine("    " + line);
            }
            Report(buffSkills == SkillRecipes.Default.BuffCount && buffSkills > 0 && buffProblems.Count == 0 && buffCycles == buffSkills && buffMotionOk == buffSkills,
                "增益类技能 " + buffSkills + " 个(逐级查了 " + buffLevelsChecked + " 级的持续时间与数值都为正)在真实数据里读得通;真实会话里按各自的指令各放一遍:施法动作无攻击框 "
                + buffMotionOk + " 个、施法结束挂上并到点失效 " + buffCycles + " 个(问题 " + buffProblems.Count + " 条)");
        }

        Console.WriteLine(bad == 0 ? "skill-check 全部通过" : "skill-check 有 " + bad + " 项失败");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>一局新的房间战斗会话(检查用:每次放技能都要一局新的),主角带着花光点数的技能簿,出招指令开着,怪物不还手。</summary>
    private static RoomCombatSession NewSession(DnfAssets assets, Options opts, HeroProgress progress)
    {
        RoomCombatSession session = DemoCombat.CreateSession(assets, opts.MapPath, opts.HeroWeapon ?? DemoHero.StarterWeapon, 1, false, progress);
        session.CommandsEnabled = true;
        return session;
    }

    /// <summary>
    /// 找一个学会了 <paramref name="skillId"/> 的主角(检查用,M65):每次都是新建的一个主角——按成长类型 0 到 5 依次转职、花光点数,返回第一个学会它的;都不行返回 null。
    /// 子职业技能默认主角(成长类型 0)学不了。
    /// </summary>
    private static HeroProgress? CasterWhoKnows(DnfAssets assets, int level, int skillId)
    {
        for (int g = 0; g < SkillRules.GrowtypeCount; g++)
        {
            HeroProgress? other = HeroProgress.CreateForLocalHero(assets, level);
            if (other == null)
            {
                return null;
            }
            if (g > 0 && !other.ChangeGrowtype(g))
            {
                continue;
            }
            SkillPlanner.SpendAll(other);
            if (other.Skills.LevelOf(skillId) > 0)
            {
                return other;
            }
        }
        return null;
    }

    /// <summary>让主角起跳并升到离地至少 <paramref name="minHeight"/> 像素(检查用:只在腾空时放得出来的技能)。返回有没有升到。</summary>
    private static bool LiftOff(RoomCombatSession session, double minHeight)
    {
        session.Update(10, 0, 0, false, true, SkillKeys.None);
        for (int t = 0; t < 3000 && !(session.Hero.Motion == HeroMotion.Jump && session.Hero.Z >= minHeight); t += 10)
        {
            session.Update(10, 0, 0, false, false, SkillKeys.None);
        }
        return session.Hero.Motion == HeroMotion.Jump && session.Hero.Z >= minHeight;
    }

    /// <summary>把一条指令打成一串按键步骤的一步。</summary>
    private readonly record struct CommandStep(double Ms, int DirX, int DirY, bool Attack, bool Jump, bool Skill, bool Buff)
    {
        /// <summary>这一步按下的出招指令键(技能键与增益键)。</summary>
        public SkillKeys Keys => (Skill ? SkillKeys.CommandSkill : SkillKeys.None) | (Buff ? SkillKeys.CommandBuff : SkillKeys.None);
    }

    private static class CommandSteps
    {
        /// <summary>
        /// 把指令打一遍:每个键按一步(方向键按下后在下一个键之前松开,"同时"连着下一个键的方向键一直按到下一个键按下那一步),
        /// 按 <paramref name="facingLeft"/> 把朝前、朝后换成实际要按的左右键。
        /// </summary>
        public static IEnumerable<CommandStep> Of(SkillCommand command, bool facingLeft)
        {
            int dirX = 0;
            int dirY = 0;
            for (int i = 0; i < command.Length; i++)
            {
                CommandKey key = CommandInput.Absolute(command.Keys[i], facingLeft);
                switch (key)
                {
                    case CommandKey.Up: dirY = -1; break;
                    case CommandKey.Down: dirY = 1; break;
                    case CommandKey.Left: dirX = -1; break;
                    case CommandKey.Right: dirX = 1; break;
                }
                yield return new CommandStep(50.0, dirX, dirY, key == CommandKey.Attack, key == CommandKey.Jump, key == CommandKey.Skill, key == CommandKey.Buff);
                if (!command.TogetherWithNext(i) && i + 1 < command.Length)
                {
                    if (dirX != 0 || dirY != 0)
                    {
                        dirX = 0;
                        dirY = 0;
                        yield return new CommandStep(30.0, 0, 0, false, false, false, false);     // 松开方向键(连着按同一个方向键时两次按下之间要有松开)
                    }
                }
            }
        }
    }

    /// <summary>前置技能的有向图里有没有环(深度优先)。</summary>
    private static bool HasCycle(SkillCatalog catalog)
    {
        var state = new Dictionary<int, int>();   // 1 = 在栈上,2 = 已查完
        bool Visit(int id)
        {
            if (state.TryGetValue(id, out int s))
            {
                return s == 1;
            }
            state[id] = 1;
            SkillDef? def = catalog.Find(id);
            if (def != null)
            {
                foreach (SkillPrereq pre in def.PreRequired)
                {
                    if (Visit(pre.SkillId))
                    {
                        return true;
                    }
                }
            }
            state[id] = 2;
            return false;
        }
        return catalog.Skills.Any(def => Visit(def.Id));
    }
}
