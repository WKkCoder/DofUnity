using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的设计:把副职业状态、目录、背包、金币和随机数拢成一个会话,给宿主一个入口(就职、放弃、加经验、分解、整修与升档分解机、存档)。
// 规则本身都在 Dnf.Game 的 ExpertJob 里(依据是服务端静态读码);这里不加规则,只做接线,并保证"要么全做、要么什么都不动"。
// 边界:这一版只执行"分解"(分解师)。炼金制作、附魔执行、人偶只存状态(种类、经验、配方位图)与接口,执行在制作线与强化线;
// 不改 HeroProgress、任务簿(QuestLog 没有重置接口)、技能簿(就职与放弃的技能授予与删除交给宿主,结果里列出来),也不碰网络包。
namespace Dnf.Play
{
    /// <summary>一次分解的结果。</summary>
    public sealed class DisjointReport
    {
        private static readonly IReadOnlyList<KeyValuePair<int, int>> NoProducts = new KeyValuePair<int, int>[0];

        public DisjointReport(ExpertJobError error, IReadOnlyList<KeyValuePair<int, int>> products, int enduranceLost, int expGain, ExpertExpResult exp, bool portableUsed)
        {
            Error = error;
            Products = products ?? NoProducts;
            EnduranceLost = enduranceLost;
            ExpGain = expGain;
            Exp = exp;
            PortableUsed = portableUsed;
        }

        public ExpertJobError Error { get; }

        public bool Ok => Error == ExpertJobError.Ok;

        /// <summary>放进背包的产物:(物品编号, 个数),按产物槽次序(主产物、附加产物、自用额外产物);同一种物品可能出现两行。</summary>
        public IReadOnlyList<KeyValuePair<int, int>> Products { get; }

        /// <summary>这次扣掉的分解机耐久(便携分解器路线为 0)。</summary>
        public int EnduranceLost { get; }

        /// <summary>这次要加的副职业经验(含穿戴的额外经验;不大于 0 就没有加)。</summary>
        public int ExpGain { get; }

        /// <summary>加经验的结果;没有加为 null。升级时里面有要重新授予的技能。</summary>
        public ExpertExpResult Exp { get; }

        /// <summary>便携分解器是否被消耗了一个。</summary>
        public bool PortableUsed { get; }

        internal static DisjointReport Failed(ExpertJobError error)
        {
            return new DisjointReport(error, null, 0, 0, null, false);
        }
    }

    /// <summary>
    /// 副职业会话:一个角色的副职业状态(<see cref="State"/>)加上目录(<see cref="Catalog"/>)。金币、背包、角色等级、穿戴来自 <see cref="HeroProgress"/>(只读它的公开接口,不改它)。
    /// <para>
    /// 接进宿主的写法:存档时 <c>var save = progress.Snapshot(); jobs.Capture(save);</c>;读档时 <c>progress.Restore(save, lookup); jobs.Restore(save);</c>。
    /// </para>
    /// </summary>
    public sealed class ExpertJobSession
    {
        private readonly HeroProgress _hero;
        private readonly Func<int, ItemInfo> _lookup;
        private readonly Func<ItemInfo, ExpertJobItemTags> _tagsOf;
        private readonly IDisjointRandom _rng;

        /// <param name="hero">角色进度(背包、金币、等级、穿戴)。</param>
        /// <param name="catalog">副职业目录。</param>
        /// <param name="lookup">按物品编号取物品(产物与背包物品用;没有返回 null)。</param>
        /// <param name="tagsOf">按物品取副职业标签(为 null 时一律当"没有标签")。</param>
        /// <param name="rng">随机数来源(为 null 用系统随机)。</param>
        /// <param name="policy">分解取舍(为 null 用 <see cref="DisjointPolicy.Default"/>)。</param>
        public ExpertJobSession(HeroProgress hero, ExpertJobCatalog catalog, Func<int, ItemInfo> lookup, Func<ItemInfo, ExpertJobItemTags> tagsOf = null, IDisjointRandom rng = null, DisjointPolicy policy = null)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _tagsOf = tagsOf ?? (item => ExpertJobItemTags.None);
            _rng = rng ?? new SystemDisjointRandom();
            Policy = policy ?? DisjointPolicy.Default;
        }

        public ExpertJobCatalog Catalog { get; }

        public ExpertJobState State { get; private set; } = new ExpertJobState();

        public DisjointPolicy Policy { get; }

        /// <summary>物品被锁(服务端 <c>CheckItemLock</c>)的判断;返回 true 就拒绝分解(0xd5)。没设就不锁。</summary>
        public Func<ItemInfo, bool> ItemLock { get; set; }

        public ExpertJobKind Kind => State.Kind;

        private ExpertJobScript Script => State.HasJob ? Catalog.Get(State.Kind) : null;

        /// <summary>当前副职业等级(带角色等级闸门,<see cref="ExpertJobLevels.CurrentLevel"/>);没有副职业为 0。</summary>
        public int Level => ExpertJobLevels.CurrentLevel(State.Kind, Script, Catalog.Etc, State.Exp, _hero.Level);

        // ---- 就职与放弃 ----

        /// <summary>
        /// 就职(任务奖励 <c>[expert job]</c>):角色等级不到 <paramref name="requiredCharLevel"/> → 0x16;已有副职业或没有这个种类的脚本 → 8。
        /// 成功后耐久、机器档、配方按脚本初始化,经验清零;结果里列出要授予的技能(宿主授予普通与觉醒两种技能树)。
        /// </summary>
        public ExpertJobChange TakeJob(ExpertJobKind kind, int requiredCharLevel = 0)
        {
            if (_hero.Level < requiredCharLevel)
            {
                return ExpertJobChange.Failed(ExpertJobError.Refused);
            }
            return State.Acquire(kind, Catalog.Get(kind));
        }

        /// <summary>
        /// 放弃副职业(<c>OnGiveup</c>):检查次序同服务端;通过后扣金币、状态清空(放弃次数加一但不超过费用表长减一)。
        /// 结果里列出要删除的技能与要重置的任务(<c>[clear quests]</c>),由宿主处理:任务簿没有重置接口,这一线不改它。
        /// </summary>
        public ExpertJobChange GiveUp()
        {
            ExpertJobScript script = Script;
            ExpertJobChange plan = State.PlanGiveUp(script, _hero.Gold, _hero.Quests.IsCleared);
            if (!plan.Ok)
            {
                return plan;
            }
            if (plan.GoldCost > 0 && !_hero.TrySpendGold(plan.GoldCost))
            {
                return ExpertJobChange.Failed(ExpertJobError.NotEnoughGold);
            }
            State.ApplyGiveUp(script);
            return plan;
        }

        // ---- 经验 ----

        /// <summary>
        /// 加副职业经验 <paramref name="amount"/>(服务端 <c>IncreaseExpertJobExp</c> 的参数是新的经验总值,这里按"当前 + 增量"换算);
        /// 增量不大于 0 时什么都不变(特别是不会碰到"落在边界上就算升级"的那条规则);没有副职业时 <see cref="SetExp"/> 里的等级规则同样什么都不变。
        /// 等级闸门与升级规则见 <see cref="ExpertJobLevels.Increase"/>。
        /// </summary>
        public ExpertExpResult GainExp(int amount)
        {
            if (amount <= 0)
            {
                return ExpertExpResult.Unchanged(State.Exp, Level);
            }
            long total = (long)State.Exp + amount;
            return SetExp(total > int.MaxValue ? int.MaxValue : (int)total);
        }

        /// <summary>
        /// 把经验总值设成 <paramref name="total"/>(GM 命令那种路径);同样走等级闸门。
        /// 负数不是合法的新经验(就职后是 0、放弃后的 −1 只有放弃会写),按没有改动处理;存档的范围也不接受比 −1 更小的值。
        /// </summary>
        public ExpertExpResult SetExp(int total)
        {
            if (total < 0)
            {
                return ExpertExpResult.Unchanged(State.Exp, Level);
            }
            ExpertExpResult r = ExpertJobLevels.Increase(State.Kind, Script, Catalog.Etc, State.Exp, total, _hero.Level);
            if (r.Changed)
            {
                State.SetExp(r.NewExp);
            }
            return r;
        }

        /// <summary>
        /// 角色等级变了以后重新过一遍副职业的等级闸门:服务端在角色升级(<c>CUser::gain_exp_sp</c> 的升级分支,0x0866a905)与改等级
        /// (<c>CUser::SetCharacLevel</c> 0x0867aea6)之后,都用"当前经验"再调一次 <c>IncreaseExpertJobExp</c>。经验停在边界上、角色等级恰好是某个上限值时算一次升级,
        /// 结果里列出要按新的副职业等级重新授予的技能;其余情况什么都不变。副职业等级本身是由经验与角色等级随时算出来的(<see cref="Level"/>),不靠这个调用更新。
        /// </summary>
        public ExpertExpResult RecheckLevelGate()
        {
            return SetExp(State.Exp);
        }

        // ---- 分解 ----

        /// <summary>
        /// 用自己的分解机分解背包里一件装备(<c>_disjoint</c> 的机器路线,请求者就是机主)。次序:机主必须是分解师(0x13)、耐久大于 0(0xbd)、
        /// 格子里有东西(0x11)、没被锁(0xd5)、物品能分解(<see cref="DisjointRules.CheckItem"/>)、结果表有这一行(0x11)、产物在数据里(1000)、背包放得下(4)。
        /// 全部通过才动:原物品拿走、产物放进背包、扣耐久、加经验;任何一步不过,背包、金币、耐久、经验都不动。
        /// </summary>
        public DisjointReport Disjoint(InventoryTab tab, int index)
        {
            ExpertJobScript script = Catalog.Get(ExpertJobKind.Disjointer);
            if (State.Kind != ExpertJobKind.Disjointer || script == null)
            {
                return DisjointReport.Failed(ExpertJobError.NotPermitted);
            }
            ExpertJobError start = DisjointMachine.CheckStart(State.MachineEndurance);
            if (start != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(start);
            }
            ItemStack stack = StackAt(tab, index);
            if (stack == null)
            {
                return DisjointReport.Failed(ExpertJobError.InvalidItem);
            }
            if (ItemLock != null && ItemLock(stack.Info))
            {
                return DisjointReport.Failed(ExpertJobError.ItemLocked);
            }
            DisjointItem item = DisjointItem.From(stack.Info, _tagsOf(stack.Info));
            ExpertJobError check = DisjointRules.CheckItem(item, true, Policy);
            if (check != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(check);
            }

            int level = Level;
            var ctx = new DisjointContext
            {
                MachineGrade = State.MachineGrade,
                IsMine = true,
                OwnerActive = true,
                OwnerBigWinBonus = ExpertJobGear.SelfBigWinRate(WornTags, (int)State.Kind, level),
                OwnerResultVariation = ExpertJobGear.SelfResultVariation(WornTags, (int)State.Kind, level),
                PriceRates = Catalog.PriceRates,
            };
            DisjointOutcome outcome = DisjointRules.Compute(script, Catalog.Etc, item, ctx, _rng);
            if (!outcome.Ok)
            {
                return DisjointReport.Failed(outcome.Error);
            }
            List<KeyValuePair<ItemInfo, int>> give = ResolveProducts(outcome, out ExpertJobError resolve);
            if (resolve != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(resolve);
            }
            if (!Fits(tab, index, 0, give))
            {
                return DisjointReport.Failed(ExpertJobError.NoSpace);
            }

            // 通过:原物品拿走,产物放进去(次序同产物槽),然后结算耐久与经验。
            _hero.Bag.RemoveAt(tab, index, 1);
            foreach (KeyValuePair<ItemInfo, int> g in give)
            {
                _hero.Bag.Add(g.Key, g.Value);
            }
            ExpertJobGear.AdditionalExp(WornTags, (int)State.Kind, level, out float chance, out int amount);
            DisjointCompletion done = DisjointMachine.Complete(script, State.MachineEndurance, chance, amount, _rng);
            State.SetMachine(State.MachineGrade, done.NewEndurance);
            ExpertExpResult exp = null;
            if (done.TotalGain > 0)
            {
                exp = GainExp(done.TotalGain);
            }
            return new DisjointReport(ExpertJobError.Ok, outcome.Products(), done.EnduranceLost, done.TotalGain, exp, false);
        }

        /// <summary>
        /// 用便携分解器分解(没有机主的旧路线 <c>DisJoint</c>):便携分解器是背包里 <paramref name="portableTab"/> 页第 <paramref name="portableIndex"/> 格的可叠加物品
        /// (脚本 <c>[portable disjoint]</c> 的序号决定能分解到几级)。次序同服务端:物品没被锁(0xd5)、物品类型(<see cref="DisjointRules.CheckItemType"/>,格子空也是 0x11)、
        /// 分解器在格子里(0x15)且有序号并登记在分解师脚本里(1)、物品等级不超过便携上限(0xd)、物品规则(<see cref="DisjointRules.CheckItemRules"/>,没有机器所以未鉴定的拒绝)、
        /// 产物在数据里、背包放得下(4)。通过才动:分解器扣一个、原物品拿走、产物放进背包。不扣耐久、不加经验、没有机器档与穿戴加成。
        /// </summary>
        public DisjointReport DisjointPortable(InventoryTab portableTab, int portableIndex, InventoryTab itemTab, int itemIndex)
        {
            ItemStack stack = StackAt(itemTab, itemIndex);
            if (stack != null && ItemLock != null && ItemLock(stack.Info))
            {
                return DisjointReport.Failed(ExpertJobError.ItemLocked);
            }
            DisjointItem item = stack == null ? null : DisjointItem.From(stack.Info, _tagsOf(stack.Info));
            ExpertJobError typeCheck = DisjointRules.CheckItemType(item, Policy);
            if (typeCheck != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(typeCheck);
            }
            ItemStack portable = StackAt(portableTab, portableIndex);
            if (portable == null || portable.Info.IsEquipment)
            {
                return DisjointReport.Failed(ExpertJobError.NotEnoughGold);
            }
            ExpertJobScript script = Catalog.Get(ExpertJobKind.Disjointer);
            ExpertJobItemTags portableTags = _tagsOf(portable.Info) ?? ExpertJobItemTags.None;
            ExpertJobError portableCheck = DisjointRules.CheckPortable(script, portableTags.PortableDisjointIndex, item);
            if (portableCheck != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(portableCheck);
            }
            ExpertJobError check = DisjointRules.CheckItemRules(item, false);
            if (check != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(check);
            }
            DisjointOutcome outcome = Catalog.Legacy.Compute(item, Catalog.PriceRates, _rng);
            if (!outcome.Ok)
            {
                return DisjointReport.Failed(outcome.Error);
            }
            List<KeyValuePair<ItemInfo, int>> give = ResolveProducts(outcome, out ExpertJobError resolve);
            if (resolve != ExpertJobError.Ok)
            {
                return DisjointReport.Failed(resolve);
            }
            if (!Fits(itemTab, itemIndex, portable.Info.Code, give))
            {
                return DisjointReport.Failed(ExpertJobError.NoSpace);
            }
            _hero.Bag.Remove(portable.Info.Code, 1);
            _hero.Bag.RemoveAt(itemTab, itemIndex, 1);
            foreach (KeyValuePair<ItemInfo, int> g in give)
            {
                _hero.Bag.Add(g.Key, g.Value);
            }
            return new DisjointReport(ExpertJobError.Ok, outcome.Products(), 0, 0, null, true);
        }

        /// <summary>
        /// 预览自己的分解机分解这件物品可能出什么(<see cref="DisjointRules.PredictItems"/>;物品编号 0 的占位项去掉);
        /// 不是分解师或没有脚本返回空表。
        /// </summary>
        public List<int> Predict(ItemInfo info)
        {
            var list = new List<int>();
            ExpertJobScript script = Catalog.Get(ExpertJobKind.Disjointer);
            if (info == null || State.Kind != ExpertJobKind.Disjointer || script == null)
            {
                return list;
            }
            DisjointItem item = DisjointItem.From(info, _tagsOf(info));
            var ctx = new DisjointContext { MachineGrade = State.MachineGrade, IsMine = true, OwnerActive = true };
            foreach (int code in DisjointRules.PredictItems(script, item, ctx))
            {
                if (code != 0)
                {
                    list.Add(code);
                }
            }
            return list;
        }

        /// <summary>便携分解器路线的预览:主产物加这一稀有度的附加产物(<see cref="LegacyDisjointScript.Predict"/>)。</summary>
        public List<int> PredictPortable(ItemInfo info)
        {
            return info == null ? new List<int>() : Catalog.Legacy.Predict(info.Rarity);
        }

        // ---- 分解机 ----

        /// <summary>整修的全价(耐久从当前修满;只给界面提示用);不是分解师为 0。</summary>
        public int RepairQuote()
        {
            ExpertJobScript script = Catalog.Get(ExpertJobKind.Disjointer);
            return State.Kind == ExpertJobKind.Disjointer && script != null ? DisjointMachine.RepairCost(script, State.MachineGrade, State.MachineEndurance) : 0;
        }

        /// <summary>整修分解机(<see cref="DisjointMachine.PlanRepair"/>);成功扣金币、设耐久。</summary>
        public MachineRepairPlan RepairMachine()
        {
            ExpertJobScript script = Catalog.Get(ExpertJobKind.Disjointer);
            if (State.Kind != ExpertJobKind.Disjointer || script == null)
            {
                return new MachineRepairPlan(ExpertJobError.NotPermitted, 0, State.MachineEndurance);
            }
            MachineRepairPlan plan = DisjointMachine.PlanRepair(script, State.MachineGrade, State.MachineEndurance, _hero.Gold);
            if (!plan.Ok)
            {
                return plan;
            }
            if (plan.Cost > 0 && !_hero.TrySpendGold(plan.Cost))
            {
                return new MachineRepairPlan(ExpertJobError.NotEnoughGold, plan.Cost, State.MachineEndurance);
            }
            State.SetMachine(State.MachineGrade, plan.NewEndurance);
            return plan;
        }

        /// <summary>升档分解机(<see cref="DisjointMachine.PlanUpgrade"/>);成功扣金币、设机器档、耐久设成新档的耐久上限。</summary>
        public MachineUpgradePlan UpgradeMachine()
        {
            ExpertJobScript script = Catalog.Get(ExpertJobKind.Disjointer);
            if (State.Kind != ExpertJobKind.Disjointer || script == null)
            {
                return new MachineUpgradePlan(ExpertJobError.NotPermitted, 0, State.MachineGrade, State.MachineEndurance);
            }
            MachineUpgradePlan plan = DisjointMachine.PlanUpgrade(script, Catalog.Etc, State.MachineGrade, Level, _hero.Level, _hero.Gold);
            if (!plan.Ok)
            {
                return plan;
            }
            if (!_hero.TrySpendGold(plan.Cost))
            {
                return new MachineUpgradePlan(ExpertJobError.Refused, plan.Cost, State.MachineGrade, State.MachineEndurance);
            }
            State.SetMachine(plan.NewGrade, plan.ResetsEndurance ? plan.NewEndurance : State.MachineEndurance);
            return plan;
        }

        // ---- 存档 ----

        /// <summary>
        /// 把副职业状态写进存档快照。只在状态不是"全新"(没有接触过副职业)时写,所以没有副职业的旧存档文本不变。
        /// </summary>
        public void Capture(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            save.ExpertJob = null;
            save.ExpertRecipes.Clear();
            List<int> recipes = State.LearnedRecipes();
            bool fresh = !State.HasJob && State.Exp == 0 && State.GiveupCount == 0 && State.MachineEndurance == 0 && State.MachineGrade == 0 && recipes.Count == 0;
            if (fresh)
            {
                return;
            }
            save.ExpertJob = new SavedExpertJob((int)State.Kind, State.Exp, State.GiveupCount, State.MachineEndurance, State.MachineGrade);
            save.ExpertRecipes.AddRange(recipes);
        }

        /// <summary>
        /// 从存档还原副职业状态(整个替换)。没有这一行当作全新;种类不认得的当没有副职业、不合法的数值被改成合法值;
        /// 返回 true 表示存档里的值原样还原、没有被改动。
        /// </summary>
        public bool Restore(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            var state = new ExpertJobState();
            bool clean = true;
            byte[] bitmap = new byte[ExpertJobState.RecipeBytes];
            foreach (int index in save.ExpertRecipes)
            {
                if (index >= 0 && index < ExpertJobState.RecipeCount)
                {
                    bitmap[index / 8] = (byte)(bitmap[index / 8] | (1 << (index % 8)));
                }
                else
                {
                    clean = false;
                }
            }
            if (save.ExpertJob.HasValue)
            {
                SavedExpertJob j = save.ExpertJob.Value;
                clean &= state.Load(j.Kind, j.Exp, j.GiveupCount, j.Endurance, j.Grade, bitmap);
            }
            else
            {
                clean &= state.Load(0, 0, 0, 0, 0, bitmap);
            }
            State = state;
            return clean;
        }

        // ---- 内部 ----

        private ItemStack StackAt(InventoryTab tab, int index)
        {
            IReadOnlyList<ItemStack> list = _hero.Bag.Tab(tab);
            return (uint)index < (uint)list.Count ? list[index] : null;
        }

        private ExpertJobItemTags WornTags(int part)
        {
            ItemInfo worn = _hero.Worn.Get(part);
            return worn == null ? null : _tagsOf(worn) ?? ExpertJobItemTags.None;
        }

        private List<KeyValuePair<ItemInfo, int>> ResolveProducts(DisjointOutcome outcome, out ExpertJobError error)
        {
            error = ExpertJobError.Ok;
            var give = new List<KeyValuePair<ItemInfo, int>>();
            foreach (KeyValuePair<int, int> p in outcome.Products())
            {
                ItemInfo info = _lookup(p.Key);
                if (info == null)
                {
                    error = ExpertJobError.UnknownProduct;
                    return null;
                }
                give.Add(new KeyValuePair<ItemInfo, int>(info, p.Value));
            }
            return give;
        }

        /// <summary>在背包的副本上试:拿走原物品(与便携分解器 <paramref name="portableCode"/>,0 表示没有),再按次序放产物,放得下才返回 true。</summary>
        private bool Fits(InventoryTab tab, int index, int portableCode, List<KeyValuePair<ItemInfo, int>> give)
        {
            Inventory scratch = _hero.Bag.Clone();
            scratch.RemoveAt(tab, index, 1);
            if (portableCode != 0)
            {
                scratch.Remove(portableCode, 1);
            }
            foreach (KeyValuePair<ItemInfo, int> g in give)
            {
                if (scratch.Add(g.Key, g.Value) != g.Value)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
