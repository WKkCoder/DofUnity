using System.Text;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// skill-check 的补充检查(技能学习服务 <see cref="SkillLearning"/> 在真实技能目录上自洽,证据 E-127):
/// (1) 批量:每个成长类型把自动机策略学的每一级记成一批,整批学下去与策略直接花光、与逐个学的终态一致,点数池的消耗对得上;只演算不动状态;
///     中间插一步必不成的,整批失败、失败下标对、状态与版本一点没动;整批原路退回与重置都回到起点;
/// (2) 一次能学几级:每个成长类型对每个技能问 LearnableLevels,与逐级问 CheckLearn 的结论一致;按策略的次序逐个 LearnMax,花掉的点与两个池子的减少对得上、问完不再有可学的级;
/// (3) 快捷栏占用:花光点数之后数"只能靠快捷键却没上栏"的技能,打开命令感知整理之后不增、技能与点数一点不变,整理后要么没有、要么六格都被只能靠快捷键的技能占着;
/// (4) 自动学:1 级主角打开自动学、一口气升到检查等级,Update 花光之后与直接按该级总额花光的主角一致。
/// 只打印通过与否和计数,不打印技能号、技能名与路径。
/// </summary>
internal static partial class SkillCheck
{
    /// <summary>技能簿的摘要:两个点数池、各技能等级(按编号)、快捷栏六格;只拿来比两个主角是不是同一个状态,不打印。</summary>
    private static string LearningDigest(HeroProgress hero, bool withBar = true)
    {
        var sb = new StringBuilder();
        sb.Append(hero.Skills.Sp).Append('/').Append(hero.Skills.Sfp).Append(';');
        foreach (KeyValuePair<int, int> learned in hero.Skills.Learned.OrderBy(p => p.Key))
        {
            sb.Append(learned.Key).Append(':').Append(learned.Value).Append(',');
        }
        if (withBar)
        {
            sb.Append(';');
            for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
            {
                sb.Append(hero.Skills.QuickAt(slot)).Append(',');
            }
        }
        return sb.ToString();
    }

    /// <summary>新建一个本地主角:<paramref name="level"/> 级、成长类型 <paramref name="growtype"/>(还一点技能点都没花)。</summary>
    private static HeroProgress? NewLearner(DnfAssets assets, int level, int growtype)
    {
        HeroProgress? hero = HeroProgress.CreateForLocalHero(assets, level);
        if (hero == null || (growtype > 0 && !hero.ChangeGrowtype(growtype)))
        {
            return null;
        }
        return hero;
    }

    /// <summary>技能学习服务的真数据汇总检查(见本文件头),<paramref name="level"/> 是检查用的主角等级。</summary>
    private static void ReportLearningService(DnfAssets assets, SkillCatalog catalog, int level, Action<bool, string> report)
    {
        int growtypes = SkillRules.GrowtypeCount;
        int missingId = catalog.Skills.Max(s => s.Id) + 1;

        // (1) 批量
        int made = 0;
        int steps = 0;
        int sameAsPlanner = 0;
        int sameAsSingle = 0;
        int poolsMatch = 0;
        int tryKeeps = 0;
        int failedClean = 0;
        int roundTrips = 0;
        int resets = 0;
        for (int g = 0; g < growtypes; g++)
        {
            HeroProgress? batch = NewLearner(assets, level, g);
            HeroProgress? single = NewLearner(assets, level, g);
            HeroProgress? spent = NewLearner(assets, level, g);
            HeroProgress? recorder = NewLearner(assets, level, g);
            HeroProgress? broken = NewLearner(assets, level, g);
            if (batch == null || single == null || spent == null || recorder == null || broken == null)
            {
                continue;
            }
            made++;
            string start = LearningDigest(batch);
            int sp0 = batch.SkillPoints;
            int ap0 = batch.AwakeningPoints;

            // 自动机策略学的每一级记成一步(次序就是策略学的次序;同一个策略的循环,这里只多记一笔)
            var list = new List<SkillStep>();
            List<SkillDef> order = SkillPlanner.Order(catalog, recorder.Growtype);
            for (bool progress = true; progress;)
            {
                progress = false;
                foreach (SkillDef def in order)
                {
                    if (recorder.LearnSkill(def.Id, 1, out _) == SkillOpResult.Ok)
                    {
                        list.Add(new SkillStep(def.Id, 1));
                        progress = true;
                    }
                }
            }
            steps += list.Count;
            SkillPlanner.SpendAll(spent);

            var service = new SkillLearning(batch);
            int version = batch.SkillsVersion;
            SkillBatchResult tried = service.TryLearnAll(list);
            if (tried.Ok && LearningDigest(batch) == start && batch.SkillsVersion == version)
            {
                tryKeeps++;
            }
            SkillBatchResult learned = service.LearnAll(list);
            if (learned.Ok && tried.Ok && learned.Sp == tried.Sp && learned.Sfp == tried.Sfp && LearningDigest(batch) == LearningDigest(spent))
            {
                sameAsPlanner++;
            }
            if (learned.Sp == sp0 - batch.SkillPoints && learned.Sfp == ap0 - batch.AwakeningPoints)
            {
                poolsMatch++;
            }
            foreach (SkillStep step in list)
            {
                single.LearnSkill(step.SkillId, step.Levels, out _);
            }
            if (LearningDigest(single) == LearningDigest(batch))
            {
                sameAsSingle++;
            }

            // 中间插一步目录里没有的技能:整批不成,失败下标就是插的位置,状态与版本一点不动
            var withBad = new List<SkillStep>(list);
            int at = withBad.Count / 2;
            withBad.Insert(at, new SkillStep(missingId, 1));
            string brokenStart = LearningDigest(broken);
            int brokenVersion = broken.SkillsVersion;
            SkillBatchResult failed = new SkillLearning(broken).LearnAll(withBad);
            if (!failed.Ok && failed.Result == SkillOpResult.Unknown && failed.FailedIndex == at && LearningDigest(broken) == brokenStart && broken.SkillsVersion == brokenVersion)
            {
                failedClean++;
            }

            // 原路退回(倒着退每一步)、再学一遍后重置:都回到起点
            var reversed = new List<SkillStep>(list);
            reversed.Reverse();
            SkillBatchResult forgot = service.ForgetAll(reversed);
            if (forgot.Ok && forgot.Sp == learned.Sp && forgot.Sfp == learned.Sfp && LearningDigest(batch) == start)
            {
                roundTrips++;
            }
            service.LearnAll(list);
            service.Reset();
            if (LearningDigest(batch) == start)
            {
                resets++;
            }
        }
        report(made == growtypes && steps > 0 && sameAsPlanner == made && sameAsSingle == made && poolsMatch == made && tryKeeps == made && failedClean == made && roundTrips == made && resets == made,
            "技能学习服务(批量):" + made + " 个成长类型各把自动机策略学的每一级(共 " + steps + " 步)记成一批——整批学下去与策略直接花光一致 " + sameAsPlanner + "、与逐个学一致 " + sameAsSingle
            + "、两个点池的消耗对得上 " + poolsMatch + "、只演算不动状态 " + tryKeeps + ";中间插一步必不成的:整批失败、失败下标对、状态不留痕 " + failedClean
            + ";原路退回回到起点 " + roundTrips + "、再学一遍后重置回到起点 " + resets);

        // (2) 一次能学几级
        int skillsAsked = 0;
        int learnableSome = 0;
        int askedBad = 0;
        int greedySettled = 0;
        int greedyMade = 0;
        for (int g = 0; g < growtypes; g++)
        {
            HeroProgress? hero = NewLearner(assets, level, g);
            if (hero == null)
            {
                continue;
            }
            greedyMade++;
            var service = new SkillLearning(hero);
            foreach (SkillDef def in catalog.Skills)
            {
                skillsAsked++;
                int n = service.LearnableLevels(def.Id);
                int current = hero.Skills.LevelOf(def.Id);
                int cap = SkillRules.TypeMaxLevel(def, hero.Growtype);
                bool good = n >= 0 && current + n <= Math.Max(cap, current);
                if (n > 0)
                {
                    learnableSome++;
                    good = good && service.CheckLearn(def.Id, n, out _) == SkillOpResult.Ok;
                }
                if (current + n < cap)
                {
                    good = good && service.CheckLearn(def.Id, n + 1, out _) != SkillOpResult.Ok;
                }
                if (!good)
                {
                    askedBad++;
                }
            }
            int sp1 = hero.SkillPoints;
            int ap1 = hero.AwakeningPoints;
            long spentSp = 0;
            long spentAp = 0;
            bool settled = true;
            foreach (SkillDef def in SkillPlanner.Order(catalog, hero.Growtype))
            {
                service.LearnMax(def.Id, out int cost);
                if (def.IsSpecial)
                {
                    spentAp += cost;
                }
                else
                {
                    spentSp += cost;
                }
                settled = settled && service.LearnableLevels(def.Id) == 0;
            }
            if (settled && sp1 - hero.SkillPoints == spentSp && ap1 - hero.AwakeningPoints == spentAp)
            {
                greedySettled++;
            }
        }
        report(greedyMade == growtypes && skillsAsked > 0 && learnableSome > 0 && askedBad == 0 && greedySettled == greedyMade,
            "技能学习服务(一次能学几级):" + greedyMade + " 个成长类型 × 目录里每个技能共问 " + skillsAsked + " 次,此刻能学的 " + learnableSome + " 次,与逐级问检查不一致的 " + askedBad
            + " 次;按策略的次序逐个学到上限,花掉的点与两个池子的减少对得上且问完不再有可学的级的成长类型 " + greedySettled + " 个");

        // (3) 快捷栏占用
        int barMade = 0;
        int strandedOff = 0;
        int strandedOn = 0;
        int barOk = 0;
        int learnedActive = 0;
        for (int g = 0; g < growtypes; g++)
        {
            HeroProgress? hero = NewLearner(assets, level, g);
            if (hero == null)
            {
                continue;
            }
            barMade++;
            SkillPlanner.SpendAll(hero);
            var service = new SkillLearning(hero);
            int before = service.Stranded();
            string kept = LearningDigest(hero, withBar: false);
            service.CommandAware = true;
            service.ArrangeBar();
            int after = service.Stranded();
            strandedOff += before;
            strandedOn += after;
            bool barFull = true;
            for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
            {
                SkillDef? def = catalog.Find(hero.Skills.QuickAt(slot));
                barFull = barFull && def != null && HeroSkills.IsImplemented(def.Id)
                    && !HeroSkills.CommandCasts(def.Command) && !(def.Id == service.QuickStandId && !def.Command.IsEmpty);
            }
            int unreachable = 0;
            foreach (SkillDef def in catalog.Skills)
            {
                if (def.IsActive && hero.Skills.LevelOf(def.Id) > 0 && HeroSkills.IsImplemented(def.Id))
                {
                    learnedActive++;
                    if (service.ReachOf(def.Id) == SkillReach.Unreachable)
                    {
                        unreachable++;
                    }
                }
            }
            if (after <= before && LearningDigest(hero, withBar: false) == kept && (after == 0 || barFull) && unreachable == after)
            {
                barOk++;
            }
        }
        report(barMade == growtypes && barOk == barMade,
            "技能学习服务(快捷栏占用):" + barMade + " 个成长类型花光点数后,学会且已实现的主动技能(各成长类型合计)" + learnedActive + " 个;只能靠快捷键却没上栏的 " + strandedOff
            + " 个,打开命令感知整理后 " + strandedOn + " 个;技能与点数不变、整理后没有或六格都被只能靠快捷键的占满、放不出来的恰好是没上栏的那些的成长类型 " + barOk + " 个");

        // (4) 自动学
        int autoMade = 0;
        int autoSame = 0;
        for (int g = 0; g < growtypes; g++)
        {
            HeroProgress? grown = NewLearner(assets, 1, g);
            HeroProgress? direct = NewLearner(assets, level, g);
            if (grown == null || direct == null)
            {
                continue;
            }
            autoMade++;
            var service = new SkillLearning(grown) { AutoLearn = true };
            grown.Gain(grown.Table.LevelExp(level) - grown.Exp);
            int learnedLevels = service.Update();
            int directLevels = SkillPlanner.SpendAll(direct);
            if (grown.Level == level && learnedLevels == directLevels && LearningDigest(grown) == LearningDigest(direct) && service.Update() == 0)
            {
                autoSame++;
            }
        }
        report(autoMade == growtypes && autoSame == autoMade,
            "技能学习服务(自动学):" + autoMade + " 个成长类型从 1 级升到 " + level + " 级,升级后自动学花光的与直接按该级总额花光的一致(级数、点数池、各技能等级、快捷栏)且升级只对账一次的有 " + autoSame + " 个");
    }
}
