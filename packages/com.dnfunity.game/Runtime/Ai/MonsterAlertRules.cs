using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 呼叫同伴的候选快照:房间里的每一只怪物一份,由 Play 层按房间对象表的顺序给(出生序)。
    /// 只放规则要看的几样:队伍、位置(整数,与客户端一致)、活没活着、是不是已经在靠近模式(12)、是不是呼叫者自己。
    /// </summary>
    public readonly struct MonsterAlertCandidate
    {
        public MonsterAlertCandidate(int team, int x, int y, bool alive, bool approaching, bool isCaller = false, bool flagged = false)
        {
            Team = team;
            X = x;
            Y = y;
            Alive = alive;
            Approaching = approaching;
            IsCaller = isCaller;
            Flagged = flagged;
        }

        /// <summary>队伍号(客户端虚槽 0x22c;怪物队默认 100,E-166)。</summary>
        public int Team { get; }

        public int X { get; }

        public int Y { get; }

        /// <summary>没死(客户端槽 +0x500 的死亡标志不为 1)。</summary>
        public bool Alive { get; }

        /// <summary>已经在靠近模式(12):叫人的循环遇到它直接跳过,不占名额。</summary>
        public bool Approaching { get; }

        /// <summary>这一份就是被打中的那只怪物自己(收集时排除)。</summary>
        public bool IsCaller { get; }

        /// <summary>
        /// 收集时被排除的标志(客户端虚槽 +0x670;对象里 +0x3528 处的受保护整数,由槽 +0x674 写入;收集函数 0x0088efb0 在 0x0088f10f 之后遇到它为 1 就不收)。
        /// 这个标志的含义没有读定,所以只按"为真就不收、也不占名额"建模;宿主眼下没有任何对应的状态,恒为假。
        /// </summary>
        public bool Flagged { get; }
    }

    /// <summary>
    /// 被打中之后叫附近同伴一起靠近(客户端怪物类虚槽 +0x84c = 0x0088f170,由受击回调 0x0089c7b0 调用;收集同伴的是 0x0088efb0)的规则,纯函数、没有状态。
    /// 静态读码(DNF.exe 普通代码段,观察,非 REA;本轮读,待入册;靠近本身见 E-148,难度读取函数 0x004382a0 的辨认见 E-134 的调用点 0x010b6e77):
    /// ① 受击回调里靠近(0x0089dd20)返回真之后,掷 rand() % 100,小于 70 才叫;
    /// ② 收集:房间对象表(出生序)里的怪物,不是自己、与自己同队、被自己的有效目标判定认可(<see cref="MonsterMoveRules.IsValidTarget"/>,视野取呼叫者此刻的视野,
    ///    靠近模式里已经放大到 1.5 倍)、<see cref="MonsterAlertCandidate.Flagged"/> 为假、没死;
    /// ③ 名额按副本难度 0 到 4 取 1、3、4、5、5;
    /// ④ 按收集的顺序逐个:已经在靠近模式的跳过(不占名额);其余的叫它去靠近,名额减一(不管它靠近成没成),减到 0 为止。
    /// 都是本机分支(怪物在本机算,沿用 E-148 的假设)。r14 补读:客户端另有非本机的分支——"本机"由 0x007f9eb0 判(某个全局状态为真,或怪物的控制者槽 +0x1b0 就是本机英雄);
    /// 判为非本机的怪物不直接调靠近,而是(控制者非空、且 0x008a8500 认可时)发一个网络包,类型 0x1d、子类 2,带被叫怪物与攻击者的编号和模式号 12,同样占名额;
    /// 这一支没有实现(网络包不在本线范围)。调用点只有受击回调一处(0x0089cef6),调用前还有 <see cref="AttackerOpensTheAlert"/> 那道门。
    /// </summary>
    public static class MonsterAlertRules
    {
        /// <summary>靠近成功之后叫同伴的几率(百分数;受击回调里 rand() % 100 与 0x46 比较,小于才叫)。</summary>
        public const int ChancePercent = 70;

        /// <summary>副本难度编号的最大值(难度 0 到 4)。</summary>
        public const int DifficultyMax = 4;

        /// <summary>难度读不出(越界)时的档位:客户端的难度读取函数越界返回 2。</summary>
        public const int FallbackDifficulty = 2;

        // 难度 0 到 4 的叫人名额(0x0088f170 里的五项跳转表:1、3、4、5、5)
        private static readonly int[] AllyLimits = { 1, 3, 4, 5, 5 };

        /// <summary>
        /// 几率门:<paramref name="random"/> 是客户端的随机数(31 位非负),对 100 取余小于 <see cref="ChancePercent"/> 就叫。
        /// 与客户端同一个有符号比较,对任意整数都一样。
        /// </summary>
        public static bool Roll(int random)
        {
            return random % 100 < ChancePercent;
        }

        /// <summary>按副本难度的叫人名额(1 到 5);难度不在 0 到 4 时按 <see cref="FallbackDifficulty"/> 档。</summary>
        public static int AllyLimit(int difficulty)
        {
            int d = difficulty >= 0 && difficulty <= DifficultyMax ? difficulty : FallbackDifficulty;
            return AllyLimits[d];
        }

        /// <summary>
        /// 候选 <paramref name="c"/> 过不过收集条件(0x0088efb0):不是呼叫者自己、同队、没死、被呼叫者当下的视野 <paramref name="callerSight"/> 认作有效目标。
        /// 视野为 0 表示无限远(<see cref="MonsterMoveRules.IsValidTarget"/>)。
        /// </summary>
        public static bool IsCollected(MonsterAlertCandidate caller, int callerSight, MonsterAlertCandidate c)
        {
            return !c.IsCaller
                && c.Team == caller.Team
                && !c.Flagged
                && c.Alive
                && MonsterMoveRules.IsValidTarget(callerSight, caller.X, caller.Y, c.X, c.Y);
        }

        /// <summary>
        /// 对象种类字段(客户端对象 +0x90)里"AI 角色一族"的两位:各类构造函数写进去的种类值里,怪物是 0x211,AI 角色类是 0x111(推断英雄属于它),
        /// 别的一些对象(种类值 0x21、0x121、0x221、0x421、0x821 等)没有第 4 位。受击回调的门用基类槽 +0x220 判它:<c>(种类 &amp; 0x11) == 0x11</c>(0x00442c80 经槽 +0x30,
        /// 槽 +0x30 的基类实现是 0x007f9d90:种类字段与参数按位与后等于参数)。有 8 个类把槽 +0x220 覆盖成恒真(0x005983b0),没有逐个辨认是哪几个类。
        /// </summary>
        public const int AiCharacterKindMask = 0x11;

        /// <summary>
        /// 受击回调里"靠近并叫同伴"的攻击者门(0x0089cea4 起,0x0089ceba 处):攻击者对象要有"负责的角色"(虚槽 +0x32c 不为空),
        /// 而且它的种类不属于 AI 角色一族(槽 +0x220 为假)。不通过就连靠近(槽 +0x7a0)带叫同伴一起跳过。
        /// 推断:这样只有投射物、被动物件一类、带着主人的攻击对象打中怪物才引出靠近与叫人,AI 角色本体(种类含两位)与别的怪物都不会;
        /// 没有核对"英雄近战命中时传进来的攻击者是英雄本体还是攻击对象",所以这个门没有接进宿主(见笔记)。
        /// </summary>
        public static bool AttackerOpensTheAlert(bool hasResponsibleActor, int attackerKind)
        {
            return hasResponsibleActor && (attackerKind & AiCharacterKindMask) != AiCharacterKindMask;
        }

        /// <summary>
        /// 挑出要叫的同伴,返回它们在 <paramref name="room"/> 里的下标(按房间顺序)。<paramref name="room"/> 含呼叫者自己(用 <see cref="MonsterAlertCandidate.IsCaller"/> 标出)。
        /// 已经在靠近模式的不挑、也不占名额;挑满 <paramref name="limit"/> 个就停。<paramref name="limit"/> 不大于 0 时一个都不挑(客户端的名额总是 1 到 5,不会出现)。
        /// </summary>
        public static IReadOnlyList<int> Pick(MonsterAlertCandidate caller, int callerSight, IReadOnlyList<MonsterAlertCandidate> room, int limit)
        {
            var picked = new List<int>();
            if (room == null || limit <= 0)
            {
                return picked;
            }
            for (int i = 0; i < room.Count; i++)
            {
                MonsterAlertCandidate c = room[i];
                if (!IsCollected(caller, callerSight, c) || c.Approaching)
                {
                    continue;
                }
                picked.Add(i);
                if (picked.Count >= limit)
                {
                    break;
                }
            }
            return picked;
        }
    }
}
