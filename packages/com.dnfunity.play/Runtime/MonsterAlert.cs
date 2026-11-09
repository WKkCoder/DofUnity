using System;
using System.Collections.Generic;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 房间里"挨了打的怪物叫同伴"的那一侧(客户端怪物类虚槽 +0x84c = 0x0088f170;规则见 <see cref="MonsterAlertRules"/>,靠近本身见 E-148)。
    /// 大脑在挨打时靠近成功又掷中几率就留下一份请求(<see cref="MonsterBrain.TakeAlertRequest"/>);房间会话在这一击结算完之后调 <see cref="Broadcast"/>:
    /// 按房间怪物表的顺序收集同队、没死、在呼叫者(放大后)视野里的同伴,按副本难度的名额逐个叫它们去靠近主角。
    /// 受击回调在基类扣血之前跑,所以致命的一击照样叫(推断:回调是否对致命伤害调用没单独读);呼叫者的视野与位置取这一击之后的值,
    /// 与扣血无关。没有大脑的怪物(脚本指挥的)也算候选、占名额,但靠近对它没有效果;原地不动的同理(推断:客户端对它们同样调靠近)。
    /// 只做本机分支;攻击者只考虑主角(本房间里所有伤害都来自主角和主角放出来的对象)。
    /// </summary>
    public static class MonsterAlert
    {
        /// <summary>
        /// 处理 <paramref name="caller"/> 刚挨的这一击留下的叫同伴请求;没有请求(没大脑、没靠近成功、没掷中、已经取走)就什么也不做。
        /// <paramref name="monsters"/> 是房间的怪物表(出生序,含呼叫者自己);<paramref name="difficulty"/> 是副本难度(0 到 4,决定名额)。返回被叫的数量。
        /// </summary>
        public static int Broadcast(MonsterActor caller, IReadOnlyList<MonsterActor> monsters, int difficulty)
        {
            MonsterBrain brain = caller?.Brain;
            if (brain == null || monsters == null || !brain.TakeAlertRequest())
            {
                return 0;
            }
            int team = caller.ActSelf.Team;
            var self = new MonsterAlertCandidate(team, Floor(caller.X), Floor(caller.Y), caller.IsAlive, true, true);
            var room = new MonsterAlertCandidate[monsters.Count];
            for (int i = 0; i < room.Length; i++)
            {
                MonsterActor m = monsters[i];
                room[i] = new MonsterAlertCandidate(m.ActSelf.Team, Floor(m.X), Floor(m.Y), m.IsAlive && !m.IsGone,
                    m.Brain != null && m.Brain.Mode == MonsterMoveMode.Approach, ReferenceEquals(m, caller));
            }
            IReadOnlyList<int> picked = MonsterAlertRules.Pick(self, brain.Sight, room, MonsterAlertRules.AllyLimit(difficulty));
            foreach (int index in picked)
            {
                monsters[index].Brain?.AlertApproach();
            }
            return picked.Count;
        }

        // 位置取整的方式与大脑一致(客户端的位置是整数,大脑里也是向下取整)
        private static int Floor(double value) => (int)Math.Floor(value);
    }
}
