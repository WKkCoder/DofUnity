using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 通关结果通知(类 0、包号 0x22,包名表里叫 PLAY_RESULT,任务单叫"结算通知 A / 通关结果")明文载荷解出的只读记录,
    /// 由 <see cref="SettlementNoticeAParser"/> 生成。没有任何可写成员,也不持有载荷数组:条目是值拷贝,<see cref="Entries"/> 是只读包装。
    /// <para>
    /// 内容:服务端给整队发的"评分结果"——评分值、通关用时、用时分、客户端上报的基础分的回显、任务迷宫标志,
    /// 以及一张"每位在队队员的个人最佳通关用时"表。各字段语义的把握程度(【观】读到的、【推】推断的、【未】读不到的)写在各属性上。
    /// </para>
    /// <para>
    /// 两种形状:正常结算(<see cref="HasBestTimeBlock"/> 为 true:9 字节头 + 条目表)与 7 字节全零形
    /// (为 false:服务端 <c>CParty::forced_set_play_result</c> 只发 7 个全零字节,没有任务迷宫标志与条目表,由
    /// <see cref="SettlementNoticeAParser.TryParseTimeoutFallback"/> 解;这条服务端路径在真实服务端是否出现没有读通,见 <see cref="SettlementNoticeAParser"/> 的类说明)。
    /// 7 字节全零形状下 <see cref="QuestMazeFlag"/> 恒为 0、<see cref="Entries"/> 恒为空。
    /// </para>
    /// </summary>
    public sealed class SettlementNoticeA
    {
        internal SettlementNoticeA(byte rankValue, int clearTime, sbyte timeBonus, sbyte baseScore, byte questMazeFlag,
            bool hasBestTimeBlock, SettlementBestTimeEntry[] entries, int trailingByteCount)
        {
            RankValue = rankValue;
            ClearTime = clearTime;
            TimeBonus = timeBonus;
            BaseScore = baseScore;
            QuestMazeFlag = questMazeFlag;
            HasBestTimeBlock = hasBestTimeBlock;
            Entries = Array.AsReadOnly(entries);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// 评分值(u8,线上第 1 个字节)。服务端写的是 <c>getRankIndex(评分点)</c> 的结果(u16,存点 0x085b251e)的低字节(<c>put_byte</c> 0x081f5df8)【观】;
        /// 台账 E-124 的读法是"评分点 ≥ 门槛的第一个门槛值本身",所以它是门槛原值而不是 0 到 8 的档位【推】;门槛数值在用户自己的数据文件里,本库不入库。
        /// 【客户端行为】客户端这个处理器读完就丢掉这个字节(0x0073b688 读到局部变量后再无引用,【观】);
        /// 结算画面的档位也不是从它来的:画面函数(0x008ec2b0 起,范围为估计)把基础分(状态对象偏移 0x4cc 的受保护值,0x008ec480)转整数,
        /// 与客户端自己的阈值向量(全局对象 0x01a6f3f0 偏移 0x64)逐项比较,得 1 到 9 的档位(0x008ec4ba..0x008ec597)【观】;
        /// 档位随后作为实参交给一个界面对象的方法(0x008ec59d 压入、0x008ec5a0 调 0x011b4bd0),按形状是按档位取图【推】,这张图是不是字母评分图也是推断【推】。
        /// 解析器给原始字节。
        /// </summary>
        public byte RankValue { get; }

        /// <summary>
        /// 通关用时(i32,线上第 2 到 5 个字节,小端)。服务端取自队伍结构偏移 0x32c(0x085b2463),存进结果结构偏移 0xc(0x085b252b),
        /// 写出点 <c>put_int</c> 0x081f5e0d【观】。单位按毫秒理解【推】:离线评分模型(<c>RankSystem.TimeBonus</c>,台账 E-124)把它与"平均用时(毫秒)"相减,
        /// 本线没有真实结算样本交叉验证【未】。有符号 32 位,不做范围检查。客户端原样存进受保护值(0x0073b7a4..0x0073b7b8)。
        /// </summary>
        public int ClearTime { get; }

        /// <summary>
        /// 用时分(<b>带符号</b>字节,线上第 6 个字节)。服务端取 <c>getClearTimeBonusPoint(平均用时, 通关用时)</c> 整数结果的低 8 位存进结果结构偏移 0x10(0x085b2539),
        /// 写出时按带符号字节扩展(<c>MOVSX</c> 0x081f5e19,<c>put_byte</c> 0x081f5e26)【观】。
        /// 【客户端行为,解析器不模拟】客户端按<b>无符号</b>读(<c>MOVZX</c> 0x0073b7c3)再转浮点并与基础分相加存进 +0x51c(0x0073b80b),
        /// 所以负值在客户端会变成 256 加该值【观】;用户数据表里的分数是否出现负值未知【未】,正常分数两种读法一致。
        /// </summary>
        public sbyte TimeBonus { get; }

        /// <summary>
        /// 基础分(<b>带符号</b>字节,线上第 7 个字节)。它是客户端发来的结算命令(类 1、包号 49)<b>第 1 个字节</b>在服务端的回显:
        /// 服务端读结算命令时把第 1 个字节写进队伍偏移 0x17fd(<c>PacketBuf::get_byte</c> 0x081f4dd1),随后从结果结构偏移 9(即这个位置)取带符号字节(0x085b24c8),
        /// 既参与评分点 <c>calRankPoint(用时分, 基础分)</c>(0x085b24ff),又原样写出(<c>MOVSX</c> 0x081f5e32,<c>put_byte</c> 0x081f5e3f)【观】。
        /// "基础分"这个叫法沿用台账 E-124 对 <c>calRankPoint</c> 参数的理解【推】;
        /// 算出它的客户端公式在受保护的代码段里没有恢复【未】。
        /// 【客户端行为,解析器不模拟】客户端按<b>无符号</b>读(<c>MOVZX</c> 0x0073b75f)并转浮点存进 +0x400 与受保护值 +0x4cc;
        /// 结算画面拿这个值(不是评分值字节)与客户端阈值向量比较得档位(见 <see cref="RankValue"/> 的说明),
        /// 客户端存的总分(+0x51c)是它与时间加成按无符号相加的和(0x0073b80b),画面函数在 0x008ec3e8 读这个总分、转整数后交给一个绘制调用(0x011bce70,
        /// 按形状像绘制数字【推】)【观:总分的算式与读取点】。
        /// </summary>
        public sbyte BaseScore { get; }

        /// <summary>
        /// 任务迷宫标志的原始字节(线上第 8 个字节;7 字节全零形下没有这个字节,恒为 0)。服务端写 <c>CBattle_Field::isQuestMaze</c> 的结果(1 或 0,
        /// <c>put_byte</c> 0x085be22a)【观】。
        /// </summary>
        public byte QuestMazeFlag { get; }

        /// <summary>
        /// 这局是不是任务迷宫:<see cref="QuestMazeFlag"/> 等于 1(与客户端 <c>CMP byte,1</c> 的判定同形,0x0073b74c,命中时置 +0x4fc)【观】。
        /// 服务端只在"不是任务迷宫"时才记最佳通关用时(0x085b254a 起,<c>setBestClearTime</c> 在 0x085b2571),所以任务迷宫局的条目表里没有真实纪录,
        /// 见 <see cref="SettlementBestTimeEntry.BestTime"/>【观】。
        /// </summary>
        public bool IsQuestMaze => QuestMazeFlag == 1;

        /// <summary>
        /// 是不是正常结算形状:true 表示带 9 字节头与最佳时间块(任务迷宫标志、条数与条目表);
        /// false 表示 7 字节全零形状(<see cref="SettlementNoticeAParser.TryParseTimeoutFallback"/> 的结果)。
        /// </summary>
        public bool HasBestTimeBlock { get; }

        /// <summary>
        /// 最佳时间条目,按线上顺序(服务端按队员槽位 0 到 3 遍历,只给有效队员写一条;条数字节写的是队伍人数,见 <see cref="SettlementNoticeAParser"/>)。
        /// 条数为 0 或 7 字节全零形时是空列表,不为 null。列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<SettlementBestTimeEntry> Entries { get; }

        /// <summary>
        /// 读完最后一个条目(7 字节全零形为读完 7 个字节)之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 真实帧里尾部可能有折叠哈希(载荷长度 1 到 0x190 时追加 4 字节)与会话加密补零;
        /// 另外条数字节写的是 <c>get_member_count</c> 而条目按有效队员写,两者不一致时多出的"条目"会吃掉尾部字节,表现为条目取值异常而不是解析失败
        /// (两者在什么时刻可能不等没有读通,见 <see cref="SettlementNoticeAParser"/>)。这些都只体现在这个数里,只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 通关结果通知里的一条"个人最佳通关用时":读序 u16 唯一号、i32 最佳用时、u8 新纪录标志(共 7 字节)。只读值,字段取值不做范围检查。
    /// </summary>
    public readonly struct SettlementBestTimeEntry
    {
        internal SettlementBestTimeEntry(ushort uniqueId, int bestTime, bool isNewRecord)
        {
            UniqueId = uniqueId;
            BestTime = bestTime;
            IsNewRecord = isNewRecord;
        }

        /// <summary>
        /// 队员的唯一号(u16)。服务端写 <c>CUser::get_unique_id</c> 的低 16 位(<c>put_short</c> 0x085be345)【观】;
        /// 客户端把它与一个受保护值(0x01ac7a10 读出,按用法应是自己的唯一号【推】)比较,只有相等的那一条才被采用(0x0073b708..0x0073b70f)【观】。
        /// </summary>
        public ushort UniqueId { get; }

        /// <summary>
        /// 最佳通关用时(i32,有符号,不做范围检查)。服务端取 <c>BestClearTime::getBestClearTime</c>(按该队员的扩展数据、副本编号、难度查,0x085be2d3);
        /// 查到 0(没有纪录)时改写成本局通关用时(0x085be311 起)【观】——所以"最佳用时"在没有纪录时就是本局用时(任务迷宫局不记纪录,即属此类)。
        /// 单位与 <see cref="SettlementNoticeA.ClearTime"/> 相同,按毫秒理解【推】。
        /// 服务端写入之前已经用本局成绩更新过纪录(<c>setBestClearTime</c> 0x085b2571 在 <c>SendResult</c> 0x085b2590 之前),所以新纪录时它等于本局用时【推】。
        /// </summary>
        public int BestTime { get; }

        /// <summary>
        /// 是不是新纪录(线上字节等于 1 为 true,与客户端 <c>CMP byte,1</c> 同形,0x0073b711,命中时置 +0x50c,0x0073b71d)。
        /// 服务端写 <c>BestClearTime::checkNewRecord</c> 的结果(1 或 0,<c>put_byte</c> 0x085be378)【观】。字节取值不是 0 与 1 时(服务端不会写)按"不是 1 就是 false"处理。
        /// </summary>
        public bool IsNewRecord { get; }
    }
}
