using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf awaken-check [--dump &lt;文件&gt;]`:觉醒(r15-awaken)的整库检查与对拍。
/// 一、对拍:用 Dnf.Game 的既有读法(<see cref="CharacterGrowth"/>、<see cref="SkillGrants"/>、<see cref="SkillDef"/>、<see cref="QuestScript"/>)读全库,
///   把"觉醒相关的读出结果"逐行写进 --dump 文件,与 `python3 tools/py/awaken_ref.py --dump` 的输出逐行比较(排序后应完全一致;参照实现只共用语义规格,各自读脚本):
///   1. 每个 .chr:每个 (等级、成长类型 0 到 5、觉醒 0 到 2) 的逐级增量与累计属性——成长表觉醒段与 <see cref="CharacterGrowth"/> 的读法对读,含 50 到 51 级那一步选哪一块;
///   2. 每个 .chr:每个 (成长类型、觉醒) 拥有的白送技能(开局、成长类型块、觉醒块的 [awakening skill] 列表);
///   3. 每个 .skl:[second growtype maximum level] 与六个成长类型 × 三个觉醒状态下的最高级(<see cref="SkillRules.TypeMaxLevel(SkillDef, int, int)"/>)、读档上限;
///   4. 每个 .qst:觉醒任务链(成就类且 [job change quest] 为 2)与觉醒奖励的觉醒号、等级范围。
/// 二、整库演习:对每个职业的每个有数据的转职方向,真数据上走一遍觉醒(49 级拒绝、次序、白送技能、点数不动、面板换块、重置、存档往返与旧存档)。
/// 三、学习上限:每个职业的每个技能在每个 (成长类型、觉醒) 下,能学的最高级不超过各自的上限、上限之上学不了、"能不能学"与"最多学到几级"互相一致。
/// 数量、编号与数值来自本机的数据,只打印在终端与 out/ 下的 --dump 文件,不要抄进入库的文件;通过与否只看一致与演习的对错。
/// </summary>
internal static class AwakenCheck
{
    private static readonly int[] DumpLevels = { 1, 14, 15, 49, 50, 99 };
    private static readonly int[] StatLevels = { 50, 51, 60, 99 };
    private static readonly int[] LimitLevels = { 1, 49, 50, 60, Leveling.MaxLevel };

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        int bad = 0;
        var dump = new List<string>();

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        List<string> paths = assets.Pvf.Entries.Select(e => e.Path.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal).ToList();

        // 1 与 2. 角色脚本:成长块与白送技能
        int chrParsed = 0, withAwakening = 0, missingBlocks = 0, atFiftyBad = 0, transferTypes = 0;
        foreach (string path in paths.Where(p => p.StartsWith("character/", StringComparison.Ordinal) && p.EndsWith(".chr", StringComparison.Ordinal)))
        {
            ScriptLine[]? lines = data.ReadScript(path);
            if (lines == null)
            {
                continue;
            }
            chrParsed++;
            CharacterGrowth growth = CharacterGrowth.Parse(lines);
            bool any = false;
            for (int g = 0; g < CharacterGrowth.GrowtypeCount; g++)
            {
                bool hasType = !growth.Growtype(g).Equals(default(StatBlock));
                if (g >= 1 && hasType)
                {
                    transferTypes++;
                }
                for (int a = 0; a <= CharacterGrowth.AwakeningCount; a++)
                {
                    foreach (int level in DumpLevels)
                    {
                        dump.Add("growth " + path + " L=" + level + " g=" + g + " a=" + a + " " + Fmt(growth.Increment(level, g, a)));
                    }
                    foreach (int level in StatLevels)
                    {
                        dump.Add("stats " + path + " L=" + level + " g=" + g + " a=" + a + " " + Fmt(growth.StatsAt(level, g, a)));
                    }
                    dump.Add("gifts " + path + " g=" + g + " a=" + a + " " + GiftText(SkillGrants.Read(lines, g, a)));
                    if (a < 1)
                    {
                        continue;
                    }
                    if (!growth.Increment(CharacterGrowth.AwakeningFromLevel, g, a).Equals(default(StatBlock)))
                    {
                        any = true;
                    }
                    else if (g >= 1 && hasType)
                    {
                        missingBlocks++;
                    }
                    // 50 级:觉醒块还没加过任何一步,面板与没觉醒时一样
                    if (g >= 1 && hasType && !growth.StatsAt(CharacterGrowth.AwakeningFromLevel, g, a).Equals(growth.StatsAt(CharacterGrowth.AwakeningFromLevel, g, 0)))
                    {
                        atFiftyBad++;
                    }
                }
            }
            withAwakening += any ? 1 : 0;
        }
        Console.WriteLine("角色脚本 " + chrParsed + " 个,其中带觉醒块的 " + withAwakening + " 个;有数据的转职方向 " + transferTypes + " 个;转职方向里没有数据的觉醒块 " + missingBlocks + " 个");
        Report(chrParsed > 0, "读到角色脚本");
        Report(atFiftyBad == 0, "50 级的面板与觉醒无关(觉醒块从 50 到 51 级那一步才起作用;违反 " + atFiftyBad + " 处)");

        // 3. 技能脚本:第二成长类型最高级
        int skills = 0, withSecond = 0, shapeBad = 0, awakeningOnly = 0, aboveEffective = 0;
        foreach (string path in paths.Where(p => p.StartsWith("skill/", StringComparison.Ordinal) && p.EndsWith(".skl", StringComparison.Ordinal)))
        {
            ScriptLine[]? lines = data.ReadScript(path);
            if (lines == null)
            {
                continue;
            }
            skills++;
            var def = new SkillDef(0, path, lines);
            int[] second = def.SecondGrowtypeMaxLevels;
            if (second.Length > 0)
            {
                withSecond++;
                if (second.Length != CharacterGrowth.GrowtypeCount * CharacterGrowth.AwakeningCount)
                {
                    shapeBad++;
                }
                if (def.SaveCeilingLevel > def.EffectiveMaxLevel)
                {
                    aboveEffective++;
                }
                if (def.GrowtypeMaxLevels.All(m => m == 0) && second.Any(m => m != 0))
                {
                    awakeningOnly++;
                }
            }
            var typeMax = new StringBuilder();
            for (int g = 0; g < CharacterGrowth.GrowtypeCount; g++)
            {
                for (int a = 0; a <= CharacterGrowth.AwakeningCount; a++)
                {
                    typeMax.Append(' ').Append(SkillRules.TypeMaxLevel(def, g, a).ToString(CultureInfo.InvariantCulture));
                }
            }
            dump.Add("skl " + path + " second=" + (second.Length == 0 ? "-" : string.Join(",", second)) + " ceiling=" + def.SaveCeilingLevel + " typemax" + typeMax);
        }
        Console.WriteLine("技能脚本 " + skills + " 个,写了 [second growtype maximum level] 的 " + withSecond + " 个;觉醒后上限高过有效最高级的 " + aboveEffective + " 个;只有觉醒才开的 " + awakeningOnly + " 个");
        Report(skills > 0, "读到技能脚本");
        Report(shapeBad == 0, "第二成长类型最高级都是 " + CharacterGrowth.GrowtypeCount * CharacterGrowth.AwakeningCount + " 个数(不是的 " + shapeBad + " 个)");

        // 4. 任务脚本:觉醒链与觉醒奖励
        int quests = 0, chain = 0, awakeReward = 0;
        foreach (string path in paths.Where(p => p.StartsWith("n_quest/", StringComparison.Ordinal) && p.EndsWith(".qst", StringComparison.Ordinal)))
        {
            ScriptLine[]? lines = data.ReadScript(path);
            if (lines == null)
            {
                continue;
            }
            quests++;
            QuestDef q = QuestScript.Parse(lines, 0, path);
            bool isChain = QuestRules.IsAwakeningChain(q);
            if (isChain || q.IsAwakeningReward)
            {
                chain += isChain ? 1 : 0;
                awakeReward += q.IsAwakeningReward ? 1 : 0;
                int awakening = q.IsAwakeningReward ? QuestRules.AwakeningOf(q) : 0;
                string max = q.MaxLevel == int.MaxValue ? "max" : q.MaxLevel.ToString(CultureInfo.InvariantCulture);
                dump.Add("qst " + path + " chain=" + (isChain ? 1 : 0) + " awk=" + awakening + " lv=" + q.MinLevel + "," + max);
            }
        }
        Console.WriteLine("任务脚本 " + quests + " 个,觉醒链上的 " + chain + " 个,奖励是觉醒的 " + awakeReward + " 个");
        Report(quests > 0, "读到任务脚本");

        if (opts.DumpFile != null)
        {
            dump.Sort(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.DumpFile))!);
            using var w = new StreamWriter(opts.DumpFile, false, new UTF8Encoding(false)) { NewLine = "\n" };
            foreach (string line in dump)
            {
                w.WriteLine(line);
            }
            Console.WriteLine("已写出 " + dump.Count + " 行 -> " + opts.DumpFile);
        }

        // 5. 整库演习
        MobRewardTable rewards = MobRewards.Load(assets, data.ExpTable);
        Dictionary<int, string> jobs = data.ReadList(GameData.CharacterListPath) ?? new Dictionary<int, string>();
        int flows = 0, specials = 0;
        var failures = new List<string>();
        foreach (int job in jobs.Keys.OrderBy(j => j))
        {
            CharacterDef? chr = data.GetCharacter(job);
            if (chr == null)
            {
                continue;
            }
            CharacterGrowth growth = data.GetGrowth(chr);
            for (int g = 1; g < CharacterGrowth.GrowtypeCount; g++)
            {
                if (growth.Growtype(g).Equals(default(StatBlock)))
                {
                    continue;
                }
                flows++;
                string? error = Flow(data, chr, rewards, g, ref specials);
                if (error != null)
                {
                    failures.Add(job + "/" + g + ":" + error);
                }
            }
        }
        Console.WriteLine("演习 " + flows + " 个(职业 × 转职方向),其中花觉醒点学过特殊技能的 " + specials + " 个");
        Report(flows > 0, "有可演习的职业与转职方向");
        Report(failures.Count == 0, "每个转职方向的觉醒演习都对(49 级拒绝、次序、白送技能、点数不动、面板换块、特殊技能的觉醒点、重置、存档往返、旧存档)"
            + (failures.Count == 0 ? "" : ":" + string.Join(" ", failures.Take(12))));

        // 6. 学习上限
        int limitChecks = 0, limitBad = 0;
        foreach (int job in jobs.Keys.OrderBy(j => j))
        {
            SkillCatalog catalog = SkillCatalog.Load(data, job);
            foreach (SkillDef def in catalog.Skills)
            {
                for (int g = 0; g < CharacterGrowth.GrowtypeCount; g++)
                {
                    for (int a = 0; a <= CharacterGrowth.AwakeningCount; a++)
                    {
                        foreach (int heroLevel in LimitLevels)
                        {
                            limitChecks++;
                            if (!LimitsAgree(def, heroLevel, g, a))
                            {
                                limitBad++;
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine("学习上限检查 " + limitChecks + " 项");
        Report(limitBad == 0, "能学的最高级不超过各自的上限、上限之上学不了、能不能学与最多学到几级互相一致(违反 " + limitBad + " 项)");

        Console.WriteLine(bad == 0 ? "awaken-check 全部通过" : "awaken-check 有 " + bad + " 项失败");
        return bad == 0 ? 0 : 1;
    }

    private static string Fmt(StatBlock s)
    {
        return string.Join(" ", new[] { s.HpMax, s.MpMax, s.PhysicalAttack, s.PhysicalDefense, s.MagicalAttack, s.MagicalDefense, s.InventoryLimit, s.MpRegenSpeed, s.HitRecovery }
            .Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }

    private static string GiftText(List<SkillGrant> grants)
    {
        return grants.Count == 0 ? "-" : string.Join(" ", grants.OrderBy(x => x.SkillId).Select(x => x.SkillId + ":" + x.Level));
    }

    /// <summary>一个技能在 (成长类型、觉醒、主角等级) 下的"能不能学"与"最多学到几级"互相一致,且都不越过上限。</summary>
    private static bool LimitsAgree(SkillDef def, int heroLevel, int growtype, int awakening)
    {
        int cap = SkillRules.TypeMaxLevel(def, growtype, awakening);
        int most = SkillRules.MaxLearnable(def, heroLevel, growtype, awakening);
        if (cap <= 0)
        {
            return most == -1 && !SkillRules.CanLearn(def, heroLevel, growtype, awakening, 1);
        }
        if (most < 0 || most > cap)
        {
            return false;
        }
        for (int target = 0; target <= cap + 1; target++)
        {
            if (SkillRules.CanLearn(def, heroLevel, growtype, awakening, target) != (target >= 1 && target <= most))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>真数据上演一遍觉醒;返回 null 表示没问题,否则是一个短词指出哪一步不对。</summary>
    private static string? Flow(GameData data, CharacterDef chr, MobRewardTable rewards, int growtype, ref int specials)
    {
        CharacterGrowth growth = data.GetGrowth(chr);
        HeroProgress hero = HeroProgress.Create(data, chr, rewards, AwakeningRules.MinLevel);
        if (!hero.ChangeGrowtype(growtype))
        {
            return "change-growtype";
        }

        // 门槛差一级:49 级拒绝,什么都不动
        hero.SetLevel(AwakeningRules.MinLevel - 1);
        int version = hero.Version, skillsVersion = hero.SkillsVersion, sp = hero.SkillPoints, sfp = hero.AwakeningPoints;
        if (hero.Awaken(1) != AwakeningCheck.LevelTooLow || hero.Awakening != 0 || hero.Version != version || hero.SkillsVersion != skillsVersion
            || hero.SkillPoints != sp || hero.AwakeningPoints != sfp)
        {
            return "below-threshold";
        }

        // 次序:没有一觉不能二觉;一觉、二觉依次进行,点数不动、面板在 50 级不变、60 级换成觉醒块
        hero.SetLevel(AwakeningRules.MinLevel);
        if (hero.Awaken(2) != AwakeningCheck.OutOfOrder || hero.Awakening != 0)
        {
            return "order";
        }
        sp = hero.SkillPoints;
        sfp = hero.AwakeningPoints;
        StatBlock before = hero.BaseStats;
        for (int awakening = 1; awakening <= AwakeningRules.Count; awakening++)
        {
            if (hero.Awaken(awakening) != AwakeningCheck.Ok || hero.Awakening != awakening)
            {
                return "awaken-" + awakening;
            }
            if (hero.SkillPoints != sp || hero.AwakeningPoints != sfp)
            {
                return "points-moved";
            }
            string? gift = MissingGift(hero, chr, growtype, awakening);
            if (gift != null)
            {
                return "gift-" + awakening;
            }
            if (!hero.BaseStats.Equals(before))
            {
                return "stats-at-fifty";
            }
            hero.SetLevel(60);
            if (!hero.BaseStats.Equals(growth.StatsAt(60, growtype, awakening)))
            {
                return "stats-" + awakening;
            }
            hero.SetLevel(AwakeningRules.MinLevel);
        }
        if (hero.Awaken(1) != AwakeningCheck.OutOfOrder || hero.Awaken(3) != AwakeningCheck.BadTarget)
        {
            return "after-second";
        }

        // 觉醒点只能花在特殊技能上、永远不为负:升到满级,找一个现在学得了的特殊技能学一级再退回去
        hero.SetLevel(Leveling.MaxLevel);
        hero.ResetSkills();
        foreach (SkillDef def in hero.Skills.Catalog.Skills.Where(d => d.IsSpecial))
        {
            if (hero.Skills.CheckLearn(def.Id, hero.Level, hero.Growtype, hero.Awakening, 1, out int cost) != SkillOpResult.Ok || cost > hero.AwakeningPoints)
            {
                continue;
            }
            int apBefore = hero.AwakeningPoints, spBefore = hero.SkillPoints;
            if (hero.LearnSkill(def.Id, 1, out int spent) != SkillOpResult.Ok || spent != cost || hero.AwakeningPoints != apBefore - cost
                || hero.SkillPoints != spBefore || hero.AwakeningPoints < 0)
            {
                return "special-learn";
            }
            if (hero.RefundSkill(def.Id, 1, out int back) != SkillOpResult.Ok || back != cost || hero.AwakeningPoints != apBefore || hero.SkillPoints != spBefore)
            {
                return "special-refund";
            }
            specials++;
            break;
        }

        // 重置:白送的(含当前觉醒的)都在、点数回到总额、觉醒不动
        hero.ResetSkills();
        if (hero.Awakening != AwakeningRules.Count || hero.SkillPoints != SkillRules.TotalSp(hero.Sp, hero.Level) || hero.AwakeningPoints != SkillRules.TotalSfp(hero.Level))
        {
            return "reset-points";
        }
        if (MissingGift(hero, chr, growtype, AwakeningRules.Count) != null)
        {
            return "reset-gift";
        }

        // 存档往返:再拍一张逐字节相同,觉醒与成长类型、面板、每个技能的等级都回来;旧存档(没有 awakening 一行)读回来是没觉醒
        string text = hero.Snapshot().ToText();
        if (!HeroSave.TryParse(text, out HeroSave? parsed, out _) || parsed == null)
        {
            return "save-parse";
        }
        HeroProgress other = HeroProgress.Create(data, chr, rewards, 1);
        other.Restore(parsed, _ => null);
        if (other.Awakening != AwakeningRules.Count || other.Growtype != growtype || other.Snapshot().ToText() != text || !other.BaseStats.Equals(hero.BaseStats))
        {
            return "save-roundtrip";
        }
        foreach (SkillDef def in hero.Skills.Catalog.Skills)
        {
            if (other.Skills.LevelOf(def.Id) != hero.Skills.LevelOf(def.Id))
            {
                return "save-skills";
            }
        }
        string oldText = string.Join("\n", text.Split('\n').Where(line => !line.StartsWith("awakening ", StringComparison.Ordinal)));
        if (!HeroSave.TryParse(oldText, out HeroSave? oldSave, out _) || oldSave == null)
        {
            return "old-save-parse";
        }
        other.Restore(oldSave, _ => null);
        if (other.Awakening != 0 || other.Growtype != growtype)
        {
            return "old-save";
        }
        return null;
    }

    /// <summary>这个觉醒状态下应有的白送技能里,技能目录里有、但主角等级没到的第一个;都有返回 null。</summary>
    private static string? MissingGift(HeroProgress hero, CharacterDef chr, int growtype, int awakening)
    {
        foreach (SkillGrant grant in SkillGrants.Read(chr.Tags.Lines, growtype, awakening))
        {
            if (hero.Skills.Catalog.Find(grant.SkillId) != null && hero.Skills.LevelOf(grant.SkillId) < grant.Level)
            {
                return grant.SkillId + ":" + grant.Level;
            }
        }
        return null;
    }
}
