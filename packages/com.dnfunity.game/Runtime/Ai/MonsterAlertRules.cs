using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 呼叫同伴的候选快照:房间里的每一只怪物一份,由 Play 层按房间对象表的顺序给(出生序)。
    /// 只放规则要看的几样:队伍、位置(整数,与客户端一致)、活没活着、是不是已经在靠近模式(12)、是不是呼叫者自己。
    /// </summary>
    public readonly struct MonsterAlertCandidate
    {
        public MonsterAlertCandidate(int team, int x, int y, bool alive, bool approaching, bool isCaller = false)
        {
            Team = team;
            X = x;
            Y = y;
            Alive = alive;
            Approaching = approaching;
            IsCaller = isCaller;
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
    }

    /// <summary>
    /// 被打中之后叫附近同伴一起靠近(客户端怪物类虚槽 +0x84c = 0x0088f170,由受击回调 0x0089c7b0 调用;收集同伴的是 0x0088efb0)的规则,纯函数、没有状态。
    /// 静态读码(DNF.exe 普通代码段,观察,非 REA;本轮读,待入册;靠近本身见 E-148,难度读取函数 0x004382a0 的辨认见 E-134 的调用点 0x010b6e77):
    /// ① 受击回调里靠近(0x0089dd20)返回真之后,掷 rand() % 100,小于 70 才叫;
    /// ② 收集:房间对象表(出生序)里的怪物,不是自己、与自己同队、被自己的有效目标判定认可(<see cref="MonsterMoveRules.IsValidTarget"/>,视野取呼叫者此刻的视野,
    ///    靠近模式里已经放大到 1.5 倍)、没死;
    /// ③ 名额按副本难度 0 到 4 取 1、3、4、5、5;
    /// ④ 按收集的顺序逐个:已经在靠近模式的跳过(不占名额);其余的叫它去靠近,名额减一(不管它靠近成没成),减到 0 为止。
    /// 都是本机分支(怪物在本机算,沿用 E-148 的假设);客户端另有非本机的分支(发网络包),没读。
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
                && c.Alive
                && MonsterMoveRules.IsValidTarget(callerSight, caller.X, caller.Y, c.X, c.Y);
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
