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
