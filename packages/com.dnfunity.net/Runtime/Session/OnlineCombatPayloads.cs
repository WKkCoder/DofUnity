using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 怪物死亡命令(类 1、包号 42)载荷里的一条攻击记录:攻击者类型码、攻击者编号、该攻击者对这只怪的累计伤害、命中次数。
    /// 线上占 10 字节(u16、u16、i32、u16)。服务端对这些值只做反作弊计数,不拒绝(见 <see cref="OnlineCombatPayloads"/> 的说明)。
    /// </summary>
    public readonly struct OnlineCombatAttack
    {
        public OnlineCombatAttack(ushort attackerType, ushort attackerId, int damage, ushort hits)
        {
            AttackerType = attackerType;
            AttackerId = attackerId;
            Damage = damage;
            Hits = hits;
        }

        /// <summary>攻击者类型码(客户端取攻击者对象的第 0x34 号虚函数;玩家是 0x111,推断)。</summary>
        public ushort AttackerType { get; }

        /// <summary>攻击者编号(客户端取攻击者对象的第 0x48 号虚函数)。</summary>
        public ushort AttackerId { get; }

        /// <summary>该攻击者对这只怪的累计伤害(客户端要求大于 0 才入表)。</summary>
        public int Damage { get; }

        /// <summary>命中次数(服务端把 0 当作一条反作弊计数)。</summary>
        public ushort Hits { get; }
    }

    /// <summary>
    /// 副本战斗里客户端要发的四条命令(击杀、结算、换房、装载完成)的载荷拼装,以及三个要读回的服务端载荷(击杀确认、击杀失败回包、换房失败回包)的读法。纯函数,不接会话。
    /// <para>
    /// 依据是既有定案,不是新发明的格式。库内账目 E-376 定下骨架:怪物死亡包 54 加 10n 字节,服务端只按编号查当前图怪表、命中即删除、
    /// 不验伤害来源,失败回错误不断线;整图清完服务端发空载荷通知,结算由客户端命令触发、服务端两段回包。
    /// 数字包号与逐字节布局来自 combat 线的静态读码笔记(下面简称"笔记",在那条线的 <c>out/combat/NOTES.md</c> 的 §4、§5,
    /// 该笔记被 .gitignore 忽略、不入库,所以这些数字在库内只以本注释与 <see cref="OnlineDungeonCombat"/> 的包号常量为准),出处逐条写在下面;
    /// 换房与装载完成两条命令的依据是 r16-multiroom 线对服务端本地副本的只读静态读码(同样只有静态证据,没有"换房成功"的真服抓包;
    /// 笔记在那条线的 <c>out/multiroom/NOTES.md</c>,同样不入库):
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// 怪物死亡命令(CMD 42 DIE_MONSTER,类 1、包号 42):载荷 54 + 10n 字节,线上顺序以服务端读取函数 <c>DisPatcher_DieMob::dispatch_sig 0x081eb0c4</c>
    /// 的读序为准(笔记 §4.1,与客户端三个发送点的写序逐字段一致)。服务端读完 24 个字段才处理,任何一个读不到都断线;多余的尾字节忽略。
    /// </item>
    /// <item>
    /// 结算命令(CMD 49 SET_PLAY_RESULT,类 1、包号 49):线上布局只能取服务端读取函数 <c>DisPatcher_SetPlayResult::dispatch_sig 0x081f4ce6</c>
    /// 的读序(笔记 §5 结算链第 1 条与证据文件 <c>srv_081f4ce6.lift.txt</c>;客户端的构造点没有读,笔记 §8 记为未知):
    /// u8、u8、u16、u8 n、n 条 {u16, i32}(服务端最多只读 4 条)、i32、i32、再 5 个 i32,n 为 0 时共 33 字节,读不满服务端断线。
    /// 各字段的业务含义没读通,所以这里一律填 0,不编造;服务端对这些值只做反作弊比较:n 之后第一个 i32 在非首领房里非 0 会记一次计数,
    /// 全 0 能通过读取与读到的全部检查(服务端另按自己的通关计时判断"清得太快"并记计数,与载荷无关,不影响放行)。
    /// </item>
    /// <item>
    /// 换房命令(CMD 48 MOVE_MAP,类 1、包号 48;服务端分发表 <c>PacketDispatcher::PacketDispatcher() 0x8590a2e</c> 里 48 登记给 <c>DisPatcher_MoveMap</c>):
    /// 载荷恰好 43 字节,读序以 <c>DisPatcher_MoveMap::read 0x81c47ba</c> 为准(静态读码;库内账目 E-369 另有对照实测:43 字节的包被读满、服务端以业务码回绝而不断线,
    /// 14 字节的包读取失败、分发器断线)。偏移 → 类型 → 含义(审查备注:账目 E-365 把读序抄成"byte,byte,u32,u32,byte,u16,byte",末尾那个 byte 是汇编里
    /// <c>get_short</c> 返回值被存进消息结构偏移 0x1a 的副作用,不是线上字节;线上合计正好 43,与账目 E-369 一致):
    /// 0 → i8 → 目标格 X;1 → i8 → 目标格 Y;2..5 → i32 → 本地角色位置 X;6..9 → i32 → 位置 Y
    /// (这两项交给 <c>Secu_HackLogCheckByParty::CheckMoveMapFitInPathHackCnt 0x827a96e</c>,它在 <c>CParty::move_map 0x85a8f4e</c> 里排在 <c>CBattle_Field::MoveMap</c> 之前,所以被拒的换房也会走到;
    /// 每个队伍的第一次换房豁免(它把 <c>IsFirstMapClear</c> 置位就返回,初值是否为假没读构造函数,推断为假),第二次起单人或自动组队的队伍要求这个位置落在当前地图的过门路径对象上
    /// (<c>CMap::checkFitInPathObject 0x834e9a6</c>),对不上就写一条"Path gate position error"日志并记一次计数(0x1f7),
    /// 只计数、不拒绝、不断线,计数过阈值后的处置没读;所以缺省的 0、0 从第二次换房起多半记数,宿主应传角色此刻真实的位置);
    /// 10 → u8 → 分层图标志(为 1 时服务端走 <c>checkLayeredMap</c>);
    /// 11..12 → u16 → trapBits,<b>必须是 7 的倍数</b>(<c>CheckTrapBitsHackCnt 0x81c4b7a</c>:非 7 的倍数记一次计数,累计超过 4 次记处罚并以 <c>DisConnSig(0x18)</c> 断线);
    /// 13..20 → 4 个 u16 → 座位项(<c>CParty::checkMoveMapHackCnt 0x85a83a2</c> 逐座位判:0xFFFF = 这个座位整个跳过;0 记一次计数;值除以本图已过秒数大于 4 也记计数;
    /// 这一整段只对"还没到过的地图"执行;座位项还会原样交给 <c>CBossTowerFunc::MoveMap</c>);
    /// 21..36 → 4 个 u32 → 各座位的本图游玩时间(<c>CheckOneMapPlayTimeHackCnt 0x81c4d26</c>:全 0 不触发任何计数;大于 10000 的值会被拆开另记计数);
    /// 37..38 → u16 → 服务端读进来但不用;39..42 → i32 → 只在"单人、为 0、且这个座位没被跳过"时记一次计数(<c>checkMoveMapHackCnt</c> 的座位循环里)。
    /// 字段含义里除目标格外都没读通(位置两项、座位项、游玩时间、末尾两项),所以缺省取"服务端读得满、trapBits 与座位项与游玩时间这几类检查都不记数"的取值(见 <see cref="BuildMoveRoom"/>),不编造;
    /// 位置两项是例外(见上:缺省的 0、0 从第二次换房起多半记 0x1f7 计数),这一条是审查上一版时补的,宿主应传真实位置(<see cref="OnlineMoveRoomRequest"/> 的两个位置参数)。
    /// 服务端的处置顺序:读不满断线;<c>check_error 0x81c4a90</c> 对"X 不小于迷宫宽或 Y 不小于高"返回 -5,<c>process 0x81c5330</c> 经 <c>LineFunc</c> 把它变成正数返回值,分发器断线;
    /// 其余检查只记计数,然后 <c>CParty::move_map 0x85a8f4e</c> → <c>CBattle_Field::MoveMap 0x8308ae6</c>。
    /// 失败回包是类 1、包号 48,载荷以 <c>{u8 0, u8 错误码}</c> 开头(<c>send_to_party_cmd_error 0x859d1aa</c> 的 2 字节正文;线上后面还跟折叠摘要与会话补零,这一层只认前两个字节),错误码:
    /// 19 队伍状态不对或副本已通关;20 当前房间还没清(<c>check_grid_clear 0x830a0e8</c>:只要还有一只待清的怪就是没清,且这一条排在范围错误之前;
    /// 账目 E-369 记的真服业务码"当前图未清不能跳图"与它含义一致,那条账目没写码值,所以"码值就是 20"是静态读码的对应,不是实测);
    /// 1 目标格超出 [0, 0x31];18 已经在目标格;21 目标格不是当前格的上下左右相邻格(斜向也不行)。另有 <c>check_boss_map</c> 等透传的码没读,见 <see cref="TryReadMoveRoomRefusal"/>。
    /// <b>成功没有 CMD 48 回包</b>:换房成功的唯一肯定回应是新房间的起始地图通知(类 0、包号 29,同进副本时的起始地图通知同一个构造函数;另一路 211 号通知的布局没读)。
    /// </item>
    /// <item>
    /// 装载完成命令(CMD 40 FINISH_LOADING,类 1、包号 40;分发表里 40 登记给 <c>DisPatcher_FinishLoad</c>,<c>read 0x8264b66</c>):载荷 8 字节 = 两个 i32(客户端放物品栏与技能栏的校验和)。
    /// 服务端只在"当前格就是起始点"时才比对它们,而且只记计数(0x25b、0x25c),所以非起始房里取什么都不影响放行;缺省两个 0(见 <see cref="BuildRoomLoaded"/>)。
    /// 为什么要发:服务端每次成功换房以及开局时起一个"装载超时"定时器(<c>TimerMapLoading::dispatch_sig 0x8633b84</c>,时限常量单人 120、组队 90,单位没读),
    /// 到点还没收到装载完成的成员会被 <c>map_load_forced_finish 0x85b1e66</c> 走 <c>giveup_game 0x85b2baa</c> 踢出副本;
    /// 收到装载完成命令后服务端 <c>check_allmember_loading 0x85b17a4</c> 全员齐了就发装载完成通知(类 0、包号 30,载荷一个 i32)。
    /// </item>
    /// </list>
    /// </summary>
    public static class OnlineCombatPayloads
    {
        /// <summary>"没有击杀者"的击杀者编号(笔记 §4.1 p2:0xFFFF = 无)。</summary>
        public const ushort NoKiller = 0xFFFF;

        /// <summary>击杀命令载荷里攻击记录之前的固定头:u16 怪编号、u16 击杀者、4 个 i32 校验值、u8 条数 = 21 字节(笔记 §4.1 p0..p20)。</summary>
        public const int KillHeadLength = 21;

        /// <summary>一条攻击记录的线上字节数(笔记 §4.1:u16、u16、i32、u16)。</summary>
        public const int AttackRecordLength = 10;

        /// <summary>攻击记录条数上限:客户端最多写 0xFE 条(笔记 §4.1 p20)。</summary>
        public const int MaxAttackRecords = 0xFE;

        /// <summary>攻击记录之后的固定尾:位置 x、y,6 字节标志与杂项,再加 i32、i32 与 7 个 u16,共 33 字节;服务端必读,缺了断线(笔记 §4.1 q+0..q+32)。</summary>
        public const int KillTailLength = 33;

        /// <summary>没有攻击记录时的击杀命令载荷长度:21 + 33 = 54。</summary>
        public const int KillBaseLength = KillHeadLength + KillTailLength;

        /// <summary>结算命令载荷的最短长度(n = 0):u8 + u8 + u16 + u8 + i32 + i32 + 5 个 i32 = 33 字节(读序见类说明)。</summary>
        public const int SettlementLength = 33;

        /// <summary>击杀失败回包(类 1、包号 42)的载荷长度:u8 成功标志(0)、u8 错误码、u16 怪编号(笔记 §4.6)。</summary>
        public const int KillRefusalLength = 4;

        /// <summary>换房命令载荷的长度:恰好 43 字节(<c>DisPatcher_MoveMap::read 0x81c47ba</c> 读满 43 字节才继续,读不满分发器断线;读序见类说明)。</summary>
        public const int MoveRoomLength = 43;

        /// <summary>
        /// 换房命令里目标格坐标的上限:服务端 <c>CBattle_Field::MoveMap 0x8308ae6</c> 要求 X、Y 都落在 [0, 0x31](越界回错误码 1)。
        /// 这是服务端自己那道门的上限,不是迷宫的实际尺寸——目标格不小于当前迷宫宽或高时更早就被 <c>check_error</c> 断线(见 <see cref="OnlineMoveRoomRequest"/>)。
        /// </summary>
        public const int MaxMoveRoomCell = 0x31;

        /// <summary>换房命令里座位项的个数(服务端按 4 个座位读,偏移 13..20 与 21..36)。</summary>
        public const int MoveRoomSeatCount = 4;

        /// <summary>座位项里的"整个座位跳过":<c>CParty::checkMoveMapHackCnt 0x85a83a2</c> 遇到 0xFFFF 就不对这个座位做任何检查(座位项为 0 或过大的计数、末尾 i32 为 0 的计数都在它之后)。</summary>
        public const ushort MoveRoomSeatSkipped = 0xFFFF;

        /// <summary>trapBits 必须是它的倍数(<c>CheckTrapBitsHackCnt 0x81c4b7a</c>:不是 7 的倍数记计数,累计超过 4 次记处罚并断线)。</summary>
        public const int TrapBitsStep = 7;

        /// <summary>装载完成命令(类 1、包号 40)载荷的长度:两个 i32 = 8 字节(<c>DisPatcher_FinishLoad::read 0x8264b66</c>)。</summary>
        public const int RoomLoadedLength = 8;

        /// <summary>换房失败回包(类 1、包号 48)里本层认的最短长度:u8 成功标志(0)、u8 错误码。</summary>
        public const int MoveRoomRefusalLength = 2;

        /// <summary>
        /// 拼一条怪物死亡命令的载荷。只有怪编号(服务端按它在当前地图的怪表里查找,命中即删除并广播确认,笔记 §4.4、§4.5)是必须对的;
        /// 击杀者、攻击记录、位置服务端都不校验(只有反作弊计数,笔记 §4.5 第 5、6 条),所以默认取"没有击杀者、0 条攻击记录、位置 0"
        /// (与客户端站点 1 的全零字段一致,笔记 §4.2),其余固定字段(四个校验值、各标志、尾部的 i32 与 u16)一律为 0。
        /// 第 q+6 字节是"被动物件"标志:1 会让服务端走销毁被动物件的分支且不回包(笔记 §4.5 第 2 条),怪物必须是 0。
        /// <para>
        /// 联调观察项:"没有击杀者"(0xFFFF)的上报在同一连接上超过 4 次后,服务端每次都会记一条反作弊计数(笔记 §4.5 第 5 条;
        /// 只计数、不拒绝、不断线,计数过阈值后的处置笔记 §8 U12 记为未读)。想避开它,就用 <see cref="OnlineDungeonCombatOptions.KillPayload"/>
        /// 换成带击杀者与攻击记录的拼法(<paramref name="killerId"/>、<paramref name="attacks"/>);主角在服务端的编号与合规的伤害值现在拿不到,所以缺省不填。
        /// </para>
        /// </summary>
        /// <param name="mobId">怪编号(起始地图怪记录的第 3 项,<see cref="StartMapMonsterRecord.C"/>)。</param>
        /// <param name="killerId">击杀者编号;默认 <see cref="NoKiller"/>。</param>
        /// <param name="attacks">攻击记录;null 或空为 0 条;超过 <see cref="MaxAttackRecords"/> 条抛 <see cref="ArgumentOutOfRangeException"/>。</param>
        /// <param name="x">怪死亡位置 x。</param>
        /// <param name="y">怪死亡位置 y。</param>
        public static byte[] BuildKill(ushort mobId, ushort killerId = NoKiller, IReadOnlyList<OnlineCombatAttack> attacks = null, ushort x = 0, ushort y = 0)
        {
            int n = attacks == null ? 0 : attacks.Count;
            if (n > MaxAttackRecords)
            {
                throw new ArgumentOutOfRangeException(nameof(attacks), "攻击记录最多 " + MaxAttackRecords + " 条");
            }

            int q = KillHeadLength + AttackRecordLength * n;
            var payload = new byte[q + KillTailLength];
            PutU16(payload, 0, mobId);
            PutU16(payload, 2, killerId);
            // p4、p8、p12、p16:四个 i32 校验值,站点 1 取 0(笔记 §4.1),保持 0
            payload[20] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                OnlineCombatAttack a = attacks[i];
                int o = KillHeadLength + AttackRecordLength * i;
                PutU16(payload, o, a.AttackerType);
                PutU16(payload, o + 2, a.AttackerId);
                PutI32(payload, o + 4, a.Damage);
                PutU16(payload, o + 8, a.Hits);
            }
            PutU16(payload, q, x);
            PutU16(payload, q + 2, y);
            // q+4..q+32:标志与杂项全 0(q+6 = 0 即"不是被动物件",q+7 = 0 即不触发移动作弊分支)
            return payload;
        }

        /// <summary>
        /// 拼一条结算命令的载荷(n = 0,33 字节全 0)。字段含义没读通(见类说明),不编造值;
        /// 服务端用它们的地方只有反作弊比较(记计数、不拒绝),全 0 读得满,读到的各项比较(第一个 i32 非 0、第二个 i32 过大、
        /// 末尾五个 i32 里的经济类组合)都不会被触发。
        /// </summary>
        public static byte[] BuildSettlement()
        {
            return new byte[SettlementLength];
        }

        /// <summary>
        /// 读击杀确认通知(类 0、包号 0x26)里的怪编号:载荷前两个字节是 u16 怪编号(笔记 §4.7;后面的掉落等不读)。
        /// 载荷里会话加密补的 0x00 仍在末尾,所以只看前两个字节;不足两个字节返回 false。
        /// </summary>
        public static bool TryReadKillConfirm(byte[] payload, out ushort mobId)
        {
            mobId = 0;
            if (payload == null || payload.Length < 2)
            {
                return false;
            }
            mobId = GetU16(payload, 0);
            return true;
        }

        /// <summary>
        /// 读击杀失败回包(类 1、包号 42)里的错误码与怪编号:载荷 <c>{u8 0, u8 错误码, u16 怪编号}</c>(笔记 §4.6)。
        /// 成功形状(首字节非 0,客户端直接忽略成功回包)、不足 4 字节、null 都返回 false。
        /// </summary>
        public static bool TryReadKillRefusal(byte[] payload, out byte code, out ushort mobId)
        {
            code = 0;
            mobId = 0;
            if (payload == null || payload.Length < KillRefusalLength || payload[0] != 0)
            {
                return false;
            }
            code = payload[1];
            mobId = GetU16(payload, 2);
            return true;
        }

        /// <summary>
        /// 拼一条换房命令的载荷(43 字节,逐字段偏移见类说明)。只有目标格是必须对的(它决定去哪一间);其余字段含义没读通,
        /// 缺省取"服务端读得满、trapBits 与座位项与游玩时间这几类检查都不记数"的取值:分层图标志 0、trapBits 0(0 是 7 的倍数)、
        /// 四个座位项都是 0xFFFF(整个座位跳过,免得 0 记计数)、四个游玩时间 0、未用的 u16 为 0、末尾 i32 为 0(座位全部跳过时它不被比较)。
        /// 位置两项缺省 0、0,但这两项<b>不保证</b>不记数:第一次换房被豁免,第二次起服务端要求它落在当前地图的过门路径对象上,对不上记 0x1f7 计数
        /// (详见类说明)——换房不止一次的调用方应当传真实位置。
        /// 调用方要启用座位项(例如组队)就得自己把各项配成合法值:单人时末尾 i32 不能为 0(会记 0x1a0 计数),座位项不能为 0。
        /// 对真服不安全的取值在这里直接拒绝(抛 <see cref="ArgumentOutOfRangeException"/>/<see cref="ArgumentException"/>):
        /// 目标格出 [0, <see cref="MaxMoveRoomCell"/>]、trapBits 不是 <see cref="TrapBitsStep"/> 的倍数(累计超过 4 次服务端处罚并断线)、座位项个数不是 4。
        /// </summary>
        /// <param name="targetX">目标格 X(线上 i8,偏移 0)。</param>
        /// <param name="targetY">目标格 Y(线上 i8,偏移 1)。</param>
        /// <param name="positionX">本地角色位置 X(i32,偏移 2)。</param>
        /// <param name="positionY">本地角色位置 Y(i32,偏移 6)。</param>
        /// <param name="layered">分层图标志(u8,偏移 10;true 写 1)。</param>
        /// <param name="trapBits">trapBits(u16,偏移 11),必须是 7 的倍数。</param>
        /// <param name="seatItems">四个座位项(u16,偏移 13、15、17、19);null 取四个 <see cref="MoveRoomSeatSkipped"/>。</param>
        /// <param name="seatPlayTimes">四个座位的本图游玩时间(u32,偏移 21、25、29、33);null 取四个 0。</param>
        /// <param name="unused">未用的 u16(偏移 37)。</param>
        /// <param name="tail">末尾 i32(偏移 39)。</param>
        public static byte[] BuildMoveRoom(int targetX, int targetY, int positionX = 0, int positionY = 0, bool layered = false,
            int trapBits = 0, IReadOnlyList<ushort> seatItems = null, IReadOnlyList<uint> seatPlayTimes = null, ushort unused = 0, int tail = 0)
        {
            if (targetX < 0 || targetX > MaxMoveRoomCell)
            {
                throw new ArgumentOutOfRangeException(nameof(targetX), "目标格 X 必须在 0.." + MaxMoveRoomCell);
            }
            if (targetY < 0 || targetY > MaxMoveRoomCell)
            {
                throw new ArgumentOutOfRangeException(nameof(targetY), "目标格 Y 必须在 0.." + MaxMoveRoomCell);
            }
            if (trapBits < 0 || trapBits > 0xFFFF || trapBits % TrapBitsStep != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trapBits), "trapBits 必须是 " + TrapBitsStep + " 的倍数且不超过 65535(否则服务端记计数、累计过阈值处罚并断线)");
            }
            if (seatItems != null && seatItems.Count != MoveRoomSeatCount)
            {
                throw new ArgumentException("座位项必须正好 " + MoveRoomSeatCount + " 个", nameof(seatItems));
            }
            if (seatPlayTimes != null && seatPlayTimes.Count != MoveRoomSeatCount)
            {
                throw new ArgumentException("座位游玩时间必须正好 " + MoveRoomSeatCount + " 个", nameof(seatPlayTimes));
            }

            var payload = new byte[MoveRoomLength];
            payload[0] = (byte)targetX;
            payload[1] = (byte)targetY;
            PutI32(payload, 2, positionX);
            PutI32(payload, 6, positionY);
            payload[10] = layered ? (byte)1 : (byte)0;
            PutU16(payload, 11, trapBits);
            for (int i = 0; i < MoveRoomSeatCount; i++)
            {
                PutU16(payload, 13 + 2 * i, seatItems == null ? MoveRoomSeatSkipped : seatItems[i]);
                PutI32(payload, 21 + 4 * i, seatPlayTimes == null ? 0 : unchecked((int)seatPlayTimes[i]));
            }
            PutU16(payload, 37, unused);
            PutI32(payload, 39, tail);
            return payload;
        }

        /// <summary>
        /// 拼一条装载完成命令的载荷(两个 i32,8 字节;读序 <c>DisPatcher_FinishLoad::read 0x8264b66</c>)。
        /// 两个值客户端放物品栏与技能栏的校验和,服务端只在"当前格是起始点"时比对、且比不上只记计数(<c>Party_hack_check 0x8264c28</c>),
        /// 非起始房里取什么都不影响放行,所以缺省 0、0,不编造校验和的算法。
        /// </summary>
        public static byte[] BuildRoomLoaded(int first = 0, int second = 0)
        {
            var payload = new byte[RoomLoadedLength];
            PutI32(payload, 0, first);
            PutI32(payload, 4, second);
            return payload;
        }

        /// <summary>
        /// 读换房失败回包(类 1、包号 48)里的错误码:载荷以 <c>{u8 0, u8 错误码}</c> 开头,后面的折叠摘要与补零不读。
        /// 成功形状(首字节非 0)、不足 2 字节、null 都返回 false。码值本层不解释:已读到的 19、20、1、18、21 见类说明,
        /// 另有一些来自 <c>check_boss_map</c> 等被透传的码没有读通,所以状态机对任何码一视同仁地当"被拒"处理。
        /// </summary>
        public static bool TryReadMoveRoomRefusal(byte[] payload, out byte code)
        {
            code = 0;
            if (payload == null || payload.Length < MoveRoomRefusalLength || payload[0] != 0)
            {
                return false;
            }
            code = payload[1];
            return true;
        }

        private static void PutU16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static void PutI32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static ushort GetU16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }
    }
}
