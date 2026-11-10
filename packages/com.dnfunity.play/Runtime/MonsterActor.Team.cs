using Dnf.Game.Act;

namespace Dnf.Play
{
    public sealed partial class MonsterActor
    {
        /// <summary>
        /// [SET TEAM] 写进来的队号;<c>null</c> 是没被改过(队号取 <see cref="IActEntity.Team"/> 的怪物默认值 100)。
        /// 客户端怪物类的 setTeam(虚槽 <c>+0x228</c>)是基类实现 <c>0x00442c90</c>:只把字段 <c>+0x644</c> 写成参数,没有别的副作用;
        /// getTeam(虚槽 <c>+0x22c</c>)读同一个字段,所以脚本改过之后一切读队号的地方(叫同伴的收集、目标集合里按队筛选)读到的都是新值。
        /// 字段只会被再次写入覆盖,没有"还原"这一步。
        /// </summary>
        internal int? ActTeamOverride { get; private set; }

        internal void SetActTeam(int teamId)
        {
            ActTeamOverride = teamId;
        }

        private sealed partial class ActBody
        {
            public int? OverrideActTeam => _m.ActTeamOverride;

            public void SetActTeam(int teamId)
            {
                _m.SetActTeam(teamId);
            }
        }
    }
}
