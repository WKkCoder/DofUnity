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
    /// 副本战斗里客户端要发的两条命令的载荷拼装,以及两个要读回的服务端载荷(击杀确认、击杀失败回包)的读法。纯函数,不接会话。
    /// <para>
    /// 依据是既有定案,不是新发明的格式。库内账目 E-376 定下骨架:怪物死亡包 54 加 10n 字节,服务端只按编号查当前图怪表、命中即删除、
    /// 不验伤害来源,失败回错误不断线;整图清完服务端发空载荷通知,结算由客户端命令触发、服务端两段回包。
    /// 数字包号与逐字节布局来自 combat 线的静态读码笔记(下面简称"笔记",在那条线的 <c>out/combat/NOTES.md</c> 的 §4、§5,
    /// 该笔记被 .gitignore 忽略、不入库,所以这些数字在库内只以本注释与 <see cref="OnlineDungeonCombat"/> 的包号常量为准),出处逐条写在下面:
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
