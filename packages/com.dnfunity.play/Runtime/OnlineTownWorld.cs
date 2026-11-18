using System;
using Dnf.Render;

namespace Dnf.Play
{
    public enum OnlineTownPromptKind
    {
        None = 0,
        Waiting = 1,
        ForbiddenMinutes = 2,
        Rejected = 3,
        UnmappedArea = 4,
    }

    /// <summary>城镇切区时的非阻塞提示状态(供叠层画在屏幕上)。</summary>
    public sealed class OnlineTownPrompt
    {
        public OnlineTownPrompt(OnlineTownPromptKind kind, int minutes = 0, long expiresAtMs = 0)
        {
            Kind = kind;
            Minutes = minutes;
            ExpiresAtMs = expiresAtMs;
        }

        public OnlineTownPromptKind Kind { get; }
        public int Minutes { get; }
        public long ExpiresAtMs { get; }

        public string Message(bool hasFonts)
        {
            switch (Kind)
            {
                case OnlineTownPromptKind.Waiting:
                    return hasFonts ? "正在切换区域…" : "CHANGING AREA...";
                case OnlineTownPromptKind.ForbiddenMinutes:
                    if (Minutes > 0)
                    {
                        return hasFonts ? "该区域暂未开放，约 " + Minutes + " 分钟后可进入" : "AREA LOCKED (" + Minutes + " MIN)";
                    }
                    return hasFonts ? "当前暂时无法进入该区域" : "AREA TEMPORARILY LOCKED";
                case OnlineTownPromptKind.Rejected:
                    return hasFonts ? "无法切换至目标区域，已留在当前区域" : "AREA MOVE REJECTED";
                case OnlineTownPromptKind.UnmappedArea:
                    return hasFonts ? "目标区域暂未映射，已留在当前区域" : "UNMAPPED AREA";
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// 联网版城镇世界(<see cref="ITownWorld"/> 装饰器):
    /// 不包装具体的 <see cref="TownSession"/>,保证城镇内名牌、任务标记等 <c>Session as TownSession</c> 判断原样成立;
    /// 只在 <see cref="Through"/> 拦截需要联网同步的区域切换,按 <c>1/38</c> + <c>0/23</c>/<c>0/24</c>(Gate 仅 <c>0/24</c>)确认后再放行进下一块区域,
    /// 失败或超时把主角安全退回出口前的位置并清理锁存出口。
    /// </summary>
    public sealed class OnlineTownWorld : ITownWorld
    {
        private const long BounceSuppressMs = 400;
        private const long PromptDurationMs = 4000;

        private readonly ITownWorld _inner;
        private readonly TownAtlas _atlas;
        private readonly OnlineTownCommandCoordinator _coordinator;
        private readonly Action<string> _log;

        private IHeroSession _activeSession;
        private TownPlace? _activePlace;
        private double _safeX;
        private double _safeY;
        private bool _safeFacingLeft;
        private bool _hasSafePos;

        private bool _hasConfirmed;
        private TownPlace _confirmedFrom;
        private TownPlace _confirmedTo;

        private bool _suppressBounce;
        private long _suppressUntilMs;
        private TownPlace? _lastDisabledFrom;
        private TownExit? _lastDisabledExit;
        private long _lastNowMs;

        public OnlineTownWorld(ITownWorld inner, TownAtlas atlas, OnlineTownCommandCoordinator coordinator, Action<string> log = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _log = log;
        }

        public ITownWorld Inner => _inner;
        public OnlineTownCommandCoordinator Coordinator => _coordinator;
        public bool IsAreaChangePending => _coordinator.HasInFlight;
        public OnlineTownPrompt CurrentPrompt { get; private set; }

        public TownPlace Start => _inner.Start;
        public TownPlace Return => _inner.Return;

        public IHeroSession Create(TownPlace place)
        {
            IHeroSession session = _inner.Create(place);
            _activeSession = session;
            _activePlace = place;
            _hasSafePos = false;
            _suppressBounce = false;
            _lastDisabledFrom = null;
            _lastDisabledExit = null;
            RecordSafePosition(session, place);
            return session;
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => _inner.Splash(from, to);

        public bool TryRoute(TownPlace from, out TownExit exit) => _inner.TryRoute(from, out exit);

        /// <summary>每一帧推进会话前记录当前不在出口触发里的安全脚底坐标。</summary>
        public void RecordSafePosition()
        {
            RecordSafePosition(_activeSession, _activePlace);
        }

        private void RecordSafePosition(IHeroSession session, TownPlace? place)
        {
            if (session == null || _coordinator.HasInFlight)
            {
                return;
            }
            if (session is ITownExitSource src && src.PendingExit.HasValue)
            {
                return;
            }
            if (session.Hero != null)
            {
                _safeX = session.Hero.X;
                _safeY = session.Hero.Y;
                _safeFacingLeft = session.Hero.FacingLeft;
                _hasSafePos = true;
            }
            else if (session is ITownExitSource exitSource)
            {
                _safeX = exitSource.HeroX;
                _safeY = exitSource.HeroY;
                _safeFacingLeft = exitSource.HeroFacingLeft;
                _hasSafePos = true;
            }
            else if (place.HasValue)
            {
                TownArrival arrival = _atlas.Arrive(place.Value);
                _safeX = arrival.X;
                _safeY = arrival.Y;
                _safeFacingLeft = arrival.FacingLeft;
                _hasSafePos = true;
            }
        }

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            TownPlace? innerTarget = _inner.Through(from, exit, facingLeft);
            if (!exit.HasTarget || !innerTarget.HasValue)
            {
                return innerTarget;
            }
            if (!_coordinator.AreaSyncEnabled)
            {
                if (!SameExit(_lastDisabledFrom, _lastDisabledExit, from, exit))
                {
                    _lastDisabledFrom = from;
                    _lastDisabledExit = exit;
                    _log?.Invoke(OnlineTownLog.AreaChange("disabled"));
                }
                return innerTarget;
            }
            if (_hasConfirmed && _confirmedFrom.Town == from.Town && _confirmedFrom.Area == from.Area
                && _confirmedTo.Town == innerTarget.Value.Town && _confirmedTo.Area == innerTarget.Value.Area)
            {
                _hasConfirmed = false;
                _suppressBounce = false;
                return innerTarget;
            }
            if (_coordinator.HasInFlight)
            {
                return null;
            }
            if (_suppressBounce)
            {
                if (_activeSession is ITownExitReset reset)
                {
                    reset.ClearPendingExit();
                }
                return null;
            }
            if (!OnlineTownAreaRules.TryMapToServerArea(_atlas, from, out byte srcVillage, out byte srcArea)
                || !OnlineTownAreaRules.TryMapToServerArea(_atlas, innerTarget.Value, out byte dstVillage, out byte dstArea))
            {
                _log?.Invoke(OnlineTownLog.AreaChange("unmapped"));
                CurrentPrompt = new OnlineTownPrompt(OnlineTownPromptKind.UnmappedArea, 0, _lastNowMs + PromptDurationMs);
                RollBackToSafeArea(from);
                return null;
            }
            ComputeFootCoordinates(from, out ushort px, out ushort py);
            TownAreaInfo dstInfo = _atlas.GetArea(innerTarget.Value.Town, innerTarget.Value.Area);
            if (_coordinator.BeginAreaChange(from, innerTarget.Value, dstInfo, dstVillage, dstArea, px, py, srcVillage, srcArea, _lastNowMs))
            {
                CurrentPrompt = new OnlineTownPrompt(OnlineTownPromptKind.Waiting);
            }
            return null;
        }

        /// <summary>每帧在 <c>GameFlow.Poll</c> 之前推进联网切区命令并在确认/失败/超时后处理回退或放行。</summary>
        public AreaChangeResult Poll(long nowMs)
        {
            _lastNowMs = nowMs;
            if (_suppressBounce && nowMs >= _suppressUntilMs)
            {
                _suppressBounce = false;
            }
            if (CurrentPrompt != null && CurrentPrompt.Kind != OnlineTownPromptKind.Waiting && CurrentPrompt.ExpiresAtMs > 0 && nowMs >= CurrentPrompt.ExpiresAtMs)
            {
                CurrentPrompt = null;
            }
            AreaChangeResult res = _coordinator.Poll(nowMs);
            switch (res.Status)
            {
                case AreaChangeStatus.Confirmed:
                    _log?.Invoke(OnlineTownLog.AreaChange("ok"));
                    CurrentPrompt = null;
                    _confirmedFrom = res.From;
                    _confirmedTo = res.To;
                    _hasConfirmed = true;
                    break;
                case AreaChangeStatus.Forbidden:
                    _log?.Invoke(OnlineTownLog.AreaChange("forbidden", res.ErrorCode, res.RemainingMinutes > 0 ? (int?)res.RemainingMinutes : null));
                    CurrentPrompt = new OnlineTownPrompt(OnlineTownPromptKind.ForbiddenMinutes, res.RemainingMinutes, nowMs + PromptDurationMs);
                    RollBackToSafeArea(res.From);
                    break;
                case AreaChangeStatus.RolledBack:
                case AreaChangeStatus.SendFailed:
                    _log?.Invoke(OnlineTownLog.AreaChange(res.Status == AreaChangeStatus.SendFailed ? "send_failed" : "rejected", res.ErrorCode));
                    CurrentPrompt = new OnlineTownPrompt(OnlineTownPromptKind.Rejected, 0, nowMs + PromptDurationMs);
                    RollBackToSafeArea(res.From);
                    break;
                case AreaChangeStatus.Timeout:
                    _log?.Invoke(OnlineTownLog.AreaChange("timeout"));
                    CurrentPrompt = null;
                    RollBackToSafeArea(res.From);
                    break;
            }
            return res;
        }

        private void RollBackToSafeArea(TownPlace from)
        {
            _suppressBounce = true;
            _suppressUntilMs = _lastNowMs + BounceSuppressMs;
            _hasConfirmed = false;
            if (_activeSession == null)
            {
                return;
            }
            if (_activeSession.Hero != null)
            {
                double x = _safeX;
                double y = _safeY;
                bool facing = _safeFacingLeft;
                if (!_hasSafePos)
                {
                    TownArrival arrival = _atlas.Arrive(from);
                    x = arrival.X;
                    y = arrival.Y;
                    facing = arrival.FacingLeft;
                }
                _activeSession.Hero.Place(x, y, facing, HeroMotion.Rest, 0);
            }
            if (_activeSession is ITownExitReset resettable)
            {
                resettable.ClearPendingExit();
            }
            _activeSession.Update(0.0, 0, 0);
        }

        private void ComputeFootCoordinates(TownPlace from, out ushort x, out ushort y)
        {
            if (_activeSession?.Hero != null)
            {
                x = OnlineTownAreaRules.FormatCoordinate(_activeSession.Hero.X);
                y = OnlineTownAreaRules.FormatCoordinate(_activeSession.Hero.Y);
                return;
            }
            if (_activeSession is ITownExitSource exitSource)
            {
                x = OnlineTownAreaRules.FormatCoordinate(exitSource.HeroX);
                y = OnlineTownAreaRules.FormatCoordinate(exitSource.HeroY);
                return;
            }
            TownArrival arrival = _atlas.Arrive(from);
            x = OnlineTownAreaRules.FormatCoordinate(arrival.X);
            y = OnlineTownAreaRules.FormatCoordinate(arrival.Y);
        }

        private static bool SameExit(TownPlace? lastFrom, TownExit? lastExit, TownPlace from, TownExit exit)
        {
            return lastFrom.HasValue && lastExit.HasValue
                && lastFrom.Value.Town == from.Town
                && lastFrom.Value.Area == from.Area
                && lastExit.Value.Town == exit.Town
                && lastExit.Value.Area == exit.Area
                && lastExit.Value.Rect.X == exit.Rect.X
                && lastExit.Value.Rect.Y == exit.Rect.Y;
        }
    }
}
