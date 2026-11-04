namespace Dnf.Game.Act
{
    /// <summary>[REALDUNGEON CHECKUP] / [IS OURTEAMATTACKED](M73,E-173)。</summary>
    public sealed partial class ActRunner
    {
        /// <summary>
        /// [REALDUNGEON CHECKUP](0x010d3e22,E-173):三整数 mode、key、third;third==0 时 mode0/1 要求 lookup==0、mode2 要求 lookup!=0,
        /// 否则 mode0/1 要求 lookup≥third、mode2 要求 lookup&lt;third(有符号)。
        /// </summary>
        public static bool RealDungeonCheckupPasses(int mode, int key, int third, System.Func<int, int, int> lookup)
        {
            if ((uint)mode > 2u)
            {
                return false;
            }
            int value = lookup(mode, key);
            if (third == 0)
            {
                return mode == 2 ? value != 0 : value == 0;
            }
            return mode == 2 ? value < third : value >= third;
        }

        private bool RealDungeonCheckupPasses(ActRecord rec)
        {
            int mode = rec.Ints != null && rec.Ints.Length > 0 ? rec.Ints[0] : 0;
            int key = rec.Ints != null && rec.Ints.Length > 1 ? rec.Ints[1] : 0;
            int third = rec.Ints != null && rec.Ints.Length > 2 ? rec.Ints[2] : 0;
            return RealDungeonCheckupPasses(mode, key, third, _host.RealDungeonLookup);
        }

        /// <summary>[IS OURTEAMATTACKED](0x010d5d94,E-173):0x010d5d31 cast 拥有者/目标 @019d91e8;由宿主查对象管理器里的受击态与攻击者字段。</summary>
        private bool KeepOurTeamAttacked(IActEntity candidate)
        {
            if (!_owner.ActTargetStateOwnerEligible)
            {
                return false;
            }
            if (!candidate.ActTargetStateSubjectEligible)
            {
                return false;
            }
            return _host.MatchesOurTeamAttacked(_owner, candidate);
        }
    }
}
