using System;
using System.Collections.Generic;

// 服务端静态读码(df_game_r.orig)的分解链。
//   分解入口:DisPatcher_DisJointItem::dispatch_sig 0x081f9fb8(命令 0x1c,没有机主 → 旧版 DisJoint,便携分解器与技能路线)与
//             Dispatcher_RequestDisjointItem::process 0x081cfcac(命令 0xef,机主按编号找,可以是自己);共用 _disjoint 0x081f92ca。
//   能不能分解:_check_disjointable_item_state 0x081f8ba1 → _checkDisjointableItem 0x081fa0f6(ENUM_ERROR 编号见 ExpertJobError)。
//   产物:CDisjointer::get_disjoint_result 0x082347ec(包装:把"请求者就是机主"当参数传进去)→ get_disjoint_result_ 0x085d234a;
//         附加产物挑选 get_additional_item 0x0896d6ab;预览 GetPredictResult 0x085d1f68。
// 观察(要点,细节看各方法的注释):
//   · 键 (机器档, 稀有度, 未鉴定) 在 .exj 的 [disjoint result] 里找行;找不到:未鉴定回 0xd3,否则回 0x11。
//   · 主产物个数 = max(1, 向零取整((1000 × 收购价(向零取整(1.1 × 物品价值)) ÷ [base const]) × 行系数 ÷ 1000))。
//   · 附加产物:先抽 0 到 9999 的数与"大成功万分比"比,小于就走大成功组、否则走普通组;再抽 0 到 9999 在组内按权重挑,个数 = max(1, 向零取整(物品等级 ÷ 除数))。
//   · 只有"请求者就是机主"才有:机器档加成表加成、穿戴加成、自用额外产物(稀有度不低于 2 时)、产物个数百分比变化;别人的分解机只出主产物与普通/大成功附加产物。
//   · 随机数抽取次序:big(10000)、附加挑选(10000)、(机主且稀有度不低于 2 时)自用(100)。
// 观察(汇编 0x085d246c 起):浮点运算在 32 位 x87 上做,整数用 FILD 精确装入,乘除的另一个操作数是单精度,取整前把舍入方式改成"向零"(FNSTCW 后把控制字的舍入位置成 11b)。
// 推断:x87 扩展精度的 64 位尾数装得下"32 位整数 × 24 位单精度"的乘积,所以这里用大整数把同一个算式精确地算出来再向零取整(<see cref="ExactFloat"/>),不依赖平台浮点宽度;
//       服务端先存回单精度的几步(大成功加成、附加产物个数、产物个数变化率)也按单精度存。
namespace Dnf.Game
{
    /// <summary>分解用的随机数来源。<c>Next(n)</c> 返回 0 到 n−1(服务端的 <c>get_rand_int(n)</c>),n 至少为 1。</summary>
    public interface IDisjointRandom
    {
        int Next(int exclusiveMax);
    }

    /// <summary>用 <see cref="Random"/> 的随机来源(游戏里用;测试用脚本化的来源)。</summary>
    public sealed class SystemDisjointRandom : IDisjointRandom
    {
        private readonly Random _random;

        public SystemDisjointRandom()
        {
            _random = new Random();
        }

        public SystemDisjointRandom(int seed)
        {
            _random = new Random(seed);
        }

        public int Next(int exclusiveMax) => exclusiveMax <= 1 ? 0 : _random.Next(exclusiveMax);
    }

    /// <summary><c>[attach type]</c> 的值与服务端编号(<c>ImportEquipmentScript</c> 0x08991aeb 里"字符串 → 赋值"链):free 0、trade 1、trade delete 2、sealing 3、sealing trade 4、account 5。</summary>
    public static class AttachTypes
    {
        public const int Free = 0;
        public const int Trade = 1;
        public const int TradeDelete = 2;
        public const int Sealing = 3;
        public const int SealingTrade = 4;
        public const int Account = 5;

        /// <summary>没写或不认得时的编号(服务端构造函数的缺省没有读到,这里用 −1:不等于任何一种)。</summary>
        public const int Unknown = -1;

        public static int FromTag(string tag)
        {
            if (tag != null)
            {
                if (tag.Equals("[free]", StringComparison.OrdinalIgnoreCase))
                {
                    return Free;
                }
                if (tag.Equals("[trade]", StringComparison.OrdinalIgnoreCase))
                {
                    return Trade;
                }
                if (tag.Equals("[trade delete]", StringComparison.OrdinalIgnoreCase))
                {
                    return TradeDelete;
                }
                if (tag.Equals("[sealing]", StringComparison.OrdinalIgnoreCase))
                {
                    return Sealing;
                }
                if (tag.Equals("[sealing trade]", StringComparison.OrdinalIgnoreCase))
                {
                    return SealingTrade;
                }
                if (tag.Equals("[account]", StringComparison.OrdinalIgnoreCase))
                {
                    return Account;
                }
            }
            return Unknown;
        }
    }

    /// <summary>
    /// 分解的取舍策略。服务端的通用分解路径只拒绝宠物(<c>Inven_Item</c> 类型 5)与非装备类;时装(类型 8)和神器(类型 6)在这条路径上过得了
    /// <c>isEquipableItemType</c>(<c>CEquipItem::make_item</c> 0x0851098a 按部位给类型:22 宠物 → 5,23 到 25 神器 → 6,时装 → 8,其余 → 1),全库也没有给它们写 "disjoint" 禁分解标记。
    /// 但时装另有单独的分解入口(<c>Dispatcher_DisJointAvatar::dispatch_sig</c> 0x08217c06,出的是徽章,不在这一线),神器是宠物装备,
    /// 所以默认策略(<see cref="Default"/>)把两者挡在通用路径之外;<see cref="ServerParity"/> 复现服务端原样的放行,用于对拍。
    /// </summary>
    public sealed class DisjointPolicy
    {
        /// <summary>默认:时装与神器不走通用分解。</summary>
        public static readonly DisjointPolicy Default = new DisjointPolicy(false, false);

        /// <summary>与服务端通用路径一致:只拒宠物与非装备类。</summary>
        public static readonly DisjointPolicy ServerParity = new DisjointPolicy(true, true);

        public DisjointPolicy(bool allowAvatar, bool allowArtifact)
        {
            AllowAvatar = allowAvatar;
            AllowArtifact = allowArtifact;
        }

        public bool AllowAvatar { get; }

        public bool AllowArtifact { get; }
    }

    /// <summary>分解规则要看的物品信息(从 <see cref="ItemInfo"/> 与 <see cref="ExpertJobItemTags"/> 抽出来,方便不带数据测试)。</summary>
    public sealed class DisjointItem
    {
        /// <summary>物品编号;0 表示没有物品。</summary>
        public int Code { get; set; }

        /// <summary>是装备类的物品(服务端 <c>isEquipableItemType</c> 认的类型);可叠加物品为 false。</summary>
        public bool IsEquipment { get; set; }

        /// <summary>装备部位(<see cref="EquipmentParts"/>);不是装备时无意义。</summary>
        public int Part { get; set; } = EquipmentParts.Unknown;

        /// <summary><c>[attach type]</c> 的服务端编号(<see cref="AttachTypes"/>)。</summary>
        public int AttachType { get; set; } = AttachTypes.Unknown;

        /// <summary>脚本 <c>[impossible contents]</c> 含 "disjoint"。</summary>
        public bool ImpossibleDisjoint { get; set; }

        /// <summary>是否已鉴定(服务端 <c>stAmplifyOption_t::isIdentified</c>;这个项目的物品栏没有鉴定状态,缺省已鉴定)。</summary>
        public bool Identified { get; set; } = true;

        /// <summary><c>[rarity]</c>。</summary>
        public int Rarity { get; set; }

        /// <summary><c>[grade]</c>(物品等级,附加产物的个数除以它)。</summary>
        public int Grade { get; set; }

        /// <summary><c>[minimum level]</c>(服务端 <c>getUsableLevel</c>)。</summary>
        public int UsableLevel { get; set; }

        /// <summary><c>[value]</c>(服务端 <c>GetSellPrice</c> 读的就是它)。</summary>
        public int Value { get; set; }

        /// <summary><c>[random option]</c>:分解主产物算价格时选"下标 2 那一档"的开关(服务端 <c>IsRandomOption</c>)。</summary>
        public bool RandomOption { get; set; }

        public bool IsCreature => IsEquipment && Part == EquipmentParts.Creature;

        public bool IsArtifact => IsEquipment && Part >= EquipmentParts.ArtifactRed && Part <= EquipmentParts.ArtifactGreen;

        public bool IsAvatar => IsEquipment && EquipmentParts.IsAvatar(Part);

        /// <summary>从物品信息和副职业标签建一个候选(<paramref name="tags"/> 为 null 当没有标签)。</summary>
        public static DisjointItem From(ItemInfo info, ExpertJobItemTags tags, bool identified = true)
        {
            if (info == null)
            {
                return new DisjointItem();
            }
            ExpertJobItemTags t = tags ?? ExpertJobItemTags.None;
            return new DisjointItem
            {
                Code = info.Code,
                IsEquipment = info.IsEquipment,
                Part = info.Part,
                AttachType = AttachTypes.FromTag(info.AttachType),
                ImpossibleDisjoint = t.ImpossibleDisjoint,
                Identified = identified,
                Rarity = info.Rarity,
                Grade = info.Grade,
                UsableLevel = info.MinimumLevel,
                Value = info.Value,
                RandomOption = t.RandomOption,
            };
        }
    }

    /// <summary>一次分解的环境:谁的机器、是不是自己用、机主的加成。</summary>
    public sealed class DisjointContext
    {
        /// <summary>机主的机器档(<c>CCharacExpertJob::get_disjoint_machine_grade</c>)。</summary>
        public int MachineGrade { get; set; }

        /// <summary>请求者就是机主(<c>CDisjointer::isMine</c> 0x08234854:机主 == 请求者)。别人的分解机只出基础产物。</summary>
        public bool IsMine { get; set; }

        /// <summary>机主在线且有角色(服务端 <c>get_state() &gt; 2</c> 且当前角色存在);只有它为真才计入穿戴加成与产物个数变化。</summary>
        public bool OwnerActive { get; set; }

        /// <summary><c>GetExpertJobSelfDisjointBigWinRate(3, 机主副职业等级)</c>:穿戴给的大成功百分点。</summary>
        public float OwnerBigWinBonus { get; set; }

        /// <summary><c>GetExpertJobSelfDisjointResultVariation(3, 机主副职业等级)</c>:产物个数的百分比变化。</summary>
        public float OwnerResultVariation { get; set; }

        /// <summary>价格倍率表(为 null 用替身 <see cref="ItemPriceRates.Neutral"/>)。</summary>
        public ItemPriceRates PriceRates { get; set; }
    }

    /// <summary>
    /// 一次分解的产物:下标 0 主产物、1 附加产物、2 自用额外产物、3 与 4 服务端的结果结构里留着但没用到。
    /// 与服务端 <c>DisJointResult</c> 的对应:个数在 <c>[0..4]</c>,物品编号在 <c>[5..9]</c>。
    /// </summary>
    public sealed class DisjointOutcome
    {
        /// <summary>产物槽数。</summary>
        public const int Slots = 5;

        public ExpertJobError Error { get; internal set; }

        public bool Ok => Error == ExpertJobError.Ok;

        public int[] Counts { get; } = new int[Slots];

        public int[] Items { get; } = new int[Slots];

        /// <summary>有物品的槽(物品编号不为 0 且个数大于 0),按槽号顺序;同一种物品可能在两个槽里出现。</summary>
        public List<KeyValuePair<int, int>> Products()
        {
            var list = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < Slots; i++)
            {
                if (Items[i] != 0 && Counts[i] > 0)
                {
                    list.Add(new KeyValuePair<int, int>(Items[i], Counts[i]));
                }
            }
            return list;
        }
    }

    /// <summary>分解的规则:能不能分解、产物是什么、预览。</summary>
    public static class DisjointRules
    {
        /// <summary>随机数范围:大成功与组内挑选用万分比。</summary>
        public const int Scale = 10000;

        /// <summary>主产物价格先乘的系数(服务端常量 0x08cc080c,单精度 1.1)。</summary>
        internal const float ValueFactor = 1.1f;

        /// <summary>主产物个数最后除的常数(服务端常量 0x08cc0810,单精度 1000)。</summary>
        internal const float Thousand = 1000.0f;

        /// <summary>
        /// 物品类型这一关(<c>_check_disjointable_item_state</c> 0x081f8ba1 的前半):宠物 → 0x11;不是装备类或没有物品 → 0x11;
        /// 策略挡下的时装、神器 → 0x11(见 <see cref="DisjointPolicy"/>)。便携分解器的序号检查在这一关之后、<see cref="CheckItemRules"/> 之前。
        /// </summary>
        public static ExpertJobError CheckItemType(DisjointItem item, DisjointPolicy policy = null)
        {
            if (item == null || item.IsCreature)
            {
                return ExpertJobError.InvalidItem;
            }
            if (!item.IsEquipment || item.Code == 0)
            {
                return ExpertJobError.InvalidItem;
            }
            DisjointPolicy p = policy ?? DisjointPolicy.Default;
            if ((item.IsAvatar && !p.AllowAvatar) || (item.IsArtifact && !p.AllowArtifact))
            {
                return ExpertJobError.InvalidItem;
            }
            return ExpertJobError.Ok;
        }

        /// <summary>
        /// 物品规则这一关(<c>_checkDisjointableItem</c> 0x081fa0f6):未鉴定且没有分解机 → 0xae;脚本禁分解 → 0x13;[attach type] 是 [trade delete] → 0x11。
        /// </summary>
        public static ExpertJobError CheckItemRules(DisjointItem item, bool hasMachine)
        {
            if (item == null)
            {
                return ExpertJobError.InvalidItem;
            }
            if (!item.Identified && !hasMachine)
            {
                return ExpertJobError.Unidentified;
            }
            if (item.ImpossibleDisjoint)
            {
                return ExpertJobError.NotPermitted;
            }
            if (item.AttachType == AttachTypes.TradeDelete)
            {
                return ExpertJobError.InvalidItem;
            }
            return ExpertJobError.Ok;
        }

        /// <summary>两关合起来(没有便携分解器的机器路线):先 <see cref="CheckItemType"/> 再 <see cref="CheckItemRules"/>。</summary>
        public static ExpertJobError CheckItem(DisjointItem item, bool hasMachine, DisjointPolicy policy = null)
        {
            ExpertJobError type = CheckItemType(item, policy);
            return type != ExpertJobError.Ok ? type : CheckItemRules(item, hasMachine);
        }

        /// <summary>
        /// 便携分解器能不能分解这件物品(<c>_check_disjointable_item_state</c> 的便携部分):序号为 −1 或没有登记 → 1;
        /// 物品要求等级为 0 时看物品等级,否则看要求等级,超过便携分解器的最高等级 → 0xd。
        /// </summary>
        public static ExpertJobError CheckPortable(ExpertJobScript script, int portableIndex, DisjointItem item)
        {
            if (portableIndex == -1)
            {
                return ExpertJobError.LimitReached;
            }
            int maxLevel = script == null ? -1 : script.GetPortableLevel(portableIndex);
            if (maxLevel == -1)
            {
                return ExpertJobError.LimitReached;
            }
            if (item == null)
            {
                return ExpertJobError.InvalidItem;
            }
            if (item.UsableLevel == 0)
            {
                if (maxLevel < item.Grade)
                {
                    return ExpertJobError.LevelTooHigh;
                }
            }
            else if (maxLevel < item.UsableLevel)
            {
                return ExpertJobError.LevelTooHigh;
            }
            return ExpertJobError.Ok;
        }

        /// <summary>
        /// 算分解产物(<c>get_disjoint_result_</c> 0x085d234a)。<paramref name="rng"/> 的抽取次序见本文件开头;错误时产物槽全空。
        /// 脚本没写 <c>[base const]</c>(为 0)时服务端会除以 0 崩溃,这里回 0x13。
        /// </summary>
        public static DisjointOutcome Compute(ExpertJobScript script, ExpertJobEtc etc, DisjointItem item, DisjointContext ctx, IDisjointRandom rng)
        {
            if (script == null)
            {
                throw new ArgumentNullException(nameof(script));
            }
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            if (ctx == null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (rng == null)
            {
                throw new ArgumentNullException(nameof(rng));
            }
            ExpertJobEtc e = etc ?? ExpertJobEtc.Empty;
            var o = new DisjointOutcome();
            var key = new DisjointKey(ctx.MachineGrade, item.Rarity, !item.Identified);
            if (!script.TryGetRow(key, out DisjointRow row))
            {
                o.Error = item.Identified ? ExpertJobError.InvalidItem : ExpertJobError.UnidentifiedNoResult;
                return o;
            }
            if (script.BaseConst == 0)
            {
                o.Error = ExpertJobError.NotPermitted;
                return o;
            }

            // 主产物:价格先乘 1.1 取整,再按收购价(下标 2 档或 0 档)算,乘 1000 除基数,乘行系数除 1000;个数至少 1。
            o.Items[0] = row.Item;
            int value = ExactFloat.TruncMul(item.Value, ValueFactor);
            ItemPriceRates rates = ctx.PriceRates ?? ItemPriceRates.Neutral;
            int sell = ItemPricing.UnitSellPrice(value, item.RandomOption ? rates.KiriProtectable : rates.Normal);
            int basis = unchecked(sell * 1000) / script.BaseConst;
            int count = ExactFloat.TruncMulDiv(basis, row.Factor, Thousand);
            o.Counts[0] = count < 1 ? 1 : count;

            // 大成功万分比:行的百分数 × 100;请求者就是机主时再加机器档加成与穿戴加成。
            int bigWin = row.BigWinPercent * 100;
            if (ctx.IsMine)
            {
                bigWin += e.BigWinRate(ctx.MachineGrade + 1) * 100;
                if (ctx.OwnerActive)
                {
                    float bonus = 100.0f * ctx.OwnerBigWinBonus;
                    bigWin = ExactFloat.TruncAdd(bigWin, bonus);
                }
            }

            int roll = rng.Next(Scale);
            IReadOnlyList<SelectRate> group = roll < bigWin ? row.BigWin : row.Additional;
            int pick = rng.Next(Scale);
            if (PickAdditional(group, pick, item.Grade, out SelectRate chosen) && chosen.Item != -1)
            {
                o.Items[1] = chosen.Item;
                int n = ToInt((float)item.Grade / chosen.Divisor);
                o.Counts[1] = n < 1 ? 1 : n;
            }

            if (!ctx.IsMine)
            {
                return o;
            }

            // 自用额外产物:稀有度不低于 2 才抽,抽中 [disjoint self service] 的个数与物品。
            if (item.Rarity >= 2 && rng.Next(100) < script.SelfService.Chance)
            {
                o.Counts[2] = script.SelfService.Count;
                o.Items[2] = script.SelfService.Item;
            }

            // 产物个数的百分比变化:每个槽的个数加上 向零取整(个数 × 变化 ÷ 100)。
            if (ctx.OwnerActive)
            {
                float variation = ctx.OwnerResultVariation / 100.0f;
                for (int i = 0; i < DisjointOutcome.Slots; i++)
                {
                    o.Counts[i] += ExactFloat.TruncMul(o.Counts[i], variation);
                }
            }
            return o;
        }

        /// <summary>
        /// 在一组附加产物里挑一项(<c>get_additional_item</c> 0x0896d6ab):按书写次序逐项把这一项"抄进结果",
        /// 在等级范围内时,<paramref name="roll"/> 不大于权重就选它,否则从 roll 里减去权重继续;一项也没选中时结果停在<b>最后一项</b>(即使它不在等级范围内)。
        /// 空组返回 false(结果保持"没有")。
        /// </summary>
        public static bool PickAdditional(IReadOnlyList<SelectRate> group, int roll, int grade, out SelectRate chosen)
        {
            chosen = default;
            if (group == null || group.Count == 0)
            {
                return false;
            }
            foreach (SelectRate entry in group)
            {
                chosen = entry;
                if (entry.MinGrade <= grade && grade <= entry.MaxGrade)
                {
                    if (roll <= entry.Weight)
                    {
                        return true;
                    }
                    roll -= entry.Weight;
                }
            }
            return true;
        }

        /// <summary>
        /// 预览这件物品可能分解出什么(<c>CDisjointer::GetPredictResult</c> 0x085d1f68):自己的机器且稀有度大于 1 时先放自用额外产物(不去重),
        /// 找到行就放主产物(不去重)、再依次放普通组与大成功组里还没出现过的物品。
        /// </summary>
        public static List<int> PredictItems(ExpertJobScript script, DisjointItem item, DisjointContext ctx)
        {
            var list = new List<int>();
            if (script == null || item == null || ctx == null)
            {
                return list;
            }
            if (ctx.IsMine && item.Rarity > 1)
            {
                list.Add(script.SelfService.Item);
            }
            var key = new DisjointKey(ctx.MachineGrade, item.Rarity, !item.Identified);
            if (script.TryGetRow(key, out DisjointRow row))
            {
                list.Add(row.Item);
                foreach (SelectRate s in row.Additional)
                {
                    if (!list.Contains(s.Item))
                    {
                        list.Add(s.Item);
                    }
                }
                foreach (SelectRate s in row.BigWin)
                {
                    if (!list.Contains(s.Item))
                    {
                        list.Add(s.Item);
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// 向零取整成 32 位整数;超出范围或不是数时给 <see cref="int.MinValue"/>(x87 的 <c>fistp</c> 在这种情况下给的"整数不定值"),
        /// 免得结果随平台变。
        /// </summary>
        internal static int ToInt(double v)
        {
            if (double.IsNaN(v) || v >= 2147483648.0 || v <= -2147483649.0)
            {
                return int.MinValue;
            }
            return (int)v;
        }
    }
}
