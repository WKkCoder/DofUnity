using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

// 证据:E-111(等级、经验、升级与等级成长;各部分的服务端读法见 Dnf.Game 的 Progress 目录)。
namespace Dnf.Play
{
    /// <summary>
    /// 主角的等级与经验(证据 E-111):升级经验表、技能点表、倍率表、基础经验表与角色成长表都是只读的数据,
    /// 变的只有等级、累计经验与升级得到的点数。<see cref="Gain"/> 走服务端的升级循环(<see cref="Leveling"/>),
    /// <see cref="Stats"/> 给出这个等级的属性(<see cref="CharacterGrowth"/>:初始值加上每升一级的增量;单位是十分之一)。
    /// 一个对象跟着主角走完整个流程:副本里每个房间的战斗会话共用同一个(房间换了、副本换了,等级与经验都留着)。
    /// 技能(M35,证据 E-127):<see cref="Skills"/> 是学会的技能、技能点余额与快捷栏;升级给的点数进它的余额,学技能花、退技能还。
    /// 没做:觉醒转职(<see cref="Awakening"/> 恒为 0,50 级以后用成长类型本身的块)、属性点与装备对属性的其余加成、任务与道具另给的技能点。
    /// </summary>
    public sealed class HeroProgress
    {
        /// <summary>新角色的成长类型(服务端缺省 0;转职前一直用第一个成长类型的增量)。</summary>
        public const int DefaultGrowtype = 0;

        public ExpTable Table { get; }
        public SpTable Sp { get; }
        public CharacterGrowth Growth { get; }
        public MobRewardTable Rewards { get; }
        public ExpRates Rates { get; }

        /// <summary>成长类型(下标 0 到 5,0 是转职前;<see cref="ChangeGrowtype"/> 转职时改)。</summary>
        public int Growtype { get; private set; }

        /// <summary>觉醒下标(0 = 没觉醒;1、2 对应 [awakening 1]、[awakening 2] 块)。</summary>
        public int Awakening { get; }

        public int Level { get; private set; }

        /// <summary>累计经验(升级之后等于新等级的门槛再加上滚下来的余量)。</summary>
        public int Exp { get; private set; }

        /// <summary>从创建起一共加过多少经验(含升级时被吞掉的封顶余量之前的全部原始增量)。</summary>
        public long TotalGained { get; private set; }

        /// <summary>从创建起一共涨过几级。</summary>
        public int LevelUps { get; private set; }

        /// <summary>剩下的技能点(升级得到,学技能花掉,退技能还回;<see cref="Skills"/> 的余额)。</summary>
        public int SkillPoints => Skills.Sp;

        /// <summary>剩下的觉醒点(新等级 ≥ 50 的每一级一点;特殊技能花它)。</summary>
        public int AwakeningPoints => Skills.Sfp;

        /// <summary>
        /// 学会的技能、点数余额与技能快捷栏(M35)。没接技能数据时(<see cref="AttachSkills"/> 之前)是个空目录的簿子:只记点数。
        /// 技能目录与成长类型在读档、转职时会换簿子里的内容,但对象本身在 <see cref="AttachSkills"/> 之后保持不变。
        /// </summary>
        public SkillBook Skills { get; private set; } = new SkillBook(SkillCatalog.Empty);

        /// <summary>技能簿的变化次数的转手(宿主层不引用 Dnf.Game):学、退、重置、点数、快捷栏变动都会加一。</summary>
        public int SkillsVersion => Skills.Version;

        /// <summary>学会了几个技能(含白送的;宿主层只认基本类型,不引用技能簿的类型)。</summary>
        public int SkillsLearned => Skills.LearnedCount;

        /// <summary>白送技能的来源(.chr 的脚本行);<see cref="AttachSkills"/> 给。</summary>
        private ScriptLine[] _chrLines;

        /// <summary>等级与经验的变化次数:各个房间的战斗会话据此发现"别的房间里升了级",把自己的生命上限与攻击面板换成新等级的。</summary>
        public int Version { get; private set; }

        /// <summary>身上的金币(拾取地上的金币、向商店卖东西加上来,在商店买东西花掉;证据 E-116、E-119)。副本里各房间的战斗会话共用同一个对象。</summary>
        public long Gold { get; private set; }

        /// <summary>从创建起一共进账多少金币(拾到的加上卖东西得到的;<see cref="Gold"/> 之后会被花掉,这个不减)。</summary>
        public long GoldGained { get; private set; }

        /// <summary>背包(证据 E-117:拾取地上的物品放进去,商店买卖,E-119;副本里各房间共用同一个对象)。消耗品在这里使用(<see cref="UseConsumable"/>,E-120)。</summary>
        public Inventory Bag { get; } = new Inventory();

        /// <summary>
        /// 快捷栏(证据 E-120):6 格,放背包里的药水(能回复生命魔法的消耗品),键 1 到 6 用第 0 到 5 格。拿到(拾取、买)合格的物品时自动放进第一个空格,
        /// 背包里这种物品用光或卖光时那一格腾空;存进存档。
        /// </summary>
        public QuickSlots Quick { get; } = new QuickSlots();

        /// <summary>消耗品的冷却(随战斗会话的时间推进,城镇里不走;不存档)。</summary>
        public ItemCooldowns Cooldowns { get; } = new ItemCooldowns();

        /// <summary>从创建起一共用掉多少件消耗品。</summary>
        public int ItemsUsed { get; private set; }

        /// <summary>快捷栏放了几格(宿主层不引用 Dnf.Game,日志用)。</summary>
        public int QuickFilled => Quick.Filled;

        /// <summary>快捷栏第 <paramref name="slot"/> 格的物品编号(空格或越界为 0)。</summary>
        public int QuickCodeAt(int slot) => Quick[slot]?.Code ?? 0;

        /// <summary>快捷栏第 <paramref name="slot"/> 格的物品在背包里还有几件(空格为 0)。</summary>
        public int QuickCountAt(int slot) => Quick[slot] == null ? 0 : Bag.CountOf(Quick[slot].Code);

        /// <summary>现在有几种消耗品在冷却。</summary>
        public int CooldownsActive => Cooldowns.Active;

        /// <summary>让药水冷却走 <paramref name="deltaMs"/> 毫秒(宿主层不引用 Dnf.Game):副本里由房间会话推进,副本外由宿主按墙钟推进,城镇底栏上的遮罩才会退。</summary>
        public void AdvanceCooldowns(double deltaMs) => Cooldowns.Advance(deltaMs);

        /// <summary>装备栏(证据 E-118:武器、称号、上衣……魔法石共 12 格,从背包穿上去;副本里各房间共用同一个对象)。</summary>
        public EquipmentSlots Worn { get; } = new EquipmentSlots();

        /// <summary>主角的职业下标(穿装备的职业检查用,<see cref="EquipRules.JobOf"/>);默认主角是剑魂。</summary>
        public int Job { get; set; } = EquipRules.SwordmanJob;

        /// <summary>装备栏的变化次数:战斗会话据此重算攻击面板、换武器外观(穿脱各算一次)。</summary>
        public int EquipVersion => Worn.Version;

        /// <summary>穿着几件装备(宿主层不引用 Dnf.Game,日志与界面用它)。</summary>
        public int EquippedCount => Worn.EquippedCount;

        /// <summary>穿着的武器的脚本路径(.equ,规范化的小写 PVF 路径);没有穿武器返回 null(这时用起手的演示武器)。</summary>
        public string WeaponPath => Worn.Get(EquipmentParts.Weapon)?.Path;

        /// <summary>
        /// 穿上背包装备页第 <paramref name="bagIndex"/> 格的装备(规则见 <see cref="EquipRules"/>,用主角现在的职业与等级);
        /// <paramref name="canShow"/> 给了就再问一次"画面上穿不穿得出来"。成功时背包里原位换成被换下来的那件(没有就腾空这一格)。
        /// </summary>
        public EquipResult Equip(int bagIndex, Func<ItemInfo, bool> canShow = null)
        {
            return EquipActions.Equip(Bag, Worn, bagIndex, Job, Level, canShow);
        }

        /// <summary>把 <paramref name="part"/> 号部位的装备脱下放回背包;槽是空的或背包装备页满了返回 false。</summary>
        public bool Unequip(int part)
        {
            return EquipActions.Unequip(Bag, Worn, part);
        }

        /// <summary>从创建起一共拾到多少件物品(<see cref="Bag"/> 里的东西以后会被用掉、卖掉,这个不减)。</summary>
        public int ItemsGained { get; private set; }

        /// <summary>任务进度(M31):进行中的任务与剩余计数、已完成的任务编号;规则与事件在 <see cref="QuestBook"/>。</summary>
        public QuestLog Quests { get; } = new QuestLog();

        /// <summary>进行中的任务数、已完成的任务数(宿主层不引用 Dnf.Game,日志与界面用这两个数)。</summary>
        public int QuestsActive => Quests.Active.Count;

        public int QuestsCleared => Quests.Cleared.Count;

        /// <summary>各副本的通关记录与难度门槛(M33,证据 E-125):通关时由 <see cref="DungeonSession"/> 记,选择界面按它决定能选到哪个难度,存进存档。</summary>
        public DungeonClears Clears { get; } = new DungeonClears();

        /// <summary>有通关记录的副本数(宿主层不引用 Dnf.Game,日志用这个数)。</summary>
        public int DungeonsCleared => Clears.Count;

        /// <summary>这个副本能选到的最高难度(0 到 3):记录值与"有没有难度 3"(<paramref name="hasUltimate"/>)决定;宿主层用基本类型取。</summary>
        public int HighestDifficulty(int dungeonId, bool hasUltimate)
        {
            return Clears.HighestSelectable(dungeonId, hasUltimate);
        }

        /// <summary>背包里一共有多少件、占了多少格(宿主层不引用 Dnf.Game,日志与界面用这两个数)。</summary>
        public int BagItems => Bag.TotalCount;

        public int BagSlots => Bag.StackCount;

        public HeroProgress(ExpTable table, SpTable sp, CharacterGrowth growth, MobRewardTable rewards, ExpRates rates, int level = 1, int exp = -1,
            int growtype = DefaultGrowtype, int awakening = 0)
        {
            Table = table ?? throw new ArgumentNullException(nameof(table));
            Sp = sp ?? new SpTable(new int[0]);
            Growth = growth ?? CharacterGrowth.Parse(null);
            Rewards = rewards ?? MobRewardTable.Placeholder(table);
            Rates = rates ?? ExpRates.Neutral;
            Growtype = growtype < 0 ? 0 : growtype;
            Awakening = awakening < 0 ? 0 : awakening;
            Level = level < 1 ? 1 : level;
            Exp = exp >= 0 ? exp : table.LevelExp(Level);
        }

        /// <summary>等级是否已经到了经验能涨到的上限(<see cref="Leveling.MaxLevel"/>)。</summary>
        public bool IsCapped => Level >= Leveling.MaxLevel;

        /// <summary>在本级里已经攒的经验(累计经验减去本级的门槛;经验条的分子,推断,见 HudArt)。</summary>
        public int ExpInLevel => unchecked(Exp - Table.LevelExp(Level));

        /// <summary>本级一共要攒的经验(下一级的门槛减去本级的门槛;经验条的分母,推断);表走到头时不大于 0。</summary>
        public int LevelSpan => Table.Section(Level);

        /// <summary>这个等级的属性(十分之一单位的整数,<see cref="CharacterGrowth.StatsAt"/>)。</summary>
        public StatBlock Stats => Growth.StatsAt(Level, Growtype, Awakening);

        /// <summary>
        /// 加经验并按服务端的循环升级。返回这一次的结果(涨了几级、给了多少点)。
        /// <paramref name="exp"/> 不大于 0 什么都不变(也不算一次变化)。
        /// </summary>
        public ExpGainResult Gain(int exp)
        {
            if (exp <= 0)
            {
                return new ExpGainResult(Level, Exp, 0, 0, 0);
            }
            ExpGainResult r = Leveling.Gain(Table, Sp, Level, Exp, exp);
            TotalGained += exp;
            if (r.Level != Level || r.Exp != Exp)
            {
                Version++;
            }
            Level = r.Level;
            Exp = r.Exp;
            LevelUps += r.LevelsGained;
            Skills.AddPoints(r.SkillPoints, r.AwakeningPoints);
            return r;
        }

        /// <summary>拾到 <paramref name="amount"/> 金币;不大于 0 什么都不变。</summary>
        public void AddGold(long amount)
        {
            if (amount <= 0)
            {
                return;
            }
            Gold += amount;
            GoldGained += amount;
        }

        /// <summary>
        /// 拾到 <paramref name="count"/> 件物品放进背包(<see cref="Inventory.Add"/>);全部放进去返回 true,放不下的不放(返回 false,已放进去的那部分保留、也计入累计)。
        /// </summary>
        public bool AddItem(ItemInfo info, int count = 1)
        {
            int put = Bag.Add(info, count);
            ItemsGained += put;
            if (put > 0)
            {
                Quick.Place(info);               // 药水自动进快捷栏第一个空格(合格才放,放不下就只留在背包里)
            }
            return put == count && count > 0;
        }

        /// <summary>
        /// 用一件消耗品(证据 E-120):检查(种类、效果、等级、职业)→ 主角此刻有生命魔法可回复(<paramref name="vitals"/> 不为 null 且活着)→ 背包里有 →
        /// 不在冷却 → 扣一件、起冷却、开始药效。服务端 <c>use_stackable</c> 是先查冷却再消耗;其余检查次序是这一层自己定的。
        /// 不合规则时什么都不动,返回原因;用掉最后一件时快捷栏那一格腾空。
        /// </summary>
        public UseResult UseConsumable(ItemInfo item, HeroVitals vitals)
        {
            UseResult check = ConsumableRules.Check(item, Job, Level);
            if (check != UseResult.Ok)
            {
                return check;
            }
            if (vitals == null || !vitals.IsAlive)
            {
                return UseResult.Unavailable;
            }
            if (Bag.CountOf(item.Code) < 1)
            {
                return UseResult.NotInBag;
            }
            if (Cooldowns.IsCooling(item))
            {
                return UseResult.Cooling;
            }
            Bag.Remove(item.Code, 1);
            Cooldowns.Start(item);
            vitals.StartRecovery(item.HpRecovery, item.MpRecovery);
            ItemsUsed++;
            SyncQuick();
            return UseResult.Ok;
        }

        /// <summary>用快捷栏第 <paramref name="slot"/> 格(键 1 到 6 是第 0 到 5 格)的消耗品;格子空着或越界返回 <see cref="UseResult.EmptySlot"/>。</summary>
        public UseResult UseQuickSlot(int slot, HeroVitals vitals)
        {
            ItemInfo item = Quick[slot];
            return item == null ? UseResult.EmptySlot : UseConsumable(item, vitals);
        }

        /// <summary>背包里已经没有的物品从快捷栏拿掉(用光、卖光之后)。</summary>
        private void SyncQuick()
        {
            for (int i = 0; i < QuickSlots.Count; i++)
            {
                ItemInfo item = Quick[i];
                if (item != null && Bag.CountOf(item.Code) <= 0)
                {
                    Quick.Clear(i);
                }
            }
        }

        /// <summary>把背包里合格又没在快捷栏里的物品按背包的次序放进空格(存档还原后补齐用)。</summary>
        private void PlaceBagPotions()
        {
            for (int t = 0; t < Inventory.TabCount; t++)
            {
                foreach (ItemStack stack in Bag.Tab((InventoryTab)t))
                {
                    Quick.Place(stack.Info);
                }
            }
        }

        /// <summary>花掉 <paramref name="amount"/> 金币;不够就一个也不扣并返回 false(金额不大于 0 也返回 false)。</summary>
        public bool TrySpendGold(long amount)
        {
            if (amount <= 0 || amount > Gold)
            {
                return false;
            }
            Gold -= amount;
            return true;
        }

        /// <summary>
        /// 在商店买 <paramref name="count"/> 件 <paramref name="item"/>(规则见 <see cref="ShopRules.CheckBuy"/>,<paramref name="shop"/> 为 null 时不查"这家店卖不卖")。
        /// 与服务端同一个次序:检查通过之后先放进背包、再扣钱;买到的不算"拾到的物品"(<see cref="ItemsGained"/> 不变)。装备按 1 件买。
        /// 不合规则时什么都不动,返回原因。
        /// </summary>
        public ShopResult Buy(ItemShopDef shop, ItemInfo item, int count)
        {
            ShopResult result = ShopRules.CheckBuy(shop, item, count, Gold, Bag);
            if (result != ShopResult.Ok)
            {
                return result;
            }
            int n = item.IsEquipment ? 1 : count;
            long cost = ItemPricing.BuyPrice(item, n);
            Bag.Add(item, n);
            Quick.Place(item);
            Gold -= cost;
            return ShopResult.Ok;
        }

        /// <summary>
        /// 把背包 <paramref name="tab"/> 页第 <paramref name="index"/> 格里的 <paramref name="count"/> 件卖给商店(规则见 <see cref="ShopRules.CheckSell"/>,
        /// 价格见 <see cref="ItemPricing.SellPrice"/>,<paramref name="rates"/> 为 null 用替身倍率)。装备按 1 件卖。卖得的钱进 <see cref="Gold"/>(也算进 <see cref="GoldGained"/>)。
        /// 不合规则时什么都不动,<paramref name="earned"/> 是 0。
        /// </summary>
        public ShopResult Sell(InventoryTab tab, int index, int count, ItemPriceRates rates, out long earned)
        {
            earned = 0;
            System.Collections.Generic.IReadOnlyList<ItemStack> stacks = Bag.Tab(tab);
            if ((uint)index >= (uint)stacks.Count)
            {
                return ShopResult.NoItem;
            }
            ItemStack stack = stacks[index];
            ShopResult result = ShopRules.CheckSell(stack.Info, count, stack.Count);
            if (result != ShopResult.Ok)
            {
                return result;
            }
            int n = stack.Info.IsEquipment ? 1 : count;
            long price = ItemPricing.SellPrice(stack.Info, n, rates);
            Bag.RemoveAt(tab, index, n);
            SyncQuick();
            AddGold(price);
            earned = price;
            return ShopResult.Ok;
        }

        /// <summary>直接设成某个等级(经验设成这一级的门槛;测试与 --level 之类的开关用),不给点数。</summary>
        public void SetLevel(int level)
        {
            int clamped = level < 1 ? 1 : level;
            if (clamped == Level && Exp == Table.LevelExp(clamped))
            {
                return;
            }
            Level = clamped;
            Exp = Table.LevelExp(clamped);
            Version++;
        }

        // ---------------- 技能(M35,证据 E-127) ----------------

        /// <summary>
        /// 接上这个角色的技能:<paramref name="catalog"/> 是职业的技能目录,<paramref name="chrLines"/> 是 .chr 的脚本行(白送技能的来源,<see cref="SkillGrants"/>)。
        /// 换一个新的 <see cref="Skills"/>:按现在的成长类型放进白送的技能;点数余额沿用,<paramref name="grantTotalPoints"/> 为真时改成"到现在等级为止应有的总点数"
        /// (新建一个直接从某级开始的角色时用:它还一点都没花)。
        /// </summary>
        public void AttachSkills(SkillCatalog catalog, ScriptLine[] chrLines, bool grantTotalPoints)
        {
            int sp = Skills.Sp;
            int sfp = Skills.Sfp;
            _chrLines = chrLines;
            Skills = new SkillBook(catalog ?? SkillCatalog.Empty);
            Skills.SetGiven(SkillGrants.Read(_chrLines, Growtype));
            if (grantTotalPoints)
            {
                sp = SkillRules.TotalSp(Sp, Level);
                sfp = SkillRules.TotalSfp(Level);
            }
            Skills.SetPoints(sp, sfp);
        }

        /// <summary>接上了技能数据(目录里有技能)。</summary>
        public bool HasSkillData => Skills.Catalog.Count > 0;

        /// <summary>
        /// 把技能 <paramref name="skillId"/> 学高 <paramref name="delta"/> 级(用主角现在的等级与成长类型,规则见 <see cref="SkillBook.Learn"/>);
        /// <paramref name="spent"/> 是花掉的点数。
        /// </summary>
        public SkillOpResult LearnSkill(int skillId, int delta, out int spent)
        {
            return Skills.Learn(skillId, Level, Growtype, delta, out spent);
        }

        /// <summary>把技能 <paramref name="skillId"/> 退掉 <paramref name="delta"/> 级(规则见 <see cref="SkillBook.Refund"/>);<paramref name="refunded"/> 是还回的点数。</summary>
        public SkillOpResult RefundSkill(int skillId, int delta, out int refunded)
        {
            return Skills.Refund(skillId, delta, out refunded);
        }

        /// <summary>
        /// 重置技能(服务端 SkillInit):所有技能清回白送的那几个,快捷栏重排,点数回到"到现在等级为止应有的总点数"(<see cref="SkillRules.TotalSp"/>)。
        /// </summary>
        public void ResetSkills()
        {
            Skills.Reset(SkillGrants.Read(_chrLines, Growtype), SkillRules.TotalSp(Sp, Level), SkillRules.TotalSfp(Level));
        }

        /// <summary>
        /// 转职:换成长类型(服务端 set_grow_type 对"从转职前转出去"的处理):技能清空后重新给开局的与新成长类型白送的技能,点数回到总额;
        /// 属性按新成长类型的增量重算(<see cref="Version"/> 加一,战斗会话据此换面板)。<paramref name="growtype"/> 越界或没变返回 false。
        /// </summary>
        public bool ChangeGrowtype(int growtype)
        {
            if (growtype < 0 || growtype >= SkillRules.GrowtypeCount || growtype == Growtype)
            {
                return false;
            }
            Growtype = growtype;
            ResetSkills();
            Version++;
            return true;
        }

        /// <summary>读档时还原技能:旧存档(没有技能行)只留白送的技能,余额就是存档里的点数;新存档整个换成存档里的。</summary>
        private void RestoreSkills(HeroSave save)
        {
            List<SkillGrant> given = SkillGrants.Read(_chrLines, Growtype);
            int sp = Math.Max(0, save.SkillPoints);
            int sfp = Math.Max(0, save.AwakeningPoints);
            Skills.SetGiven(given);
            if (save.Skills.Count == 0 && save.SkillQuick.Count == 0)
            {
                Skills.Reset(given, sp, sfp);
                return;
            }
            var levels = new List<KeyValuePair<int, int>>();
            foreach (SavedSkill skill in save.Skills)
            {
                levels.Add(new KeyValuePair<int, int>(skill.Id, skill.Level));
            }
            var quick = new List<KeyValuePair<int, int>>();
            foreach (SavedSkillSlot slot in save.SkillQuick)
            {
                quick.Add(new KeyValuePair<int, int>(slot.Slot, slot.SkillId));
            }
            Skills.Restore(levels, quick, sp, sfp);
        }

        /// <summary>
        /// 现在的进度拍成存档快照(M26,<see cref="HeroSave"/>):等级、经验、点数、金币、背包每一格(物品编号与数量)、装备栏每一件。
        /// 只有数字与物品编号,不含物品本身的任何内容。
        /// </summary>
        public HeroSave Snapshot()
        {
            var save = new HeroSave
            {
                Job = Job,
                Level = Level,
                Exp = Exp,
                Growtype = Growtype,
                SkillPoints = SkillPoints,
                AwakeningPoints = AwakeningPoints,
                LevelUps = LevelUps,
                TotalGained = TotalGained,
                Gold = Gold,
                GoldGained = GoldGained,
                ItemsGained = ItemsGained,
            };
            for (int t = 0; t < Inventory.TabCount; t++)
            {
                foreach (ItemStack stack in Bag.Tab((InventoryTab)t))
                {
                    save.Bag.Add(new SavedStack(stack.Info.Code, stack.Count));
                }
            }
            for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
            {
                ItemInfo worn = Worn.Get(part);
                if (worn != null)
                {
                    save.Worn.Add(new SavedWorn(part, worn.Code));
                }
            }
            for (int slot = 0; slot < QuickSlots.Count; slot++)
            {
                ItemInfo quick = Quick[slot];
                if (quick != null)
                {
                    save.Quick.Add(new SavedQuick(slot, quick.Code));
                }
            }
            foreach (ActiveQuest active in Quests.Active)
            {
                save.Quests.Add(new SavedQuest(active.Id, (int[])active.Remaining.Clone()));
            }
            var cleared = new List<int>(Quests.Cleared);
            cleared.Sort();
            save.ClearedQuests.AddRange(cleared);
            foreach (KeyValuePair<int, int> clear in Clears.Entries())
            {
                save.Clears.Add(new SavedClear(clear.Key, clear.Value));
            }
            foreach (KeyValuePair<int, int> skill in Skills.Learned)
            {
                save.Skills.Add(new SavedSkill(skill.Key, skill.Value));
            }
            for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
            {
                int skillId = Skills.QuickAt(slot);
                if (skillId != 0)
                {
                    save.SkillQuick.Add(new SavedSkillSlot(slot, skillId));
                }
            }
            return save;
        }

        /// <summary>
        /// 把存档还原到这个进度上(整个替换:等级、经验、点数、金币、背包与装备栏);<paramref name="lookup"/> 按物品编号取物品(没有返回 null)。
        /// 背包的格子布局原样回来;存档里穿着的装备通过穿戴规则才穿回去,穿不上(数据变了)的放回背包,编号找不到或背包放不下的丢掉,数目在返回值里。
        /// 等级夹在 1 到 <see cref="Leveling.MaxLevel"/>,经验不低于这一级的门槛。
        /// </summary>
        public SaveApplyReport Restore(HeroSave save, Func<int, ItemInfo> lookup)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }
            Job = save.Job >= 0 && save.Job < EquipRules.JobCount ? save.Job : EquipRules.SwordmanJob;
            Level = Math.Max(1, Math.Min(save.Level, Leveling.MaxLevel));
            int floor = Table.LevelExp(Level);
            Exp = save.Exp > floor ? save.Exp : floor;
            Growtype = Math.Max(0, Math.Min(save.Growtype, SkillRules.GrowtypeCount - 1));
            RestoreSkills(save);
            LevelUps = Math.Max(0, save.LevelUps);
            TotalGained = Math.Max(0L, save.TotalGained);
            Gold = Math.Max(0L, save.Gold);
            GoldGained = Math.Max(Gold, save.GoldGained);
            ItemsGained = Math.Max(0, save.ItemsGained);
            Version++;
            Bag.Clear();
            Worn.Clear();
            int stacks = 0;
            int moved = 0;
            int dropped = 0;
            int wornCount = 0;
            foreach (SavedStack saved in save.Bag)
            {
                ItemInfo info = lookup(saved.Code);
                int put = info == null ? 0 : Bag.RestoreStack(info, saved.Count);
                if (put > 0)
                {
                    stacks++;
                }
                if (put < saved.Count)
                {
                    dropped++;
                }
            }
            foreach (SavedWorn saved in save.Worn)
            {
                ItemInfo info = lookup(saved.Code);
                if (info == null)
                {
                    dropped++;
                }
                else if (info.IsEquipment && info.Part == saved.Part && EquipmentSlots.IsSlot(saved.Part) && Worn.Get(saved.Part) == null
                    && EquipRules.Check(info, saved.Part, Job, Level) == EquipResult.Ok)
                {
                    Worn.Restore(saved.Part, info);
                    wornCount++;
                }
                else if (Bag.RestoreStack(info, 1) == 1)
                {
                    moved++;
                }
                else
                {
                    dropped++;
                }
            }
            Quick.ClearAll();                      // 快捷栏:存档里记的格先回原位(物品还在背包里才算),再把没放进去的合格药水按背包次序补进空格
            Cooldowns.Clear();
            foreach (SavedQuick saved in save.Quick)
            {
                ItemInfo info = lookup(saved.Code);
                if (info != null && QuickSlots.Eligible(info) && Bag.CountOf(info.Code) > 0)
                {
                    Quick.Set(saved.Slot, info);
                }
            }
            PlaceBagPotions();
            Quests.Clear();                        // 任务:进行中的与已完成的整个换成存档里的(编号与计数是否合规由 QuestBook.Reconcile 按数据核对)
            foreach (SavedQuest saved in save.Quests)
            {
                Quests.Add(saved.Id, (int[])saved.Remaining.Clone());
            }
            foreach (int id in save.ClearedQuests)
            {
                Quests.MarkCleared(id);
            }
            Clears.Clear();                        // 副本通关记录:整个换成存档里的
            foreach (SavedClear saved in save.Clears)
            {
                Clears.Set(saved.DungeonId, saved.Opened);
            }
            return new SaveApplyReport(stacks, wornCount, moved, dropped);
        }

        /// <summary>宿主用:物品按编号从用户的数据里读(<see cref="ItemCatalog.Get"/>),其余同 <see cref="Restore(HeroSave, Func{int, ItemInfo})"/>。</summary>
        public SaveApplyReport RestoreFromData(HeroSave save, DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            var catalog = new ItemCatalog(new GameData(new PvfScriptSource(assets.Pvf)));
            return Restore(save, catalog.Get);
        }

        /// <summary>
        /// 读真实数据造一个主角的进度:升级经验表、技能点表、倍率表来自数据(缺文件时是空表或中性倍率),
        /// 基础经验表优先用用户导出的(<see cref="MobRewards"/>),没有就用占位表。
        /// </summary>
        public static HeroProgress Create(GameData data, CharacterDef chr, MobRewardTable rewards, int level = 1, int exp = -1)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            ExpTable table = data.ExpTable;
            var progress = new HeroProgress(table, data.SpTable, data.GetGrowth(chr), rewards ?? MobRewardTable.Placeholder(table), data.ExpRates, level, exp);
            progress.AttachSkills(SkillCatalog.Load(data, chr.Job), chr.Tags.Lines, grantTotalPoints: true);
            return progress;
        }

        /// <summary>
        /// 为本地存档的默认主角(0 号角色,与 <see cref="RoomCombatSession.Create"/> 用的是同一个)造进度:读数据里的各张表,
        /// 基础经验表按 <see cref="MobRewards.Load"/> 的顺序找。读不到这个角色的定义时返回 null(杀怪就不给经验)。
        /// </summary>
        public static HeroProgress CreateForLocalHero(DnfAssets assets, int level = 1, int exp = -1)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            CharacterDef chr = data.GetCharacter(0);
            if (chr == null)
            {
                return null;
            }
            return Create(data, chr, MobRewards.Load(assets, data.ExpTable), level, exp);
        }
    }
}
