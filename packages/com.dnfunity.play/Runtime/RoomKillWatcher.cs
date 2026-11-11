using System;
using System.Collections.Generic;

namespace Dnf.Play
{
    /// <summary>
    /// 房间里"地图出生点的怪"死亡的只读观察者:盯着一个房间会话的怪物列表,把新死的怪的序号(<see cref="MonsterActor.Index"/>)交出来,
    /// 每只怪只交一次。联网战斗要的就是这个——本地怪死了,把它的序号报给服务端的击杀口(联网层按起始地图记录的顺序查对应表,序号就是记录的下标)。
    /// <para>
    /// 为什么是轮询而不是在战斗核心里加回调:房间会话已有的击杀事件(<see cref="IQuestEvents.MonsterKilled"/>)只带怪物编码、不带是哪一只,
    /// 对不上联网对应表;给伤害结算加一个"某只怪死了"的出口就得改战斗核心,而这里只需要"现在谁已经死了"这个事实——血量归零是持久的状态,
    /// 轮询不会漏。所以观察者不碰、不改房间会话,只读它的怪物列表。
    /// </para>
    /// <para>
    /// 只交出地图出生点的怪:脚本召唤出来的怪(<see cref="MonsterActor.IsSummoned"/>)服务端没有它们的出生记录(召唤关系由服务端另造,客户端那一半没读,
    /// 推断,E-134),它们的序号接在出生点怪之后,报上去也对不上表,所以不交。
    /// </para>
    /// <para>
    /// 序号的含义:出生点怪在房间里的 <see cref="MonsterActor.Index"/> 就是它们在 <see cref="RoomCombatSession.Monsters"/> 里的位置(地图 [monster] 的顺序,
    /// 造不出来的怪被跳过、后面的序号前移)。这与联网起始地图的怪记录顺序是否一一对应,取决于宿主用什么地图建房间——宿主拿服务端记录建本地怪时才有语义上的对应(这一版还没做),
    /// 现在只是"按位置对号"。
    /// </para>
    /// 一个观察者盯一个房间;同一间房可以随时新建观察者(它会把房间里已经死了的怪也交一遍,联网层按"每只怪只报一次"去重)。不是线程安全的,与房间会话一样在宿主的主线程用。
    /// </summary>
    public sealed class RoomKillWatcher
    {
        private readonly RoomCombatSession _room;
        private readonly HashSet<int> _reported = new HashSet<int>();

        /// <param name="room">要盯的房间会话。</param>
        /// <exception cref="ArgumentNullException"><paramref name="room"/> 为 null。</exception>
        public RoomKillWatcher(RoomCombatSession room)
        {
            _room = room ?? throw new ArgumentNullException(nameof(room));
        }

        /// <summary>盯着的房间会话。</summary>
        public RoomCombatSession Room => _room;

        /// <summary>到目前为止交出过多少只怪(每只一次)。</summary>
        public int Reported => _reported.Count;

        /// <summary>
        /// 扫一遍房间:把自上次扫描以来新死的出生点怪的序号追加到 <paramref name="into"/>(按怪物列表顺序,也就是出生顺序),返回追加了几个。
        /// 活着的怪、召唤出来的怪、已经交过的怪都不交。
        /// </summary>
        /// <param name="into">接收序号的列表(宿主复用同一个列表,免得每帧分配)。</param>
        /// <exception cref="ArgumentNullException"><paramref name="into"/> 为 null。</exception>
        public int Poll(List<int> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            IReadOnlyList<MonsterActor> monsters = _room.Monsters;
            int found = 0;
            for (int i = 0; i < monsters.Count; i++)
            {
                MonsterActor monster = monsters[i];
                if (monster.IsSummoned || monster.IsAlive)
                {
                    continue;
                }
                if (!_reported.Add(monster.Index))
                {
                    continue;
                }
                into.Add(monster.Index);
                found++;
            }
            return found;
        }
    }
}
