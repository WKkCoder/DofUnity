using Dnf.Game.Act;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物未决簇 1 在被动对象上的逻辑状态:
    /// <list type="bullet">
    /// <item>[SET VISIBLITY]:写入 <see cref="ActVisibilityByte"/>(出生为 1,0x007fb7a7),与 [IS VISIBLE] 共享。</item>
    /// <item>[SET PASSTYPE]:写入 <see cref="ActPassType"/>(0x010cb686);碰撞过滤未接,只存。</item>
    /// <item>[SET WIDTH]:写入 <see cref="ActWidthX"/> / <see cref="ActWidthY"/>;物理未接,只存。</item>
    /// <item>[SET TEAM] / [CHANGE TEAM] 子对象级联:写入 <see cref="OverrideActTeam"/>。</item>
    /// </list>
    /// </summary>
    public sealed partial class PassiveObject : IActRestStateEntity
    {
        private bool _actVisible = true;
        private int? _actTeamOverride;

        public int ActVisibilityByte => _actVisible ? 1 : 0;

        public int ActPassType { get; private set; }

        public int ActWidthX { get; private set; }

        public int ActWidthY { get; private set; }

        int? IActEntity.OverrideActTeam => _actTeamOverride;

        void IActEntity.SetActTeam(int teamId)
        {
            _actTeamOverride = teamId;
        }

        int IActEntity.ActVisibilityByte => ActVisibilityByte;

        bool IActRestStateEntity.IsActGhost => false;

        int IActRestStateEntity.ActChargedTimeMs => 0;

        void IActRestStateEntity.SetActVisibility(bool visible)
        {
            _actVisible = visible;
        }

        void IActRestStateEntity.SetActGrabbable(bool grabbable)
        {
            // 客户端 [SET GRABABLE] 只转 Monster
        }

        void IActRestStateEntity.SetActDamageCheck(bool on, int durationMs)
        {
            // 客户端 [SET DAMAGE CHECK] 只转 Monster
        }

        void IActRestStateEntity.SetActUnbeatable(bool on, int durationMs)
        {
            // 客户端 [SET UNBEATABLE] 只转 ActiveObject
        }

        void IActRestStateEntity.SetActPassType(int passType)
        {
            ActPassType = passType;
        }

        void IActRestStateEntity.SetActWidth(int widthX, int widthY)
        {
            ActWidthX = widthX;
            ActWidthY = widthY;
        }

        bool IActRestStateEntity.TryBeginActChangeTeam(int newTeam, int durationMs, int hpMaxPercent, IActEntity source, out int originalTeam)
        {
            originalTeam = 0;
            return false;
        }

        bool IActRestStateEntity.TryEndActChangeTeam(out int originalTeam, out int hpMaxPercent, out IActEntity source)
        {
            originalTeam = 0;
            hpMaxPercent = 0;
            source = null;
            return false;
        }
    }
}
