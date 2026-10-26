using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// exp-check:经验与升级的检查(证据 E-111)。
/// 读真实数据里的升级经验表、技能点表、三张经验倍率表、主角 .chr 的成长块与全部副本的经验权重,检查它们的结构(递增、读得到、与一级属性一致),
/// 再把服务端的升级循环从 1 级逐级走到封顶、验证每一级的区间刚好升一级、封顶后不再涨级;
/// --level N 额外打印这一级的属性、升到下一级所需经验与同级怪物的击杀经验。
/// 表里的数值是用户自己的数据,只打印在本机的终端上(不要把它们抄进入库的文件);通过与否只看结构。
/// 也给 combat-check、dungeon-check、flow-check、sound-check 提供按选项造主角等级进度的方法(<see cref="ProgressFor"/>)。
/// </summary>
internal static class ExpCheck
{
    /// <summary>
    /// 按命令行选项造主角的等级进度:--no-exp 或 --no-damage(主角没有生命值与底栏)时是 null(杀怪不给经验),
    /// 否则是 --level(默认 1)级的本地默认主角;读不到角色定义时也是 null。
    /// </summary>
    public static HeroProgress? ProgressFor(DnfAssets assets, Options opts)
    {
        if (opts.NoExp || opts.NoDamage)
        {
            return null;
        }
        return HeroProgress.CreateForLocalHero(assets, opts.HeroLevel);
    }

    /// <summary>一行经验小结:起止等级、升了几级、累计经验、本级进度、点数、基础经验表的来源。</summary>
    public static string Summary(HeroProgress p, int startLevel)
    {
        return "  经验:等级 " + startLevel + " → " + p.Level + "(升了 " + p.LevelUps + " 级),累计得到 " + p.TotalGained + " 点,本级进度 " + p.ExpInLevel + "/" + p.LevelSpan
            + ",技能点 +" + p.SkillPoints + ",觉醒点 +" + p.AwakeningPoints
            + ";基础经验表:" + (p.Rewards.IsPlaceholder
                ? "占位表(没有导出的 " + MobRewards.FileName + ",不是原版数据)"
                : "来自 " + p.Rewards.Source + "(" + p.Rewards.Count + " 级)");
    }

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

        // 1. 升级经验表
        ExpTable table = data.ExpTable;
        int increasingBad = 0;
        for (int level = 2; level <= Leveling.MaxLevel - 1; level++)
        {
            if (table.LevelExp(level + 1) <= table.LevelExp(level))
            {
                increasingBad++;
            }
        }
        int sectionBad = 0;
        for (int level = 1; level <= Leveling.MaxLevel - 1; level++)
        {
            if (table.Section(level) <= 0)
            {
                sectionBad++;
            }
        }
        Console.WriteLine("升级经验表 " + ExpTable.Path + ":" + table.Count + " 项");
        Report(table.Count >= Leveling.MaxLevel - 1 && increasingBad == 0 && sectionBad == 0,
            "门槛覆盖到 " + (table.Count + 1) + " 级;2 到 " + Leveling.MaxLevel + " 级的门槛严格递增(违反 " + increasingBad + " 处),1 到 " + (Leveling.MaxLevel - 1)
            + " 级每级的区间大于 0(违反 " + sectionBad + " 处)");

        // 2. 技能点表
        SpTable sp = data.SpTable;
        int noSp = 0;
        for (int level = 2; level <= Leveling.MaxLevel; level++)
        {
            if (sp.AtLevelUp(level) <= 0)
            {
                noSp++;
            }
        }
        Console.WriteLine("技能点表 " + SpTable.Path + ":" + sp.Count + " 项");
        Report(sp.Count > Leveling.MaxLevel, "覆盖到 " + (sp.Count - 1) + " 级;2 到 " + Leveling.MaxLevel + " 级里升级不给技能点的有 " + noSp + " 级");

        // 3. 三张倍率表(读到的标志是"不全为 1";缺文件或缺标签时是全 1 的中性表)
        ExpRates rates = data.ExpRates;
        bool party = false, difficulty = false, kind = false;
        for (int i = 1; i <= ExpRates.PartySize; i++)
        {
            party |= rates.PartyRate(i) != 1f;
        }
        for (int i = 0; i < ExpRates.DifficultyCount; i++)
        {
            difficulty |= rates.DifficultyRate(i) != 1f;
        }
        for (int i = 0; i < ExpRates.KindCount; i++)
        {
            kind |= rates.KindRate(i) != 1f;
        }
        Console.WriteLine("经验倍率表 " + ExpRates.Path);
        Report(party && difficulty && kind, "队伍人数表" + (party ? "读到" : "全为 1") + "、副本难度表" + (difficulty ? "读到" : "全为 1") + "、怪物种类表" + (kind ? "读到" : "全为 1"));

        // 4. 主角 .chr 的成长块
        CharacterDef? chr = data.GetCharacter(0);
        if (chr == null)
        {
            Report(false, "读不到主角的 .chr(0 号角色)");
            return 1;
        }
        CharacterGrowth growth = data.GetGrowth(chr);
        int decreases = 0;
        int checkedTypes = 0;
        for (int g = 0; g < CharacterGrowth.GrowtypeCount; g++)
        {
            StatBlock inc = growth.Growtype(g);
            if (inc.Equals(default(StatBlock)))
            {
                continue;
            }
            checkedTypes++;
            StatBlock previous = growth.StatsAt(1, g);
            for (int level = 2; level <= Leveling.MaxLevel; level++)
            {
                StatBlock now = growth.StatsAt(level, g);
                if (now.HpMax < previous.HpMax || now.MpMax < previous.MpMax || now.PhysicalAttack < previous.PhysicalAttack || now.PhysicalDefense < previous.PhysicalDefense
                    || now.MagicalAttack < previous.MagicalAttack || now.MagicalDefense < previous.MagicalDefense || now.MpRegenSpeed < previous.MpRegenSpeed
                    || now.HitRecovery < previous.HitRecovery)
                {
                    decreases++;
                }
                previous = now;
            }
        }
        Console.WriteLine("角色成长 " + chr.Path + ":读到初始值" + (growth.HasInitial ? "" : "(没有)") + "与 " + growth.GrowtypeBlocks + " 个成长类型块");
        Report(growth.HasInitial && growth.GrowtypeBlocks >= 1 && decreases == 0,
            "初始值与成长块读到;逐级属性不下降(" + checkedTypes + " 个有数据的成长类型,下降 " + decreases + " 处)");

        // 5. 一级属性与不带等级进度的老路径一致(HeroVitals.FromChr / HeroPanel.FromChr)
        HeroVitals oldVitals = HeroVitals.FromChr(chr);
        HeroVitals newVitals = HeroVitals.FromStats(1, growth.Initial);
        bool vitalsSame = oldVitals.Level == newVitals.Level && oldVitals.HpMax == newVitals.HpMax && oldVitals.MpMax == newVitals.MpMax
            && oldVitals.PhysicalDefense == newVitals.PhysicalDefense && oldVitals.MagicalDefense == newVitals.MagicalDefense
            && oldVitals.HitRecovery == newVitals.HitRecovery && oldVitals.MpRegenPerMinute == newVitals.MpRegenPerMinute;
        HeroPanel oldPanel = HeroPanel.FromChr(chr, 1, default);
        HeroPanel newPanel = HeroPanel.FromStats(1, growth.Initial, default);
        bool panelSame = oldPanel.PhysicalAttack == newPanel.PhysicalAttack && oldPanel.MagicalAttack == newPanel.MagicalAttack;
        Report(vitalsSame && panelSame, "一级的生命魔法与防御" + (vitalsSame ? "一致" : "不一致") + "、攻击面板" + (panelSame ? "一致" : "不一致") + "(新路径 FromStats 对老路径 FromChr)");

        // 6. 升级循环:每一级给刚好一个区间的经验升一级,封顶后不再涨级
        int loopBad = 0;
        for (int level = 1; level < Leveling.MaxLevel; level++)
        {
            ExpGainResult r = Leveling.Gain(table, sp, level, table.LevelExp(level), table.Section(level));
            if (r.Level != level + 1 || r.Exp != table.LevelExp(level + 1) || r.LevelsGained != 1)
            {
                loopBad++;
            }
        }
        ExpGainResult capped = Leveling.Gain(table, sp, Leveling.MaxLevel, table.LevelExp(Leveling.MaxLevel), 1000000);
        ExpGainResult all = Leveling.Gain(table, sp, 1, 0, int.MaxValue);
        Report(loopBad == 0 && capped.Level == Leveling.MaxLevel && all.Level == Leveling.MaxLevel,
            "升级循环:1 到 " + (Leveling.MaxLevel - 1) + " 级每级给一个区间都刚好升一级(违反 " + loopBad + " 处),封顶后加经验不涨级,一次给足从 1 级升到 " + all.Level
            + " 级(技能点 +" + all.SkillPoints + "、觉醒点 +" + all.AwakeningPoints + ")");

        // 7. 基础经验表与击杀经验
        MobRewardTable rewards = MobRewards.Load(assets, table);
        int killBad = 0;
        for (int level = 1; level < Leveling.MaxLevel; level++)
        {
            if (KillExp.ForKill(rewards, rates, level, level, 0, false, 0, 1f) <= 0)
            {
                killBad++;
            }
        }
        Console.WriteLine("怪物基础经验表(服务端数据库表 monster_reward_ref,不在客户端数据里):" + (rewards.IsPlaceholder
            ? "占位表(没有用户导出的 " + MobRewards.FileName + ",也没设环境变量 " + MobRewards.EnvVariable + ";每级 = 本级区间 / " + MobRewardTable.PlaceholderDivisor + ",不是原版数据)"
            : "来自 " + rewards.Source + "(" + rewards.Count + " 级)"));
        Report(killBad == 0, "同级普通怪每级击杀经验都大于 0(违反 " + killBad + " 处)");

        // 8. 副本里的经验权重
        int dungeons = 0, withWeight = 0, unreadable = 0;
        float minWeight = float.MaxValue, maxWeight = float.MinValue;
        foreach (PvfEntry e in assets.Pvf.Entries)
        {
            if (!e.Path.StartsWith("dungeon/", StringComparison.OrdinalIgnoreCase) || !e.Path.EndsWith(".dgn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            DungeonMaze? maze;
            try
            {
                maze = DungeonMaze.Read(assets, e.Path);
            }
            catch (Exception)
            {
                unreadable++;
                continue;
            }
            if (maze == null)
            {
                continue;
            }
            dungeons++;
            if (maze.ExpWeight != 1f)
            {
                withWeight++;
                minWeight = Math.Min(minWeight, maze.ExpWeight);
                maxWeight = Math.Max(maxWeight, maze.ExpWeight);
            }
        }
        Console.WriteLine("副本经验权重(.dgn 的 [experience increasing point]):读通迷宫的副本 " + dungeons + " 座,其中权重不为 1 的 " + withWeight + " 座"
            + (withWeight > 0 ? "(范围 " + F(minWeight) + " 到 " + F(maxWeight) + ")" : string.Empty));
        Report(unreadable == 0 && minWeight >= 0f, "没有读不动的 .dgn(" + unreadable + " 个),权重没有负数");

        // 8b. 副本评分系统与通关经验(M32,证据 E-124):评分文件读得到、门槛与用时档有序、平均用时记录对得上副本列表、评分加成比例读到;
        // 标准等级([basis level])读得到;通关经验的算术(对每一级、用占位或导出的基础经验表):总量随击杀数不减、评分加成不为负、没有评分档位时加成为 0
        RankSystem rank = data.RankSystem;
        bool thresholdsOrdered = rank.ThresholdsGiven == RankSystem.ThresholdCount && rank.Threshold(RankSystem.ThresholdCount - 1) > 0;
        for (int i = 1; i < rank.ThresholdsGiven; i++)
        {
            thresholdsOrdered &= rank.Threshold(i) < rank.Threshold(i - 1);
        }
        bool stepsOrdered = rank.StepCount >= 1;
        for (int i = 1; i < rank.StepCount; i++)
        {
            stepsOrdered &= rank.StepAt(i).Threshold > rank.StepAt(i - 1).Threshold && rank.StepAt(i).Bonus >= rank.StepAt(i - 1).Bonus;
        }
        var listed = new List<KeyValuePair<int, string>>(WorldMapDef.ParseList(assets.ReadScript(DungeonRoomPool.DungeonListPath), "dungeon/"));
        int listedWithAverage = 0, averageBad = 0, withStandardLevel = 0, standardBad = 0, unreadableScripts = 0;
        foreach (KeyValuePair<int, string> d in listed)
        {
            for (int difficultyIndex = 0; difficultyIndex < 4; difficultyIndex++)
            {
                if (rank.AverageTime(d.Key, 1, difficultyIndex) < 0)
                {
                    averageBad++;
                }
            }
            if (rank.AverageTime(d.Key, 1, 0) > 0)
            {
                listedWithAverage++;
            }
            ScriptLine[] script;
            try
            {
                script = assets.ReadScript(d.Value);
            }
            catch (Exception)
            {
                unreadableScripts++;
                continue;
            }
            int standard = GoldDrop.StandardLevel(script);
            if (standard > 0)
            {
                withStandardLevel++;
            }
            if (standard < 0 || standard > MobRewardTable.MaxLevel)
            {
                standardBad++;
            }
        }
        Console.WriteLine("副本评分系统 " + RankSystem.Path + ":门槛 " + rank.ThresholdsGiven + " 个,用时奖励 " + rank.StepCount + " 档,平均用时记录 " + rank.AverageRecordCount + " 条;"
            + "副本列表 " + listed.Count + " 座,其中单人普通难度有平均用时的 " + listedWithAverage + " 座,读到标准等级([basis level])的 " + withStandardLevel + " 座");
        Report(thresholdsOrdered, "评分门槛读到 " + RankSystem.ThresholdCount + " 个,从高到低严格递减,最低一档大于 0");
        Report(stepsOrdered, "用时奖励至少一档,门槛严格递增、分数不减");
        Report(rank.AverageRecordCount > 0 && listedWithAverage > 0 && averageBad == 0, "平均用时记录读到,有副本在列表里查得到单人普通难度的平均用时,没有负数(违反 " + averageBad + " 处)");
        Report(unreadableScripts == 0 && withStandardLevel > 0 && standardBad == 0, "副本脚本都读得动(" + unreadableScripts + " 个读不动),标准等级读得到,没有越界(1 到 " + MobRewardTable.MaxLevel + ",基础经验表的范围;违反 " + standardBad + " 处)");

        bool bonusRead = false, bonusOrdered = true;
        for (int i = 0; i < ExpRates.RankBonusCount; i++)
        {
            float rate = rates.RankBonusRate(i);
            bonusRead |= rate > 0f;
            bonusOrdered &= rate >= 0f && (i == 0 || rate >= rates.RankBonusRate(i - 1));
        }
        Report(bonusRead && bonusOrdered && rates.RankBonusRate(-1) == 0f && rates.RankBonusRate(ExpRates.RankBonusCount) == 0f,
            "评分加成比例([clear rank exp bonusrate])读到,档位 0 到 " + (ExpRates.RankBonusCount - 1) + " 不为负、随档位不减,没有档位(−1)与越界的加成为 0");

        int clearBad = 0, boundsBad = 0;
        for (int level = 1; level < Leveling.MaxLevel; level++)
        {
            ClearExp few = ClearReward.Compute(rewards, rates, level, level, 10, 0, 1f, ExpRates.RankBonusCount - 1);
            ClearExp many = ClearReward.Compute(rewards, rates, level, level, 20, 0, 1f, ExpRates.RankBonusCount - 1);
            ClearExp none = ClearReward.Compute(rewards, rates, level, level, 10, 0, 1f, -1);
            if (few.Base == 0 || few.DungeonTotal == 0 || many.DungeonTotal < few.DungeonTotal || many.Base < few.Base || none.RankBonus != 0 || none.Base != few.Base)
            {
                clearBad++;
            }
            if (few.Total != (long)few.Base + few.RankBonus || ClearReward.Compute(rewards, rates, level, level, 0, 0, 1f, 0).Total != 0)
            {
                boundsBad++;
            }
        }
        Report(clearBad == 0 && boundsBad == 0, "通关经验的算术:每级(同级标准等级、10 次击杀)分到的基础经验与总量大于 0,击杀翻倍不减,没有评分档位时加成为 0,没打死怪物时为 0(违反 " + clearBad + " + " + boundsBad + " 处)");

        // 9. --level N:这一级的样子
        int shown = Math.Max(1, Math.Min(opts.HeroLevel, Leveling.MaxLevel));
        var progress = new HeroProgress(table, sp, growth, rewards, rates, shown);
        StatBlock stats = progress.Stats;
        Console.WriteLine();
        Console.WriteLine("等级 " + shown + "(--level):生命 " + F(StatBlock.Units(stats.HpMax)) + " 魔法 " + F(StatBlock.Units(stats.MpMax)) + " 物攻 " + F(StatBlock.Units(stats.PhysicalAttack))
            + " 物防 " + F(StatBlock.Units(stats.PhysicalDefense)) + " 魔攻 " + F(StatBlock.Units(stats.MagicalAttack)) + " 魔防 " + F(StatBlock.Units(stats.MagicalDefense))
            + " 受击恢复 " + F(StatBlock.Units(stats.HitRecovery)) + " 魔法恢复 " + F(StatBlock.Units(stats.MpRegenSpeed)));
        Console.WriteLine("  累计经验 " + progress.Exp + ",升到下一级要 " + progress.LevelSpan + " 点;升级时得技能点 " + sp.AtLevelUp(shown + 1) + ",觉醒点 " + (shown + 1 >= Leveling.AwakeningFirstLevel ? 1 : 0));
        foreach ((string name, int role, bool named) in new[] { ("普通怪", 0, false), ("小精英", 1, false), ("大精英", 2, false), ("首领", 3, false), ("有名怪", 0, true) })
        {
            Console.WriteLine("  同级" + name + "(副本权重 1、普通难度、单人):击杀经验 " + KillExp.ForKill(rewards, rates, shown, shown, role, named, 0, 1f));
        }
        Console.WriteLine(Summary(progress, shown));

        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "经验与升级检查通过" : "经验与升级检查有 " + bad + " 项失败");
        return bad == 0 ? 0 : 1;
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
