using System;
using System.Collections.Generic;

// 服务端静态读码:制作入口 CUser::compound_item 0x0867218c(取配方物品 → 时装材料走另一条路 → 技能/会员/学会检查 → 输入表加上 [bead item] 额外表、
// 输入与产物各乘数量 → 产物里多件装备拒绝 → 材料够不够 → 装备条件 → 在背包副本上先试扣料、试扣配方物品、试放产物 → 都过了才真做),
// 真做的部分是 CUser::_ProcCompoundCore 0x08671102(扣料 → 掷一次成功率 → 失败时把产物换成失败产物表 → 放产物)。
namespace Dnf.Game
{
    /// <summary>制作的配方来源:背包里的配方物品(每份消耗一件),或配方簿里已学的配方(配方物品不动)。</summary>
    public enum CraftSource
    {
        /// <summary>按背包里的配方物品制作:每做一份消耗一件配方物品(服务端 byCode 为假,delete_item 扣 <c>数量</c> 件)。</summary>
        RecipeItem = 0,

        /// <summary>按配方簿里已学的配方制作:配方物品不消耗(服务端 byCode 为真;已学检查是副职业配方的 IsLearnRecipe 0x0849b590)。</summary>
        Learned = 1,
    }

    /// <summary>
    /// 制作规则:学会配方、预览、执行制作。只动传进来的背包、钱包与配方簿;别的系统的状态都通过 <see cref="ICraftGate"/> 进来
    /// (制作线不读副职业状态)。每个动作要么整个做完、要么什么也不动——材料不够、放不下、没学会都不会扣任何东西。
    /// <para>
    /// 规则(服务端读码为准,与读码不同的地方在注释里单列):
    /// 输入表接上 <c>[bead item]</c> 额外表,输入与产物各乘数量;成功率为 0 不掷骰、必成,否则一次制作只掷一次(不是每份一次),
    /// 掷出的数 ≥ 成功率就失败;失败时材料照扣、产物换成失败产物表(不乘数量,表为空就什么也得不到);产物与失败产物先在背包副本上试放,放不下就拒绝。
    /// </para>
    /// </summary>
    public sealed class CraftBench
    {
        /// <summary>一次最多做几份(服务端的数量是 16 位无符号数)。</summary>
        public const int MaxCount = ushort.MaxValue;

        /// <summary>成功率的掷骰范围:掷 [0, 100),掷出的数小于成功率才成功。</summary>
        public const int RollRange = 100;

        private readonly Func<int, ItemInfo> _lookup;
        private readonly ICraftRoll _roll;
        private readonly ICraftGate _gate;

        /// <param name="lookup">按物品编号取物品(产物、材料的种类与叠加上限都从这里来)。</param>
        /// <param name="roll">成功率的掷骰;不给就用以当前时间为种子的自带掷骰(只有带成功率的配方才会用到)。</param>
        /// <param name="gate">闸门;不给就全放行。</param>
        public CraftBench(Func<int, ItemInfo> lookup, ICraftRoll roll = null, ICraftGate gate = null)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _roll = roll ?? new SeededCraftRoll(Environment.TickCount);
            _gate = gate ?? OpenCraftGate.Instance;
        }

        /// <summary>
        /// 用背包里的一件配方物品学会这个配方:配方物品消耗一件、编号记进配方簿。
        /// 配方为空或残缺、背包里没有配方物品、已经学会、闸门不放行时返回对应结果,什么都不动。
        /// </summary>
        public LearnResult Learn(RecipeDef recipe, RecipeBook book, Inventory bag)
        {
            if (book == null)
            {
                throw new ArgumentNullException(nameof(book));
            }
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (!Usable(recipe))
            {
                return LearnResult.UnknownRecipe;
            }
            if (bag.CountOf(recipe.Code) < 1)
            {
                return LearnResult.NoRecipeItem;
            }
            if (book.IsLearned(recipe.Code))
            {
                return LearnResult.AlreadyLearned;
            }
            if (!_gate.CanLearn(recipe))
            {
                return LearnResult.NotAllowed;
            }
            bag.Remove(recipe.Code, 1);
            book.Learn(recipe.Code);
            return LearnResult.Ok;
        }

        /// <summary>按背包里的配方物品制作 <paramref name="count"/> 份(<see cref="CraftSource.RecipeItem"/>)。</summary>
        public CraftReport CraftFromItem(RecipeDef recipe, Inventory bag, ICraftPurse purse, int count = 1)
        {
            return Craft(recipe, CraftSource.RecipeItem, null, bag, purse, count);
        }

        /// <summary>按配方簿里已学的配方制作 <paramref name="count"/> 份(<see cref="CraftSource.Learned"/>)。</summary>
        public CraftReport CraftLearned(RecipeDef recipe, RecipeBook book, Inventory bag, ICraftPurse purse, int count = 1)
        {
            return Craft(recipe, CraftSource.Learned, book, bag, purse, count);
        }

        /// <summary>预览:现在做 <paramref name="count"/> 份会是什么结果、材料够不够;不动背包、不掷骰。</summary>
        public CraftPreview Preview(RecipeDef recipe, CraftSource source, RecipeBook book, Inventory bag, ICraftPurse purse, int count = 1)
        {
            Plan plan = Prepare(recipe, source, book, bag, purse, count);
            return new CraftPreview(plan.Result, plan.Lines, plan.Outputs, plan.MaxByMaterials);
        }

        /// <summary>制作 <paramref name="count"/> 份;见类说明。<paramref name="book"/> 只有按已学配方制作时才用。</summary>
        public CraftReport Craft(RecipeDef recipe, CraftSource source, RecipeBook book, Inventory bag, ICraftPurse purse, int count = 1)
        {
            Plan plan = Prepare(recipe, source, book, bag, purse, count);
            if (plan.Result != CraftResult.Ok)
            {
                return new CraftReport(plan.Result);
            }

            bool rolled = recipe.HasSuccessRoll;
            int roll = rolled ? _roll.Below(RollRange) : -1;
            bool success = !rolled || roll < recipe.SuccessRate;
            List<RecipeItem> outputs = plan.Outputs;
            if (!success)
            {
                outputs = plan.FailOutputs;
                if (!Fits(bag, recipe, plan, outputs))
                {
                    return new CraftReport(CraftResult.BagFull);
                }
            }

            // 提交:先扣金币(钱包自己也可能拒绝),再扣材料、扣配方物品、放产物;前面的试放已经保证后面都做得到。
            if (plan.Gold > 0 && (purse == null || !purse.TrySpend(plan.Gold)))
            {
                return new CraftReport(CraftResult.MissingMaterials);
            }
            foreach (RecipeItem input in plan.Items)
            {
                bag.Remove(input.Code, input.Count);
            }
            if (plan.ConsumesRecipeItem)
            {
                bag.Remove(recipe.Code, count);
            }
            var granted = new List<RecipeItem>();
            foreach (RecipeItem output in outputs)
            {
                if (output.Count > 0)
                {
                    bag.Add(_lookup(output.Code), output.Count);
                    granted.Add(output);
                }
            }
            var report = new CraftReport(
                CraftResult.Ok, count, success, roll, plan.Gold, plan.ConsumesRecipeItem ? count : 0, plan.Items, granted);
            _gate.OnCrafted(recipe, report);
            return report;
        }

        private sealed class Plan
        {
            public CraftResult Result;
            public int Requested;
            public List<MaterialLine> Lines = new List<MaterialLine>();
            public List<RecipeItem> Items = new List<RecipeItem>();
            public long Gold;
            public List<RecipeItem> Outputs = new List<RecipeItem>();
            public List<RecipeItem> FailOutputs = new List<RecipeItem>();
            public bool ConsumesRecipeItem;
            public int MaxByMaterials;
        }

        private static bool Usable(RecipeDef recipe)
        {
            if (recipe == null || recipe.Malformed)
            {
                return false;
            }
            // 负数量会把"扣料"变成"送料",数据里没有,拒绝。
            foreach (RecipeItem item in recipe.AllInputs)
            {
                if (item.Count < 0)
                {
                    return false;
                }
            }
            foreach (RecipeItem item in recipe.Outputs)
            {
                if (item.Count < 0)
                {
                    return false;
                }
            }
            foreach (RecipeItem item in recipe.FailOutputs)
            {
                if (item.Count < 0)
                {
                    return false;
                }
            }
            return true;
        }

        private Plan Prepare(RecipeDef recipe, CraftSource source, RecipeBook book, Inventory bag, ICraftPurse purse, int count)
        {
            var plan = new Plan();
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (!Usable(recipe))
            {
                plan.Result = CraftResult.UnknownRecipe;
                return plan;
            }
            if (count < 1 || count > MaxCount)
            {
                plan.Result = CraftResult.InvalidCount;
                return plan;
            }

            // 输入表合并同编号后乘数量(服务端先拷输入表、接上额外表,再逐项乘数量)。
            plan.Requested = count;
            List<RecipeItem> unit = Merge(recipe.AllInputs, 1);
            List<RecipeItem> scaledInputs = Merge(recipe.AllInputs, count);
            List<RecipeItem> scaledOutputs = Merge(recipe.Outputs, count);
            List<RecipeItem> failOutputs = Merge(recipe.FailOutputs, 1);
            if (unit == null || scaledInputs == null || scaledOutputs == null || failOutputs == null)
            {
                plan.Result = CraftResult.InvalidCount;
                return plan;
            }
            plan.FailOutputs = failOutputs;
            long have = purse == null ? 0 : purse.Gold;
            foreach (RecipeItem input in scaledInputs)
            {
                if (input.Count <= 0)
                {
                    continue;   // 零数量的项不算要求(数据里有两个配方写了零数量;服务端对它的处理没有读到,这里当作不需要)
                }
                if (input.IsGold)
                {
                    plan.Gold = input.Count;
                }
                else
                {
                    plan.Items.Add(input);
                }
            }
            if (plan.Gold > 0)
            {
                plan.Lines.Add(new MaterialLine(RecipeItem.GoldCode, plan.Gold, have));
            }
            foreach (RecipeItem input in plan.Items)
            {
                plan.Lines.Add(new MaterialLine(input.Code, input.Count, bag.CountOf(input.Code)));
            }
            plan.Outputs = scaledOutputs;
            plan.ConsumesRecipeItem = source == CraftSource.RecipeItem;
            plan.MaxByMaterials = MaxByMaterials(unit, plan.ConsumesRecipeItem ? recipe.Code : -1, bag, have);

            // 配方的来源:背包里的配方物品,或配方簿里学会的。
            if (plan.ConsumesRecipeItem)
            {
                if (bag.CountOf(recipe.Code) < count)
                {
                    plan.Result = CraftResult.NoRecipeItem;
                    return plan;
                }
            }
            else if (book == null || !book.IsLearned(recipe.Code))
            {
                plan.Result = CraftResult.NotLearned;
                return plan;
            }
            foreach (RecipeItem input in plan.Items)
            {
                ItemInfo info = _lookup(input.Code);
                if (info != null && info.IsAvatar)
                {
                    plan.Result = CraftResult.AvatarInput;
                    return plan;
                }
            }
            if (!_gate.CanCraft(recipe))
            {
                plan.Result = CraftResult.NotAllowed;
                return plan;
            }

            // 产物检查(服务端 _IsCompoundResult2MoreEquip 逐项看、不合并同编号):有产物、编号都查得到(金币不能当产物),
            // 装备乘数量后不多于一件;失败产物的编号也要查得到。
            if (recipe.Outputs.Count == 0)
            {
                plan.Result = CraftResult.NoProduct;
                return plan;
            }
            foreach (RecipeItem output in recipe.Outputs)
            {
                ItemInfo info = output.IsGold ? null : _lookup(output.Code);
                if (info == null)
                {
                    plan.Result = CraftResult.NoProduct;
                    return plan;
                }
                if (info.IsEquipment && (long)output.Count * count > 1)
                {
                    plan.Result = CraftResult.TooManyEquipment;
                    return plan;
                }
            }
            foreach (RecipeItem fail in recipe.FailOutputs)
            {
                if (fail.IsGold || _lookup(fail.Code) == null)
                {
                    plan.Result = CraftResult.NoProduct;
                    return plan;
                }
            }

            // 材料够不够(金币看钱包,其余看背包里同编号的总数)。
            foreach (MaterialLine line in plan.Lines)
            {
                if (!line.Enough)
                {
                    plan.Result = CraftResult.MissingMaterials;
                    return plan;
                }
            }
            foreach (RecipeItem input in plan.Items)
            {
                if (input.Code == RecipeItem.PvpPointCode)
                {
                    plan.Result = CraftResult.MissingMaterials;
                    return plan;
                }
            }

            if (!Fits(bag, recipe, plan, plan.Outputs))
            {
                plan.Result = CraftResult.BagFull;
                return plan;
            }
            plan.Result = CraftResult.Ok;
            return plan;
        }

        /// <summary>在背包副本上先扣材料、扣配方物品、再放 <paramref name="outputs"/>,能不能全部放下;真实的背包不动。</summary>
        private bool Fits(Inventory bag, RecipeDef recipe, Plan plan, IReadOnlyList<RecipeItem> outputs)
        {
            Inventory scratch = bag.Clone();
            foreach (RecipeItem input in plan.Items)
            {
                if (!scratch.Remove(input.Code, input.Count))
                {
                    return false;
                }
            }
            if (plan.ConsumesRecipeItem && !scratch.Remove(recipe.Code, plan.Requested))
            {
                return false;
            }
            foreach (RecipeItem output in outputs)
            {
                if (output.Count <= 0)
                {
                    continue;
                }
                ItemInfo info = _lookup(output.Code);
                if (info == null || scratch.Add(info, output.Count) != output.Count)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>同编号的项合并(保留首次出现的次序)并乘上 <paramref name="factor"/>;乘出来超过 int 范围返回 null。</summary>
        private static List<RecipeItem> Merge(IReadOnlyList<RecipeItem> items, int factor)
        {
            var result = new List<RecipeItem>();
            var index = new Dictionary<int, int>();
            var totals = new List<long>();
            foreach (RecipeItem item in items)
            {
                if (index.TryGetValue(item.Code, out int at))
                {
                    totals[at] += (long)item.Count * factor;
                }
                else
                {
                    index[item.Code] = totals.Count;
                    totals.Add((long)item.Count * factor);
                    result.Add(item);
                }
            }
            for (int i = 0; i < result.Count; i++)
            {
                if (totals[i] > int.MaxValue)
                {
                    return null;
                }
                result[i] = new RecipeItem(result[i].Code, (int)totals[i]);
            }
            return result;
        }

        private static int MaxByMaterials(List<RecipeItem> unit, int recipeItemCode, Inventory bag, long gold)
        {
            long best = MaxCount;
            foreach (RecipeItem item in unit)
            {
                if (item.Count <= 0)
                {
                    continue;
                }
                long have = item.IsGold ? gold : bag.CountOf(item.Code);
                best = Math.Min(best, have / item.Count);
            }
            if (recipeItemCode >= 0)
            {
                best = Math.Min(best, bag.CountOf(recipeItemCode));
            }
            return (int)Math.Max(0, best);
        }
    }
}
