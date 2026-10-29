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
                if (!InLibrary(chr.CustomAnimationPath(st.MotionIndex)))
                {
                    recipeProblems.Add(label + " 第 " + i + " 段:动作读不到");
                }
                if (st.HasOwnStrike && !InLibrary(chr.CustomAttackInfoPath(st.AttackInfoIndex)))
                {
                    recipeProblems.Add(label + " 第 " + i + " 段:攻击信息读不到");
                }
                if (!st.HasOwnStrike && recipe.Objects.Count == 0 && recipe.Hop == null)
                {
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
                AniFile? motion = assets.LoadAni(chr.CustomAnimationPath(recipe.Stages[Math.Min(o.Stage, recipe.Stages.Count - 1)].MotionIndex) ?? "");
                if (motion == null || o.Frame >= motion.Frames.Length)
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
            + " 个、定结束帧 " + recipeEndFrames + " 段、蓄力跳跃 " + recipeHops + " 个、多段命中 " + recipeMultiHits + " 个)在真实数据里都读得通"
            + "(动作、攻击信息、特效、对象、蓄力、结束帧、[static data]、[level info] 列;问题 " + recipeProblems.Count + " 条)");
        Console.WriteLine(bad == 0 ? "skill-check 全部通过" : "skill-check 有 " + bad + " 项失败");
        return bad == 0 ? 0 : 1;
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
