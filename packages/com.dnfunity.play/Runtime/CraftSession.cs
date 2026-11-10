using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的设计:把配方簿、配方目录和制作规则拢成一个会话,给宿主一个入口(学配方、预览、制作、存档)。
// 规则本身都在 Dnf.Game 的 Craft 里(依据是服务端静态读码);这里不加规则,只做接线。
// 与副职业线互不相干:这里不读也不写副职业的任何状态,副职业与会员等别的系统的条件由宿主通过 ICraftGate 注入。
namespace Dnf.Play
{
    /// <summary>把角色进度的金币接成制作用的钱包(只用 <see cref="HeroProgress.Gold"/> 与 <see cref="HeroProgress.TrySpendGold"/>,不改角色进度)。</summary>
    public sealed class HeroCraftPurse : ICraftPurse
    {
        private readonly HeroProgress _hero;

        public HeroCraftPurse(HeroProgress hero)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
        }

        public long Gold => _hero.Gold;

        public bool TrySpend(long amount)
        {
            return _hero.TrySpendGold(amount);
        }
    }

    /// <summary>从存档还原配方簿的结果统计。</summary>
    public sealed class CraftRestoreReport
    {
        public CraftRestoreReport(int recipes, int dropped)
        {
            Recipes = recipes;
            Dropped = dropped;
        }

        /// <summary>还原回配方簿的配方数。</summary>
        public int Recipes { get; }

        /// <summary>丢掉的行数:编号在用户的数据里不是配方物品(数据变了),或编号重复。</summary>
        public int Dropped { get; }
    }

    /// <summary>
    /// 制作会话:配方簿(<see cref="Book"/>)、配方目录(<see cref="Catalog"/>)与制作规则(<see cref="Bench"/>)。
    /// <para>
    /// 接进宿主的写法:存档时 <c>var save = progress.Snapshot(); craft.Capture(save);</c>;读档时 <c>progress.Restore(save, lookup); craft.Restore(save);</c>。
    /// 使用配方物品那一步(物品使用线)调 <see cref="Learn(int, Inventory)"/>:配方物品消耗一件、配方记进配方簿。
    /// </para>
    /// </summary>
    public sealed class CraftSession
    {
        public CraftSession(RecipeCatalog catalog, ICraftRoll roll = null, ICraftGate gate = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Bench = new CraftBench(catalog.Items.Get, roll, gate);
        }

        public RecipeCatalog Catalog { get; }

        public RecipeBook Book { get; } = new RecipeBook();

        public CraftBench Bench { get; }

        /// <summary>用背包里的配方物品学会 <paramref name="recipeCode"/> 号配方(配方物品消耗一件);见 <see cref="CraftBench.Learn"/>。</summary>
        public LearnResult Learn(int recipeCode, Inventory bag)
        {
            return Bench.Learn(Catalog.Get(recipeCode), Book, bag);
        }

        /// <summary>按已学的 <paramref name="recipeCode"/> 号配方制作 <paramref name="count"/> 份;没学会返回 <see cref="CraftResult.NotLearned"/>。</summary>
        public CraftReport Craft(int recipeCode, Inventory bag, ICraftPurse purse, int count = 1)
        {
            return Bench.CraftLearned(Catalog.Get(recipeCode), Book, bag, purse, count);
        }

        /// <summary>同 <see cref="Craft(int, Inventory, ICraftPurse, int)"/>,背包与金币取自角色进度。</summary>
        public CraftReport Craft(int recipeCode, HeroProgress hero, int count = 1)
        {
            if (hero == null)
            {
                throw new ArgumentNullException(nameof(hero));
            }
            return Craft(recipeCode, hero.Bag, new HeroCraftPurse(hero), count);
        }

        /// <summary>预览按已学配方制作 <paramref name="count"/> 份会是什么结果(不动背包、不掷骰)。</summary>
        public CraftPreview Preview(int recipeCode, Inventory bag, ICraftPurse purse, int count = 1)
        {
            return Bench.Preview(Catalog.Get(recipeCode), CraftSource.Learned, Book, bag, purse, count);
        }

        /// <summary>背包里的配方物品(材料页里种类为"设计图"的格子,按放入先后,同编号只列一次;编号查不到配方定义的不列)。</summary>
        public IReadOnlyList<ItemInfo> RecipeItemsIn(Inventory bag)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            var list = new List<ItemInfo>();
            var seen = new HashSet<int>();
            foreach (ItemStack stack in bag.Tab(InventoryTab.Material))
            {
                ItemInfo info = stack.Info;
                if (info.IsStackable && info.StackableType == StackableTypes.Recipe && seen.Add(info.Code) && Catalog.Get(info.Code) != null)
                {
                    list.Add(info);
                }
            }
            return list;
        }

        /// <summary>把配方簿写进存档(先清掉 <paramref name="save"/> 里原有的配方行):每个学会的配方一行,编号从小到大。</summary>
        public void Capture(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            save.Recipes.Clear();
            save.Recipes.AddRange(Book.Codes);
        }

        /// <summary>
        /// 把存档里的配方还原到配方簿上(整个替换);编号在用户的数据里不是配方物品、或重复的行丢掉并计入 <see cref="CraftRestoreReport.Dropped"/>。
        /// </summary>
        public CraftRestoreReport Restore(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            Book.Clear();
            int restored = 0;
            int dropped = 0;
            foreach (int code in save.Recipes)
            {
                if (Catalog.Get(code) != null && Book.Learn(code))
                {
                    restored++;
                }
                else
                {
                    dropped++;
                }
            }
            return new CraftRestoreReport(restored, dropped);
        }
    }
}
