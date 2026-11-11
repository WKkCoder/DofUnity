using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的接线设计:把"强化、附魔、套装"的规则(Dnf.Game 里的 ItemUpgrade、EnchantRules、EquipSets,依据是服务端静态读码)接到主角的背包、装备栏与金币上。
// 这里不加规则:先让规则在不改任何东西的前提下判一遍,通过了才扣金币、扣材料、换装备;窗口只给数据(UpgradeWindow),不画。
namespace Dnf.Play
{
    /// <summary>被强化或附魔的装备在哪儿。</summary>
    public enum EnhancePlace
    {
        /// <summary>在背包装备页里,<see cref="EnhanceTarget.Index"/> 是格号。</summary>
        Bag = 0,

        /// <summary>穿在身上,<see cref="EnhanceTarget.Index"/> 是部位号(10 到 21)。</summary>
        Worn = 1,
    }

    /// <summary>强化或附魔的目标装备。</summary>
    public readonly struct EnhanceTarget
    {
        public EnhanceTarget(EnhancePlace place, int index)
        {
            Place = place;
            Index = index;
        }

        public EnhancePlace Place { get; }

        /// <summary>背包装备页的格号,或装备栏的部位号。</summary>
        public int Index { get; }

        /// <summary>背包装备页第 <paramref name="index"/> 格。</summary>
        public static EnhanceTarget InBag(int index) => new EnhanceTarget(EnhancePlace.Bag, index);

        /// <summary>身上穿着的某个部位。</summary>
        public static EnhanceTarget OnBody(int part) => new EnhanceTarget(EnhancePlace.Worn, part);
    }

    /// <summary>强化窗口要的数据:这次能不能做(错误码)、花什么、几成把握。</summary>
    public sealed class UpgradeWindow
    {
        internal UpgradeWindow()
        {
        }

        /// <summary>0 表示可以做;否则是 <see cref="UpgradeCodes"/> 里的错误码。</summary>
        public int Code { get; internal set; }

        public bool Ready => Code == 0;

        /// <summary>装备当前强化等级。</summary>
        public int Level { get; internal set; }

        /// <summary>成功后的强化等级(直升材料为它的目标等级)。</summary>
        public int NextLevel { get; internal set; }

        public bool Amplified { get; internal set; }

        /// <summary>要付的金币与手上有的金币。</summary>
        public long GoldCost { get; internal set; }

        public long Gold { get; internal set; }

        /// <summary>要的材料编号、数量与背包里有几个。</summary>
        public int MaterialCode { get; internal set; }

        public int MaterialCount { get; internal set; }

        public int MaterialOwned { get; internal set; }

        /// <summary>失败阈值(0 到 <see cref="ItemUpgrade.ProbabilityDenominator"/>,抽到比它小的就失败;0 表示必定成功)。直升材料没有。</summary>
        public long FailThreshold { get; internal set; }

        /// <summary>成功率:普通强化是 1 减失败阈值占 [0, 分母] 共 分母 + 1 个数的比例;直升材料是券上的百分数。</summary>
        public double SuccessChance { get; internal set; }

        /// <summary>失败时走哪一档惩罚(1 不动、2 降级、3 销毁或被保护);直升材料失败没有惩罚(0)。</summary>
        public int PenaltyType { get; internal set; }

        /// <summary>用的是直升材料(强化券)。</summary>
        public bool Jump { get; internal set; }
    }

    /// <summary>一次强化或附魔的结果。</summary>
    public sealed class EnhanceOutcome
    {
        internal EnhanceOutcome(int code)
        {
            Code = code;
        }

        /// <summary>0 表示流程走完(强化失败也是 0);否则是拒绝的错误码(<see cref="UpgradeCodes"/> 或 <see cref="EnchantCodes"/>),什么都没动。</summary>
        public int Code { get; internal set; }

        /// <summary>强化的详细结果(不是强化流程为 null)。</summary>
        public UpgradeResult Upgrade { get; internal set; }

        /// <summary>附魔的详细结果(不是附魔流程为 null)。</summary>
        public EnchantResult Enchant { get; internal set; }

        /// <summary>流程之后目标位置上的装备;被销毁或被拒绝时为 null(拒绝时原装备还在原位)。</summary>
        public ItemInfo Equipment { get; internal set; }

        /// <summary>销毁产物放进了背包的件数。</summary>
        public int ProductsAdded { get; internal set; }

        /// <summary>销毁产物背包放不下、丢掉的件数。</summary>
        public int ProductsLost { get; internal set; }
    }

    /// <summary>从存档还原装备个体状态的结果。</summary>
    public sealed class EquipStateReport
    {
        public EquipStateReport(int applied, int dropped)
        {
            Applied = applied;
            Dropped = dropped;
        }

        /// <summary>还原到装备上的条数。</summary>
        public int Applied { get; }

        /// <summary>丢掉的条数:对不上装备(编号变了、格子没了、部位变了)。</summary>
        public int Dropped { get; }
    }

    /// <summary>
    /// 强化会话:把规则接到 <see cref="HeroProgress"/>(背包、装备栏、金币)上。创建时把附加来源(附魔、套装)装到装备栏上,
    /// 此后 <c>progress.Stats</c> 与 <c>progress.WornStats</c> 自然带上附魔与套装的加成,<c>HeroProgress</c> 本身一点没改。
    /// <para>
    /// 接进宿主的写法:存档时 <c>var save = progress.Snapshot(); enhance.Capture(save);</c>;读档时 <c>progress.Restore(save, lookup); enhance.Restore(save);</c>
    /// (<c>Restore</c> 必须在 <c>progress.Restore</c> 之后,它把个体状态补回刚还原出来的装备上)。
    /// </para>
    /// </summary>
    public sealed class EnhanceSession
    {
        private readonly Func<int, ItemInfo> _lookup;

        public EnhanceSession(HeroProgress progress, Func<int, ItemInfo> lookup, UpgradeSettings settings, IDropRandom random = null)
        {
            Progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (Settings.Random == null)
            {
                Settings.Random = random ?? new DropRandom(Environment.TickCount);
            }
            Overlay = new EquipBonusOverlay(lookup);
            Overlay.Attach(progress.Worn);
        }

        public HeroProgress Progress { get; }

        public UpgradeSettings Settings { get; }

        /// <summary>装在装备栏上的附加来源(附魔、套装;要接强化等级、品级换算时设它的 <see cref="EquipBonusOverlay.InstanceBonus"/>)。</summary>
        public EquipBonusOverlay Overlay { get; }

        /// <summary>
        /// 能挡销毁的保护道具的物品编号,按服务端的取用次序排(靠前的先用);背包里有哪个就用哪个、扣一个。服务端写死的四个真实编号不入库,由宿主给。
        /// </summary>
        public IReadOnlyList<int> ProtectionCodes { get; set; } = new int[0];

        /// <summary>宝珠附魔的世界兼容判断(参数:装备、宝珠;服务端 <c>EnableWorld</c>,没有读到规则,不给就当通过)。</summary>
        public Func<ItemInfo, ItemInfo, bool> EnableWorld { get; set; }

        /// <summary>目标位置上的装备;没有返回 null。</summary>
        public ItemInfo ItemAt(EnhanceTarget target)
        {
            if (target.Place == EnhancePlace.Worn)
            {
                return Progress.Worn.Get(target.Index);
            }
            IReadOnlyList<ItemStack> tab = Progress.Bag.Tab(InventoryTab.Equipment);
            return (uint)target.Index < (uint)tab.Count ? tab[target.Index].Info : null;
        }

        /// <summary>现在穿着的各套套装的状态(穿了几件、总共几件、哪些档生效)。</summary>
        public IReadOnlyList<EquipSetState> ActiveSets()
        {
            return EquipSets.Evaluate(Progress.Worn.Items, _lookup);
        }

        /// <summary>强化窗口的数据:这次(装备 + 材料 + 可选的第二物品)能不能做、要花什么、几成把握。不改任何东西。</summary>
        public UpgradeWindow Preview(EnhanceTarget target, bool amplify, int materialCode, int ticketCode = 0)
        {
            ItemInfo equip = ItemAt(target);
            UpgradeRequest request = BuildRequest(equip, amplify, materialCode, ticketCode);
            ItemUpgrade.Plan plan = ItemUpgrade.Preview(Settings, request);
            var window = new UpgradeWindow
            {
                Code = plan.Code,
                Level = plan.Level,
                Amplified = plan.Amplified,
                Gold = Progress.Gold,
                MaterialOwned = materialCode == 0 ? 0 : Progress.Bag.CountOf(materialCode),
            };
            if (plan.Info != null)
            {
                window.GoldCost = plan.Gold;
                window.MaterialCode = plan.Info.MaterialCode;
                window.MaterialCount = plan.Info.MaterialCount;
                window.Jump = plan.Jump;
                if (plan.Jump)
                {
                    window.NextLevel = plan.JumpLevel;
                    window.SuccessChance = plan.JumpRate / 100.0;
                    window.PenaltyType = 0;
                }
                else
                {
                    window.NextLevel = plan.Level + 1;
                    window.FailThreshold = plan.Threshold;
                    window.SuccessChance = plan.Threshold <= 0 ? 1.0 : Math.Max(0.0, 1.0 - plan.Threshold / (double)(ItemUpgrade.ProbabilityDenominator + 1));
                    window.PenaltyType = equip != null && plan.Table != null ? ItemUpgrade.PenaltyTypeFor(plan.Table, equip, plan.Info, plan.Ticket, plan.Level) : 0;
                }
            }
            return window;
        }

        /// <summary>
        /// 强化一次:规则判通过之后先收金币,再扣票据与材料(失败也扣),按结果换装备(销毁则拿掉并把分解产物放进背包),被保护道具挡下时扣一个保护道具。
        /// 判不过什么都不动,<see cref="EnhanceOutcome.Code"/> 是错误码。
        /// </summary>
        public EnhanceOutcome Upgrade(EnhanceTarget target, bool amplify, int materialCode, int ticketCode = 0)
        {
            ItemInfo equip = ItemAt(target);
            UpgradeRequest request = BuildRequest(equip, amplify, materialCode, ticketCode);
            UpgradeResult result = ItemUpgrade.Run(Settings, request);
            if (result.Outcome == UpgradeOutcome.Rejected)
            {
                return new EnhanceOutcome(result.Code) { Upgrade = result };
            }
            if (result.GoldSpent > 0 && !Progress.TrySpendGold(result.GoldSpent))
            {
                return new EnhanceOutcome(UpgradeCodes.NotEnoughGold) { Upgrade = result };
            }
            if (result.TicketSpent)
            {
                Progress.Bag.Remove(ticketCode, 1);
            }
            Progress.Bag.Remove(request.Material.Code, result.MaterialSpent);
            if (result.ProtectionUsed != 0)
            {
                Progress.Bag.Remove(result.ProtectionUsed, 1);
            }
            var outcome = new EnhanceOutcome(0) { Upgrade = result };
            if (result.Destroyed)
            {
                RemoveTarget(target);
                if (result.Products != null)
                {
                    foreach (ItemStack product in result.Products)
                    {
                        int put = Progress.Bag.Add(product.Info, product.Count);
                        outcome.ProductsAdded += put;
                        outcome.ProductsLost += product.Count - put;
                    }
                }
            }
            else
            {
                Replace(target, result.Equipment);
                outcome.Equipment = result.Equipment;
            }
            return outcome;
        }

        /// <summary>
        /// 用宝珠给装备附魔(服务端 <c>OnEnchantByBead</c> 的流程):宝珠换出卡片,检查部位,覆盖旧卡,扣一个宝珠。宝珠必须在背包里。
        /// 判不过什么都不动。
        /// </summary>
        public EnhanceOutcome Enchant(EnhanceTarget target, int beadCode)
        {
            ItemInfo equip = ItemAt(target);
            ItemInfo bead = beadCode != 0 && Progress.Bag.CountOf(beadCode) > 0 ? _lookup(beadCode) : null;
            EnchantResult result = EnchantRules.ApplyBead(equip, bead, _lookup, EnableWorld);
            if (!result.Success)
            {
                return new EnhanceOutcome(result.Code) { Enchant = result };
            }
            Progress.Bag.Remove(beadCode, 1);
            Replace(target, result.Equipment);
            return new EnhanceOutcome(0) { Enchant = result, Equipment = result.Equipment };
        }

        private UpgradeRequest BuildRequest(ItemInfo equip, bool amplify, int materialCode, int ticketCode)
        {
            Inventory bag = Progress.Bag;
            ItemInfo material = materialCode != 0 && bag.CountOf(materialCode) > 0 ? _lookup(materialCode) : null;
            ItemInfo ticket = ticketCode != 0 && bag.CountOf(ticketCode) > 0 ? _lookup(ticketCode) : null;
            int protection = 0;
            foreach (int code in ProtectionCodes)
            {
                if (code != 0 && bag.CountOf(code) > 0)
                {
                    protection = code;
                    break;
                }
            }
            return new UpgradeRequest
            {
                Equipment = equip,
                RequestAmplify = amplify,
                Material = material,
                MaterialCount = material == null ? 0 : bag.CountOf(materialCode),
                Ticket = ticket,
                CharacterLevel = Progress.Level,
                Gold = Progress.Gold,
                FreeSlots = Math.Max(0, bag.Capacity - bag.Tab(InventoryTab.Material).Count),
                ProtectionCode = protection,
            };
        }

        private void Replace(EnhanceTarget target, ItemInfo item)
        {
            if (target.Place == EnhancePlace.Worn)
            {
                Progress.Worn.Restore(target.Index, item);
            }
            else
            {
                Progress.Bag.ReplaceAt(InventoryTab.Equipment, target.Index, item);
            }
        }

        private void RemoveTarget(EnhanceTarget target)
        {
            if (target.Place == EnhancePlace.Worn)
            {
                Progress.Worn.Take(target.Index);
            }
            else
            {
                Progress.Bag.TakeAt(InventoryTab.Equipment, target.Index);
            }
        }

        // ------------------------------------------------------------ 存档

        /// <summary>
        /// 把装备的个体状态写进存档(先清掉 <paramref name="save"/> 里原有的 <see cref="HeroSave.EquipStates"/>):
        /// 只写有状态的装备;背包里的按"同编号的第几件(背包装备页的次序)"定位,身上的按部位号定位——这样存档还原时背包少了几件找不到的装备,也不会错位。
        /// </summary>
        public void Capture(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            save.EquipStates.Clear();
            var seen = new Dictionary<int, int>();
            foreach (ItemStack stack in Progress.Bag.Tab(InventoryTab.Equipment))
            {
                int code = stack.Info.Code;
                seen.TryGetValue(code, out int nth);
                seen[code] = nth + 1;
                EquipInstance state = stack.Info.Instance;
                if (state != null && !state.IsDefault)
                {
                    save.EquipStates.Add(SavedEquipState.From(EnhancePlace.Bag, nth, code, state));
                }
            }
            for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
            {
                ItemInfo worn = Progress.Worn.Get(part);
                EquipInstance state = worn?.Instance;
                if (state != null && !state.IsDefault)
                {
                    save.EquipStates.Add(SavedEquipState.From(EnhancePlace.Worn, part, worn.Code, state));
                }
            }
        }

        /// <summary>
        /// 把存档里的个体状态补回已经还原好的背包与装备栏(先 <c>progress.Restore</c>)。每条按位置找到装备、编号对得上才补,对不上的丢掉并计数;
        /// 补的是同一份定义的个体拷贝,所以不影响别的同编号装备。
        /// </summary>
        public EquipStateReport Restore(HeroSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            int applied = 0;
            int dropped = 0;
            foreach (SavedEquipState saved in save.EquipStates)
            {
                EquipInstance state = saved.ToInstance();
                if (saved.Place == (int)EnhancePlace.Worn)
                {
                    ItemInfo worn = Progress.Worn.Get(saved.Index);
                    if (worn != null && worn.Code == saved.Code && worn.IsEquipment)
                    {
                        Progress.Worn.Restore(saved.Index, worn.Definition.WithInstance(state));
                        applied++;
                    }
                    else
                    {
                        dropped++;
                    }
                    continue;
                }
                int at = FindBagStack(saved.Code, saved.Index);
                if (at >= 0)
                {
                    ItemInfo info = Progress.Bag.Tab(InventoryTab.Equipment)[at].Info;
                    Progress.Bag.ReplaceAt(InventoryTab.Equipment, at, info.Definition.WithInstance(state));
                    applied++;
                }
                else
                {
                    dropped++;
                }
            }
            return new EquipStateReport(applied, dropped);
        }

        private int FindBagStack(int code, int nth)
        {
            IReadOnlyList<ItemStack> tab = Progress.Bag.Tab(InventoryTab.Equipment);
            int seen = 0;
            for (int i = 0; i < tab.Count; i++)
            {
                if (tab[i].Info.Code == code && tab[i].Info.IsEquipment)
                {
                    if (seen == nth)
                    {
                        return i;
                    }
                    seen++;
                }
            }
            return -1;
        }
    }
}
