using System;
using System.Collections.Generic;

// 证据(服务端静态读码,没有运行;地址是 df_game_r.orig 的虚拟地址,写死在服务端里的真实物品编号与表里的数值不入库):
// 入口 WongWork::CItemUpgrade::ProcUpgrade 0x0854779c 的次序:装备格空→4、材料格空→0x16、按材料算"直升等级与成功率"(_getUpgradeCountByUpgradeItem 0x08548e68、
// _getUpgradeRateByUpgradeItem 0x08548ffa)、第二物品(票据)在 CDataManager+0x4d0 的登记表里查(找不到→9,强化等级不在 [下限,上限]→8)、按"有没有增幅属性"选普通表或增幅表、
// _GetUpgradeInfo 0x0854678a(等级≥31 或取不到行→0xd)、_CheckCondition 0x08546342(见 Check)、增幅请求与装备状态不符→0x17;
// 抽签 _DoProcUpgrade 0x0854755a(分母 100000,CItemUpgrade 构造 0x085461c8 写入;r = randInt(分母) 含上界,r 小于阈值算失败);
// 失败走 _ProcPenalty 0x08546bba(类型 1 不动、2 降级、3 销毁或被保护);付费 use_money 在抽签之后。
namespace Dnf.Game
{
    /// <summary>服务端强化流程的错误码(<c>ProcUpgrade</c> 返回值)。0 表示流程走完(成功或失败都是 0)。</summary>
    public static class UpgradeCodes
    {
        public const int Ok = 0;

        /// <summary>装备格空,或物品不是这件装备(服务端核对编号)。</summary>
        public const int NoEquipment = 4;

        /// <summary>耐久没满(强化要求耐久满)。</summary>
        public const int Durability = 7;

        /// <summary>票据要求的强化等级区间不含装备当前等级。</summary>
        public const int TicketLevel = 8;

        /// <summary>第二物品不在票据登记表里。</summary>
        public const int TicketUnknown = 9;

        /// <summary>金币不够。</summary>
        public const int NotEnoughGold = 10;

        /// <summary>强化等级到头(≥ 31,或表里没有这一行)。</summary>
        public const int MaxLevel = 0xd;

        /// <summary>这件东西不能这样强化(不是装备、部位不对、角色等级不在区间、增幅券用在没增幅的装备上……)。</summary>
        public const int NotUpgradable = 0x13;

        /// <summary>背包空位不够放销毁产物。</summary>
        public const int NeedSlots = 0x15;

        /// <summary>材料不对或不够。</summary>
        public const int Material = 0x16;

        /// <summary>普通强化请求与增幅装备(或反过来)不符。</summary>
        public const int ModeMismatch = 0x17;

        /// <summary>稀有度的最大强化等级已到。</summary>
        public const int LevelCap = 0x5f;

        /// <summary>销毁被保护道具挡下(装备保留、强化等级归零)。这是失败流程的一个正常结果,不是拒绝。</summary>
        public const int Protected = 0xd8;

        /// <summary>增幅属性还没鉴定。</summary>
        public const int NotIdentified = 0xae;
    }

    /// <summary>第二物品(票据)的登记项(服务端 <c>stUpgradeItemTicketParam_t</c>):能用于哪段强化等级、失败时怎么降级,以及两个写死的特例的数据化。</summary>
    public sealed class UpgradeTicketDef
    {
        public UpgradeTicketDef(int minLevel, int maxLevel, int flag, bool freeGold = false, int normalMaterialCount = 0, int amplifyMaterialCount = 0)
        {
            MinLevel = minLevel;
            MaxLevel = maxLevel;
            Flag = flag;
            FreeGold = freeGold;
            NormalMaterialCount = normalMaterialCount;
            AmplifyMaterialCount = amplifyMaterialCount;
        }

        /// <summary>装备当前强化等级的下限(含)。</summary>
        public int MinLevel { get; }

        /// <summary>上限(含)。</summary>
        public int MaxLevel { get; }

        /// <summary>
        /// 失败时的降级标志(有符号字节):0 表示不惩罚;大于 0 表示每次失败降这么多级;小于 0 表示失败归零
        /// (服务端 <c>_ProcPenalty</c>:有票据时类型 = 标志为 0 ? 1 : 2,类型 2 里 <c>标志 ≥ 0</c> 才按数目降)。
        /// </summary>
        public int Flag { get; }

        /// <summary>用它强化不收金币(服务端对两个写死的票据编号把金币费用改成 0;编号不入库,所以做成登记项的属性)。</summary>
        public bool FreeGold { get; }

        /// <summary>用它做第二物品时材料数量改成这个数(普通强化;0 表示不改)。服务端对两个写死的票据编号改成 50 与 100 一类的数。</summary>
        public int NormalMaterialCount { get; }

        /// <summary>同上,增幅强化。</summary>
        public int AmplifyMaterialCount { get; }
    }

    /// <summary>一次强化要用到的环境:两张表、随机源、票据登记、保护道具与几处"佩戴称号"一类的外部调整。</summary>
    public sealed class UpgradeSettings
    {
        /// <summary>普通强化表(没有增幅属性的装备走它)。</summary>
        public UpgradeTable NormalTable { get; set; }

        /// <summary>增幅强化表(有增幅属性的装备走它;没有增幅这一步可以不给)。</summary>
        public UpgradeTable AmplifyTable { get; set; }

        /// <summary>抽签用的随机源(<see cref="IDropRandom.NextInclusive"/> 含上界,与服务端 <c>CMTRand::randInt</c> 相同)。</summary>
        public IDropRandom Random { get; set; }

        /// <summary>第二物品登记表(物品编号 → 登记项)。服务端这张表的来源没有读到,由调用方给;不给时第二物品一律"不在登记表里"(9)。</summary>
        public IReadOnlyDictionary<int, UpgradeTicketDef> Tickets { get; set; }

        /// <summary>
        /// 旧式写死的直升材料(物品编号 → 直升到的强化等级,成功率 100%,只对没有增幅属性的装备生效):服务端有六个这样的真实编号,编号不入库,由调用方给。
        /// </summary>
        public IReadOnlyDictionary<int, int> LegacyJumpMaterials { get; set; }

        /// <summary>
        /// 失败走销毁档时,预测这件装备分解会出几种产物(服务端 <c>DisJoint::getPredictResultItemTypeCount</c>);
        /// 要的空位 = 它 + 1(+ 1 若有增幅属性)。不给按 0(分解产物归副职业线)。
        /// </summary>
        public Func<ItemInfo, int> PredictDestroyTypes { get; set; }

        /// <summary>销毁实际给的分解产物(<c>DisJoint::GetResult</c> 与追加物品,归副职业线);不给就没有产物。参数:装备、销毁前的强化等级。</summary>
        public Func<ItemInfo, int, IReadOnlyList<ItemStack>> DestroyProducts { get; set; }

        /// <summary>佩戴在称号格的装备带来的"强化成功率增加"(<c>getUpgradeProbIncrease</c>,失败阈值减去它);没有为 0。</summary>
        public int TitleProbIncrease { get; set; }

        /// <summary>佩戴在称号格的装备带来的"强化费用折扣"百分数(<c>getUpgradeCostDiscount</c>,费用变成 费用 − 折扣 × 费用 ÷ 100);没有为 0。</summary>
        public float TitleCostDiscount { get; set; }

        /// <summary>是否有称号格装备(决定上面两项要不要生效;服务端是"取称号格物品、查得到才用")。</summary>
        public bool HasTitleItem { get; set; }

        /// <summary>活动的"强化费用减免"百分数(只对没有增幅属性的装备;0 表示没有活动)。</summary>
        public int EventCostReducePercent { get; set; }
    }

    /// <summary>一次强化的输入:谁、用什么、有多少金币和空位。</summary>
    public sealed class UpgradeRequest
    {
        /// <summary>要强化的装备(定义或个体拷贝)。</summary>
        public ItemInfo Equipment { get; set; }

        /// <summary>请求的是增幅强化(包体里的标志字节为 1);必须与装备有没有增幅属性一致,否则 0x17。</summary>
        public bool RequestAmplify { get; set; }

        /// <summary>材料格里的物品;空格为 null。</summary>
        public ItemInfo Material { get; set; }

        /// <summary>材料格里有几个。</summary>
        public int MaterialCount { get; set; }

        /// <summary>第二物品(票据),没有为 null;用一次扣一个。</summary>
        public ItemInfo Ticket { get; set; }

        /// <summary>角色等级(强化等级区间与成长型装备的换算用)。</summary>
        public int CharacterLevel { get; set; }

        /// <summary>背包里的金币。</summary>
        public long Gold { get; set; }

        /// <summary>装备页还有几个空格(销毁档要放分解产物)。不给按够用。</summary>
        public int FreeSlots { get; set; } = int.MaxValue;

        /// <summary>背包里有一件能挡销毁的保护道具时,它的物品编号(0 表示没有);只对没有增幅属性的装备有用。</summary>
        public int ProtectionCode { get; set; }
    }

    /// <summary>强化的结局。</summary>
    public enum UpgradeOutcome
    {
        /// <summary>前置检查没过:什么都没动、什么都没扣(<see cref="UpgradeResult.Code"/> 是错误码)。</summary>
        Rejected,

        /// <summary>成功(强化等级加一,或用直升材料直接到了目标等级)。</summary>
        Success,

        /// <summary>失败(抽中了失败区间;惩罚见 <see cref="UpgradeResult.Penalty"/>)。直升材料没抽中也算失败,但没有惩罚。</summary>
        Failure,
    }

    /// <summary>一次强化的结果:新的装备、扣了什么、抽到了什么。</summary>
    public sealed class UpgradeResult
    {
        public UpgradeOutcome Outcome { get; internal set; }

        /// <summary>0 表示流程走完;被拒绝时是错误码(<see cref="UpgradeCodes"/>);销毁被保护道具挡下时是 <see cref="UpgradeCodes.Protected"/>。</summary>
        public int Code { get; internal set; }

        /// <summary>流程走完之后的装备(拒绝时是原来的;销毁时为 null)。</summary>
        public ItemInfo Equipment { get; internal set; }

        public int OldLevel { get; internal set; }

        public int NewLevel { get; internal set; }

        /// <summary>失败时走的惩罚类型(1 不动、2 降级、3 销毁或被挡);没有失败为 0。</summary>
        public int Penalty { get; internal set; }

        /// <summary>装备被销毁了。</summary>
        public bool Destroyed { get; internal set; }

        /// <summary>销毁被保护道具挡下(装备还在,强化等级归零)。</summary>
        public bool Protected { get; internal set; }

        /// <summary>用掉的保护道具编号(没用 0)。</summary>
        public int ProtectionUsed { get; internal set; }

        public long GoldSpent { get; internal set; }

        public int MaterialSpent { get; internal set; }

        public bool TicketSpent { get; internal set; }

        /// <summary>这次用的是直升材料(强化券一类)。</summary>
        public bool Jump { get; internal set; }

        /// <summary>抽到的数(失败阈值那一抽,或直升的 0 到 99 那一抽);没抽 −1。</summary>
        public int Roll { get; internal set; } = -1;

        /// <summary>失败阈值(没有抽签为 0)。</summary>
        public long Threshold { get; internal set; }

        /// <summary>这次取到的那一行(拒绝得早时为 null)。</summary>
        public UpgradeInfo Info { get; internal set; }

        /// <summary>销毁给的分解产物。</summary>
        public IReadOnlyList<ItemStack> Products { get; internal set; }

        public bool Success => Outcome == UpgradeOutcome.Success;
    }

    /// <summary>强化的规则:把服务端 <c>ProcUpgrade</c> 的判断次序与结果原样搬过来,不碰背包与金币(调用方按结果去扣,见 <c>EnhanceSession</c>)。</summary>
    public static class ItemUpgrade
    {
        /// <summary>失败抽签的分母(服务端 <c>CItemUpgrade</c> 构造里写入的常量):表里的失败概率是它的分子。</summary>
        public const int ProbabilityDenominator = 100000;

        /// <summary>直升材料的抽签:0 到 99 里抽,小于成功率百分数算成功。</summary>
        public const int JumpRollMax = 99;

        /// <summary>前置检查的结果(不抽签、不改任何东西):错误码为 0 表示这次可以做,并带着取到的表行与要付的金币。</summary>
        public sealed class Plan
        {
            internal Plan()
            {
            }

            /// <summary>0 表示可以做;否则是 <see cref="UpgradeCodes"/>。</summary>
            public int Code { get; internal set; }

            /// <summary>取到的那一行(拒绝得早时为 null);材料、金币已按直升材料与票据的特例改过。</summary>
            public UpgradeInfo Info { get; internal set; }

            /// <summary>实际要付的金币(活动减免与称号折扣之后;直升材料为 0)。</summary>
            public long Gold { get; internal set; }

            /// <summary>用的是直升材料(强化券一类)。</summary>
            public bool Jump { get; internal set; }

            /// <summary>直升材料要直达的强化等级(不是直升为 0)。</summary>
            public int JumpLevel { get; internal set; }

            /// <summary>直升材料的成功率百分数(0 到 100)。</summary>
            public int JumpRate { get; internal set; }

            /// <summary>第二物品的登记项(没有为 null)。</summary>
            public UpgradeTicketDef Ticket { get; internal set; }

            /// <summary>这次要走的表(普通或增幅)。</summary>
            public UpgradeTable Table { get; internal set; }

            /// <summary>当前强化等级。</summary>
            public int Level { get; internal set; }

            /// <summary>装备有增幅属性。</summary>
            public bool Amplified { get; internal set; }

            /// <summary>
            /// 失败阈值(称号格装备的"成功率增加"已扣掉);直升材料没有这个概念,为 0。
            /// 抽 0 到 <see cref="ProbabilityDenominator"/>(含上界),小于它就是失败;为 0 时必定成功。
            /// </summary>
            public long Threshold { get; internal set; }
        }

        /// <summary>只做前置检查:这次强化能不能做、会取哪一行、收多少金币、失败阈值是多少(窗口预览用)。</summary>
        public static Plan Preview(UpgradeSettings settings, UpgradeRequest request)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Prepare(settings, request);
        }

        /// <summary>按服务端的次序检查并执行一次强化(检查不过什么都不动,见 <see cref="UpgradeResult.Code"/>)。</summary>
        public static UpgradeResult Run(UpgradeSettings settings, UpgradeRequest request)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            Plan plan = Prepare(settings, request);
            ItemInfo equip = request.Equipment;
            if (plan.Code != 0)
            {
                return Reject(equip, plan.Level, plan.Code, plan.Info);
            }
            UpgradeInfo info = plan.Info;
            int level = plan.Level;
            var result = new UpgradeResult
            {
                OldLevel = level,
                NewLevel = level,
                Equipment = equip,
                Info = info,
                Jump = plan.Jump,
                MaterialSpent = info.MaterialCount,
                TicketSpent = plan.Ticket != null,
            };
            IDropRandom random = settings.Random ?? throw new InvalidOperationException("强化需要随机源(UpgradeSettings.Random)");

            bool success;
            bool penalize = false;
            if (plan.Jump)
            {
                int roll = random.NextInclusive(JumpRollMax);
                result.Roll = roll;
                success = roll < plan.JumpRate;
                if (success)
                {
                    result.NewLevel = plan.JumpLevel;
                }
                result.GoldSpent = 0;
            }
            else
            {
                long threshold = plan.Threshold;
                result.Threshold = threshold;
                bool failed = false;
                if (threshold != 0)
                {
                    int roll = random.NextInclusive(ProbabilityDenominator);
                    result.Roll = roll;
                    failed = roll < threshold;
                }
                success = !failed;
                if (success)
                {
                    result.NewLevel = level + 1;
                }
                penalize = failed;
                result.GoldSpent = plan.Gold;
            }

            result.Outcome = success ? UpgradeOutcome.Success : UpgradeOutcome.Failure;
            EquipInstance next = equip.StateOrDefault;
            next.Upgrade = result.NewLevel;
            if (penalize)
            {
                ApplyPenalty(settings, request, equip, plan.Table, info, plan.Ticket, level, plan.Amplified, next, result);
            }
            if (!result.Destroyed)
            {
                next.Upgrade = result.NewLevel;
                result.Equipment = equip.WithInstance(next);
            }
            else
            {
                result.Equipment = null;
            }
            return result;
        }

        private static Plan Prepare(UpgradeSettings settings, UpgradeRequest request)
        {
            var plan = new Plan();
            ItemInfo equip = request.Equipment;
            if (equip == null)
            {
                plan.Code = UpgradeCodes.NoEquipment;
                return plan;
            }
            EquipInstance state = equip.Instance;
            int level = state != null ? state.Upgrade : 0;
            bool amplified = state != null && state.Amplified;
            bool identified = state == null || state.Identified;
            bool damaged = state != null && state.Damaged;
            plan.Level = level;
            plan.Amplified = amplified;
            if (!equip.IsEquipment)
            {
                plan.Code = UpgradeCodes.NotUpgradable;
                return plan;
            }

            // 材料格空
            ItemInfo material = request.Material;
            if (material == null || request.MaterialCount <= 0)
            {
                plan.Code = UpgradeCodes.Material;
                return plan;
            }

            // 材料算出"直升等级"与"成功率"
            int jumpLevel = JumpLevelOf(settings, material, amplified, out int jumpRate, out int jumpError);
            if (jumpError != 0)
            {
                plan.Code = jumpError;
                return plan;
            }

            // 第二物品(票据)
            UpgradeTicketDef ticket = null;
            if (request.Ticket != null)
            {
                if (settings.Tickets == null || !settings.Tickets.TryGetValue(request.Ticket.Code, out ticket))
                {
                    plan.Code = UpgradeCodes.TicketUnknown;
                    return plan;
                }
                if (level < ticket.MinLevel || ticket.MaxLevel < level)
                {
                    plan.Code = UpgradeCodes.TicketLevel;
                    return plan;
                }
            }
            plan.Ticket = ticket;

            // 选表、取行
            UpgradeTable table = amplified ? settings.AmplifyTable : settings.NormalTable;
            UpgradeInfo info = null;
            if (table != null && level < EquipInstance.MaxUpgrade)
            {
                info = table.GetInfo(equip.Rarity, equip.Grade, level, equip.Part);
            }
            plan.Table = table;
            if (info == null)
            {
                plan.Code = UpgradeCodes.MaxLevel;
                return plan;
            }
            plan.Info = info;

            // 直升材料:材料就是它自己、一个、不收金币
            if (jumpLevel != 0)
            {
                info.MaterialCode = material.Code;
                info.MaterialCount = 1;
                info.GoldCost = 0;
            }
            if (ticket != null)
            {
                if (ticket.FreeGold)
                {
                    info.GoldCost = 0;
                }
                int over = request.RequestAmplify ? ticket.AmplifyMaterialCount : ticket.NormalMaterialCount;
                if (over > 0)
                {
                    info.MaterialCount = over;
                }
            }
            plan.Jump = jumpLevel != 0;
            plan.JumpLevel = jumpLevel;
            plan.JumpRate = jumpRate;

            // 要付的金币先算好(预览窗口在材料不够、金币不够时也要显示"要多少"),再按次序做检查
            long cost = PayableGold(settings, info.GoldCost, amplified);
            plan.Gold = cost;
            int code = Check(settings, request, equip, level, amplified, identified, damaged, table, info, cost);
            if (code == 0 && request.RequestAmplify != amplified)
            {
                code = UpgradeCodes.ModeMismatch;
            }
            plan.Code = code;
            if (!plan.Jump)
            {
                long threshold = info.FailChance;
                if (settings.HasTitleItem && threshold != 0)
                {
                    // 服务端用无符号数相减:减过头会绕回成很大的数,大于分母就按 0(必定成功)
                    uint adjusted = unchecked((uint)threshold - (uint)settings.TitleProbIncrease);
                    threshold = adjusted > ProbabilityDenominator ? 0 : adjusted;
                }
                plan.Threshold = threshold;
            }
            return plan;
        }

        /// <summary>
        /// 材料算出的直升等级(0 表示不是直升材料)与成功率。次序同服务端:没有增幅属性时先认旧式写死的材料(成功率 100);
        /// 材料是强化券就取券上的数,增幅券只能用在有增幅属性的装备上(否则 0x13)。
        /// </summary>
        private static int JumpLevelOf(UpgradeSettings settings, ItemInfo material, bool amplified, out int rate, out int error)
        {
            int count = 0;
            rate = 0;
            error = 0;
            int legacyLevel = 0;
            bool legacy = !amplified && settings.LegacyJumpMaterials != null && settings.LegacyJumpMaterials.TryGetValue(material.Code, out legacyLevel);
            if (legacy)
            {
                // 旧式材料:等级取登记值、成功率 100(服务端成功率函数在这里直接返回,不再看后面的券)
                count = legacyLevel;
                rate = 100;
            }
            EnhanceData data = material.IsStackable ? material.Enhance : null;
            if (data == null)
            {
                return count;
            }
            if (data.ReinforceTicket.HasValue)
            {
                ReinforceTicket t = data.ReinforceTicket.Value;
                count = ClampLevel(t.Level);
                if (!legacy)
                {
                    rate = ClampRate(t.RatePercent);
                }
            }
            else if (data.AmplifyTicket.HasValue)
            {
                if (amplified)
                {
                    ReinforceTicket t = data.AmplifyTicket.Value;
                    count = ClampLevel(t.Level);
                    rate = ClampRate(t.RatePercent);
                }
                else
                {
                    error = UpgradeCodes.NotUpgradable;
                    count = 0;
                    rate = 0;
                }
            }
            return count;
        }

        private static int ClampLevel(int level) => level < 0 || level > EquipInstance.MaxUpgrade ? 0 : level;

        private static int ClampRate(int rate) => rate < 0 || rate > 100 ? 0 : rate;

        /// <summary>
        /// <c>_CheckCondition</c> 0x08546342:按顺序检查,返回第一个不通过的错误码(0 通过);<paramref name="cost"/> 是实际要付的金币(<see cref="PayableGold"/>)。
        /// 次序:未鉴定 → 材料编号 → 材料数量 → 金币 → 部位范围 → 称号部位 → 耐久 → 稀有度封顶 → 销毁档空位 → 角色等级区间。
        /// 服务端在金币之后还有一道"强化等级不小于表的行数 → 0xd",但取行(<c>_GetUpgradeInfo</c>)在它之前已经把这种情形挡掉了,走不到,这里不重复。
        /// </summary>
        private static int Check(UpgradeSettings s, UpgradeRequest r, ItemInfo equip, int level, bool amplified, bool identified, bool damaged,
            UpgradeTable table, UpgradeInfo info, long cost)
        {
            if (amplified && !identified)
            {
                return UpgradeCodes.NotIdentified;
            }
            if (r.Material.Code != info.MaterialCode || r.MaterialCount < info.MaterialCount)
            {
                return UpgradeCodes.Material;
            }
            if (r.Gold < cost)
            {
                return UpgradeCodes.NotEnoughGold;
            }
            int part = equip.Part;
            if (part < UpgradeTable.FirstPart || part > UpgradeTable.LastPart || part == EquipmentParts.TitleName)
            {
                return UpgradeCodes.NotUpgradable;
            }
            if (damaged)
            {
                return UpgradeCodes.Durability;
            }
            int cap = table.MaxLevel(equip.Rarity);
            if (cap != -1 && level >= cap)
            {
                return UpgradeCodes.LevelCap;
            }
            if (table.PenaltyTypeAt(level, equip.Rarity) == 3)
            {
                int need = (s.PredictDestroyTypes != null ? s.PredictDestroyTypes(equip) : 0) + 1 + (amplified ? 1 : 0);
                if (r.FreeSlots < need)
                {
                    return UpgradeCodes.NeedSlots;
                }
            }
            UpgradeLevelRange? range = equip.Enhance?.RangeOf(amplified ? 2 : 1);
            if (range.HasValue && !range.Value.Contains(r.CharacterLevel))
            {
                return UpgradeCodes.NotUpgradable;
            }
            return 0;
        }

        /// <summary>
        /// 实际要付的金币:活动减免(只对没有增幅属性的装备:费用 − 费用 × 百分数 ÷ 100,整数除法,不低于 0),
        /// 再按称号格装备的折扣 <c>(int)(折扣 × 费用 ÷ −100 + 费用)</c>(x87 扩展精度,截断)。
        /// </summary>
        public static long PayableGold(UpgradeSettings s, int baseCost, bool amplified)
        {
            int cost = baseCost;
            if (!amplified && s.EventCostReducePercent > 0)
            {
                cost = baseCost - (int)((long)baseCost * s.EventCostReducePercent / 100);
                if (cost < 0)
                {
                    cost = 0;
                }
            }
            if (s.HasTitleItem)
            {
                X87Value discounted = X87.Add(
                    X87.Div(X87.Mul(X87.FromSingle(s.TitleCostDiscount), X87.FromInt(cost)), X87.FromSingle(-100f)),
                    X87.FromInt(cost));
                cost = X87.TruncateToInt(discounted);
            }
            return cost;
        }

        /// <summary>
        /// 失败时走哪一档惩罚(<c>_ProcPenalty</c> 开头):没有第二物品时看表(<see cref="UpgradeTable.DestroyLevel"/> 没有或强化等级还没到,用表行的类型,到了就是销毁 3);
        /// 有第二物品时 = 它的降级标志为 0 ? 1(不动) : 2(降级)。
        /// </summary>
        public static int PenaltyTypeFor(UpgradeTable table, ItemInfo equip, UpgradeInfo info, UpgradeTicketDef ticket, int level)
        {
            if (ticket != null)
            {
                return ticket.Flag == 0 ? 1 : 2;
            }
            int destroy = table.DestroyLevel(equip.Rarity);
            return destroy == -1 || level < destroy ? info.PenaltyType : 3;
        }

        /// <summary><c>_ProcPenalty</c> 0x08546bba。</summary>
        private static void ApplyPenalty(UpgradeSettings s, UpgradeRequest r, ItemInfo equip, UpgradeTable table, UpgradeInfo info,
            UpgradeTicketDef ticket, int level, bool amplified, EquipInstance next, UpgradeResult result)
        {
            int type = PenaltyTypeFor(table, equip, info, ticket, level);
            result.Penalty = type;
            if (type == 1)
            {
                next.Upgrade = level;
                result.NewLevel = level;
            }
            else if (type == 2)
            {
                int n = info.PenaltyLevels;
                int flag = ticket != null ? ticket.Flag : 0;
                int now;
                if (n < level && flag >= 0)
                {
                    // 服务端:标志不大于 0(这里就是 0)降 n 级,标志为正降标志那么多级
                    now = flag < 1 ? level - n : level - flag;
                }
                else
                {
                    now = 0;
                }
                if (now < 0)
                {
                    now = 0;
                }
                next.Upgrade = now;
                result.NewLevel = now;
            }
            else if (type == 3)
            {
                if (!amplified && r.ProtectionCode != 0)
                {
                    // 保护道具:扣一个,装备留着,强化等级归零
                    result.ProtectionUsed = r.ProtectionCode;
                    result.Protected = true;
                    result.Code = UpgradeCodes.Protected;
                    next.Upgrade = 0;
                    result.NewLevel = 0;
                }
                else
                {
                    result.Destroyed = true;
                    result.NewLevel = 0;
                    result.Products = s.DestroyProducts != null ? s.DestroyProducts(equip, level) : new ItemStack[0];
                }
            }
        }

        private static UpgradeResult Reject(ItemInfo equip, int level, int code, UpgradeInfo info = null)
        {
            return new UpgradeResult
            {
                Outcome = UpgradeOutcome.Rejected,
                Code = code,
                Equipment = equip,
                OldLevel = level,
                NewLevel = level,
                Info = info,
            };
        }
    }
}
