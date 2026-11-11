using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的设计:把称号图鉴、成就进度、主角的背包与装备栏拢成一个会话,给宿主一个入口(穿脱称号、图鉴与背包装备栏之间搬运、成就触发、存档)。
// 规则本身在 Dnf.Game 的 Titles 里(依据是服务端静态读码);这里不加规则,只做接线,并把"称号占装备栏的 11 号格"这件事讲清楚:
//  · 称号部位(11)在装备栏里是普通的一格(EquipmentSlots 的 12 个格之一),穿上去它的属性就算进 HeroProgress.Stats(推荐的汇总出口)——这条路本来就通,
//    本会话的穿脱只是在它前面加了"必须是称号物品"的检查;图鉴收集的属性加成(推断,默认不开)在调用侧叠加,不改 EquipStats。
//  · 装备栏的 11 号格与图鉴是两个容器:一件称号只在其中一处(搬动就是移动);服务端没有"图鉴里有同款就不能穿"的互斥(CTitleBook::_getOwnerTitleInfo 0x08641994 只用来组包)。
//    装备栏 10 到 21 与时装栏 0 到 9 是互不相干的两个栏,称号与时装、普通装备任意次序穿脱,结果互不影响。
namespace Dnf.Play
{
    /// <summary>属性汇总:调用侧把几份来源叠在一起,各份都留着,谁加了多少看得见。</summary>
    public sealed class TitleSummary
    {
        public TitleSummary(StatBlock total, StatBlock hero, EquipStats equipment, EquipStats wornTitle, TitleBonus collection)
        {
            Total = total;
            Hero = hero;
            Equipment = equipment;
            WornTitle = wornTitle;
            Collection = collection;
        }

        /// <summary>总属性(十分之一单位的整数):<see cref="Hero"/> 加上图鉴收集加成折成的属性块。</summary>
        public StatBlock Total { get; }

        /// <summary>主角属性:等级成长加上穿着的全部装备(含称号),即 <c>HeroProgress.Stats</c>。</summary>
        public StatBlock Hero { get; }

        /// <summary>穿着的全部装备的基础属性(含称号;<c>HeroProgress.WornStats</c>)。</summary>
        public EquipStats Equipment { get; }

        /// <summary>穿着的那件称号自己的属性(没穿是 <see cref="EquipStats.Empty"/>);它已经包含在 <see cref="Equipment"/> 里,单列只为看出它的份额。</summary>
        public EquipStats WornTitle { get; }

        /// <summary>图鉴收集的加成(没打开是 <see cref="TitleBonus.None"/>)。</summary>
        public TitleBonus Collection { get; }

        /// <summary>全部装备加上收集加成的完整属性集(调用侧用 <see cref="EquipStats.Plus"/> 叠加,不改 <see cref="EquipStats"/> 本身)。</summary>
        public EquipStats Combined => Equipment.Plus(Collection.Stats);
    }

    /// <summary>从存档还原称号图鉴与成就的结果统计。</summary>
    public sealed class TitleRestoreReport
    {
        public TitleRestoreReport(int titles, int titlesDropped, int achievements, int achievementsDropped)
        {
            Titles = titles;
            TitlesDropped = titlesDropped;
            Achievements = achievements;
            AchievementsDropped = achievementsDropped;
        }

        /// <summary>还原回图鉴的称号数。</summary>
        public int Titles { get; }

        /// <summary>丢掉的称号数:物品编号找不到、不是称号物品,或分类与格越界。</summary>
        public int TitlesDropped { get; }

        /// <summary>还原回去的成就进度条数。</summary>
        public int Achievements { get; }

        /// <summary>丢掉的成就进度条数:成就不存在(数据变了)、计数越界,或超过服务端的条数上限。</summary>
        public int AchievementsDropped { get; }
    }

    /// <summary>
    /// 称号会话:图鉴与成就(<see cref="Core"/>)、主角进度(<see cref="Hero"/>,背包与装备栏在它上面)、物品查询。
    /// <para>
    /// 接进宿主的写法:存档时 <c>var save = progress.Snapshot(); titles.Capture(save);</c>;读档时 <c>progress.Restore(save, lookup); titles.Restore(save);</c>;
    /// 登录与任务完成之后调 <see cref="Verify"/>(把"要求某任务完成"的成就补成完成)。
    /// </para>
    /// </summary>
    public sealed class TitleSession
    {
        private readonly Func<int, ItemInfo> _lookup;

        public TitleSession(HeroProgress hero, TitleSystem system, Func<int, ItemInfo> lookup, TitleCatalog catalog = null)
        {
            Hero = hero ?? throw new ArgumentNullException(nameof(hero));
            Core = system ?? throw new ArgumentNullException(nameof(system));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            Catalog = catalog;
        }

        public HeroProgress Hero { get; }

        /// <summary>图鉴、成就进度与待投递邮件。</summary>
        public TitleSystem Core { get; }

        /// <summary>外观与气泡数据的目录(可以是 null:只做规则、不查外观)。</summary>
        public TitleCatalog Catalog { get; }

        public TitleBook Book => Core.Book;

        public AchievementTracker Achievements => Core.Achievements;

        /// <summary>图鉴收集的属性加成要不要叠进 <see cref="Summary"/>(默认不叠:这条是推断,宿主明确要才开)。</summary>
        public bool CollectionBonusEnabled { get; set; }

        /// <summary>图鉴与成就进度的变化次数。</summary>
        public int Version => Core.Version;

        /// <summary>现在穿着的称号;没穿是 null。</summary>
        public ItemInfo WornTitle => Hero.Worn.Get(EquipmentParts.TitleName);

        /// <summary>穿着的称号的外观与时限数据;没穿、没接目录或读不到是 null。</summary>
        public TitleAppearance WornAppearance
        {
            get
            {
                ItemInfo worn = WornTitle;
                return worn == null || Catalog == null ? null : Catalog.AppearanceOf(worn.Code);
            }
        }

        /// <summary>物品是称号物品(装备,部位 11)。</summary>
        public static bool IsTitleItem(ItemInfo item)
        {
            return item != null && item.IsEquipment && item.Part == EquipmentParts.TitleName;
        }

        // ---------------------------------------------------------------- 穿脱

        /// <summary>
        /// 穿上背包装备页第 <paramref name="bagIndex"/> 格的称号:不是称号物品返回 <see cref="EquipResult.WrongSlot"/>,其余规则与结果同 <see cref="HeroProgress.Equip"/>
        /// (11 号格上原来的称号放回背包被穿上的那一格)。
        /// </summary>
        public EquipResult Wear(int bagIndex)
        {
            IReadOnlyList<ItemStack> tab = Hero.Bag.Tab(InventoryTab.Equipment);
            if ((uint)bagIndex >= (uint)tab.Count)
            {
                return EquipResult.NoItem;
            }
            if (!IsTitleItem(tab[bagIndex].Info))
            {
                return EquipResult.WrongSlot;
            }
            return Hero.Equip(bagIndex);
        }

        /// <summary>脱下称号放回背包;没穿或背包装备页满了返回 false。</summary>
        public bool Unwear()
        {
            return Hero.Unequip(EquipmentParts.TitleName);
        }

        // ---------------------------------------------------------------- 图鉴与背包、装备栏之间搬运(服务端 putTitle / getTitle)

        /// <summary>
        /// 把背包装备页第 <paramref name="bagIndex"/> 格的称号放进图鉴的 (<paramref name="category"/>, <paramref name="slot"/>):检查与返回码见 <see cref="TitleBook.CheckPut"/>
        /// (来源不是称号物品算 <see cref="TitleBookResult.Mismatch"/>);原来占着这一格的称号放回背包被放入的那一格。
        /// </summary>
        public TitleBookResult PutFromBag(int bagIndex, TitleCategory category, int slot)
        {
            IReadOnlyList<ItemStack> tab = Hero.Bag.Tab(InventoryTab.Equipment);
            if ((uint)bagIndex >= (uint)tab.Count || !IsTitleItem(tab[bagIndex].Info))
            {
                return TitleBookResult.Mismatch;
            }
            ItemInfo item = tab[bagIndex].Info;
            TitleBookResult result = Book.CheckPut(category, slot, item, Hero.Level);
            if (result != TitleBookResult.Ok)
            {
                return result;
            }
            if (!TryOccupant(category, slot, out ItemInfo old))
            {
                return TitleBookResult.Mismatch;
            }
            Book.Put(category, slot, item, Hero.Level, out _);
            if (old == null)
            {
                Hero.Bag.TakeAt(InventoryTab.Equipment, bagIndex);
            }
            else
            {
                Hero.Bag.ReplaceAt(InventoryTab.Equipment, bagIndex, old);
            }
            return TitleBookResult.Ok;
        }

        /// <summary>把现在穿着的称号放进图鉴的这一格;原来占着这一格的称号换到装备栏 11 号格上(服务端把被换下的放回来源)。没穿返回 <see cref="TitleBookResult.Mismatch"/>。</summary>
        public TitleBookResult PutFromWorn(TitleCategory category, int slot)
        {
            ItemInfo worn = WornTitle;
            if (worn == null)
            {
                return TitleBookResult.Mismatch;
            }
            TitleBookResult result = Book.CheckPut(category, slot, worn, Hero.Level);
            if (result != TitleBookResult.Ok)
            {
                return result;
            }
            if (!TryOccupant(category, slot, out ItemInfo old))
            {
                return TitleBookResult.Mismatch;
            }
            Book.Put(category, slot, worn, Hero.Level, out _);
            SetWorn(old);
            return TitleBookResult.Ok;
        }

        /// <summary>
        /// 把图鉴这一格的称号拿回背包(服务端 getTitle 去背包):范围 → 对得上 → 物品存在 → 这一格不绑成就(5)→ 背包装备页放得下(11)。
        /// </summary>
        public TitleBookResult TakeToBag(TitleCategory category, int slot)
        {
            int code = Book.CodeAt(category, slot);
            TitleBookResult result = Book.CheckTake(category, slot, code, false);
            if (result != TitleBookResult.Ok)
            {
                return result;
            }
            ItemInfo item = _lookup(code);
            if (item == null)
            {
                return TitleBookResult.Mismatch;
            }
            result = Book.CheckTake(category, slot, code, true);
            if (result != TitleBookResult.Ok)
            {
                return result;
            }
            if (!Hero.Bag.CanAdd(item))
            {
                return TitleBookResult.BagFull;
            }
            Book.Remove(category, slot);
            Hero.Bag.Add(item);
            return TitleBookResult.Ok;
        }

        /// <summary>
        /// 把图鉴这一格的称号直接穿上(服务端 getTitle 去装备栏):范围 → 对得上 → 物品存在 → 11 号格是空的(7,服务端装备栏插入检查要求格空)→ 穿得上(6,职业与等级)。
        /// 绑了成就的格里的称号可以直接穿(不受"不能拿回背包"限制)。
        /// </summary>
        public TitleBookResult TakeAndWear(TitleCategory category, int slot)
        {
            int code = Book.CodeAt(category, slot);
            TitleBookResult result = Book.CheckTake(category, slot, code, false);
            if (result != TitleBookResult.Ok)
            {
                return result;
            }
            ItemInfo item = _lookup(code);
            if (item == null)
            {
                return TitleBookResult.Mismatch;
            }
            if (WornTitle != null)
            {
                return TitleBookResult.SlotUnavailable;
            }
            if (EquipRules.Check(item, EquipmentParts.TitleName, Hero.Job, Hero.Level) != EquipResult.Ok)
            {
                return TitleBookResult.NotEquipable;
            }
            Book.Remove(category, slot);
            SetWorn(item);
            return TitleBookResult.Ok;
        }

        /// <summary>图鉴这一格现在的占用者(物品);空格给 null 并返回 true;格里有编号但物品查不到返回 false(换不回去,放入就拒绝)。</summary>
        private bool TryOccupant(TitleCategory category, int slot, out ItemInfo item)
        {
            int existing = Book.CodeAt(category, slot);
            item = existing == 0 ? null : _lookup(existing);
            return existing == 0 || item != null;
        }

        /// <summary>
        /// 装备栏 11 号格直接设成 <paramref name="title"/>(null 是清空),不动别的格。装备栏没有公开的"清一格"接口,
        /// 这里用"清空再逐格放回"实现:装备栏版本号会多走几步,内容只有 11 号格变了。
        /// </summary>
        private void SetWorn(ItemInfo title)
        {
            var keep = new List<KeyValuePair<int, ItemInfo>>();
            for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
            {
                ItemInfo item = Hero.Worn.Get(part);
                if (item != null && part != EquipmentParts.TitleName)
                {
                    keep.Add(new KeyValuePair<int, ItemInfo>(part, item));
                }
            }
            Hero.Worn.Clear();
            foreach (KeyValuePair<int, ItemInfo> pair in keep)
            {
                Hero.Worn.Restore(pair.Key, pair.Value);
            }
            if (title != null)
            {
                Hero.Worn.Restore(EquipmentParts.TitleName, title);
            }
        }

        // ---------------------------------------------------------------- 属性汇总

        /// <summary>
        /// 现在的属性汇总:<see cref="TitleSummary.Hero"/> 是主角属性(装备含称号,穿上加、脱下扣,与穿戴次序无关),
        /// 打开 <see cref="CollectionBonusEnabled"/> 时再叠上图鉴收集加成。每次现算。
        /// </summary>
        public TitleSummary Summary()
        {
            ItemInfo worn = WornTitle;
            TitleBonus bonus = CollectionBonusEnabled ? Core.Bonus() : TitleBonus.None;
            StatBlock hero = Hero.Stats;
            return new TitleSummary(hero + bonus.Stats.ToStatBlock(), hero, Hero.WornStats, worn == null ? EquipStats.Empty : worn.Stats, bonus);
        }

        // ---------------------------------------------------------------- 成就

        /// <summary>一次成就触发(角色等级取主角现在的等级);见 <see cref="TitleSystem.Trigger"/>。</summary>
        public TitleTriggerOutcome Trigger(int questId, int a, int b, int c)
        {
            return Core.Trigger(questId, a, b, c, Hero.Level);
        }

        /// <summary>核对:要求某任务完成的成就,任务已完成就补成完成(任务进度取主角的任务簿);见 <see cref="TitleSystem.Verify"/>。返回改动了几个成就。</summary>
        public int Verify()
        {
            ItemInfo worn = WornTitle;
            return Core.Verify(Hero.Quests.IsCleared, worn == null ? 0 : worn.Code);
        }

        /// <summary>补发(任务进度取主角的任务簿);见 <see cref="TitleSystem.Adjust"/>。返回处理了几项。</summary>
        public int Adjust()
        {
            return Core.Adjust(Hero.Quests.IsCleared);
        }

        // ---------------------------------------------------------------- 存档

        /// <summary>把图鉴与成就进度写进存档(先清掉 <paramref name="save"/> 里原有的称号字段);穿着的称号在装备栏,随 <c>worn</c> 行存。</summary>
        public void Capture(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            save.Titles.Clear();
            save.Achievements.Clear();
            foreach (TitleEntry entry in Book.Entries())
            {
                save.Titles.Add(new SavedTitle((int)entry.Category, entry.Slot, entry.Code));
            }
            foreach (AchievementProgress progress in Achievements.Entries())
            {
                save.Achievements.Add(new SavedAchievement(progress.Id, progress.Remaining0, progress.Remaining1, progress.Remaining2));
            }
        }

        /// <summary>
        /// 把存档里的图鉴与成就进度还原到这个会话(整个替换,待投递的邮件丢掉)。称号:物品存在且是称号物品、分类与格没越界才放回(格定义有没有变不查,服务端读档也是原样);
        /// 成就:编号还在目录里、计数没越界、不超过服务端的条数上限才放回。丢掉的数目在返回值里。
        /// </summary>
        public TitleRestoreReport Restore(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            Core.Clear();
            int titles = 0;
            int titlesDropped = 0;
            foreach (SavedTitle saved in save.Titles)
            {
                ItemInfo item = _lookup(saved.Code);
                if (IsTitleItem(item) && TitleCategories.IsValid((TitleCategory)saved.Category) && Book.Restore((TitleCategory)saved.Category, saved.Slot, saved.Code))
                {
                    titles++;
                }
                else
                {
                    titlesDropped++;
                }
            }
            int achievements = 0;
            int achievementsDropped = 0;
            foreach (SavedAchievement saved in save.Achievements)
            {
                if (Achievements.Restore(saved.Id, saved.Remaining0, saved.Remaining1, saved.Remaining2))
                {
                    achievements++;
                }
                else
                {
                    achievementsDropped++;
                }
            }
            return new TitleRestoreReport(titles, titlesDropped, achievements, achievementsDropped);
        }
    }
}
