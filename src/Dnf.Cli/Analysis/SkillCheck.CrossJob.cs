using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// skill-check 的第 11 项检查(阶段 2a 跨职业状态族配方与作用域隔离):
/// - 验证 <see cref="SkillRecipes.AllJobBuffs"/>、<see cref="SkillRecipes.AllJobGuards"/>、<see cref="SkillRecipes.AllJobThrows"/>
///   覆盖到的全部职业在真实 PVF 中的角色脚本、技能目录、动作帧(无攻击框/有攻击框)、被动对象、逐级 [static data] 与 [level info] 列、
///   以及 <see cref="HeroSkills.Create(GameData, CharacterDef, int, SkillBook, SkillRecipes)"/> 建槽与阶段机参数完全读得通;
/// - 验证全职业间编号碰撞隔离:非首职业不误继承首职业专属手写技与首职业局部配方表,但全职业均共享 5 项基类能力(后跳/倒地起身/3 个基类增益)。
/// 输出只打印汇总计数,不打印任何技能号、职业名或路径。
/// </summary>
internal static partial class SkillCheck
{
    private static void ReportCrossJobRecipes(DnfAssets assets, GameData data, Action<bool, string> report)
    {
        var problems = new List<string>();
        var jobsSeen = new HashSet<int>();
        var chrCache = new Dictionary<int, CharacterDef?>();
        var catCache = new Dictionary<int, SkillCatalog>();

        CharacterDef? ChrOf(int job)
        {
            if (!chrCache.TryGetValue(job, out CharacterDef? c))
            {
                c = data.GetCharacter(job);
                chrCache[job] = c;
            }
            return c;
        }

        SkillCatalog CatOf(int job)
        {
            if (!catCache.TryGetValue(job, out SkillCatalog? cat))
            {
                cat = SkillCatalog.Load(data, job);
                catCache[job] = cat;
            }
            return cat;
        }

        bool HasPvfEntry(string? path) => !string.IsNullOrEmpty(path) && assets.Pvf.TryGetEntry(path, out _);

        int buffChecked = 0;
        int buffLevels = 0;
        int buffExtras = 0;
        foreach (JobBuffEntry entry in SkillRecipes.Default.AllJobBuffs)
        {
            buffChecked++;
            jobsSeen.Add(entry.Job);
            string label = "跨职业增益 #" + buffChecked;
            CharacterDef? chr = ChrOf(entry.Job);
            if (chr == null)
            {
                problems.Add(label + ":角色脚本读不到");
                continue;
            }
            string? buffMotionPath = chr.MotionPath("[buff motion]");
            AniFile? buffAni = buffMotionPath == null ? null : assets.LoadAni(buffMotionPath);
            if (buffAni == null || buffAni.Frames.Length == 0 || buffAni.Frames.Any(fr => fr.Boxes.Any(b => b.Type == AniBoxType.Attack)))
            {
                problems.Add(label + ":角色 [buff motion] 读不到、没有帧或含攻击框");
            }
            SkillCatalog cat = CatOf(entry.Job);
            BuffRecipe recipe = entry.Recipe;
            SkillDef? def = cat.Find(recipe.SkillId);
            if (def == null || !def.IsActive)
            {
                problems.Add(label + ":技能不在目录里或不是主动技能");
                continue;
            }
            int needCols = Math.Max(recipe.DurationColumn, recipe.AmountStaticIndex >= 0 ? 0 : recipe.AmountColumn) + 1;
            int maxStatic = recipe.AmountStaticIndex;
            foreach (BuffStatEntry ex in recipe.ExtraStats)
            {
                if (ex.AmountStaticIndex >= 0)
                {
                    maxStatic = Math.Max(maxStatic, ex.AmountStaticIndex);
                }
                else
                {
                    needCols = Math.Max(needCols, ex.AmountColumn + 1);
                }
            }
            if (def.LevelInfo.Columns < needCols || !def.LevelInfo.IsConsistent || def.LevelInfo.Levels < def.MaximumLevel)
            {
                problems.Add(label + ":[level info] 列数或行数不够");
            }
            if (maxStatic >= 0 && def.StaticData.Length <= maxStatic)
            {
                problems.Add(label + ":[static data] 不够长");
            }
            int learnable = def.GrowtypeMaxLevels.Length > 0 ? Math.Min(def.GrowtypeMaxLevels.Max(), def.MaximumLevel) : def.MaximumLevel;
            for (int lv = 1; lv <= learnable; lv++)
            {
                buffLevels++;
                int dur = def.GetLevel(lv, recipe.DurationColumn, false);
                int amt = recipe.AmountStaticIndex >= 0 ? def.GetStatic(recipe.AmountStaticIndex, false) : def.GetLevel(lv, recipe.AmountColumn, false);
                if (dur <= 0 || amt <= 0)
                {
                    problems.Add(label + " " + lv + " 级:持续时间或主属性数值不是正数");
                }
                if ((recipe.Stat == BuffStat.SuperArmorChance || recipe.Stat == BuffStat.AutoGuardChance) && amt * recipe.AmountScale > 1.0)
                {
                    problems.Add(label + " " + lv + " 级:概率超过 100%");
                }
                foreach (BuffStatEntry ex in recipe.ExtraStats)
                {
                    buffExtras++;
                    if (ex.Evaluate(def, lv) == 0.0)
                    {
                        problems.Add(label + " " + lv + " 级:额外属性修正读出来为 0");
                    }
                }
            }
            if (recipe.Stat == BuffStat.AutoGuardChance)
            {
                SkillDef? guardDef = cat.Find(recipe.GuardSkillId);
                if (SkillRecipes.Default.FindGuard(entry.Job, recipe.GuardSkillId) == null || guardDef == null || !guardDef.IsActive)
                {
                    problems.Add(label + ":借用的同职业防御姿态不在配方表或目录里");
                }
                else if (!def.PreRequired.Any(pr => pr.SkillId == recipe.GuardSkillId))
                {
                    problems.Add(label + ":借用的同职业防御姿态不在前置要求里");
                }
            }

            var book = new SkillBook(cat);
            book.GrantGiven(new[] { new SkillGrant(recipe.SkillId, 1) });
            book.AssignQuick(0, recipe.SkillId);
            HeroSkills heroSkills = HeroSkills.Create(data, chr, entry.Job, book);
            HeroSkillSlot? slot = heroSkills.SlotOfSkill(recipe.SkillId);
            if (!HeroSkills.IsImplemented(entry.Job, recipe.SkillId) || heroSkills.Job != entry.Job
                || slot?.Buff != recipe || slot.BuffDurationMs <= 0 || slot.BuffAmount <= 0.0
                || slot.BuffExtraAmounts.Count != recipe.ExtraStats.Count)
            {
                problems.Add(label + ":HeroSkills 按职业建槽失败或增益参数为空");
            }
        }

        int guardChecked = 0;
        int guardLevels = 0;
        foreach (JobGuardEntry entry in SkillRecipes.Default.AllJobGuards)
        {
            guardChecked++;
            jobsSeen.Add(entry.Job);
            string label = "跨职业姿态 #" + guardChecked;
            CharacterDef? chr = ChrOf(entry.Job);
            if (chr == null)
            {
                problems.Add(label + ":角色脚本读不到");
                continue;
            }
            SkillCatalog cat = CatOf(entry.Job);
            GuardRecipe recipe = entry.Recipe;
            SkillDef? def = cat.Find(recipe.SkillId);
            if (def == null || !def.IsActive)
            {
                problems.Add(label + ":技能不在目录里或不是主动技能");
                continue;
            }
            int needCols = Math.Max(Math.Max(recipe.RecoilSpeedColumn, recipe.RecoilMsColumn), Math.Max(recipe.PhysicalColumn, recipe.MagicalColumn)) + 1;
            if (def.LevelInfo.Columns < needCols || !def.LevelInfo.IsConsistent || def.LevelInfo.Levels < def.MaximumLevel || def.StaticData.Length <= recipe.MinHoldStaticIndex)
            {
                problems.Add(label + ":[static data] 或 [level info] 不够长");
            }
            int learnable = def.GrowtypeMaxLevels.Length > 0 ? Math.Min(def.GrowtypeMaxLevels.Max(), def.MaximumLevel) : def.MaximumLevel;
            for (int lv = 1; lv <= learnable; lv++)
            {
                guardLevels++;
                int recoilMs = def.GetLevel(lv, recipe.RecoilMsColumn, false);
                int phys = def.GetLevel(lv, recipe.PhysicalColumn, false);
                int mag = def.GetLevel(lv, recipe.MagicalColumn, false);
                if (recoilMs <= 0 || phys < 0 || phys > 100 || mag < 0 || mag > 100)
                {
                    problems.Add(label + " " + lv + " 级:后滑时长或减伤百分比越界");
                }
            }
            string? posePath = chr.CustomAnimationPath(recipe.MotionIndex);
            AniFile? pose = posePath == null ? null : assets.LoadAni(posePath);
            if (pose == null || pose.Frames.Length < 2 || pose.Frames.Any(fr => fr.Boxes.Any(b => b.Type == AniBoxType.Attack)))
            {
                problems.Add(label + ":[etc motion] 动作读不到、不足两帧或带攻击框");
            }
            var book = new SkillBook(cat);
            book.GrantGiven(new[] { new SkillGrant(recipe.SkillId, 1) });
            book.AssignQuick(0, recipe.SkillId);
            HeroSkills heroSkills = HeroSkills.Create(data, chr, entry.Job, book);
            HeroSkillSlot? slot = heroSkills.SlotOfSkill(recipe.SkillId);
            if (!HeroSkills.IsImplemented(entry.Job, recipe.SkillId) || slot?.Guard != recipe || slot.GuardMinHoldMs <= 0)
            {
                problems.Add(label + ":HeroSkills 按职业建槽失败");
            }
        }

        int throwChecked = 0;
        int throwLevels = 0;
        int throwObjects = 0;
        foreach (JobThrowEntry entry in SkillRecipes.Default.AllJobThrows)
        {
            throwChecked++;
            jobsSeen.Add(entry.Job);
            string label = "跨职业投掷 #" + throwChecked;
            CharacterDef? chr = ChrOf(entry.Job);
            if (chr == null)
            {
                problems.Add(label + ":角色脚本读不到");
                continue;
            }
            SkillCatalog cat = CatOf(entry.Job);
            ThrowRecipe recipe = entry.Recipe;
            SkillDef? def = cat.Find(recipe.SkillId);
            if (def == null || !def.IsActive)
            {
                problems.Add(label + ":技能不在目录里或不是主动技能");
                continue;
            }
            int needStatic = new[] { recipe.ChargeStaticIndex, recipe.ShootStaticIndex, recipe.ArmorStaticIndex }.Max() + 1;
            if (def.StaticData.Length < needStatic || (!recipe.UsesZeroChargeFallback && def.GetStatic(recipe.ChargeStaticIndex, false) <= 0)
                || def.GetStatic(recipe.ShootStaticIndex, false) <= 0)
            {
                problems.Add(label + ":[static data] 阶段时长无效");
            }
            int needCols = recipe.Objects.Max(o => o.DamageRateColumn) + 1;
            if (def.LevelInfo.Columns < needCols || !def.LevelInfo.IsConsistent || def.LevelInfo.Levels < def.MaximumLevel)
            {
                problems.Add(label + ":[level info] 列数或行数不够");
            }
            else
            {
                int learnable = def.GrowtypeMaxLevels.Length > 0 ? Math.Min(def.GrowtypeMaxLevels.Max(), def.MaximumLevel) : def.MaximumLevel;
                for (int lv = 1; lv <= learnable; lv++)
                {
                    throwLevels++;
                    if (recipe.Objects.Any(o => def.GetLevel(lv, o.DamageRateColumn, false) <= 0))
                    {
                        problems.Add(label + " " + lv + " 级:对象伤害列不是正数");
                    }
                }
            }
            foreach (string tag in new[] { recipe.ChargePoseTag, recipe.ShootPoseTag })
            {
                string? posePath = chr.MotionPath(tag);
                AniFile? pose = posePath == null ? null : assets.LoadAni(posePath);
                if (pose == null || pose.Frames.Length == 0)
                {
                    problems.Add(label + ":角色脚本的 " + tag + " 读不到或没有帧");
                }
            }
            for (int i = 0; i < recipe.Objects.Count; i++)
            {
                throwObjects++;
                Dnf.Game.PassiveObjectDef? objDef = data.GetPassiveObject(recipe.Objects[i].ObjectId);
                AniFile? objAni = objDef?.BasicMotionPath == null ? null : assets.LoadAni(objDef.BasicMotionPath);
                if (objDef == null || objDef.BasicActionPath != null || objAni == null || objAni.Frames.Length == 0
                    || !objAni.Frames.Any(fr => fr.Boxes.Any(b => b.Type == AniBoxType.Attack)) || !HasPvfEntry(objDef.AttackInfoPath))
                {
                    problems.Add(label + " 对象 " + i + ":老式被动对象动画、攻击框或攻击信息无效");
                }
            }

            var book = new SkillBook(cat);
            book.GrantGiven(new[] { new SkillGrant(recipe.SkillId, 1) });
            book.AssignQuick(0, recipe.SkillId);
            HeroSkills heroSkills = HeroSkills.Create(data, chr, entry.Job, book);
            HeroSkillSlot? slot = heroSkills.SlotOfSkill(recipe.SkillId);
            ThrowRun? run = slot?.NewThrowRun();
            int expectedCharge = recipe.UsesZeroChargeFallback ? ThrowRun.ZeroChargeFallbackMs : def.GetStatic(recipe.ChargeStaticIndex, false);
            int expectedShoot = def.GetStatic(recipe.ShootStaticIndex, false);
            if (!HeroSkills.IsImplemented(entry.Job, recipe.SkillId) || slot?.Throw != recipe || slot.ThrowPoses == null
                || slot.ObjectStrikes.Count != recipe.Objects.Count || run == null
                || run.ChargeMs != expectedCharge || run.ShootMs != expectedShoot || run.ArmorActive != slot.ThrowArmor)
            {
                problems.Add(label + ":HeroSkills 按职业建槽或 ThrowRun 阶段机参数不对");
            }
        }

        // 职业作用域隔离自检:对角色表全部职业逐一核对共享基类项与非首职业碰撞隔离
        IReadOnlyDictionary<int, string>? charList = data.ReadList(GameData.CharacterListPath);
        int isolationJobsChecked = 0;
        int isolationViolations = 0;
        var job0ExclusiveIds = new HashSet<int> { SkillIds.HardAttack, SkillIds.UpperSlash };
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            job0ExclusiveIds.Add(r.SkillId);
        }
        foreach (BuffRecipe r in SkillRecipes.Default.AllClassBuffs)
        {
            job0ExclusiveIds.Add(r.SkillId);
        }
        foreach (GuardRecipe r in SkillRecipes.Default.AllGuards)
        {
            job0ExclusiveIds.Add(r.SkillId);
        }
        foreach (ThrowRecipe r in SkillRecipes.Default.AllThrows)
        {
            job0ExclusiveIds.Add(r.SkillId);
        }
        if (charList != null)
        {
            foreach (int job in charList.Keys.OrderBy(k => k))
            {
                isolationJobsChecked++;
                if (!HeroSkills.IsImplemented(job, SkillIds.BackStep) || !HeroSkills.IsImplemented(job, SkillIds.QuickStand)
                    || SkillRecipes.Default.AllBuffs.Any(b => !HeroSkills.IsImplemented(job, b.SkillId)))
                {
                    isolationViolations++;
                }
                if (job == EquipRules.SwordmanJob)
                {
                    continue;
                }
                var scopedIds = new HashSet<int>();
                foreach (JobBuffEntry b in SkillRecipes.Default.AllJobBuffs)
                {
                    if (b.Job == job) scopedIds.Add(b.Recipe.SkillId);
                }
                foreach (JobGuardEntry g in SkillRecipes.Default.AllJobGuards)
                {
                    if (g.Job == job) scopedIds.Add(g.Recipe.SkillId);
                }
                foreach (JobThrowEntry t in SkillRecipes.Default.AllJobThrows)
                {
                    if (t.Job == job) scopedIds.Add(t.Recipe.SkillId);
                }
                foreach (int exId in job0ExclusiveIds)
                {
                    if (scopedIds.Contains(exId))
                    {
                        continue;
                    }
                    if (HeroSkills.IsImplemented(job, exId))
                    {
                        isolationViolations++;
                    }
                }
            }
        }
        if (isolationViolations > 0)
        {
            problems.Add("跨职业编号碰撞隔离存在 " + isolationViolations + " 处越界泄漏");
        }

        foreach (string line in problems)
        {
            Console.WriteLine("    " + line);
        }
        int totalEntries = SkillRecipes.Default.JobBuffCount + SkillRecipes.Default.JobGuardCount + SkillRecipes.Default.JobThrowCount;
        report(totalEntries > 0 && buffChecked == SkillRecipes.Default.JobBuffCount && guardChecked == SkillRecipes.Default.JobGuardCount
            && throwChecked == SkillRecipes.Default.JobThrowCount && isolationJobsChecked > 1 && isolationViolations == 0 && problems.Count == 0,
            "跨职业状态族配方(阶段 2a):覆盖 " + jobsSeen.Count + " 个职业、共 " + totalEntries + " 条(增益 " + buffChecked + " 条/" + buffLevels + " 级/额外属性 " + buffExtras
            + " 项、防御姿态 " + guardChecked + " 条/" + guardLevels + " 级、投掷 " + throwChecked + " 条/" + throwLevels + " 级/对象 " + throwObjects
            + " 个)在真实 PVF 与按职业建槽中全部读得通;全 " + isolationJobsChecked + " 个职业的基类共享与编号碰撞隔离违例 " + isolationViolations + " 处(问题 " + problems.Count + " 条)");
    }
}
