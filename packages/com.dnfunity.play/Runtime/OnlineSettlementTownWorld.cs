using System;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>联网结算回城时的落位来源。</summary>
    public enum OnlineSettlementReturnSource
    {
        Default = 0,
        PreDungeonArea = 1,
        ServerUserArea = 2,
    }

    /// <summary>联网结算回城时交给城镇世界的落位目标(来自 0/23 或进副本前的城镇区)。</summary>
    public readonly struct OnlineSettlementReturnTarget
    {
        public OnlineSettlementReturnTarget(
            OnlineSettlementReturnSource source,
            TownPlace? place,
            double? x = null,
            double? y = null,
            bool? facingLeft = null)
        {
            Source = source;
            Place = place;
            X = x;
            Y = y;
            FacingLeft = facingLeft;
        }

        public OnlineSettlementReturnSource Source { get; }
        public TownPlace? Place { get; }
        public double? X { get; }
        public double? Y { get; }
        public bool? FacingLeft { get; }

        public bool HasCoordinates => X.HasValue && Y.HasValue;

        public string SourceToken
        {
            get
            {
                switch (Source)
                {
                    case OnlineSettlementReturnSource.ServerUserArea: return "server_area";
                    case OnlineSettlementReturnSource.PreDungeonArea: return "pre_dungeon";
                    default: return "default";
                }
            }
        }
    }

    /// <summary>
    /// 联网结算回城落位装饰器(<see cref="ITownWorld"/>):不改 <see cref="GameFlow"/> 与 <see cref="OnlineTownWorld"/>,
    /// 只在结算回城前装载一次性的回城目标(收到 <c>0/23</c> 按服务端村/区/坐标落位,没收到退回进副本前所在城镇区,都没设沿用底层 <see cref="ITownWorld.Return"/>)。
    /// 消费完一次 <see cref="Create"/> 后自动清除覆盖,不影响后续正常回城。
    /// </summary>
    public sealed class OnlineSettlementTownWorld : ITownWorld
    {
        private readonly ITownWorld _inner;
        private OnlineSettlementReturnTarget? _pendingReturn;

        public OnlineSettlementTownWorld(ITownWorld inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public ITownWorld Inner => _inner;
        public bool HasPendingReturn => _pendingReturn.HasValue && _pendingReturn.Value.Place.HasValue;

        public TownPlace Start => _inner.Start;

        public TownPlace Return => _pendingReturn.HasValue && _pendingReturn.Value.Place.HasValue
            ? _pendingReturn.Value.Place.Value
            : _inner.Return;

        /// <summary>装载下一次回城的单次落位覆盖(调用 <see cref="GameFlow.EnterTown"/> 前设置)。</summary>
        public void ArmReturn(OnlineSettlementReturnTarget target)
        {
            if (target.Place.HasValue)
            {
                _pendingReturn = target;
            }
            else
            {
                _pendingReturn = null;
            }
        }

        /// <summary>清除尚未消费的落位覆盖。</summary>
        public void ClearReturn()
        {
            _pendingReturn = null;
        }

        public IHeroSession Create(TownPlace place)
        {
            OnlineSettlementReturnTarget? consumed = null;
            if (_pendingReturn.HasValue && _pendingReturn.Value.Place.HasValue
                && _pendingReturn.Value.Place.Value.Town == place.Town
                && _pendingReturn.Value.Place.Value.Area == place.Area)
            {
                consumed = _pendingReturn;
                _pendingReturn = null;
            }

            IHeroSession session = _inner.Create(place);
            if (consumed.HasValue && consumed.Value.HasCoordinates && session != null)
            {
                if (session.Hero != null)
                {
                    bool facing = consumed.Value.FacingLeft ?? session.Hero.FacingLeft;
                    session.Hero.Place(consumed.Value.X.Value, consumed.Value.Y.Value, facing, HeroMotion.Rest, 0);
                    session.Camera?.Restart(session.Hero.X, session.Hero.Y);
                }
                if (session is ITownExitReset resettable)
                {
                    resettable.ClearPendingExit();
                }
                if (_inner is OnlineTownWorld onlineTown)
                {
                    onlineTown.RecordSafePosition();
                }
            }
            return session;
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => _inner.Splash(from, to);

        public TownPlace? Through(TownPlace from, TownExit exit, bool heroFacingLeft) => _inner.Through(from, exit, heroFacingLeft);

        public bool TryRoute(TownPlace from, out TownExit exit) => _inner.TryRoute(from, out exit);
    }
}
