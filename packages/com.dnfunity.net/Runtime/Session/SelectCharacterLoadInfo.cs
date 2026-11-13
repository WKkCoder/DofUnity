using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 选角成功回包(类 1、包号 4,成功标志非 0)里"选角后角色杂项"的只读记录,由 <see cref="SelectCharacterResultParser"/> 生成。
    /// 没有任何可写成员,也不持有载荷数组:字段是值拷贝,各列表都是只读包装(转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>)。
    /// <para>
    /// 命名:属性名取自服务端写这些字段时调用的访问器名(<c>Inter_LoadEtc::SendResult</c> 0x84c2b82..0x84c311a 里的 getXxx,符号表上的事实【观】),
    /// 不是业务命名。字段"在业务上叫什么"是推断【推】,有几处还是未知【未】(都写在各属性的说明里);语义命名另行拍板,
    /// 目前没有外部使用者,改名是纯机械操作。笔记里的字段代号(A1、B2、E1……)只是笔记的指代,不进公开名字。
    /// 读序与每个字段的客户端落点见 packetread_NOTES §3.3、§3.4(该笔记被 .gitignore 忽略,不入库);
    /// 数值类字段一律给线上原值,不做范围检查,也不复刻客户端的钳制与丢弃。
    /// </para>
    /// </summary>
    public sealed class SelectCharacterLoadInfo
    {
        internal SelectCharacterLoadInfo(
            uint regTime, uint createTime, ushort uniqueId,
            ushort totalFatigue, ushort totalMaxFatigue, ushort usedFatigue,
            SelectCharacterPremium[] premiums, int cera,
            SelectCharacterQuestSlot[] questSlots, uint[] questNotifyValues,
            byte village, byte tutorialSkipable, byte[] tutorialFlagIndices,
            ushort fatigueBatteryCharging, ushort fatigueGrownUpBuff, byte tradePunishOverThirtyDays)
        {
            RegTime = regTime;
            CreateTime = createTime;
            UniqueId = uniqueId;
            TotalFatigue = totalFatigue;
            TotalMaxFatigue = totalMaxFatigue;
            UsedFatigue = usedFatigue;
            Premiums = Array.AsReadOnly(premiums);
            Cera = cera;
            QuestSlots = Array.AsReadOnly(questSlots);
            QuestNotifyValues = Array.AsReadOnly(questNotifyValues);
            Village = village;
            TutorialSkipable = tutorialSkipable;
            TutorialFlagIndices = Array.AsReadOnly(tutorialFlagIndices);
            FatigueBatteryCharging = fatigueBatteryCharging;
            FatigueGrownUpBuff = fatigueGrownUpBuff;
            TradePunishOverThirtyDays = tradePunishOverThirtyDays;
        }

        /// <summary>
        /// 读序第 1 个 u32。服务端写 <c>getRegTime()</c>【观】:只由登录信息分派处 <c>setRegTime(long)</c> 写入,值取自 DB 登录信息结构,本地服务端不换算。
        /// 客户端读后丢弃:处理器内除读调用与初始清零外无任何引用【观】。线索是"注册时间一类"【推】,单位与历元未知(换算发生在本地没有的 DB 进程,样本取值不像 unix 秒)【未】。
        /// </summary>
        public uint RegTime { get; }

        /// <summary>
        /// 读序第 2 个 u32。服务端写 <c>getCurCharacCreateTime()</c>【观】,客户端存进一个全局(0x1ac7a04)【观】。
        /// 线索是"角色创建时间"【推】;样本取值像 unix 秒(大于 2^30),但这只是形态,不是换算依据。
        /// </summary>
        public uint CreateTime { get; }

        /// <summary>
        /// 读序第 1 个 u16。服务端写 <c>get_unique_id()</c>【观】:来自 <c>CUserGlobalInfoHandle::get_uniqueid()</c>,在 0..0xfffe 的字节标志表里找第一个空位置位并返回下标,登出时清位【观】。
        /// 所以它是在线连接的槽号:会话内唯一、不持久、登出后可复用【推】——<b>不是</b>选中角色的编号(台账 E-382 把这类字段口头概括成"选中角色编号",以读码为准)。
        /// </summary>
        public ushort UniqueId { get; }

        /// <summary>读序第 2 个 u16。服务端写 <c>getCurCharacTotalFatigue()</c>【观】;线索是"疲劳(当前量一类)"【推】。</summary>
        public ushort TotalFatigue { get; }

        /// <summary>读序第 3 个 u16。服务端写 <c>getCurCharacTotalMaxFatigue()</c>【观】;线索是"疲劳上限一类"【推】。样本里 <see cref="TotalFatigue"/> 与 <see cref="UsedFatigue"/> 都不大于它,但这不是解析的合法性条件。</summary>
        public ushort TotalMaxFatigue { get; }

        /// <summary>读序第 4 个 u16。服务端写 <c>getCurCharacUsedFatigue()</c>【观】;线索是"已用疲劳一类"【推】。</summary>
        public ushort UsedFatigue { get; }

        /// <summary>
        /// 特权项列表,按线上顺序(先 u8 个数 N,再 N 条 {u8 类型, 剩余秒};个数就是列表长度)。没有时为空列表,不为 null。
        /// 服务端遍历 <c>GetPremiumInfoList</c>,跳过两类条目后写 <c>put_byte(id)</c>、<c>put_int(expireSec - getCurSec())</c>【观】,不按到期过滤。
        /// </summary>
        public IReadOnlyList<SelectCharacterPremium> Premiums { get; }

        /// <summary>
        /// 读序里的那个 u32 金额型字段,按有符号给出。服务端写 <c>GetCera()</c>【观】;客户端按有符号处理、为负则重写为 0【观】。
        /// 解析器只给带符号的原始值,不复刻"负值钳 0"。线索是"一个金额型整数"【推】。
        /// </summary>
        public int Cera { get; }

        /// <summary>
        /// 进行中任务表,固定 20 项,按线上顺序(第 j 项的键与值总是成对写出,空槽的值照样存在,不能跳过)。列表长度恒为 20。
        /// 服务端写 <c>UserQuest</c> 里两个 20 项 dword 数组【观】;线索是"进行中任务表"【推】,客户端向量上限 20 与之吻合。
        /// </summary>
        public IReadOnlyList<SelectCharacterQuestSlot> QuestSlots { get; }

        /// <summary>
        /// 5 个 u32,线上顺序,列表长度恒为 5。服务端写 <c>UserQuest</c> 里一个 10 项 dword 块的前 5 项【观】;
        /// 客户端把非零且未在自己向量里的项追加进"任务提醒"向量并刷新界面【观】,线索是"任务提醒号一类"【推】。
        /// 块的后 5 项用途未知,本协议路径下写入恒为 0、也不下发【未】。解析器只给原始值,不判断"非零才有效"。
        /// </summary>
        public IReadOnlyList<uint> QuestNotifyValues { get; }

        /// <summary>
        /// 读序里 5 个 u32 之后的那个 u8。服务端写 <c>getCurCharacVill()</c>【观】;客户端把它交给两处调用(0x1135b50、0x1116020)【观】。
        /// 线索是"所在城镇/村庄号"【推】。解析器只给原始字节;既有的进城快照里"区域编号"的缺口(见 <see cref="TownEntrySnapshot"/>)正对着它,本版不去回填。
        /// </summary>
        public byte Village { get; }

        /// <summary>
        /// 教程相关的第一个 u8。服务端写 <c>GetTutorialSkipable()</c>【观】;客户端读后丢弃【观】。线索是"教程可跳过标志"【推】。
        /// </summary>
        public byte TutorialSkipable { get; }

        /// <summary>
        /// 教程完成标记的"置位下标集合",按线上顺序(先 u8 个数 b,再 b 个 u8;个数就是列表长度)。没有时为空列表,不为 null。
        /// 服务端扫 0..0x7f,对 <c>getCurCharacEscaladeTutorialFlag(i)</c> 为真的下标写出【观】,所以最多 128 个且严格升序(升序不是合法性条件)。
        /// 解析器按服务端语义读满 b 个;原版客户端只读 min(b, 0x4e) 个,b 超过 0x4e 时它自己会错位,那是客户端行为,不模拟。
        /// </summary>
        public IReadOnlyList<byte> TutorialFlagIndices { get; }

        /// <summary>读序倒数第 3 项(u16)。服务端写 <c>GetFatigueBatteryCharging()</c>【观】;线索是"疲劳相关"【推】。</summary>
        public ushort FatigueBatteryCharging { get; }

        /// <summary>读序倒数第 2 项(u16)。服务端写 <c>getCurCharacFatigueGrownUpBuff()</c>【观】;线索是"疲劳相关"【推】。</summary>
        public ushort FatigueGrownUpBuff { get; }

        /// <summary>
        /// 读序最后一项(u8)。服务端写 <c>isTradePunishOverThirtyDays()</c>【观】;客户端取值为 1 时弹一个提示窗口(0x18c)【观】。
        /// 线索是"交易惩罚相关的提示开关"【推】。解析器只给原始字节,不布尔化。
        /// </summary>
        public byte TradePunishOverThirtyDays { get; }
    }

    /// <summary>
    /// 选角成功回包里特权项列表的一条:读序 u8 类型、i32 剩余秒(共 5 字节)。只读值。
    /// </summary>
    public readonly struct SelectCharacterPremium
    {
        internal SelectCharacterPremium(byte id, int remainingSeconds)
        {
            Id = id;
            RemainingSeconds = remainingSeconds;
        }

        /// <summary>类型(u8)。服务端写条目的 id 的低 8 位【观】;客户端按它分两路处理(类型 5 与 0xc 走一路,其余走另一路)【观】。</summary>
        public byte Id { get; }

        /// <summary>
        /// 剩余秒(4 字节,按有符号给出)。服务端写 <c>entry.expireSec - getCurSec()</c>,即"到期秒减发送时的当前秒"【观】,
        /// 服务端不过滤已到期的条目,所以差值可以是 0 或负数。
        /// 解析器只给带符号的差值,<b>不判断是否已到期</b>:"恰好为 0 算到期还是未到期"、负值怎么处理,读码没有给出依据【未】,
        /// 笔记里也没有给出客户端消费这个值时的有符号处理【未】,由用到它的上层定。
        /// </summary>
        public int RemainingSeconds { get; }
    }

    /// <summary>
    /// 选角成功回包里进行中任务表的一项:读序 u16 键、u32 值(共 6 字节)。只读值。
    /// </summary>
    public readonly struct SelectCharacterQuestSlot
    {
        /// <summary>空槽的键。服务端在键为 0 时写它,客户端读了不处理【观】;空槽的值照样存在。</summary>
        public const ushort EmptyKey = 0xFFFF;

        internal SelectCharacterQuestSlot(ushort key, uint value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>键(u16)。线索是"任务号"【推】;<see cref="EmptyKey"/> 表示空槽。</summary>
        public ushort Key { get; }

        /// <summary>
        /// 值(u32,原样保存)。服务端写的是打包的触发计数:三个 9 位计数 c0 + (c1 &lt;&lt; 9) + (c2 &lt;&lt; 18),非 0 表示进行中【观】/【推】;
        /// 三个计数在业务上各是什么未知【未】,所以不拆。空槽的值通常是 0,但解析器不依赖它。
        /// </summary>
        public uint Value { get; }

        /// <summary>键是不是 <see cref="EmptyKey"/>(客户端对这样的槽不处理)。</summary>
        public bool IsEmpty => Key == EmptyKey;
    }
}
