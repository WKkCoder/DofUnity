using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 用户信息通知(类 0、包号 2)模式 2(角色选择列表)明文载荷解出的只读记录,由 <see cref="UserInfoParser"/> 生成。
    /// 没有任何可写成员,也不持有载荷数组:字段是值拷贝,<see cref="Entries"/> 以及每条里的 <see cref="UserInfoEntry.NameBytes"/>、<see cref="UserInfoEntry.Equips"/> 都是只读包装。
    /// <para>
    /// 命名:客户端静态读码定下了读序、宽度与"落到哪个字段"(charre_NOTES §2.3【观】),但头部与条目里不少字节的业务含义没读通(§6【未】),
    /// 这些一律按读序中性命名(头部 A、B、C、D;条目 X1、D、G、H、I、J、K、A2、B2、C2、D2),读到的线索只写在各属性的说明里,不臆造业务名。
    /// 读通了的才用业务名:<see cref="UserInfoEntry.Job"/>、<see cref="UserInfoEntry.Level"/>、<see cref="UserInfoEntry.GrowTypeByte"/> 等。
    /// 该笔记被 .gitignore 忽略,不入库。
    /// </para>
    /// </summary>
    public sealed class UserInfoNotice
    {
        internal UserInfoNotice(byte a, byte b, byte c, uint d, UserInfoEntry[] entries, int trailingByteCount)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            Entries = Array.AsReadOnly(entries);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// A(u8,模式字节之后的第 1 项)。客户端把它写进 secure 全局,并据它算出每页 6 个的页数【观】,服务端写的是角色栏位上限的访问器结果【观】,
        /// 所以"栏位总数"是【推】。解析器只给原始值,不核对它与条目个数的关系。
        /// </summary>
        public byte A { get; }

        /// <summary>B(u8,第 2 项)。客户端与 A 一起算栏位页数【观】;服务端写的是栏位特效个数的访问器结果,"扩展栏位数"是【推】。解析器只给原始值。</summary>
        public byte B { get; }

        /// <summary>C(u8,第 3 项)。客户端存进一个共用结构的第 1 个字段,谁来消费没读到【未】;服务端写的是管理等级的访问器结果【观】。</summary>
        public byte C { get; }

        /// <summary>D(u32,第 4 项,小端)。客户端存进同一个共用结构的第 2 个字段,消费者没读到【未】;服务端写的是管理点数的访问器结果【观】。</summary>
        public uint D { get; }

        /// <summary>
        /// 角色条目,按线上顺序(线上先是 u16 个数 count,再是 count 个条目,个数就是列表长度)。没有条目时为空列表,不为 null。
        /// 客户端点选角色后回传的是界面里当前选中的下标(charre_NOTES §1.2【观】),它就是条目在这个列表里的位置是【推】
        /// (§4.3 第 10 条:频道类型特殊时服务端只写 1 条,位置与服务端的真实角色下标可能不同,未验证),所以"当前选中的是哪一条"按位置取。
        /// 列表只读:转成 <see cref="IList{T}"/> 后任何写操作都会抛 <see cref="NotSupportedException"/>。
        /// </summary>
        public IReadOnlyList<UserInfoEntry> Entries { get; }

        /// <summary>
        /// 读完最后一个条目之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 客户端读完就不再读(也没有"必须读完"的检查);服务端在正文长度落在 1 到 0x190 字节时,于明文末尾追加 4 字节折叠 PI 尾,
        /// 客户端从不检查它(charre_NOTES §3.5【观】),所以真实帧里常见的尾巴恰是 4 字节;正文更长时没有这段尾巴。
        /// 这个数只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 角色选择列表里的一个条目(一个角色),读序见 <see cref="UserInfoParser"/>。只读。
    /// 条目线上长度 = 32 + 名字字节数 + 22 × 装备条数。
    /// </summary>
    public sealed class UserInfoEntry
    {
        internal UserInfoEntry(
            ushort uid, byte[] nameBytes, byte x1, byte job, byte growTypeByte, byte level, byte d, byte status,
            UserInfoEquip[] equips, byte g, byte h, uint i, byte j, byte premium, uint k, byte a2, byte b2, byte c2, uint d2)
        {
            Uid = uid;
            NameBytes = Array.AsReadOnly(nameBytes);
            X1 = x1;
            Job = job;
            GrowTypeByte = growTypeByte;
            Level = level;
            D = d;
            Status = status;
            Equips = Array.AsReadOnly(equips);
            G = g;
            H = h;
            I = i;
            J = j;
            Premium = premium;
            K = k;
            A2 = a2;
            B2 = b2;
            C2 = c2;
            D2 = d2;
        }

        /// <summary>
        /// uid(u16,条目第 1 项)。服务端写的是"运行序号"(从 0 起,只对实际写出的条目递增);客户端只在处理条目的末尾拿它与本地玩家 id 比较,
        /// 没存进条目结构,选角回传的也不是它【观】(charre_NOTES §1.2、§2.3 第 1 项、§4.2)。所以它<b>不能</b>当选角槽号用;
        /// 解析器只给原始值,不核对是否从 0 连续。
        /// </summary>
        public ushort Uid { get; }

        /// <summary>
        /// 角色名的原始字节(线上是 u32 长度前缀加这么多字节,没有 NUL;长度前缀不在这里)。<b>只做字节拷贝</b>:不转字符串、不去 NUL、不校验编码
        /// (客户端按 UTF-8 解,首字节 ≥ 0xF0 或序列被截断时它自己放弃,那是客户端的行为,解析器不模拟)。长度为 0 时是空列表,不为 null。
        /// 列表只读,也不与载荷数组共享内存。调用方要显示或落盘自行决定;日志与诊断输出里不要带它。
        /// </summary>
        public IReadOnlyList<byte> NameBytes { get; }

        /// <summary>
        /// X1(u8,名字之后的第 1 个字节)。客户端把它压进一个向量,值为 1 时点"开始游戏"只弹提示、不发选角命令【观】;
        /// 服务端写的是名字特殊字符检查的结果【观】,"1 表示名字有特殊字符"是【推】。解析器只给原始字节,不判断可选与否。
        /// </summary>
        public byte X1 { get; }

        /// <summary>
        /// 职业编号(u8,X1 之后)。客户端落到条目结构的职业字段,服务端写的正是职业访问器读的那个字段【观】(charre_NOTES §2.3 第 4 项)。取值不做范围检查。
        /// </summary>
        public byte Job { get; }

        /// <summary>
        /// 成长类型字节(u8,职业之后),<b>原样</b>。客户端把它拆成两段:低 4 位与 bit4 到 bit6 分别落到条目结构里的两个字段,bit7 丢弃【观】;
        /// 服务端写的是同一个字节,由"设置当前成长类型(first, second)"写成 first 加 second 左移 4 位(first ≤ 6、second ≤ 2 是写入侧的约束)【服务端侧观】
        /// (charre_NOTES §2.3 第 5 项、§2.4)。拆好的两段见 <see cref="FirstGrowType"/> 与 <see cref="SecondGrowType"/>;这里留原始字节供对账(含 bit7)。
        /// </summary>
        public byte GrowTypeByte { get; }

        /// <summary>
        /// 一转(第一成长类型,0 到 15):<see cref="GrowTypeByte"/> 的低 4 位。客户端落到条目结构的"第一段"字段,服务端侧有同名拆法的读取器【观】。
        /// 取值不做范围检查。<see cref="TownEntrySnapshot.GrowType"/> 取的就是它。
        /// </summary>
        public byte FirstGrowType => (byte)(GrowTypeByte & 0x0F);

        /// <summary>
        /// 二转(第二成长类型,0 到 7):<see cref="GrowTypeByte"/> 的 bit4 到 bit6。客户端落到条目结构的"第二段"字段,但之后由谁消费没读到【未】(charre_NOTES §6);
        /// 服务端侧有同名拆法的读取器【观】。取值不做范围检查;bit7 不在其中。
        /// </summary>
        public byte SecondGrowType => (byte)((GrowTypeByte >> 4) & 0x07);

        /// <summary>
        /// 等级(u8,成长类型之后)。客户端落到条目结构里的一个 secure 成员;服务端的等级是 u16,包里只写低字节【观】(charre_NOTES §2.3 第 6 项),
        /// 所以这里最大 255。取值不做范围检查。
        /// </summary>
        public byte Level { get; }

        /// <summary>D(u8,等级之后;语义未定)。客户端落到条目结构里的一个 secure 成员;服务端模式 2 恒写 0【观】。</summary>
        public byte D { get; }

        /// <summary>
        /// 状态(u8,D 之后)。客户端落到条目结构的状态字段:值为 8 时点"开始游戏"被拦截(与 <see cref="X1"/> 同款)【观】,值为 4 是另一种显示态【推】;
        /// 服务端按角色的几个标志写 0、4、8 三种值【观】。解析器只给原始字节。
        /// </summary>
        public byte Status { get; }

        /// <summary>
        /// 装备子表,按线上顺序(线上先是 u8 条数 n,再是 n 条 22 字节的记录,条数就是列表长度)。没有装备时为空列表,不为 null。
        /// 列表只读,规则同 <see cref="NameBytes"/>。
        /// </summary>
        public IReadOnlyList<UserInfoEquip> Equips { get; }

        /// <summary>G(u8,装备子表之后的第 1 个字节;语义未定)。客户端落到条目结构里的一个字段;服务端写的是装备子表函数末尾的一个单字节【观】。</summary>
        public byte G { get; }

        /// <summary>H(u8;语义未定)。客户端存进条目结构里的一个 secure 成员;服务端恒写 0【观】。</summary>
        public byte H { get; }

        /// <summary>I(u32,小端;语义未定)。客户端存进条目结构里的一个 secure 成员;服务端写的是公会键的访问器结果【推】。</summary>
        public uint I { get; }

        /// <summary>J(u8;语义未定)。客户端存成"是否等于 1"的布尔;服务端写的是私人商店权限的判断【推】。</summary>
        public byte J { get; }

        /// <summary>
        /// premium(u8)。客户端存成"是否非 0"的布尔;服务端写的是"是否受高级服务影响"的判断【推】(charre_NOTES §2.3 第 14 项)。解析器只给原始字节,不布尔化。
        /// </summary>
        public byte Premium { get; }

        /// <summary>K(u32,小端;语义未定)。客户端落到条目结构里的一个字段;服务端写的是 PC 房号的访问器结果【推】。</summary>
        public uint K { get; }

        /// <summary>A2(u8;语义未定)。客户端存进角色对象的一个 secure 成员,读取点静态没找到【未】;服务端写的是角色信息里的一个单字节。</summary>
        public byte A2 { get; }

        /// <summary>B2(u8;语义未定)。客户端存成角色对象里的一个布尔,读取点静态没找到【未】。</summary>
        public byte B2 { get; }

        /// <summary>C2(u8)。客户端读完就丢弃,不存任何地方【观】。</summary>
        public byte C2 { get; }

        /// <summary>
        /// D2(u32,小端,条目最后一个字段)。客户端存进角色对象的一个字段,该字段非 0 即判定"待删除"【观】;
        /// 服务端写的是待删除剩余天数(到期分支写 0)【推】。解析器只给原始值。
        /// </summary>
        public uint D2 { get; }
    }

    /// <summary>
    /// 角色条目里的一条装备记录,线上恒 22 字节:u8 槽号、u32 编号、u32 blobLen(必须 = 4)加 4 字节 blob、u8 packed、u32 clearAvatar、u32 extra。只读值。
    /// 业务含义没读通的字段按读序中性命名(见 <see cref="UserInfoNotice"/>)。
    /// </summary>
    public readonly struct UserInfoEquip
    {
        internal UserInfoEquip(byte slot, uint itemId, uint blob, byte packed, uint clearAvatar, uint extra)
        {
            Slot = slot;
            ItemId = itemId;
            Blob = blob;
            Packed = packed;
            ClearAvatar = clearAvatar;
            Extra = extra;
        }

        /// <summary>
        /// 槽号(u8)。客户端拿它当下标、没有范围检查(合法范围 0 到 25 是按条目结构布局推断的【推】,charre_NOTES §2.3 装备子表);
        /// 解析器同样不检查取值,也不核对是否严格递增。
        /// </summary>
        public byte Slot { get; }

        /// <summary>编号(u32,小端)。客户端落到条目结构里按槽号索引的数组。</summary>
        public uint ItemId { get; }

        /// <summary>
        /// blob:线上是 u32 blobLen(必须 = 4,否则整包按错位处理)加 4 个原始字节,这里是那 4 个字节按小端读成的 u32(无损,可原样还原)。
        /// 客户端把它们读进一个 4 字节的栈变量,再落到按槽号索引的数组【观】;含义没读通【未】。
        /// </summary>
        public uint Blob { get; }

        /// <summary>
        /// packed(u8)。客户端拆成"右移 1 位"与"最低位"两份,分别落到按槽号索引的两个数组(前者【推】是强化或锻造档)【观】。
        /// 解析器不拆,只给原始字节。
        /// </summary>
        public byte Packed { get; }

        /// <summary>clearAvatar(u32,小端)。客户端在它非 0 时才落到按槽号索引的数组【观】;含义没读通【未】。</summary>
        public uint ClearAvatar { get; }

        /// <summary>extra(u32,小端,记录的最后一个字段)。客户端读完就丢弃,不存任何地方【观】。</summary>
        public uint Extra { get; }
    }
}
