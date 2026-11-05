using Dnf.Game;
using Dnf.Game.Act;

// 证据:[APPEND HIT] 执行块 0x010bdcac,强行命中函数 0x00878f50 → 命中应用 0x00875de0(E-057);
// 不走 tryHit 的几何、已命中登记、最大目标数与队伍这几道门(静态读码,非 REA)。
namespace Dnf.Play
{
    /// <summary>
    /// 脚本的 [APPEND HIT] 落到房间里:拥有者对主角强行打一击。
    /// <para>
    /// 观察:不看攻击框有没有碰到、不看"这一招已经打中过"的登记;伤害按拥有者当前的攻击信息结算,落点是行为给的位置。
    /// 推断(只往"更不容易误伤"的方向取):主角的无敌与已死照常挡住这一击(受击方自己的检查在受保护区);拥有者是怪物时攻击者是这只怪物
    /// (命中停顿一起定住,与普通命中一致),拥有者是被动对象时没有怪物当攻击者;主角一侧的对象去打怪物的追加命中没有落地(受击方的检查读不到),只计数。
    /// 强行命中不唤醒拥有者的 [ON ATTACKSUCCESS](客户端有没有这条回调没读到)。
    /// </para>
    /// </summary>
    public sealed partial class RoomCombatSession
    {
        /// <summary>脚本强行追加、真打到了主角的命中次数。</summary>
        public int AppendHitsLanded { get; private set; }

        /// <summary>追加命中没有落到主角身上的次数:目标不是主角、主角已死或无敌或没有生命条、拥有者不是怪物这一条线上的对象。</summary>
        public int AppendHitsSkipped { get; private set; }

        private void ActAppendHit(IActEntity owner, IActEntity target, float x, float y, float z)
        {
            MonsterActor attacker = null;
            MonsterActor monster;
            HeroStrikeInfo info;
            bool facingLeft;
            if (owner is PassiveObject passive)
            {
                monster = passive.Owner;
                info = passive.Strike;
                facingLeft = passive.FacingLeft;
            }
            else
            {
                monster = MonsterActor.From(owner);
                attacker = monster;
                info = monster?.Strike ?? default;
                facingLeft = monster != null && monster.FacingLeft;
            }
            if (monster == null || !ReferenceEquals(target, _heroEntity) || Vitals == null || !Vitals.IsAlive || HeroInvincible)
            {
                AppendHitsSkipped++;
                return;
            }
            // 落点取整数、做成一个点(伤害数字按相交盒的中心放,点的中心就是它自己)
            int ix = (int)x, iy = (int)y, iz = (int)z;
            AppendHitsLanded++;
            StrikeHero(monster, info, facingLeft, _room.Hero, attacker, new Aabb(ix, ix, iy, iy, iz, iz));
        }

        private sealed partial class ActWorld : IActMessageHost
        {
            public void AppendHit(IActEntity owner, IActEntity target, float x, float y, float z)
            {
                _session.ActAppendHit(owner, target, x, y, z);
            }
        }
    }
}
