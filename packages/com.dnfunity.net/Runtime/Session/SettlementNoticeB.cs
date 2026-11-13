using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 通关奖励通知(类 0、包号 0x23,包名表里叫 CLEAR_DUNGEON_REWARD,任务单叫"结算通知 B / 通关奖励")明文载荷解出的只读记录,
    /// 由 <see cref="SettlementNoticeBParser"/> 生成。没有任何可写成员,也不持有载荷数组:条目是值拷贝,各列表是只读包装。
    /// <para>
    /// 与通关结果(<see cref="SettlementNoticeA"/>,整队共用一份)不同,这条通知<b>逐接收者各发一份</b>:服务端对每个有效队员各自拼一份再单发,
    /// 同一队里各人收到的内容不同(经验分项、任务物品条目、各种标志都按这位接收者算)。
    /// </para>
    /// <para>
    /// 内容分四块,读序与来源见 <see cref="SettlementNoticeBParser"/>:(1)经验分项(<see cref="ExpValues"/> 与 <see cref="ExpKind"/>);
    /// (2)任务清图物品(<see cref="QuestItems"/>,服务端已经把这些物品放进了背包);(3)四个队员槽位的奖励信息(<see cref="Rewards"/>、
    /// <see cref="PremiumRewards"/>)及夹在中间的两个杂项字段;(4)尾部的比例与三个标志。
    /// 客户端处理器是虚拟化桩,读不出它怎么用这些字段【未】,所以全部语义来自服务端写出点的代码路径,
    /// 把握程度写在各成员上:【观】是读到的,【推】是按函数名与调用关系推断的,【未】是没读通的。
    /// </para>
    /// </summary>
    public sealed class SettlementNoticeB
    {
        internal SettlementNoticeB(int[] expValues, byte expKind, SettlementItemEntry[] questItems, SettlementRewardInfo[] rewards,
            int receiverValue, byte[] reservedBytes, SettlementRewardInfo[] premiumRewards, int percent,
            bool isReturnUserParty, bool isEventCharacParty, bool hasDeadUserPenalty, int trailingByteCount)
        {
            ExpValues = Array.AsReadOnly(expValues);
            ExpKind = expKind;
            QuestItems = Array.AsReadOnly(questItems);
            Rewards = Array.AsReadOnly(rewards);
            ReceiverValue = receiverValue;
            ReservedBytes = Array.AsReadOnly(reservedBytes);
            PremiumRewards = Array.AsReadOnly(premiumRewards);
            Percent = percent;
            IsReturnUserParty = isReturnUserParty;
            IsEventCharacParty = isEventCharacParty;
            HasDeadUserPenalty = hasDeadUserPenalty;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>经验分项的个数(线上先 4 个 i32,再 1 个 u8 的 <see cref="ExpKind"/>,再 12 个 i32,共 16 个 i32)。</summary>
        public const int ExpValueCount = 16;

        /// <summary>奖励信息槽位数(服务端按队员槽位 0 到 3 各写一份)。</summary>
        public const int RewardSlotCount = 4;

        /// <summary>
        /// 经验分项,16 个 i32,按线上顺序(<see cref="ExpValueCount"/> 个,不为 null)。服务端 <c>CParty::clear_reward</c> 对这位接收者算出一组"基础经验 × 各种加成比例",
        /// 逐项写出(写出点 0x085b01b3 到 0x085b04a5)【观】。下标 0 到 15 依次对应服务端的算式与参数来源【推,按函数名】:
        /// <list type="table">
        /// <item><term>0</term><description>基础经验(死亡惩罚折算之后)加一个"乘参数偏移 0x18"的分量(该参数没有找到写入点,按 0 理解)</description></item>
        /// <item><term>1</term><description>评分加成经验(基础经验 × 按评分档位取的比例,<c>GetExpBonusWeight</c>)</description></item>
        /// <item><term>2</term><description>由"队伍经验加成活动比例"折算出的一项,算式见 settlenoti_NOTES §4.2,含义没读通【未】</description></item>
        /// <item><term>3</term><description>头像套装类加成(<c>CalcAvatarBonus</c>,比例非 0 时至少为 1)</description></item>
        /// <item><term>4</term><description>活动类加成之和:按 <see cref="ExpKind"/> 选的一项(PC 房燃烧活动或双倍经验活动或全局效果)+ 服务端活动比例 + 疲劳燃烧活动</description></item>
        /// <item><term>5</term><description>经验道具加成(<c>GetExpAffectRate</c>)</description></item>
        /// <item><term>6</term><description>特权类加成(<c>GetAdvantageExpRate</c>)</description></item>
        /// <item><term>7</term><description>同一 PC 房队员加成</description></item>
        /// <item><term>8</term><description>频道加成(<c>channel_script_t::getBonusRate</c>)</description></item>
        /// <item><term>9</term><description>上位队员经验(<c>CUser::gainExpAsUpperMember</c> 的返回值)</description></item>
        /// <item><term>10</term><description>宠物类加成(<c>CalcCreatureBonus</c>,比例非 0 时至少为 1)</description></item>
        /// <item><term>11</term><description>回归用户队伍加成(比例 0.2)</description></item>
        /// <item><term>12</term><description>新角色活动加成(<c>EventNewCharacterReward::eventExpRatio</c>)</description></item>
        /// <item><term>13</term><description>"管理"类加成(<c>CParty::CalcurateManageBonus</c>)</description></item>
        /// <item><term>14</term><description>成长力经验(<c>CUser::get_growth_power_exp_reward</c>)</description></item>
        /// <item><term>15</term><description>特权类加成之二(<c>getAddExpRateFromPremium</c>)</description></item>
        /// </list>
        /// 注意:服务端给角色实际加的经验还包括公会技能经验与战力战奖励经验两项(<c>gainGuildSkillExp</c>、<c>gainPowerWarRewardExp</c>,0x085ad823、0x085ad83a),
        /// 它们<b>不在</b>这 16 项里,所以把 16 项相加不等于实际入账经验【观】。取值是有符号 32 位,不做范围检查。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<int> ExpValues { get; }

        /// <summary>
        /// 基础经验:<see cref="ExpValues"/> 的下标 0(便捷读法,含义与限定见 <see cref="ExpValues"/>)。
        /// 台账 E-124 的离线通关经验模型(<c>ClearReward.MemberBase</c>)算的就是这一份的主体【推】。
        /// </summary>
        public int BaseExp => ExpValues[0];

        /// <summary>
        /// 评分加成经验:<see cref="ExpValues"/> 的下标 1(便捷读法)。服务端算式是 基础经验 × <c>GetExpBonusWeight(评分档位)</c>(0x085add41、0x085ad324),
        /// 与台账 E-124 的 <c>ClearReward.RankBonus</c> 同一件事【推】。同一个值服务端还写进角色的"队伍加成经验"字段(<c>set_charac_party_bonus_exp</c>)。
        /// </summary>
        public int RankBonusExp => ExpValues[1];

        /// <summary>
        /// 活动类加成取的是哪一种(u8,线上第 5 个字段,夹在经验分项第 4 与第 5 项之间)。服务端取值【观】:0 没有、1 双倍经验活动(或疲劳燃烧活动)、
        /// 2 PC 房燃烧活动、4 全局效果(与前两者取大,0x085ad5bf 起);3 在这个函数里不会写出。含义是"<see cref="ExpValues"/> 下标 4 里那一项来自哪种活动"【推】。
        /// 解析器给原始字节,不做范围检查。
        /// </summary>
        public byte ExpKind { get; }

        /// <summary>
        /// 任务清图物品条目,按线上顺序(线上先 u8 个数,再按个数给条目,个数就是列表长度)。个数为 0 时是空列表,不为 null。
        /// 服务端在写这些条目<b>之前</b>就已经把物品放进了接收者的背包(<c>CInventory::insertItemIntoInventory</c> 0x085b07ab),放不进去的不写条目【观】,
        /// 所以条目是"已经入包的结果",不是待领取清单【推】。个数是一个字节,服务端实际条数上限 = 任务槽 20 个 × 每个任务清图列表长度,超过 255 时服务端的个数字节会回绕【推】。
        /// </summary>
        public IReadOnlyList<SettlementItemEntry> QuestItems { get; }

        /// <summary>
        /// 四个队员槽位(0 到 3)的奖励信息,按槽位顺序,恒为 <see cref="RewardSlotCount"/> 项。槽位无人时服务端只写状态 0,见 <see cref="SettlementRewardInfo"/>。
        /// 它们对应服务端 <c>CParty::clear_reward</c> 里按队员槽位准备好的奖励(队伍结构偏移 0x3a0 起每槽 0x7a 字节,写出点 0x085b0a00)【观】;
        /// 服务端里与它相邻的函数名带"卡片"(<c>SetCardNumber</c>、<c>IsSelectCard</c>、<c>CompleteCardSelect</c> 等),所以这是"通关结算卡片"一类的奖励【推】,
        /// 它与卡片选择流程的先后关系、客户端怎么展示、是否还要选牌都没有读通【未】。
        /// </summary>
        public IReadOnlyList<SettlementRewardInfo> Rewards { get; }

        /// <summary>
        /// 夹在两组奖励信息之间的 i32:服务端写队伍结构偏移 0x1f4 起的数组里"接收者槽位"那一项(0x085b0a32,地址 = 队伍 + 0x1f4 + 4 × 接收者槽位)【观】。
        /// 这个数组谁写、写什么没有读通(静态检索没有找到写入点)【未】,所以不起名字也不做解释:类型 i32,用法只能是"原样保存"。
        /// </summary>
        public int ReceiverValue { get; }

        /// <summary>
        /// 夹在 <see cref="ReceiverValue"/> 与 <see cref="PremiumRewards"/> 之间的 4 个字节。服务端的写点循环两个分支都写 0(0x085b0a6a、0x085b0a82),
        /// 所以恒为 0【观】;解析器给原始字节,不做检查。列表只读,恒 4 项。
        /// </summary>
        public IReadOnlyList<byte> ReservedBytes { get; }

        /// <summary>
        /// 第二组四个槽位的奖励信息(特权卡片一类,恒 <see cref="RewardSlotCount"/> 项)。服务端对"槽位有人且该队员的特权类型 0xc 生效"的槽位才写内容
        /// (<c>CUser::isAffectedPremium(…, 0xc)</c> 0x085b0af8、<c>_makeRewardItemInfo</c> 0x085b0b43),其余槽位只写状态 0【观】;
        /// 特权类型 0xc 具体是什么没读通【未】。数据在队伍结构偏移 0x588 起每槽 0x7a 字节。
        /// </summary>
        public IReadOnlyList<SettlementRewardInfo> PremiumRewards { get; }

        /// <summary>
        /// 一个比例(i32)。服务端算式是 队伍结构偏移 0xb0c 的单精度数 × 100 ÷ 队伍结构偏移 0xd0c 的整数,截断后写出(0x085b0b76..0x085b0bd0)【观】;
        /// 这两个队伍字段是什么没读通,所以含义不下结论【未】(按算式形状它是个百分数)。除数为 0 时的结果没有读(x87 浮点除零,值不确定)。
        /// </summary>
        public int Percent { get; }

        /// <summary>
        /// 标志一:这位接收者所在的队伍是回归用户队伍(<c>CParty::IsReturnUserParty</c>,0x085b0c1a)【观】。线上字节非 0 为 true(服务端只写 0 与 1)。
        /// 与经验分项下标 11 的"回归用户队伍加成"来自同一个判定【推】。
        /// </summary>
        public bool IsReturnUserParty { get; }

        /// <summary>
        /// 标志二:这位接收者所在的队伍是活动角色队伍(<c>CParty::IsEventCharacParty</c>,0x085b0ca6)【观】。线上字节非 0 为 true(服务端只写 0 与 1)。
        /// "活动角色"指什么没读通【未】。
        /// </summary>
        public bool IsEventCharacParty { get; }

        /// <summary>
        /// 标志三:死亡队员惩罚生效(服务端写的是"经验参数偏移 0 的单精度数不等于 0",0x085b0ced..0x085b0d11;该参数由 <c>CParty::calDeadUserDungeonPenalty</c> 填,
        /// 0x085add27)【观】。线上字节非 0 为 true(服务端只写 0 与 1)。"死亡队员惩罚"的名字来自函数名【推】:基础经验会按 (1 − 该数) 折算(0x085ad2bf)。
        /// </summary>
        public bool HasDeadUserPenalty { get; }

        /// <summary>
        /// 读完最后一个标志之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 真实帧里尾部可能有折叠哈希(载荷长度 1 到 0x190 时追加 4 字节;本通知最短 89 字节,常见长度在这个范围内)与会话加密补零;
        /// 这些都只体现在这个数里,只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 通关奖励通知里的一条任务清图物品:读序 u16 背包槽位、i32 物品编号、i32 附加信息、u16 耐久、u8 增幅类型、u16 增幅值(共 15 字节)。
    /// 只读值,字段取值不做范围检查。这是 15 字节的<b>短形</b>,与背包同步通知(类 0、包号 13)里 25 字节的物品条目是两种形状,不可混用
    /// (后者见 itemsync 线的笔记;这里只读了服务端写点,没有与实网样本对过)。
    /// </summary>
    public readonly struct SettlementItemEntry
    {
        internal SettlementItemEntry(ushort slot, int itemId, int data, ushort durability, byte amplifyType, ushort amplifyValue)
        {
            Slot = slot;
            ItemId = itemId;
            Data = data;
            Durability = durability;
            AmplifyType = amplifyType;
            AmplifyValue = amplifyValue;
        }

        /// <summary>
        /// 物品被放进的背包槽位(u16)。取自 <c>CInventory::insertItemIntoInventory</c> 的返回值(非负才写,<c>put_short</c> 0x085b07cd)【观】。
        /// </summary>
        public ushort Slot { get; }

        /// <summary>
        /// 物品编号(i32,有符号,不做范围检查;写出点 <c>put_int</c> 0x085b07e5)。来自任务的清图物品表(<c>Quest::get_clear_itemlist</c> 的第一项,0x085b060d)【观】。
        /// </summary>
        public int ItemId { get; }

        /// <summary>
        /// 附加信息(i32,有符号,不做范围检查;<c>Inven_Item::get_add_info</c> 0x085b07f3,<c>put_int</c> 0x085b0805)。
        /// 创建物品时清图物品表的第二项(<c>get_clear_itemlist</c> 的第二项)被当作数量参数传入 <c>CItemList::create_item</c>(0x085b06cb)【观】,
        /// 所以对可叠加物品它是数量【推】;其余物品的含义同背包物品的"附加信息"字段。
        /// </summary>
        public int Data { get; }

        /// <summary>耐久(u16;服务端取新建物品结构偏移 0xb 的 16 位,<c>put_short</c> 0x085b0821)【观】。</summary>
        public ushort Durability { get; }

        /// <summary>增幅类型(u8;<c>stAmplifyOption_t::getAbilityType</c>,<c>put_byte</c> 0x085b0847)【观】。含义由背包物品的增幅选项决定,这里只给原始字节。</summary>
        public byte AmplifyType { get; }

        /// <summary>增幅值(u16;<c>stAmplifyOption_t::getAbilityValue</c> 的低 16 位,<c>put_short</c> 0x085b086d)【观】。</summary>
        public ushort AmplifyValue { get; }
    }

    /// <summary>
    /// 通关奖励通知里一个队员槽位的"奖励信息"。线上形状由首字节 <see cref="Status"/> 决定,三种,没有别的取值:
    /// <list type="bullet">
    /// <item><description>0:只有这 1 个字节(槽位无人,或第二组里该队员没有生效的特权)。</description></item>
    /// <item><description>1:状态 + i32 保留位(服务端写 0)+ i32 <see cref="Value"/>,共 9 字节(没有物品奖励,或有物品但背包放不下被丢弃)。</description></item>
    /// <item><description>2:状态 + i32 保留位 + i32 <see cref="Value"/> + i32 <see cref="ItemId"/> + i32 <see cref="ItemData"/>,共 17 字节(物品已放进背包)。</description></item>
    /// </list>
    /// 只读值,字段取值不做范围检查;状态不在 0、1、2 里时整份通知读不下去(无法确定后续长度),解析器返回 false。
    /// </summary>
    public readonly struct SettlementRewardInfo
    {
        internal SettlementRewardInfo(byte status, int reserved, int value, int itemId, int itemData)
        {
            Status = status;
            Reserved = reserved;
            Value = value;
            ItemId = itemId;
            ItemData = itemData;
        }

        /// <summary>
        /// 状态(u8)。服务端写点【观】:0 来自槽位无人或特权未生效的分支(<c>put_byte(0)</c> 0x085b09b7、0x085b0acc、0x085b0b5b);
        /// 1 与 2 来自 <c>CParty::_makeRewardItemInfo</c> 0x085ad0be:物品成功放进背包写 2(0x085ad1ae),没有物品写 1(0x085ad1fb),
        /// 有物品但 <c>tryInsertItemIntoInventory</c> 失败也写 1 并清掉物品(0x085ad1c3)。
        /// </summary>
        public byte Status { get; }

        /// <summary>
        /// 保留位(i32;状态 1、2 才有,状态 0 时为 0)。服务端在 <see cref="Value"/> 之前固定写 0(<c>put_int(0)</c> 0x085ad20e)【观】;含义不明【未】。
        /// </summary>
        public int Reserved { get; }

        /// <summary>
        /// 奖励的数值(i32;状态 1、2 才有,状态 0 时为 0)。服务端写该槽位奖励结构里<b>第一件物品的附加信息</b>(<c>Inven_Item::get_add_info</c> 0x085ad219,
        /// <c>put_int</c> 0x085ad228)【观】。这件物品来自奖励生成器(<c>getGenerator(2)</c>,0x085ae4bb 与 0x085ae568)的结果,<c>clear_reward</c> 随后先按死亡队员惩罚把
        /// 它的附加信息乘 (1 − 惩罚)(0x085aee48..0x085aeea9),再乘一个百分比系数(0x085aeed6..0x085aef58);结果集合里还有专门加金币的
        /// <c>increase_result_card_gold</c>(0x085ae59d)。按这些用法它是金币数量【推,没有实网样本核对】。
        /// </summary>
        public int Value { get; }

        /// <summary>是不是带物品(状态为 2)。</summary>
        public bool HasItem => Status == 2;

        /// <summary>物品编号(i32;只有状态 2 才有,其余为 0)。服务端写该槽位奖励结构里<b>第二件物品</b>的编号(<c>put_int</c> 0x085ad24f)【观】。</summary>
        public int ItemId { get; }

        /// <summary>物品附加信息(i32;只有状态 2 才有,其余为 0)。第二件物品的 <c>get_add_info</c>(0x085ad25d,<c>put_int</c> 0x085ad26c)【观】;可叠加物品时是数量【推】。</summary>
        public int ItemData { get; }
    }
}
